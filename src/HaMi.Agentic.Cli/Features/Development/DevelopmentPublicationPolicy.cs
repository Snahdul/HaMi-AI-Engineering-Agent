using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

// Host-only policy. A reviewed version defines the permitted capability;
// the operator must additionally opt in and pin the issue and current base.
internal sealed record DevelopmentPublicationPolicy(
    int SchemaVersion, string Repository, string TaskId, string TargetBranch,
    string SourcePath)
{
    internal const string PolicyPath = "contracts/development/production-publication.json";

    internal static DevelopmentPublicationPolicy Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string[] names = ["schemaVersion", "repository", "taskId", "targetBranch", "sourcePath"];
        if (root.ValueKind != JsonValueKind.Object ||
            !root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(names.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Publication policy fields rejected.");
        var policy = JsonSerializer.Deserialize<DevelopmentPublicationPolicy>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Publication policy missing.");
        if (policy.SchemaVersion != 1 || policy.Repository != GitHubIssueGateway.Repository
            || policy.TaskId != "production-retry-budget" || policy.TargetBranch != "main"
            || policy.SourcePath != DevelopmentProductionVerifier.SourcePath)
            throw new InvalidOperationException("Publication policy scope rejected.");
        return policy;
    }

    internal static bool IsSha(string? value, int length) =>
        value is not null && value.Length == length
        && value.All(c => char.IsAsciiHexDigit(c));

    internal static async Task<DevelopmentPublicationPolicy> LoadAsync(string repo,
        string resultsRoot, bool explicitlyAuthorized, int issueNumber, string approvedBase)
    {
        if (!explicitlyAuthorized || issueNumber <= 0 || !IsSha(approvedBase, 40))
            throw new InvalidOperationException("Explicit publication authorization required.");
        await DevelopmentProductionIsolatedRunner.PreflightAsync(repo, resultsRoot);
        string actual = (await DevelopmentWorktreeGuard.GitAsync(repo, "rev-parse", "HEAD")).Trim();
        if (actual != approvedBase)
            throw new InvalidOperationException("Publication base changed.");
        var policy = Parse(await DevelopmentWorktreeGuard.GitAsync(repo, "show", actual + ":" + PolicyPath));
        // A worktree can be clean even when checkout filters converted LF to
        // CRLF. The Git object API publishes raw verified bytes, not filtered
        // bytes. Reject such baselines rather than silently widen the diff.
        string committedBlob = (await DevelopmentWorktreeGuard.GitAsync(repo,
            "rev-parse", actual + ":" + policy.SourcePath)).Trim();
        string rawBlob = (await DevelopmentWorktreeGuard.GitAsync(repo,
            "hash-object", "--no-filters", "--", policy.SourcePath)).Trim();
        if (committedBlob != rawBlob)
            throw new InvalidOperationException("Publication requires byte-identical baseline checkout; review Git checkout filters.");
        return policy;
    }

    internal static DevelopmentJobAttempt RequireSuccessfulAttempt(DevelopmentJobReport job,
        int issue, string approvedBase)
    {
        if (issue <= 0 || job.IssueNumber != issue || !IsSha(approvedBase, 40)
            || !IsSha(job.JobId, 32) || job.Status != "passed"
            || job.Attempts.Length is < 1 or > 2
            || job.Attempts.Count(a => a.Status == "passed") != 1
            || job.Attempts.Any(a => !a.CleanupSucceeded || a.TimedOut))
            throw new InvalidOperationException("Job is not eligible for publication.");
        var attempt = job.Attempts[^1];
        var evidence = attempt.ProductionEvidence;
        if (attempt.Status != "passed" || attempt.RestoreExitCode != 0
            || attempt.BuildExitCode != 0 || attempt.RunExitCode != 0
            || attempt.OutputAccepted != true || evidence is null
            || evidence.Status != "passed" || evidence.BaseCommit != approvedBase
            || evidence.SourcePath != DevelopmentProductionVerifier.SourcePath
            || evidence.RestoreExitCode != 0 || evidence.BuildExitCode != 0
            || evidence.RunExitCode != 0 || !evidence.CleanupSucceeded
            || !evidence.OutputAccepted || !evidence.CheckoutUnchanged
            || !IsSha(evidence.SourceSha256, 64) || !IsSha(evidence.DiffSha256, 64)
            || attempt.CandidateWorktree is null || attempt.DiffPath is null
            || attempt.DiffPath != evidence.DiffPath || attempt.ReportPath != evidence.SandboxReport)
            throw new InvalidOperationException("Production evidence is not eligible for publication.");
        return attempt;
    }

    // Read-only final gate. Consume only an in-process report from the trusted
    // coordinator, never an arbitrary deserialized job supplied by the user.
    // The publisher must still verify the resulting commit/tree before writing.
    internal static async Task<byte[]> ValidateCandidateAsync(string repo, string resultsRoot,
        DevelopmentJobReport job, int issue, string approvedBase, bool explicitlyAuthorized)
    {
        var policy = await LoadAsync(repo, resultsRoot, explicitlyAuthorized, issue, approvedBase);
        var attempt = RequireSuccessfulAttempt(job, issue, approvedBase);
        var evidence = attempt.ProductionEvidence!;
        string candidate = attempt.CandidateWorktree!;
        string[] changes = await DevelopmentWorktreeGuard.CheckAsync(repo, candidate,
            DevelopmentAgentContract.ProductionScopePath);
        if (!changes.SequenceEqual([policy.SourcePath], StringComparer.Ordinal))
            throw new InvalidOperationException("Publication path scope changed.");
        string status = await DevelopmentWorktreeGuard.GitAsync(candidate, "status",
            "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=none");
        if (status != " M " + policy.SourcePath + '\0')
            throw new InvalidOperationException("Publication candidate status changed.");
        string sourcePath = Path.Combine(candidate, policy.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        byte[] bytes = await File.ReadAllBytesAsync(sourcePath);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        string diff = await DevelopmentWorktreeGuard.GitAsync(candidate, "diff",
            "--no-ext-diff", "--no-textconv", "--", policy.SourcePath);
        string diffHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(diff)));
        if (hash != evidence.SourceSha256 || diffHash != evidence.DiffSha256
            || DevelopmentProductionVerifier.FileHash(attempt.DiffPath!) != evidence.DiffSha256)
            throw new InvalidOperationException("Publication candidate evidence changed.");
        string filteredBlob = (await DevelopmentWorktreeGuard.GitAsync(candidate,
            "hash-object", "--", policy.SourcePath)).Trim();
        string rawBlob = (await DevelopmentWorktreeGuard.GitAsync(candidate,
            "hash-object", "--no-filters", "--", policy.SourcePath)).Trim();
        if (filteredBlob != rawBlob)
            throw new InvalidOperationException("Publication candidate differs under Git clean filters.");
        DevelopmentProductionVerifier.ValidateSourceChange(
            await File.ReadAllTextAsync(Path.Combine(repo, policy.SourcePath.Replace('/', Path.DirectorySeparatorChar))),
            System.Text.Encoding.UTF8.GetString(bytes));
        return bytes;
    }
    internal static void SelfTest()
    {
        string json = """{"schemaVersion":1,"repository":"Snahdul/HaMi-AI-Engineering-Agent","taskId":"production-retry-budget","targetBranch":"main","sourcePath":"src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs"}""";
        _ = Parse(json);
        foreach (string invalid in new[] {
            json.Replace("main", "other", StringComparison.Ordinal),
            json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
            json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
            json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"enabled\":true", StringComparison.Ordinal)
        })
            Reject(() => Parse(invalid));

        string sha = new('a', 40);
        string hash = new('b', 64);
        var evidence = new DevelopmentProductionVerifier.ProductionReport(
            "passed", sha, DevelopmentProductionVerifier.SourcePath, hash,
            "patch", "sandbox", 0, 0, 0, true, true, true, hash);
        var attempt = new DevelopmentJobAttempt(1, "passed", 0, 0, 0, false, true,
            "sandbox", true, "candidate", "patch", evidence);
        var job = new DevelopmentJobReport(new string('c', 32), "passed", 1, [attempt], null, 55);
        _ = RequireSuccessfulAttempt(job, 55, sha);
        Reject(() => RequireSuccessfulAttempt(job, 56, sha));
        Reject(() => RequireSuccessfulAttempt(job, 55, new string('d', 40)));
        Reject(() => RequireSuccessfulAttempt(job with { Status = "failed" }, 55, sha));
        foreach (var invalid in new[] {
            attempt with { CleanupSucceeded = false },
            attempt with { TimedOut = true },
            attempt with { OutputAccepted = false },
            attempt with { ProductionEvidence = null },
            attempt with { ProductionEvidence = evidence with { DiffSha256 = "" } },
            attempt with { ProductionEvidence = evidence with { CheckoutUnchanged = false } }
        })
            Reject(() => RequireSuccessfulAttempt(job with { Attempts = [invalid] }, 55, sha));

        static void Reject(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Unsafe publication evidence or policy accepted.");
        }
    }

}
