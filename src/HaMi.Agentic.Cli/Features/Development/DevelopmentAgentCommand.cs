using Akka.Actor;
using Akka.Hosting;
using HaMi.Agentic.Cli.Runtime.Logging;
using Microsoft.Extensions.Logging;
using HaMi.Agentic.Cli.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>
/// Composition root for the first actor-driven coding use case.
/// Only the model adapter and the approved sandbox runner perform work;
/// Akka exclusively coordinates their outcomes.
/// </summary>
internal static class DevelopmentAgentCommand
{
    public static async Task<int> RunAsync(
        string repo, string? contractPath, string? sourceDirectory,
        string resultsDirectory, bool live, string? requestedModel)
    {
        string jobId = Guid.NewGuid().ToString("N");
        string phase = "logging";
        AgentLoggingSession? logging = null;
        Microsoft.Extensions.Logging.ILogger? logger = null;
        try
        {
            // One ILogger event is sent to both console and the rolling .clef file.
            // The same ID names the actor job, its temporary directory and its report.
            logging = AgentLoggingSession.Create(repo, includeConsole: true);
            logger = logging.CreateLogger("HaMi.Agentic.Cli.Features.Development");
            if (logging.Settings.LogSuccessfulSteps)
                DevelopmentLogEvents.JobRequested(logger, jobId, repo, resultsDirectory,
                    live ? "live" : "offline-scripted",
                    live ? requestedModel switch { null => "gpt-5.4-mini", { Length: > 64 } => "invalid-model-name", _ => requestedModel } : "scripted",
                    contractPath ?? DevelopmentAgentContract.TrustedPath);

            phase = "contract";
            DevelopmentAgentContract contract = DevelopmentAgentContract.Load(repo, contractPath);
            phase = "results";
            if (!Path.IsPathFullyQualified(resultsDirectory))
                throw new InvalidOperationException("Agent results directory must be absolute.");
            phase = "source-override";
            if (sourceDirectory is not null)
                throw new InvalidOperationException("First actor slice cannot override the human-reviewed source fixture.");

            phase = "snapshot";
            string source = Path.Combine(repo, "samples", "SandboxCoding");
            if (!Path.IsPathFullyQualified(source))
                throw new InvalidOperationException("Agent source snapshot must be absolute.");
            DevelopmentSandboxRunner.InspectSnapshot(source, contract.ProjectFile);

            phase = "storage";
            DevelopmentIsolatedRunner.ValidateStoragePaths(repo, source, resultsDirectory);

            phase = "api-key";
            string? key = live ? RuntimeSettings.GetApiKey() : null;
            if (live && string.IsNullOrWhiteSpace(key))
            {
                DevelopmentLogEvents.JobFailed(logger, jobId, phase, "API_KEY_MISSING",
                    "none", "Host-API-Schluessel konfigurieren oder --live weglassen; kein Job gestartet.");
                return 2;
            }

            if (logging.Settings.LogSuccessfulSteps)
                DevelopmentLogEvents.PreflightAccepted(logger, jobId, contract.TaskId,
                    contract.ProjectFile, contract.MaxAttempts);
            phase = "results-allocation";
            string runRoot = Path.Combine(resultsDirectory, "agent-job-" + jobId);
            Directory.CreateDirectory(runRoot);

            phase = "generator";
            using Microsoft.Extensions.AI.IChatClient? client =
                live ? DevelopmentAiGenerator.CreateClient(key!) : null;
            IDevelopmentCandidateGenerator generator = live
                ? new DevelopmentAiGenerator(client!, requestedModel ?? "gpt-5.4-mini")
                : new DevelopmentScriptedGenerator();
            IDevelopmentIsolatedRunner sandbox =
                new DevelopmentIsolatedRunner(repo, source, runRoot, logger, jobId,
                    logging.Settings.LogSuccessfulSteps);

            phase = "host";
            HostApplicationBuilder builder = Host.CreateApplicationBuilder();
            // The owned job logger emits the lifecycle; suppress default host
            // informational startup/shutdown chatter on the second pipeline.
            builder.Logging.ClearProviders();
            builder.Services.AddAkka("hami-developer", _ => { });
            using IHost host = builder.Build();
            await host.StartAsync();
            try
            {
                phase = "job";
                ActorSystem system = host.Services.GetRequiredService<ActorSystem>();
                DevelopmentJobReport result = await DevelopmentActorCoordinator.RunAsync(
                    system, contract, generator, sandbox, logger: logger, jobId: jobId,
                    logSuccessfulSteps: logging.Settings.LogSuccessfulSteps,
                    logDecisionSummaries: logging.Settings.LogDecisionSummaries);

                phase = "report";
                string jsonPath = Path.Combine(runRoot, "agent-report.json");
                await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(result,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
                if (logging.Settings.LogSuccessfulSteps)
                    DevelopmentLogEvents.JobFinished(logger, jobId, result.Status,
                        result.ModelCalls, result.Attempts.Length, jsonPath);
                return result.Status == "passed" ? 0 : 1;
            }
            finally
            {
                phase = "host-shutdown";
                await host.StopAsync();
            }
        }
        catch (Exception ex)
        {
            // Never emit raw model/sandbox data or untrusted exception messages.
            string reason = phase switch
            {
                "logging" => "LOG_CONFIGURATION_INVALID",
                "contract" => "CONTRACT_REJECTED",
                "results" => "RESULTS_PATH_NOT_ABSOLUTE",
                "source-override" => "SOURCE_OVERRIDE_NOT_ALLOWED",
                "storage" when ex is InvalidOperationException &&
                    ex.Message == "Agent results must be separate from the repository and its fixture."
                    => "RESULTS_PATH_OVERLAPS_REPOSITORY",
                "storage" when ex is InvalidOperationException &&
                    ex.Message == "Agent results path may not traverse directory reparse points."
                    => "RESULTS_PATH_REPARSE_POINT",
                "storage" => "RESULTS_PATH_REJECTED",
                "snapshot" => "SNAPSHOT_REJECTED",
                "api-key" => "API_KEY_INVALID",
                "results-allocation" => "RESULTS_DIRECTORY_UNWRITABLE",
                "generator" => "GENERATOR_INITIALIZATION_FAILED",
                "host" => "HOST_START_FAILED",
                "host-shutdown" => "HOST_STOP_FAILED",
                "report" => "REPORT_WRITE_FAILED",
                _ => "JOB_EXECUTION_FAILED"
            };
            string action = phase switch
            {
                "logging" => "config/logging.json und den externen Logpfad pruefen.",
                "contract" => "Den versionierten, geprueften Agent-Auftrag pruefen.",
                "results" or "storage" => "--results-dir absolut, ausserhalb des Repository und ohne Junctions/Symlinks angeben.",
                "source-override" => "--source entfernen; der gepruefte Agent-Contract bestimmt die einzige Fixture.",
                "snapshot" => "Die freigegebene Demo-Fixture und erlaubte Dateien kontrollieren.",
                "api-key" => "Host-API-Schluessel pruefen.",
                "results-allocation" or "report" => "Dateiberechtigungen und freien Speicher im Ergebnisverzeichnis pruefen.",
                "generator" => "Modell-/Provider-Konfiguration pruefen.",
                "host" => "Akka.Hosting-Konfiguration und DI pruefen.",
                "host-shutdown" => "Akka-CoordinatedShutdown und vorhandene Job-Reports pruefen.",
                _ => "Status der MicroVM und vorhandene Teilberichte pruefen; kein automatischer Neustart."
            };
            if (logger is not null)
                DevelopmentLogEvents.JobFailed(logger, jobId, phase, reason,
                    ex.GetType().Name, action);
            else
                Console.Error.WriteLine(
                    $"Agent job {jobId} failed before logging (stage={phase}, reason={reason}, exception={ex.GetType().Name}). {action}");
            return 1;
        }
        finally
        {
            logging?.Dispose();
        }
    }

    internal static async Task SelfTestAsync(string repo)
    {
        CommandLineRouter.SelfTest();
        DevelopmentAgentContract.SelfTest();
        DevelopmentAgentContract contract = DevelopmentAgentContract.Load(repo, null);
        await DevelopmentAiGenerator.SelfTestAsync(contract);

        // A denied destination must create no folder, stage no source
        // and make no model call. No Docker is involved in this test.
        string rejectedResults = Path.Combine(repo, ".hami-invalid-agent-results");
        if (Directory.Exists(rejectedResults))
            throw new InvalidOperationException("Preflight test destination unexpectedly already exists.");
        int denied = await RunAsync(repo, null, null, rejectedResults, live: false, requestedModel: null);
        if (denied != 1 || Directory.Exists(rejectedResults))
            throw new InvalidOperationException("Results inside repository were not rejected before writes.");
        DevelopmentIsolatedRunner.ValidateStoragePaths(repo,
            Path.Combine(repo, "samples", "SandboxCoding"),
            Path.Combine(Path.GetTempPath(), "hami-safe-output-external"));
        // Use the same Akka.Hosting composition root in CI as in the real
        // agent command; this catches DI/host startup errors without Docker.
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddAkka("hami-development-offline-tests", _ => { });
        using IHost host = builder.Build();
        await host.StartAsync();
        try
        {
            ActorSystem system = host.Services.GetRequiredService<ActorSystem>();
            var sandbox = new SimulatedIsolatedRunner();
            DevelopmentJobReport completed = await DevelopmentActorCoordinator.RunAsync(
                system, contract, new DevelopmentScriptedGenerator(), sandbox);
            if (completed.Status != "passed" || completed.ModelCalls != 2
                || completed.Attempts.Length != 2
                || completed.Attempts[0].Status != "build-failed"
                || completed.Attempts[1].Status != "passed"
                || sandbox.Calls != 2)
                throw new InvalidOperationException("Akka retry flow did not handle compiler feedback correctly.");

            var limited = contract with { MaxAttempts = 1 };
            var budgetSandbox = new SimulatedIsolatedRunner();
            DevelopmentJobReport exhausted = await DevelopmentActorCoordinator.RunAsync(
                system, limited, new DevelopmentScriptedGenerator(), budgetSandbox);
            if (exhausted.Status != "exhausted" || exhausted.ModelCalls != 1
                || budgetSandbox.Calls != 1)
                throw new InvalidOperationException("Actor ignored the approved retry budget.");

            var hanging = new SimulatedIsolatedRunner(timeout: true);
            DevelopmentJobReport timeout = await DevelopmentActorCoordinator.RunAsync(
                system, contract, new DevelopmentScriptedGenerator(), hanging);
            if (timeout.Status != "failed" || timeout.ModelCalls != 1
                || hanging.Calls != 1 || timeout.Attempts[0].Status != "timed-out")
                throw new InvalidOperationException("Actor retried an unsafe sandbox timeout.");

            foreach (string failure in new[] { "preflight", "restore", "build-exec", "cleanup" })
            {
                var faultRunner = new SimulatedIsolatedRunner(failure: failure);
                DevelopmentJobReport fault = await DevelopmentActorCoordinator.RunAsync(
                    system, contract, new DevelopmentScriptedGenerator(), faultRunner);
                string expected = failure == "cleanup" ? "cleanup-failed" : "infrastructure-failed";
                if (fault.Status != "failed" || fault.ModelCalls != 1
                    || faultRunner.Calls != 1 || fault.Attempts.Length != 1
                    || fault.Attempts[0].Status != expected)
                    throw new InvalidOperationException(
                        "Actor retried an infrastructure or unconfirmed-cleanup error: " + failure);
            }

            // A coordinator deadline must NOT complete the job or shut down
            // its ActorSystem while the sandbox task is still running.
            var deferredCleanup = new DelayedIsolatedRunner();
            Task<DevelopmentJobReport> deadlineJob =
                DevelopmentActorCoordinator.RunAsync(system, contract,
                    new DevelopmentScriptedGenerator(), deferredCleanup,
                    jobTimeout: TimeSpan.FromMilliseconds(100));
            await deferredCleanup.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await deferredCleanup.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (deadlineJob.IsCompleted || deferredCleanup.Calls != 1)
                throw new InvalidOperationException(
                    "Coordinator completed before active sandbox cleanup finished.");
            deferredCleanup.Finish(new DevelopmentSandboxAttempt(false, 0, 1, -1,
                false, true, "error CS0103: expected", null));
            DevelopmentJobReport cancelled = await deadlineJob.WaitAsync(TimeSpan.FromSeconds(10));
            if (cancelled.Status != "cancelled" || cancelled.ModelCalls != 1
                || cancelled.Attempts.Length != 1 || deferredCleanup.Calls != 1
                || !cancelled.Attempts[0].CleanupSucceeded)
                throw new InvalidOperationException(
                    "Coordinator did not await cleanup or incorrectly restarted cancelled job.");

            await VerifyNarrativeLoggingAsync(system, contract, repo);

            Console.WriteLine("Akka development offline self-test passed: fail/correct, "
                + "retry budget, timeout, preflight, restore, build-exec, cleanup "
                + "and deadline-awaited sandbox cleanup.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task VerifyNarrativeLoggingAsync(
        ActorSystem system, DevelopmentAgentContract contract, string repo)
    {
        string id = "offline-selftest-development-001";
        string temp = Path.Combine(Path.GetTempPath(), "hami-development-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            AgentLoggingSettings settings = AgentLoggingSettings.Load(repo);
            using (var session = AgentLoggingSession.CreateForTest(settings, temp))
            {
                Microsoft.Extensions.Logging.ILogger logger =
                    session.CreateLogger("HaMi.Agentic.Cli.Features.Development");
                DevelopmentLogEvents.JobRequested(logger, id, repo, temp, "offline-scripted",
                    "scripted", DevelopmentAgentContract.TrustedPath);
                DevelopmentLogEvents.PreflightAccepted(logger, id, contract.TaskId,
                    contract.ProjectFile, contract.MaxAttempts);
                DevelopmentJobReport report = await DevelopmentActorCoordinator.RunAsync(
                    system, contract, new DevelopmentScriptedGenerator(),
                    new SimulatedIsolatedRunner(), logger: logger, jobId: id);
                if (report.Status != "passed" || report.JobId != id || report.Attempts.Length != 2)
                    throw new InvalidOperationException("Development log test actor report differs.");
                DevelopmentLogEvents.JobFinished(logger, id, report.Status,
                    report.ModelCalls, report.Attempts.Length, "selftest-report.json");
            }

            string folder = Path.Combine(temp, settings.RelativeDirectory);
            string[] logFiles = Directory.GetFiles(folder, "agent-*.clef");
            if (logFiles.Length == 0)
                throw new InvalidOperationException("Development narrative file log is missing.");

            string[] readableFiles = Directory.GetFiles(folder, "agent-*.log");
            if (readableFiles.Length == 0)
                throw new InvalidOperationException("Readable development narrative log is missing.");
            string narrative = string.Join("\n", readableFiles.SelectMany(File.ReadAllLines));
            int begin = narrative.IndexOf("JobRequested", StringComparison.Ordinal);
            int failure = narrative.IndexOf("AttemptFailed", StringComparison.Ordinal);
            int retry = narrative.IndexOf("RETRY_APPROVED", StringComparison.Ordinal);
            int finished = narrative.IndexOf("JobFinished", StringComparison.Ordinal);
            if (begin < 0 || failure <= begin || retry <= failure || finished <= retry)
                throw new InvalidOperationException("Readable narrative events are out of order.");

            string combined = string.Join("\n", logFiles.SelectMany(File.ReadAllLines));
            foreach (string eventLine in combined.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using JsonDocument document = JsonDocument.Parse(eventLine);
                if (!document.RootElement.TryGetProperty("JobId", out JsonElement jobProperty)
                    || jobProperty.GetString() != id)
                    throw new InvalidOperationException("Uncorrelated development log event.");
            }

            foreach (string expected in new[] { "JobRequested", "PreflightAccepted",
                "GenerationStarted", "GenerationFinished", "SandboxStarted", "SandboxFinished",
                "AttemptFailed", "DecisionRecorded", "JobFinished", "CS0103" })
                if (!combined.Contains(expected, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Development log is missing the expected event: " + expected);
            if (combined.Contains("UnknownIdentifier", StringComparison.Ordinal)
                || narrative.Contains("UnknownIdentifier", StringComparison.Ordinal)
                || combined.Contains("OPENAI_API_KEY", StringComparison.Ordinal)
                || narrative.Contains("OPENAI_API_KEY", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe raw data entered development logs.");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private sealed class DelayedIsolatedRunner : IDevelopmentIsolatedRunner
    {
        private readonly TaskCompletionSource<DevelopmentSandboxAttempt> finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CancelObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        public Task<DevelopmentSandboxAttempt> ExecuteAsync(
            DevelopmentAgentContract contract, string generatedSource, int attempt,
            CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.Register(() => CancelObserved.TrySetResult(true));
            Started.TrySetResult(true);
            // Simulate a real sbx invocation: cancellation requests do not
            // bypass the VM's in-flight cleanup/finally.
            return finished.Task;
        }

        public void Finish(DevelopmentSandboxAttempt result) =>
            finished.TrySetResult(result);
    }

    private sealed class SimulatedIsolatedRunner(bool timeout = false, string? failure = null)
        : IDevelopmentIsolatedRunner
    {
        public int Calls { get; private set; }

        public Task<DevelopmentSandboxAttempt> ExecuteAsync(
            DevelopmentAgentContract contract, string generatedSource, int attempt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (failure == "preflight")
                return Task.FromResult(new DevelopmentSandboxAttempt(false, -1, -1, -1,
                    false, true, "Guest credentials must be empty", null, "Credential preflight failed."));
            if (failure == "restore")
                return Task.FromResult(new DevelopmentSandboxAttempt(false, 1, -1, -1,
                    false, true, "Offline NuGet restore failed", null));
            if (failure == "build-exec")
                return Task.FromResult(new DevelopmentSandboxAttempt(false, 0, 1, -1,
                    false, true, "Sandbox execution failed before compiler diagnostics", null));
            if (failure == "cleanup")
                return Task.FromResult(new DevelopmentSandboxAttempt(false, 0, 1, -1,
                    false, false, "error CS0103: UnknownIdentifier", null));
            if (timeout)
                return Task.FromResult(new DevelopmentSandboxAttempt(false, 0, 0, -1,
                    true, true, "Simulated timeout", null));
            if (generatedSource.Contains("UnknownIdentifier", StringComparison.Ordinal))
                return Task.FromResult(new DevelopmentSandboxAttempt(false, 0, 1, -1,
                    false, true, "error CS0103: The name UnknownIdentifier does not exist.", null));
            if (generatedSource.Contains("HaMi isolated correction passed", StringComparison.Ordinal))
                return Task.FromResult(new DevelopmentSandboxAttempt(true, 0, 0, 0,
                    false, true, "", null));
            throw new InvalidOperationException("Unexpected fake source content.");
        }
    }
}
