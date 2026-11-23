using Akka.Actor;
using Akka.Pattern;

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
    string? InfrastructureError = null);

internal sealed record DevelopmentJobAttempt(
    int Number, string Status, int RestoreExitCode, int BuildExitCode,
    int RunExitCode, bool TimedOut, bool CleanupSucceeded, string? ReportPath);

internal sealed record DevelopmentJobReport(
    string JobId, string Status, int ModelCalls, DevelopmentJobAttempt[] Attempts,
    string? Failure);

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

    private readonly string jobId = Guid.NewGuid().ToString("N");
    private IActorRef? requester;
    private string feedback = "";
    private int attempt;
    private int modelCalls;
    private Stage stage = Stage.Idle;

    private enum Stage { Idle, Generating, Executing, Finished }

    public DevelopmentJobActor(DevelopmentAgentContract contract,
        IDevelopmentCandidateGenerator generator,
        IDevelopmentIsolatedRunner isolatedRunner)
    {
        this.contract = contract;
        this.generator = generator;
        this.isolatedRunner = isolatedRunner;

        Receive<Start>(_ =>
        {
            if (stage != Stage.Idle)
            {
                Sender.Tell(new DevelopmentJobReport(jobId, "rejected-duplicate", 0, [], "Job already started."));
                return;
            }

            requester = Sender;
            attempt = 1;
            BeginGeneration();
        });

        Receive<Generated>(message =>
        {
            if (stage != Stage.Generating || message.Attempt != attempt) return;
            modelCalls++;
            if (string.IsNullOrWhiteSpace(message.Source) || message.Source.Length > 16 * 1024)
            {
                attempts.Add(new DevelopmentJobAttempt(attempt, "source-rejected", -1, -1, -1,
                    false, true, null));
                feedback = "Generated source must be a nonempty C# file, at most 16 KiB.";
                RetryOrFinish();
                return;
            }

            stage = Stage.Executing;
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
            if (stage != Stage.Executing || message.Attempt != attempt) return;
            DevelopmentSandboxAttempt result = message.Result;
            // Retry ONLY an observed compiler or program failure. In particular,
            // sbx/preflight/restore failures have -1 phase codes and MUST NOT
            // be mistaken for ordinary C# compiler diagnostics.
            string status = ClassifyResult(result);
            attempts.Add(new DevelopmentJobAttempt(attempt, status,
                result.RestoreExitCode, result.BuildExitCode, result.RunExitCode,
                result.TimedOut, result.CleanupSucceeded, result.ReportPath));

            if (status == "passed")
            {
                Finish("passed", null);
                return;
            }

            // No automatic retry after infrastructure, restore, timeout or
            // unconfirmed cleanup failure. The next attempt may run ONLY after
            // a completed isolated compiler/program failure with confirmed cleanup.
            if (status is not ("build-failed" or "run-failed"))
            {
                Finish("failed", status);
                return;
            }

            feedback = DevelopmentSandboxRunner.SafeLogPreview(result.Feedback);
            if (feedback.Length > 1200) feedback = feedback[..1200];
            if (string.IsNullOrWhiteSpace(feedback))
                feedback = "The isolated build or run failed with a nonzero exit code.";
            RetryOrFinish();
        });

        Receive<Faulted>(message =>
        {
            if (message.Attempt != attempt || stage == Stage.Finished) return;
            Finish("failed", message.Stage + ":" + message.FailureType);
        });

        Receive<Cancel>(_ =>
        {
            if (stage != Stage.Finished)
            {
                cancellation.Cancel();
                Finish("cancelled", "Cancelled by caller.");
            }
        });
    }

    private static string ClassifyResult(DevelopmentSandboxAttempt result)
    {
        if (!result.CleanupSucceeded) return "cleanup-failed";
        if (result.TimedOut) return "timed-out";
        if (result.InfrastructureError is not null || result.RestoreExitCode != 0
            || result.BuildExitCode < 0)
            return "infrastructure-failed";
        if (result.Succeeded && result.BuildExitCode == 0 && result.RunExitCode == 0)
            return "passed";

        // Only C# compiler diagnostics from the completed build are eligible
        // for a repair attempt. sbx failures without a compiler diagnostic
        // must fail closed, not spend an additional MicroVM attempt.
        if (result.BuildExitCode > 0 && result.RunExitCode == -1
            && result.Feedback.Contains("error CS", StringComparison.OrdinalIgnoreCase))
            return "build-failed";
        if (result.BuildExitCode == 0 && result.RunExitCode > 0)
            return "run-failed";
        return "infrastructure-failed";
    }

    private void BeginGeneration()
    {
        stage = Stage.Generating;
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

    private void RetryOrFinish()
    {
        if (attempt >= contract.MaxAttempts)
        {
            Finish("exhausted", "Approved attempt budget exhausted.");
            return;
        }

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
    public static Task<DevelopmentJobReport> RunAsync(
        ActorSystem system, DevelopmentAgentContract contract,
        IDevelopmentCandidateGenerator generator, IDevelopmentIsolatedRunner sandbox,
        CancellationToken cancellationToken = default)
    {
        IActorRef actor = system.ActorOf(
            Props.Create(() => new DevelopmentJobActor(contract, generator, sandbox)),
            "coding-" + Guid.NewGuid().ToString("N"));

        // The actor owns this job; a Cancel message prevents later model retries.
        CancellationTokenRegistration registration = cancellationToken.Register(
            () => actor.Tell(new DevelopmentJobActor.Cancel()));
        return AwaitAndDisposeAsync(
            actor.Ask<DevelopmentJobReport>(new DevelopmentJobActor.Start(),
                TimeSpan.FromMinutes(12), cancellationToken), registration);
    }

    private static async Task<DevelopmentJobReport> AwaitAndDisposeAsync(
        Task<DevelopmentJobReport> work, CancellationTokenRegistration registration)
    {
        try { return await work; }
        finally { registration.Dispose(); }
    }
}
