using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HaMi.Agentic.Cli.Features.Coding;

namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>
/// A bounded, review-only production candidate gate. Takes a pre-existing
/// detached Git worktree, verifies a real tracked production edit, then runs
/// its source ONLY in a fresh offline MicroVM with a host-owned test harness.
/// This path does not call a model, create a PR, or modify either checkout.
/// </summary>
internal static class DevelopmentProductionVerifier
{
    internal const string ContractPath = "contracts/development/retry-budget-refinement.json";
    internal const string SourcePath = "src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs";
    private const string OtherAllowedPath = "src/HaMi.Agentic.Cli/Features/Coding/CodingTaskDefinition.cs";
    private const string HarnessPath = "samples/RetryBudget/Program.cs";
    private const string ProjectPath = "samples/RetryBudget/RetryBudget.csproj";
    private const string ExpectedStdout = "RetryBudget: 268 tests, 0 failures.";
    private const string Signature = "internal static bool HasAnotherAttempt(int attempt, int maxCalls) =>";
    private static readonly Regex Identifiers = new(
        @"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant);
    private static readonly Regex AllowedExpression = new(
        @"^[A-Za-z0-9_!<>=&|()?:+\-\s]+$", RegexOptions.CultureInvariant);

    internal sealed record ProductionReport(
        string Status, string BaseCommit, string SourcePath,
        string SourceSha256, string DiffPath, string? SandboxReport,
        int RestoreExitCode, int BuildExitCode, int RunExitCode,
        bool CleanupSucceeded, bool OutputAccepted, bool CheckoutUnchanged);

    internal static async Task<int> RunAsync(
        string repository, string candidate, string contractPath, string resultsRoot)
    {
        if (contractPath != ContractPath)
            throw new InvalidOperationException("Unreviewed production verification contract.");
        if (!Path.IsPathFullyQualified(candidate) || !Path.IsPathFullyQualified(resultsRoot))
            throw new InvalidOperationException("Candidate and results root must be absolute.");

        string repo = Path.GetFullPath(repository);
        string worktree = Path.GetFullPath(candidate);
        string results = Path.GetFullPath(resultsRoot);
        string harness = Path.Combine(repo, "samples", "RetryBudget");
        DevelopmentIsolatedRunner.ValidateStoragePaths(repo, harness, results);
        DevelopmentIsolatedRunner.ValidateStoragePaths(worktree, harness, results);

        // Do not trust a dirty main checkout as the source of independent tests.
        await EnsureMainCleanAsync(repo);
        string baseSha = (await DevelopmentWorktreeGuard.GitAsync(repo,
            "rev-parse", "--verify", "refs/heads/main")).Trim();

        string[] changes = await DevelopmentWorktreeGuard.CheckAsync(
            repo, worktree, ContractPath);
        if (changes.Length != 1 || changes[0] != SourcePath)
            throw new InvalidOperationException(
                "Production verification requires only a modified RetryBudget.cs.");
        string expectedStatus = " M " + SourcePath + '\0';
        string status = await DevelopmentWorktreeGuard.GitAsync(worktree,
            "status", "--porcelain=v1", "-z",
            "--untracked-files=all", "--ignore-submodules=none");
        if (!string.Equals(status, expectedStatus, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Only one unstaged tracked source modification is authorized.");

        DevelopmentContract scope = DevelopmentContract.Parse(
            await DevelopmentWorktreeGuard.GitAsync(repo, "show",
                "refs/heads/main:" + ContractPath));
        if (scope.TaskId != "retry-budget-refinement"
            || !scope.AllowedWritePaths.SequenceEqual(
                [SourcePath, OtherAllowedPath], StringComparer.Ordinal))
            throw new InvalidOperationException("Production scope changed unexpectedly.");

        string baseline = await File.ReadAllTextAsync(ToPath(repo, SourcePath));
        string source = await File.ReadAllTextAsync(ToPath(worktree, SourcePath));
        ValidateSourceChange(baseline, source);
        string sourceHash = FileHash(ToPath(worktree, SourcePath));

        string diff = await DevelopmentWorktreeGuard.GitAsync(worktree,
            "diff", "--no-ext-diff", "--no-textconv", "--", SourcePath);
        if (string.IsNullOrWhiteSpace(diff) || diff.Length > 64 * 1024)
            throw new InvalidOperationException("Candidate diff is missing or exceeds the limit.");

        string root = Path.Combine(results, "production-check-" + Guid.NewGuid().ToString("N"));
        string snapshot = Path.Combine(root, "snapshot");
        string sandbox = Path.Combine(root, "sandbox");
        Directory.CreateDirectory(snapshot);
        CopyTrusted(repo, snapshot, HarnessPath);
        CopyTrusted(repo, snapshot, ProjectPath);
        string target = ToPath(snapshot, SourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, source);

        string[] manifest = DevelopmentSandboxRunner.InspectSnapshot(snapshot, ProjectPath)
            .Select(s => s.Replace('\\', '/')).ToArray();
        ValidateManifest(manifest);
        if (FileHash(ToPath(snapshot, HarnessPath)) != FileHash(ToPath(repo, HarnessPath))
            || FileHash(ToPath(snapshot, ProjectPath)) != FileHash(ToPath(repo, ProjectPath)))
            throw new InvalidOperationException("Trusted test harness was altered.");

        string diffPath = Path.Combine(root, "candidate.patch");
        await File.WriteAllTextAsync(diffPath, diff);

        // Suppress guest-produced console text; the complete diagnostics
        // stay within the local sandbox report directory.
        int cliExit = await DevelopmentSandboxRunner.RunAsync(repo,
            snapshot, ProjectPath, "run", sandbox, (_, _, _, _) => { });

        string reports = Path.Combine(sandbox, "reports");
        string[] reportDirectories = Directory.Exists(reports)
            ? Directory.GetDirectories(reports) : [];
        if (reportDirectories.Length != 1)
            throw new InvalidOperationException("Expected one auditable MicroVM report.");
        string reportPath = Path.Combine(reportDirectories[0], "report.json");
        var report = JsonSerializer.Deserialize<DevelopmentSandboxRunner.SandboxRunReport>(
            await File.ReadAllTextAsync(reportPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("MicroVM report was missing.");

        bool executionPassed = cliExit == 0 && report.Error is null
            && !report.TimedOut && report.RestoreExitCode == 0
            && report.BuildExitCode == 0 && report.ExecutionExitCode == 0
            && report.SandboxRemovalSucceeded;
        string log = Path.Combine(reportDirectories[0], "execution.log");
        bool outputAccepted = executionPassed && File.Exists(log)
            && DevelopmentOutputAcceptance.Matches(
                await File.ReadAllTextAsync(log), ExpectedStdout);

        // Check again after guest cleanup: the approval applies only to the
        // exact candidate bytes and Git baseline that were executed.
        string afterStatus = await DevelopmentWorktreeGuard.GitAsync(worktree,
            "status", "--porcelain=v1", "-z",
            "--untracked-files=all", "--ignore-submodules=none");
        bool unchanged = string.Equals(afterStatus, expectedStatus, StringComparison.Ordinal)
            && FileHash(ToPath(worktree, SourcePath)) == sourceHash
            && (await DevelopmentWorktreeGuard.GitAsync(worktree, "rev-parse", "HEAD")).Trim() == baseSha
            && (await DevelopmentWorktreeGuard.GitAsync(repo, "rev-parse", "HEAD")).Trim() == baseSha
            && (await DevelopmentWorktreeGuard.GitAsync(repo,
                "status", "--porcelain=v1", "-z",
                "--untracked-files=all", "--ignore-submodules=none")).Length == 0;

        bool passed = outputAccepted && unchanged;
        var evidence = new ProductionReport(
            passed ? "passed" : "failed", baseSha, SourcePath, sourceHash,
            diffPath, reportPath, report.RestoreExitCode, report.BuildExitCode,
            report.ExecutionExitCode, report.SandboxRemovalSucceeded,
            outputAccepted, unchanged);
        string evidencePath = Path.Combine(root, "production-report.json");
        await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(
            evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            }));

        Console.WriteLine("Production candidate: " + (passed ? "passed" : "failed"));
        Console.WriteLine("Local review diff: " + diffPath);
        Console.WriteLine("Local evidence: " + evidencePath);
        return passed ? 0 : 1;
    }

    internal static void ValidateSourceChange(string baseline, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 16 * 1024
            || string.Equals(baseline, candidate, StringComparison.Ordinal))
            throw new InvalidOperationException("Production candidate must contain a bounded real change.");

        int start = candidate.IndexOf(Signature, StringComparison.Ordinal);
        if (start < 0 || candidate.LastIndexOf(Signature, StringComparison.Ordinal) != start)
            throw new InvalidOperationException("Approved RetryBudget method not found uniquely.");
        start += Signature.Length;
        int end = candidate.IndexOf(';', start);
        if (end < 0)
            throw new InvalidOperationException("Approved method expression is missing.");
        string expression = candidate[start..end].Trim();
        if (expression.Length is < 1 or > 350
            || expression.Contains('\n') || expression.Contains('\r')
            || !AllowedExpression.IsMatch(expression)
            || expression.Contains("//", StringComparison.Ordinal)
            || expression.Contains("/*", StringComparison.Ordinal)
            || expression.Contains("*/", StringComparison.Ordinal)
            || Identifiers.Matches(expression)
                .Any(m => !CodingTaskDefinition.RetryBudget.AllowedIdentifiers.Contains(m.Value))
            || !string.Equals(CodingTaskDefinition.RetryBudget.ReplaceExpression(
                baseline, expression), candidate, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Production edit changes more than the approved expression or contains forbidden syntax.");
    }

    internal static void ValidateManifest(string[] manifest)
    {
        var expected = new HashSet<string>(
            [HarnessPath, ProjectPath, SourcePath], StringComparer.Ordinal);
        if (manifest.Length != expected.Count
            || manifest.Any(p => !expected.Remove(p))
            || expected.Count != 0)
            throw new InvalidOperationException("Snapshot contains unreviewed files.");
    }

    private static async Task EnsureMainCleanAsync(string repo)
    {
        string status = await DevelopmentWorktreeGuard.GitAsync(repo,
            "status", "--porcelain=v1", "-z",
            "--untracked-files=all", "--ignore-submodules=none");
        if (status.Length != 0)
            throw new InvalidOperationException("Main checkout must be clean.");
    }

    private static void CopyTrusted(string repo, string snapshot, string relative)
    {
        string target = ToPath(snapshot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(ToPath(repo, relative), target);
    }

    private static string ToPath(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    internal static string FileHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    internal static void SelfTest()
    {
        const string original = """
            namespace HaMi.Agentic.Cli.Features.Coding;

            internal static class RetryBudget
            {
                internal static bool HasAnotherAttempt(int attempt, int maxCalls) =>
                    attempt >= 1 && maxCalls >= 1 && attempt < maxCalls;
            }
            """;
        string changed = CodingTaskDefinition.RetryBudget.ReplaceExpression(
            original, "attempt > 0 && maxCalls > 0 && attempt < maxCalls");
        ValidateSourceChange(original, changed);
        foreach (string invalid in new[]
        {
            original,
            changed.Replace("internal static class", "public static class",
                StringComparison.Ordinal),
            changed.Replace("attempt > 0", "Environment.Exit(0) == 0",
                StringComparison.Ordinal),
            changed.Replace("attempt > 0", "true; System.Console.WriteLine(42)",
                StringComparison.Ordinal),
            changed + "\n// unrelated edit\n",
            changed.Replace("attempt < maxCalls", "unexpectedVariable",
                StringComparison.Ordinal)
        })
        {
            try { ValidateSourceChange(original, invalid); }
            catch (InvalidOperationException) { continue; }
            throw new InvalidOperationException("Unsafe production source change was accepted.");
        }

        ValidateManifest([ProjectPath, SourcePath, HarnessPath]);
        foreach (string[] invalid in new[]
        {
            new[] { HarnessPath, SourcePath },
            new[] { HarnessPath, ProjectPath, SourcePath, "new.cs" },
            new[] { HarnessPath, ProjectPath, HarnessPath }
        })
        {
            try { ValidateManifest(invalid); }
            catch (InvalidOperationException) { continue; }
            throw new InvalidOperationException("Unsafe production snapshot manifest was accepted.");
        }

        if (!DevelopmentOutputAcceptance.Matches(ExpectedStdout + "\r\n", ExpectedStdout)
            || DevelopmentOutputAcceptance.Matches(ExpectedStdout + "\nextra", ExpectedStdout))
            throw new InvalidOperationException("Production stdout acceptance gate failed.");
    }
}
