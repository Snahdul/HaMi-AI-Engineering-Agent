using Akka.Actor;
using Akka.Pattern;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HaMi.Agentic.Cli.Features.Development;

internal interface IDevelopmentCandidateGenerator
{
    Task<string> GenerateAsync(
        DevelopmentAgentContract contract, string feedback, int attempt,
        CancellationToken cancellationToken);
}

internal interface IDevelopmentIsolatedRunner
{
    Task<DevelopmentSandboxAttempt> ExecuteAsync(
        DevelopmentAgentContract contract, string generatedSource, int attempt,
        CancellationToken cancellationToken);
}

internal sealed record DevelopmentSandboxAttempt(
    bool Succeeded, int RestoreExitCode, int BuildExitCode, int RunExitCode,
    bool TimedOut, bool CleanupSucceeded, string Feedback, string? ReportPath,
    string? InfrastructureError = null, bool? OutputAccepted = null);

internal sealed record DevelopmentJobAttempt(
    int Number, string Status, int RestoreExitCode, int BuildExitCode,
    int RunExitCode, bool TimedOut, bool CleanupSucceeded, string? ReportPath,
    bool? OutputAccepted = null);

internal sealed record DevelopmentJobReport(
    string JobId, string Status, int ModelCalls, DevelopmentJobAttempt[] Attempts,
    string? Failure,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? IssueNumber = null);

/// <summary>
/// Single-job Akka coordinator. Only this actor owns the retry counter and
/// state transitions. The async services report completions through its mailbox.
/// No actor restart can recreate external sandbox side effects.
/// </summary>
internal sealed class DevelopmentJobActor : ReceiveActor
{
    internal sealed record Start;
    internal sealed record Cancel;
    private sealed record Generated(int Attempt, string Source);
    private sealed record Executed(int Attempt, DevelopmentSandboxAttempt Result);
    private sealed record Faulted(int Attempt, string Stage, string FailureType);

    private readonly DevelopmentAgentContract contract;
    private readonly IDevelopmentCandidateGenerator generator;
    private readonly IDevelopmentIsolatedRunner isolatedRunner;
    private readonly CancellationTokenSource cancellation = new();
    private readonly List<DevelopmentJobAttempt> attempts = [];

    private readonly string jobId;
    private readonly ILogger? logger;
    private readonly bool logSuccessfulSteps;
    private readonly bool logDecisionSummaries;
    private long generationStarted;
    private long sandboxStarted;
    private IActorRef? requester;
    private string feedback = "";
    private int attempt;
    private int modelCalls;
    private bool cancelRequested;
    private Stage stage = Stage.Idle;

    private enum Stage { Idle, Generating, Executing, Finished }

    public DevelopmentJobActor(DevelopmentAgentContract contract,
        IDevelopmentCandidateGenerator generator,
        IDevelopmentIsolatedRunner isolatedRunner, ILogger? logger = null, string? requestedJobId = null,
        bool logSuccessfulSteps = true, bool logDecisionSummaries = true)
    {
        this.contract = contract;
        this.generator = generator;
        this.isolatedRunner = isolatedRunner;
        this.logger = logger;
        this.jobId = requestedJobId ?? Guid.NewGuid().ToString("N");
        this.logSuccessfulSteps = logSuccessfulSteps;
        this.logDecisionSummaries = logDecisionSummaries;

        Receive<Start>(_ =>
        {
            if (stage != Stage.Idle)
            {
                Sender.Tell(new DevelopmentJobReport(jobId, "rejected-duplicate", 0, [], "Job already started."));
                return;
            }

            requester = Sender;
            if (cancelRequested)
            {
                Finish("cancelled", "Cancelled before job start.");
                return;
            }
            attempt = 1;
            BeginGeneration();
        });

        Receive<Generated>(message =>
        {
            if (stage != Stage.Generating || message.Attempt != attempt)
            {
                if (logger is not null) DevelopmentLogEvents.MessageIgnored(logger, jobId, message.Attempt, stage.ToString());
                return;
            }
            modelCalls++;
            if (logger is not null && logSuccessfulSteps)
                DevelopmentLogEvents.GenerationFinished(logger, jobId, attempt,
                    message.Source?.Length ?? 0, (long)Stopwatch.GetElapsedTime(generationStarted).TotalMilliseconds);
            if (string.IsNullOrWhiteSpace(message.Source) || message.Source.Length > 16 * 1024)
            {
                attempts.Add(new DevelopmentJobAttempt(attempt, "source-rejected", -1, -1, -1,
                    false, true, null));
                feedback = "Generated source must be a nonempty C# file, at most 16 KiB.";
                if (logger is not null)
                    DevelopmentLogEvents.AttemptFailed(logger, jobId, attempt, "source-rejected", "SOURCE_SIZE_OR_EMPTY");
                RetryOrFinish("SOURCE_REJECTED");
                return;
            }

            stage = Stage.Executing;
            sandboxStarted = Stopwatch.GetTimestamp();
            if (logger is not null && logSuccessfulSteps) DevelopmentLogEvents.SandboxStarted(logger, jobId, attempt);
            int current = attempt;
            IActorRef self = Self;
            Task<DevelopmentSandboxAttempt> task;
            try { task = isolatedRunner.ExecuteAsync(contract, message.Source, current, cancellation.Token); }
            catch (Exception ex)
            {
                self.Tell(new Faulted(current, "sandbox", ex.GetType().Name));
                return;
            }
            _ = task.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                    self.Tell(new Executed(current, t.Result));
                else
                    self.Tell(new Faulted(current, "sandbox",
                        t.IsCanceled ? "OperationCanceled" : t.Exception?.GetBaseException().GetType().Name ?? "Unknown"));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        });

        Receive<Executed>(message =>
        {
            if (stage != Stage.Executing || message.Attempt != attempt)
            {
                if (logger is not null) DevelopmentLogEvents.MessageIgnored(logger, jobId, message.Attempt, stage.ToString());
                return;
            }
            DevelopmentSandboxAttempt result = message.Result;
            // Retry ONLY an observed compiler or program failure. In particular,
            // sbx/preflight/restore failures have -1 phase codes and MUST NOT
            // be mistaken for ordinary C# compiler diagnostics.
            string status = ClassifyResult(result);
            if (logger is not null && logSuccessfulSteps)
                DevelopmentLogEvents.SandboxFinished(logger, jobId, attempt, status,
                    result.RestoreExitCode, result.BuildExitCode, result.RunExitCode,
                    result.TimedOut, result.CleanupSucceeded,
                    (long)Stopwatch.GetElapsedTime(sandboxStarted).TotalMilliseconds);
            attempts.Add(new DevelopmentJobAttempt(attempt, status,
                result.RestoreExitCode, result.BuildExitCode, result.RunExitCode,
                result.TimedOut, result.CleanupSucceeded, result.ReportPath,
                result.BuildExitCode == 0 && result.RunExitCode == 0
                    && result.CleanupSucceeded && result.InfrastructureError is null
                    ? result.OutputAccepted : null));

            // A coordinator deadline stops NEW attempts, but must not stop the
            // actor or its host until the already-running sbx task has returned
            // from its own bounded cleanup/finally.
            if (cancelRequested)
            {
                Finish(result.CleanupSucceeded ? "cancelled" : "failed",
                    result.CleanupSucceeded ? "Cancelled after sandbox cleanup." : "cleanup-failed");
                return;
            }

            if (status == "passed")
            {
                Finish("passed", null);
                return;
            }

            // No automatic retry after infrastructure, restore, timeout or
            // unconfirmed cleanup failure. The next attempt may run ONLY after
            // a completed isolated compiler/program failure with confirmed cleanup.
            if (status is not ("build-failed" or "run-failed" or "acceptance-failed"))
            {
                if (logger is not null)
                    DevelopmentLogEvents.JobFailed(logger, jobId, "sandbox", status, "none",
                        status switch
                        {
                            "cleanup-failed" => "MicroVM-Zustand vor einem erneuten Start manuell pruefen.",
                            "timed-out" => "Zeitlimit und Sandbox-Status kontrollieren; kein automatischer Retry.",
                            _ => "Sandbox-Phasenreport (restore/build/exec) und Sicherheits-Preflight pruefen."
                        });
                Finish("failed", status);
                return;
            }

            if (logger is not null)
            {
                Match diagnostic = Regex.Match(result.Feedback, @"\bCS\d{4}\b",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                DevelopmentLogEvents.AttemptFailed(logger, jobId, attempt, status,
                    diagnostic.Success ? diagnostic.Value.ToUpperInvariant()
                        : status == "acceptance-failed" ? "ACCEPTANCE_MISMATCH" : "EXECUTION_NONZERO");
            }

            feedback = DevelopmentSandboxRunner.SafeLogPreview(result.Feedback);
            if (feedback.Length > 1200) feedback = feedback[..1200];
            if (string.IsNullOrWhiteSpace(feedback))
                feedback = "The isolated build or run failed with a nonzero exit code.";
            RetryOrFinish(status);
        });

        Receive<Faulted>(message =>
        {
            if (message.Attempt != attempt || stage == Stage.Finished) return;
            if (logger is not null)
                DevelopmentLogEvents.JobFailed(logger, jobId, message.Stage, "OPERATION_FAULT",
                    message.FailureType, message.Stage == "generator"
                        ? "Modell-/Generator-Konfiguration und Auftragsbudget pruefen."
                        : "Sandbox-Report und lokalen MicroVM-Zustand pruefen.");
            Finish(cancelRequested ? "cancelled" : "failed",
                message.Stage + ":" + message.FailureType);
        });

        Receive<Cancel>(_ =>
        {
            if (stage == Stage.Finished) return;
            cancelRequested = true;
            if (logger is not null && logDecisionSummaries)
                DevelopmentLogEvents.DecisionRecorded(logger, jobId, attempt,
                    "CANCEL_REQUESTED", "HOST_CANCEL_OR_JOB_DEADLINE");
            cancellation.Cancel();
            // During a sandbox execution wait for Executed/Faulted: stopping
            // the ActorSystem now could abandon the in-flight MicroVM cleanup.
            if (stage == Stage.Generating)
                Finish("cancelled", "Cancelled during generation.");
            // In Idle, Start must first capture the Ask reply sender.
        });
    }

    private static string ClassifyResult(DevelopmentSandboxAttempt result)
    {
        if (!result.CleanupSucceeded) return "cleanup-failed";
        if (result.TimedOut) return "timed-out";
        if (result.InfrastructureError is not null || result.RestoreExitCode != 0
            || result.BuildExitCode < 0)
            return "infrastructure-failed";
        if (result.Succeeded && result.BuildExitCode == 0 && result.RunExitCode == 0
            && result.OutputAccepted == true)
            return "passed";

        // Only C# compiler diagnostics from the completed build are eligible
        // for a repair attempt. sbx failures without a compiler diagnostic
        // must fail closed, not spend an additional MicroVM attempt.
        if (result.BuildExitCode > 0 && result.RunExitCode == -1
            && result.Feedback.Contains("error CS", StringComparison.OrdinalIgnoreCase))
            return "build-failed";
        if (result.BuildExitCode == 0 && result.RunExitCode > 0)
            return "run-failed";
        // The guest may exit successfully while violating the human-reviewed
        // stdout contract. This is a correctable task failure, not success or
        // a sandbox infrastructure failure.
        if (result.BuildExitCode == 0 && result.RunExitCode == 0
            && result.OutputAccepted == false)
            return "acceptance-failed";
        return "infrastructure-failed";
    }

    private void BeginGeneration()
    {
        stage = Stage.Generating;
        generationStarted = Stopwatch.GetTimestamp();
        if (logger is not null && logSuccessfulSteps)
            DevelopmentLogEvents.GenerationStarted(logger, jobId, attempt,
                !string.IsNullOrEmpty(feedback));
        int current = attempt;
        string currentFeedback = feedback;
        IActorRef self = Self;
        Task<string> task;
        try { task = generator.GenerateAsync(contract, currentFeedback, current, cancellation.Token); }
        catch (Exception ex)
        {
            self.Tell(new Faulted(current, "generator", ex.GetType().Name));
            return;
        }
        _ = task.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                self.Tell(new Generated(current, t.Result));
            else
                self.Tell(new Faulted(current, "generator",
                    t.IsCanceled ? "OperationCanceled" : t.Exception?.GetBaseException().GetType().Name ?? "Unknown"));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void RetryOrFinish(string reasonCode)
    {
        if (attempt >= contract.MaxAttempts)
        {
            if (logger is not null)
                DevelopmentLogEvents.JobFailed(logger, jobId, "retry", "ATTEMPT_BUDGET_EXHAUSTED",
                    "none", "Auftragsbudget pruefen; keine automatische Budgeterhoehung.");
            Finish("exhausted", "Approved attempt budget exhausted.");
            return;
        }

        if (logger is not null && logDecisionSummaries)
            DevelopmentLogEvents.DecisionRecorded(logger, jobId, attempt,
                "RETRY_APPROVED", reasonCode);
        attempt++;
        BeginGeneration();
    }

    private void Finish(string status, string? failure)
    {
        if (stage == Stage.Finished) return;
        stage = Stage.Finished;
        requester?.Tell(new DevelopmentJobReport(jobId, status, modelCalls, [.. attempts], failure));
        Context.Stop(Self);
    }

    protected override void PostStop()
    {
        cancellation.Cancel();
        cancellation.Dispose();
        base.PostStop();
    }
}

internal static class DevelopmentActorCoordinator
{
    public static async Task<DevelopmentJobReport> RunAsync(
        ActorSystem system, DevelopmentAgentContract contract,
        IDevelopmentCandidateGenerator generator, IDevelopmentIsolatedRunner sandbox,
        CancellationToken cancellationToken = default, TimeSpan? jobTimeout = null,
        ILogger? logger = null, string? jobId = null,
        bool logSuccessfulSteps = true, bool logDecisionSummaries = true)
    {
        TimeSpan deadline = jobTimeout ?? TimeSpan.FromMinutes(12);
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromMinutes(12))
            throw new ArgumentOutOfRangeException(nameof(jobTimeout));

        IActorRef actor = system.ActorOf(
            Props.Create(() => new DevelopmentJobActor(contract, generator, sandbox,
                logger, jobId, logSuccessfulSteps, logDecisionSummaries)),
            "coding-" + Guid.NewGuid().ToString("N"));

        // Do NOT cancel/timeout Ask at the job deadline: doing so would tear
        // down Akka.Hosting before the in-flight sandbox has reached its
        // finally/cleanup. Deliver Cancel to the actor, then await its
        // terminal report. The underlying sbx operations have phase limits.
        Task<DevelopmentJobReport> completed = actor.Ask<DevelopmentJobReport>(
            new DevelopmentJobActor.Start(), TimeSpan.FromMinutes(45));
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenRegistration registration = combined.Token.Register(
            () => actor.Tell(new DevelopmentJobActor.Cancel()));
        combined.CancelAfter(deadline);
        return await completed;
    }
}
