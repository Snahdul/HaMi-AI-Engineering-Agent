using System.Reflection;
using HaMi.Agentic.Cli.Features.Development;
using HaMi.Agentic.Cli.Runtime;

// Explicit test-executable entry only. It is not a production CLI command.
// The human must approve both the exact Issue #73 order and the exact host
// revision after CI/build and exclusive local backend preflight.
internal static class PairAcceptanceEntry
{
    internal static async Task RequireHostAsync(string hostRepository, string hostCommit)
    {
        OrderCheck.Require(OperatingSystem.IsWindows() && OrderCheck.Commit(hostCommit), "ACCEPTANCE_HOST_COMMIT");
        string repo = Path.GetFullPath(hostRepository);
        for (DirectoryInfo? directory = new(repo); directory is not null; directory = directory.Parent)
            OrderCheck.Require(directory.Exists && directory.LinkTarget is null
                && (directory.Attributes & FileAttributes.ReparsePoint) == 0, "ACCEPTANCE_HOST_LINK");
        await DevelopmentIssueWorkflow.VerifyOriginAsync(repo);
        string root = (await DevelopmentWorktreeGuard.GitAsync(repo, "--no-replace-objects",
            "rev-parse", "--show-toplevel")).Trim();
        string actual = (await DevelopmentWorktreeGuard.GitAsync(repo, "--no-replace-objects",
            "rev-parse", "--verify", "HEAD")).Trim();
        string status = await DevelopmentWorktreeGuard.GitAsync(repo, "--no-optional-locks", "-c",
            "core.fsmonitor=false", "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=none");
        string tracked = await DevelopmentWorktreeGuard.GitAsync(repo, "ls-files", "-v", "-z");
        OrderCheck.Require(string.Equals(Path.GetFullPath(root), repo, StringComparison.OrdinalIgnoreCase)
            && actual == hostCommit && status.Length == 0 && tracked.Length > 0 && tracked.EndsWith('\0')
            && tracked.Split('\0')[..^1].All(item => item.StartsWith("H ", StringComparison.Ordinal)),
            "ACCEPTANCE_HOST_CHECKOUT");
        string output = Path.Combine(repo, "tests", "HaMi.Agentic.JobState.Probe", "bin", "Release", "net11.0");
        for (DirectoryInfo? directory = new(output); directory is not null; directory = directory.Parent)
            OrderCheck.Require(directory.Exists && directory.LinkTarget is null
                && (directory.Attributes & FileAttributes.ReparsePoint) == 0, "ACCEPTANCE_HOST_LINK");
        foreach (Assembly assembly in new[] { typeof(PairAcceptanceEntry).Assembly, typeof(DevelopmentAiGenerator).Assembly })
        {
            OrderCheck.Require(string.Equals(Path.GetDirectoryName(assembly.Location), output,
                    StringComparison.OrdinalIgnoreCase)
                && File.Exists(assembly.Location) && (File.GetAttributes(assembly.Location) & FileAttributes.ReparsePoint) == 0,
                "ACCEPTANCE_HOST_ASSEMBLY");
        }
        // This verifies the checked-out revision and expected Release
        // assembly location, not a cryptographic attestation of the binary.
        // The operator must build the reviewed clean revision before release.
    }

    // Immutable host metadata only, never a second workflow checkpoint.
    // Reentry must not switch to a different host even if another valid
    // current issue decision names that revision.
    internal static void RequireHostBinding(string root, DevelopmentOrderClaim claim, string hostCommit, bool create)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Pair acceptance is restricted to HaMi11.");
        OrderCheck.Require(OrderCheck.Commit(hostCommit), "ACCEPTANCE_HOST_COMMIT");
        string path = Path.Combine(root, "host-binding.json");
        byte[] expected = OrderStorageJson.Encode(new
        {
            Version = 1,
            claim.JobId,
            claim.ArchiveHash,
            claim.Binding.OrderHash,
            HostCommit = hostCommit
        }, DevelopmentJobState.MaxBytes);
        if (create)
        {
            using var stream = DevelopmentJobStateRoot.CreateWindowsPrivateFile(path, FileOptions.WriteThrough);
            stream.Write(expected);
            stream.Flush(flushToDisk: true);
        }
        byte[] actual = DevelopmentJobEvidence.ReadBoundedFile(path, DevelopmentJobState.MaxBytes);
        OrderCheck.Require(actual.AsSpan().SequenceEqual(expected), "ACCEPTANCE_HOST_BINDING");
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        bool first = args[0] == "pair-acceptance-73-start";
        string hostRepository = Path.GetFullPath(args[1]);
        string baselineRepository = Path.GetFullPath(args[2]);
        string hostCommit = args[3];
        await RequireHostAsync(hostRepository, hostCommit);
        string jobId = first ? Guid.NewGuid().ToString("N") : args[4];
        OrderCheck.Require(jobId.Length == 32 && jobId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "ACCEPTANCE_JOB_ID");
        using var gateway = new GitHubIssueGateway();
        var profile = PairAcceptanceProfile.Load();
        // No claim, local journal or guest before current authenticated
        // authority and exact reviewed baseline have been checked.
        _ = await profile.ObserveApprovalAsync(baselineRepository, gateway, null, hostCommit, CancellationToken.None);
        string? apiKey = RuntimeSettings.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Acceptance model credential missing.");
        string workspace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaMiPairAcceptance73-" + jobId);
        DevelopmentIsolatedRunner.ValidateStoragePaths(hostRepository, baselineRepository, workspace);
        var store = DevelopmentJobStateRoot.Open(hostRepository, workspace);
        using var owner = store.AcquireOrder(PairAcceptanceProfile.IssueNumber);
        OrderCheck.Require(first ? owner.CurrentOrder is null : owner.CurrentOrder?.JobId == jobId,
            "ACCEPTANCE_EXISTING_CLAIM");
        store.RequireAcceptanceBackendIdle(PairAcceptanceProfile.IssueNumber);
        var approval = await profile.ObserveApprovalAsync(baselineRepository, gateway, owner.CurrentOrder,
            hostCommit, CancellationToken.None);
        if (first)
            owner.ClaimOrder(jobId, approval);
        string root = DevelopmentJobStateRoot.PreparePairAcceptanceWorkspace(jobId, first);
        OrderCheck.Require(root == workspace, "ACCEPTANCE_WORKSPACE_IDENTITY");
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Acceptance claim missing.");
        RequireHostBinding(root, claim, hostCommit, create: first);
        return await PersistenceBridgeProbe.RunPairAcceptanceAsync(hostRepository, hostCommit, baselineRepository,
            root, store, owner, gateway, apiKey, first, CancellationToken.None);
    }
}
