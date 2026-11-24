using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

// Bind the approved business task and the reviewed Git objects, rather
// than using mutable checkout paths as the identity of a job.
internal static class DevelopmentJobIdentityFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<DevelopmentJobIdentity> CreateAsync(
        string repo, string resultsRoot, string jobId, int issueNumber,
        ApprovedDevelopmentIssue issue, DevelopmentAgentContract contract,
        string approvedBase, bool live)
    {
        if (!contract.IsProduction || issue.Number != issueNumber || issueNumber <= 0)
            throw new InvalidOperationException("Recovery requires the approved production issue.");
        contract.Validate();
        await DevelopmentPublicationPolicy.LoadAsync(repo, resultsRoot, true, issueNumber, approvedBase);

        // Bind the exact reviewed contract, scope and publication policy.
        // A clean Git status alone is not proof of raw byte identity.
        string contractHash = await ReviewedFileHashAsync(repo, approvedBase, DevelopmentAgentContract.ProductionPath);
        string scopeHash = await ReviewedFileHashAsync(repo, approvedBase, DevelopmentAgentContract.ProductionScopePath);
        string policyHash = await ReviewedFileHashAsync(repo, approvedBase, DevelopmentPublicationPolicy.PolicyPath);
        var identity = new DevelopmentJobIdentity(jobId, GitHubIssueGateway.Repository,
            issueNumber, approvedBase, TaskHash(issue), contractHash, scopeHash, policyHash,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(resultsRoot)),
            live ? "live" : "offline-scripted", contract.MaxAttempts,
            DevelopmentJobState.PublicationLimit, contract.TaskId, "main", true, false, false);
        _ = DevelopmentJobState.Create(identity);
        return identity;
    }

    internal static string TaskHash(ApprovedDevelopmentIssue issue)
    {
        // ExtractSection uses the host newline convention. Normalize only
        // that convention, not spelling, ordering or business semantics.
        return DevelopmentJobState.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            issue.Number,
            issue.Title,
            Goal = issue.Goal.Replace("\r\n", "\n", StringComparison.Ordinal),
            issue.Criteria
        }, JsonOptions));
    }

    private static async Task<string> ReviewedFileHashAsync(string repo, string approvedBase, string relative)
    {
        DevelopmentProductionIsolatedRunner.RejectReparsePoints(repo, relative);
        string path = Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar));
        byte[] bytes = DevelopmentJobEvidence.ReadBoundedFile(path, 64 * 1024);
        string committed = (await DevelopmentWorktreeGuard.GitAsync(
            repo, "rev-parse", approvedBase + ":" + relative)).Trim();
        string working = (await DevelopmentWorktreeGuard.GitAsync(
            repo, "hash-object", "--no-filters", "--", relative)).Trim();
        if (committed != working || !bytes.SequenceEqual(DevelopmentJobEvidence.ReadBoundedFile(path, 64 * 1024)))
            throw new InvalidOperationException("Recovery reviewed contract bytes changed.");
        return DevelopmentJobState.Hash(bytes);
    }

    internal static void SelfTest()
    {
        var task = new ApprovedDevelopmentIssue(62, "Approved task", "First line\nSecond line", ["Criterion one", "Criterion two"]);
        string hash = TaskHash(task);
        if (TaskHash(task with { Goal = "First line\r\nSecond line" }) != hash
            || TaskHash(task with { Number = 63 }) == hash
            || TaskHash(task with { Title = "Different task" }) == hash
            || TaskHash(task with { Goal = "Different goal" }) == hash
            || TaskHash(task with { Criteria = ["Criterion two", "Criterion one"] }) == hash)
            throw new InvalidOperationException("Recovery task identity regression.");
    }
}
