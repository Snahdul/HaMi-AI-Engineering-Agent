using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>
/// Only adapter allowed to hand candidate code to the already approved sbx
/// runner. Builds/tests NEVER occur directly on the Windows host.
/// </summary>
internal sealed class DevelopmentIsolatedRunner(
    string repository, string trustedFixture, string jobResultsRoot,
    ILogger? logger = null, string? jobId = null, bool logSuccessfulSteps = true)
    : IDevelopmentIsolatedRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string repo = Path.GetFullPath(repository);
    private readonly string fixture = Path.GetFullPath(trustedFixture);
    private readonly string root = Path.GetFullPath(jobResultsRoot);

    public async Task<DevelopmentSandboxAttempt> ExecuteAsync(
        DevelopmentAgentContract contract, string generatedSource, int attempt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (generatedSource.Length > 16 * 1024 || string.IsNullOrWhiteSpace(generatedSource))
            throw new InvalidOperationException("Generated source is empty or exceeds the reviewed cap.");

        ValidateStoragePaths(repo, fixture, root);

        string[] files = DevelopmentSandboxRunner.InspectSnapshot(fixture, contract.ProjectFile);
        if (files.Length != 2
            || !files.Contains(contract.ProjectFile, StringComparer.Ordinal)
            || !files.Contains(contract.SourceFile, StringComparer.Ordinal))
            throw new InvalidOperationException("Only the two reviewed console fixture files may enter the sandbox.");

        string dir = Path.Combine(root, "candidates", attempt.ToString("D2"));
        string sandboxDir = Path.Combine(root, "sandbox", attempt.ToString("D2"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(sandboxDir);
        foreach (string relative in files)
            File.Copy(Path.Combine(fixture, relative), Path.Combine(dir, relative), overwrite: false);
        // Only Program.cs is mutable; the trusted project and other paths cannot
        // be influenced by the model. The original fixture is not written.
        await File.WriteAllTextAsync(Path.Combine(dir, contract.SourceFile),
            generatedSource, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        // The sandbox runner owns the full MicroVM lifecycle and its bounded
        // finally/cleanup. A coordinator deadline may prevent further retries,
        // but must not interrupt reading the completed, auditable result.
        // The runner emits only host-defined phase codes, generated sandbox IDs,
        // elapsed times and exit codes. No raw compiler, model or sbx output
        // enters ILogger. A supplied observer also suppresses the legacy
        // direct-console progress/diagnostic preview for this agent invocation.
        Action<string, string, long, int>? progress = logger is null ? null
            : (phaseCode, sandboxName, elapsedMs, exitCode) =>
            {
                if (logSuccessfulSteps)
                    DevelopmentLogEvents.SandboxPhase(logger, jobId ?? "uncorrelated",
                        attempt, DescribePhase(phaseCode, exitCode),
                        sandboxName, phaseCode, elapsedMs, exitCode);
            };
        int cliExit = await DevelopmentSandboxRunner.RunAsync(
            repo, dir, contract.ProjectFile, contract.Mode, sandboxDir, progress);

        string reports = Path.Combine(sandboxDir, "reports");
        if (!Directory.Exists(reports))
            throw new InvalidOperationException("Sandbox did not create an auditable report.");
        string[] dirs = Directory.GetDirectories(reports);
        if (dirs.Length != 1)
            throw new InvalidOperationException("Expected exactly one sandbox report per attempt.");
        string reportPath = Path.Combine(dirs[0], "report.json");
        var report = JsonSerializer.Deserialize<DevelopmentSandboxRunner.SandboxRunReport>(
            await File.ReadAllTextAsync(reportPath, CancellationToken.None), JsonOptions)
            ?? throw new InvalidOperationException("Sandbox report could not be parsed.");

        bool passed = cliExit == 0 && report.Error is null
            && report.RestoreExitCode == 0 && report.BuildExitCode == 0
            && report.ExecutionExitCode == 0
            && !report.TimedOut && report.SandboxRemovalSucceeded;

        // Restore/preflight failures are infrastructure failures, NOT
        // compiler failures. Return their status explicitly to the actor.
        string log = report.RestoreExitCode != 0 ? "restore.log"
            : report.BuildExitCode != 0 ? "build.log" : "execution.log";
        string file = Path.Combine(dirs[0], log);
        string feedback = File.Exists(file)
            ? await File.ReadAllTextAsync(file, CancellationToken.None) : "No sandbox diagnostics recorded.";
        string boundedFeedback = DevelopmentSandboxRunner.SafeLogPreview(feedback);
        if (boundedFeedback.Length > 1200) boundedFeedback = boundedFeedback[..1200];

        return new DevelopmentSandboxAttempt(
            passed, report.RestoreExitCode, report.BuildExitCode, report.ExecutionExitCode,
            report.TimedOut, report.SandboxRemovalSucceeded,
            boundedFeedback, reportPath, report.Error);
    }

    // Phase descriptions are trusted host-owned vocabulary, not guest text.
    private static string DescribePhase(string code, int exitCode) => code switch
    {
        "MICROVM_READY" => "MicroVM erstellt",
        "ISOLATION_VERIFIED" => "Isolation, Credentials und Netzwerksperre bestaetigt",
        "DOTNET_SDK_READY" => ".NET 11 im Gast einsatzbereit",
        "SNAPSHOT_TRANSFERRED" => "Freigegebenen Snapshot uebertragen",
        "RESTORE_COMPLETED" => exitCode == 0 ? "Offline-Restore erfolgreich" : "Offline-Restore fehlgeschlagen",
        "BUILD_COMPLETED" => exitCode == 0 ? "Release-Build erfolgreich" : "Release-Build fehlgeschlagen",
        "EXECUTION_COMPLETED" => exitCode == 0 ? "Ausfuehrung erfolgreich" : "Ausfuehrung fehlgeschlagen",
        "OPERATION_TIMEOUT" => "Zeitlimit erreicht, Bereinigung wird versucht",
        "OPERATION_FAULT" => "Sandbox-Operation fehlgeschlagen, Bereinigung wird versucht",
        "CLEANUP_CONFIRMED" => "MicroVM laut Runner entfernt",
        "CLEANUP_UNCONFIRMED" => "ACHTUNG: MicroVM-Entfernung nicht bestaetigt",
        _ => "Unbekannter Sandbox-Zustand"
    };

    internal static void ValidateStoragePaths(
        string repository, string trustedFixture, string resultsDirectory)
    {
        if (!Path.IsPathFullyQualified(resultsDirectory))
            throw new InvalidOperationException("Agent results directory must be absolute.");

        string repoPath = Path.GetFullPath(repository);
        string fixturePath = Path.GetFullPath(trustedFixture);
        string resultsPath = Path.GetFullPath(resultsDirectory);

        if (IsInside(repoPath, resultsPath) || IsInside(fixturePath, resultsPath)
            || IsInside(resultsPath, repoPath) || IsInside(resultsPath, fixturePath))
            throw new InvalidOperationException(
                "Agent results must be separate from the repository and its fixture.");

        // Prevent a results path outside the checkout from following an
        // existing junction/symlink back into the protected checkout.
        for (DirectoryInfo? directory = new(resultsPath);
             directory is not null; directory = directory.Parent)
        {
            if (directory.Exists &&
                (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "Agent results path may not traverse directory reparse points.");
        }
    }

    private static bool IsInside(string parent, string child)
    {
        string relative = Path.GetRelativePath(parent, child);
        return relative == "." || (relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative));
    }
}

/// <summary>
/// Deterministic, zero-cost model fake: first emits a compiler error; the
/// second response uses the untrusted compiler feedback to repair it.
/// </summary>
internal sealed class DevelopmentScriptedGenerator : IDevelopmentCandidateGenerator
{
    public Task<string> GenerateAsync(DevelopmentAgentContract contract,
        string feedback, int attempt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (attempt == 1 && string.IsNullOrEmpty(feedback))
            return Task.FromResult("Console.WriteLine(UnknownIdentifier);");
        if (attempt == 2 && feedback.Contains("CS0103", StringComparison.Ordinal))
            return Task.FromResult("Console.WriteLine(\"HaMi isolated correction passed\");");
        throw new InvalidOperationException("Fake generator received unexpected retry feedback.");
    }
}
