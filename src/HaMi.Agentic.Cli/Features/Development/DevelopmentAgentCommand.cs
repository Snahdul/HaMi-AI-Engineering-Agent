using Akka.Actor;
using Akka.Hosting;
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
        try
        {
            DevelopmentAgentContract contract = DevelopmentAgentContract.Load(repo, contractPath);
            if (!Path.IsPathFullyQualified(resultsDirectory))
                throw new InvalidOperationException("Agent results directory must be absolute.");
            if (sourceDirectory is not null)
                throw new InvalidOperationException("First actor slice cannot override the human-reviewed source fixture.");
            string source = Path.Combine(repo, "samples", "SandboxCoding");
            if (!Path.IsPathFullyQualified(source))
                throw new InvalidOperationException("Agent source snapshot must be absolute.");
            // Verify snapshot (including symlink/junction/credential restrictions)
            // before any model call or file staging.
            DevelopmentSandboxRunner.InspectSnapshot(source, contract.ProjectFile);

            string? key = live ? RuntimeSettings.GetApiKey() : null;
            if (live && string.IsNullOrWhiteSpace(key))
            {
                Console.Error.WriteLine("Agent live mode requires a configured API key; no job started.");
                return 2;
            }

            string runRoot = Path.Combine(resultsDirectory,
                "agent-job-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runRoot);

            using Microsoft.Extensions.AI.IChatClient? client =
                live ? DevelopmentAiGenerator.CreateClient(key!) : null;
            IDevelopmentCandidateGenerator generator = live
                ? new DevelopmentAiGenerator(client!, requestedModel ?? "gpt-5.4-mini")
                : new DevelopmentScriptedGenerator();
            IDevelopmentIsolatedRunner sandbox =
                new DevelopmentIsolatedRunner(repo, source, runRoot);

            HostApplicationBuilder builder = Host.CreateApplicationBuilder();
            builder.Services.AddAkka("hami-developer", _ => { });
            using IHost host = builder.Build();
            await host.StartAsync();
            try
            {
                ActorSystem system = host.Services.GetRequiredService<ActorSystem>();
                Console.WriteLine("HaMi Developer: actor-coordinated job started; source writes only in temporary snapshots.");
                Console.WriteLine($"Contract: {contract.TaskId}; attempts <= {contract.MaxAttempts}; model: {(live ? "live" : "offline-scripted")}");
                DevelopmentJobReport result = await DevelopmentActorCoordinator.RunAsync(
                    system, contract, generator, sandbox);

                string jsonPath = Path.Combine(runRoot, "agent-report.json");
                await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(result,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
                Console.WriteLine($"Agent status: {result.Status}; model calls: {result.ModelCalls}; attempts: {result.Attempts.Length}");
                foreach (DevelopmentJobAttempt attempt in result.Attempts)
                    Console.WriteLine($"  #{attempt.Number}: {attempt.Status}, build={attempt.BuildExitCode}, run={attempt.RunExitCode}, cleanup={attempt.CleanupSucceeded}");
                Console.WriteLine("Agent report: " + jsonPath);
                return result.Status == "passed" ? 0 : 1;
            }
            finally
            {
                await host.StopAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException
            or InvalidOperationException or UnauthorizedAccessException or JsonException
            or TimeoutException)
        {
            // Do not log raw untrusted model/sandbox text in infrastructure errors.
            Console.Error.WriteLine($"Agent job failed ({ex.GetType().Name}). Check the bounded job report.");
            return 1;
        }
    }

    internal static async Task SelfTestAsync(string repo)
    {
        CommandLineRouter.SelfTest();
        DevelopmentAgentContract.SelfTest();
        DevelopmentAgentContract contract = DevelopmentAgentContract.Load(repo, null);
        await DevelopmentAiGenerator.SelfTestAsync(contract);
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

            Console.WriteLine("Akka development offline self-test passed: fail/correct, "
                + "retry budget, timeout, preflight, restore, build-exec and cleanup fail-closed.");
        }
        finally
        {
            await host.StopAsync();
        }
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
