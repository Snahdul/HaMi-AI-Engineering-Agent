using System.Text.Json;
using HaMi.Agentic.Cli.Features.Coding;

namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>
/// Host-owned adapter between Akka's candidate attempts and the already
/// approved M3a detached-worktree / offline-MicroVM production verifier.
/// No generated code is built or executed on the host.
/// </summary>
internal sealed class DevelopmentProductionIsolatedRunner(
    string repository, string jobRoot) : IDevelopmentIsolatedRunner
{
    private readonly string repo = Path.GetFullPath(repository);
    private readonly string root = Path.GetFullPath(jobRoot);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task PreflightAsync(string repository, string resultsDirectory)
    {
        string repo = Path.GetFullPath(repository);
        string harness = Path.Combine(repo, "samples", "RetryBudget");
        DevelopmentIsolatedRunner.ValidateStoragePaths(repo, harness, resultsDirectory);
        await DevelopmentIssueWorkflow.VerifyOriginAsync(repo);
        string branch = (await DevelopmentWorktreeGuard.GitAsync(
            repo, "branch", "--show-current")).Trim();
        string head = (await DevelopmentWorktreeGuard.GitAsync(
            repo, "rev-parse", "HEAD")).Trim();
        string main = (await DevelopmentWorktreeGuard.GitAsync(
            repo, "rev-parse", "refs/heads/main")).Trim();
        string changes = await DevelopmentWorktreeGuard.GitAsync(
            repo, "status", "--porcelain=v1", "-z",
            "--untracked-files=all", "--ignore-submodules=none");
        if (branch != "main" || head != main || changes.Length != 0)
            throw new InvalidOperationException(
                "Production generation requires a clean main checkout at its branch tip.");
        DevelopmentContract scope = DevelopmentContract.Parse(
            await DevelopmentWorktreeGuard.GitAsync(repo, "show",
                "refs/heads/main:" + DevelopmentAgentContract.ProductionScopePath));
        if (scope.TaskId != "production-retry-budget"
            || !scope.AllowedWritePaths.SequenceEqual(
                [DevelopmentProductionVerifier.SourcePath], StringComparer.Ordinal))
            throw new InvalidOperationException("Production agent scope changed.");
    }

    public async Task<DevelopmentSandboxAttempt> ExecuteAsync(
        DevelopmentAgentContract contract, string generatedSource, int attempt,
        CancellationToken cancellationToken)
    {
        if (!contract.IsProduction || attempt is < 1 or > 2)
            throw new InvalidOperationException("Unreviewed production execution attempt.");
        contract.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        string expression = DevelopmentProductionExpression.Parse(generatedSource);
        await PreflightAsync(repo, root);
        string baseCommit = (await DevelopmentWorktreeGuard.GitAsync(
            repo, "rev-parse", "HEAD")).Trim();
        string baselinePath = Path.Combine(repo,
            DevelopmentProductionVerifier.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        string baseline = await File.ReadAllTextAsync(baselinePath, cancellationToken);
        string generated = CodingTaskDefinition.RetryBudget.ReplaceExpression(baseline, expression);
        DevelopmentProductionVerifier.ValidateSourceChange(baseline, generated);

        string worktree = Path.Combine(root, "worktrees", attempt.ToString("D2"));
        string evaluations = Path.Combine(root, "evaluations", attempt.ToString("D2"));
        if (Directory.Exists(worktree))
            throw new InvalidOperationException("Production attempt worktree already exists.");

        cancellationToken.ThrowIfCancellationRequested();
        await DevelopmentWorktreeGuard.GitAsync(
            repo, "worktree", "add", "--detach", worktree, baseCommit);
        string target = Path.Combine(worktree,
            DevelopmentProductionVerifier.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        RejectReparsePoints(worktree, DevelopmentProductionVerifier.SourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(target, generated, cancellationToken);

        // M3a is the ONLY authority that verifies the real Git diff and the
        // independent, host-owned tests in a freshly created isolated MicroVM.
        int verificationExit = await DevelopmentProductionVerifier.RunAsync(
            repo, worktree, DevelopmentAgentContract.ProductionScopePath, evaluations);

        string[] directories = Directory.GetDirectories(evaluations, "production-check-*");
        if (directories.Length != 1)
            throw new InvalidOperationException("Expected one production verification report.");
        string reportFile = Path.Combine(directories[0], "production-report.json");
        var evidence = JsonSerializer.Deserialize<DevelopmentProductionVerifier.ProductionReport>(
            await File.ReadAllTextAsync(reportFile, CancellationToken.None), JsonOptions)
            ?? throw new InvalidOperationException("Production verification report is missing.");
        if (evidence.BaseCommit != baseCommit
            || evidence.SourcePath != DevelopmentProductionVerifier.SourcePath
            || !File.Exists(evidence.DiffPath)
            || evidence.SandboxReport is null
            || !File.Exists(evidence.SandboxReport))
            throw new InvalidOperationException("Production evidence failed consistency checks.");

        var microVm = JsonSerializer.Deserialize<DevelopmentSandboxRunner.SandboxRunReport>(
            await File.ReadAllTextAsync(evidence.SandboxReport, CancellationToken.None),
            JsonOptions) ?? throw new InvalidOperationException("MicroVM report is missing.");

        string phaseLog = evidence.BuildExitCode != 0 ? "build.log" : "execution.log";
        string feedbackPath = Path.Combine(Path.GetDirectoryName(evidence.SandboxReport)!, phaseLog);
        string diagnostic = File.Exists(feedbackPath)
            ? DevelopmentSandboxRunner.SafeLogPreview(
                await File.ReadAllTextAsync(feedbackPath, CancellationToken.None))
            : "No bounded guest diagnostic available.";
        string feedback = evidence.BuildExitCode == 0 && evidence.RunExitCode == 0
            && !evidence.OutputAccepted
            ? "The unchanged trusted RetryBudget tests rejected the candidate output."
            : diagnostic;
        if (feedback.Length > 1200) feedback = feedback[..1200];

        bool succeeded = verificationExit == 0 && evidence.Status == "passed"
            && evidence.CheckoutUnchanged && evidence.OutputAccepted
            && microVm.Error is null && !microVm.TimedOut
            && evidence.CleanupSucceeded;
        return new DevelopmentSandboxAttempt(
            succeeded, evidence.RestoreExitCode, evidence.BuildExitCode,
            evidence.RunExitCode, microVm.TimedOut, evidence.CleanupSucceeded,
            feedback, evidence.SandboxReport,
            evidence.CheckoutUnchanged ? microVm.Error : "CANDIDATE_CHANGED",
            evidence.OutputAccepted, worktree, evidence.DiffPath);
    }

    internal static void RejectReparsePoints(string root, string relative)
    {
        string current = Path.GetFullPath(root);
        foreach (string part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Production source path contains a reparse point.");
        }
    }
}
