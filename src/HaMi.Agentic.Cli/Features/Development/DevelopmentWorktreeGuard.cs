using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace HaMi.Agentic.Cli.Features.Development;

// Exact bytes observed from a trusted clean main checkout and its Git
// objects. This is observation only, not a fresh issue approval.
internal sealed record OrderGitBaseline(string Commit, byte[] Contract, byte[] Scope,
    byte[] Calculator, byte[] Formatter)
{
    internal void RequireBinding(OrderBinding binding) =>
        OrderCheck.Require(Commit == binding.BaseCommit
            && DevelopmentJobState.Hash(Contract) == binding.ContractHash
            && DevelopmentJobState.Hash(Scope) == binding.ScopeHash
            && DevelopmentJobState.Hash(Calculator) == binding.CalculatorBaselineHash
            && DevelopmentJobState.Hash(Formatter) == binding.FormatterBaselineHash,
            "ORDER_GIT_BASELINE_BINDING");
}

/// <summary>
/// Read-only verifier. It never builds, executes, integrates or writes model code.
/// Git status is examined as NUL-delimited data to avoid ambiguous paths.
/// </summary>
internal static class DevelopmentWorktreeGuard
{
    public static async Task<string[]> CheckAsync(
        string repository, string candidate, string contractPath)
    {
        if (!DevelopmentContract.IsContractPath(contractPath))
            throw new InvalidOperationException("Contract path must be a safe contracts/development/*.json path.");

        string repo = Path.GetFullPath(repository);
        string worktree = Path.GetFullPath(candidate);
        string repoRoot = (await GitAsync(repo, "rev-parse", "--show-toplevel")).Trim();
        string worktreeRoot = (await GitAsync(worktree, "rev-parse", "--show-toplevel")).Trim();
        if (!SamePath(repo, repoRoot) || !SamePath(worktree, worktreeRoot)
            || SamePath(repo, worktree) || IsNestedWithin(repo, worktree)
            || IsNestedWithin(worktree, repo))
            throw new InvalidOperationException("Expected two different, non-nested Git worktree roots.");

        if ((await GitAsync(repo, "branch", "--show-current")).Trim() != "main")
            throw new InvalidOperationException("Source checkout must be on main.");

        string baseSha = (await GitAsync(repo, "rev-parse", "--verify", "refs/heads/main")).Trim();
        string candidateSha = (await GitAsync(worktree, "rev-parse", "--verify", "HEAD")).Trim();
        if (!string.Equals(baseSha, candidateSha, StringComparison.Ordinal))
            throw new InvalidOperationException("Candidate is not based on the current trusted main commit.");

        GitResult branch = await GitTryAsync(worktree, "symbolic-ref", "--quiet", "HEAD");
        if (branch.ExitCode != 1)
            throw new InvalidOperationException("Candidate must be a detached worktree.");

        string repoCommonDir = ResolveGitPath(repo, await GitAsync(repo, "rev-parse", "--git-common-dir"));
        string candidateCommonDir = ResolveGitPath(worktree,
            await GitAsync(worktree, "rev-parse", "--git-common-dir"));
        if (!SamePath(repoCommonDir, candidateCommonDir))
            throw new InvalidOperationException("Candidate belongs to a different Git repository.");

        // Contract is read from protected main's Git object, not from the candidate's filesystem.
        string committedJson = await GitAsync(repo, "show", "refs/heads/main:" + contractPath);
        DevelopmentContract contract = DevelopmentContract.Parse(committedJson);

        string status = await GitAsync(worktree, "status", "--porcelain=v1", "-z",
            "--untracked-files=all", "--ignore-submodules=none");
        string[] changedPaths = VerifyChanges(status, contract);
        foreach (string path in changedPaths)
            RejectReparsePoints(worktree, path);
        return changedPaths;
    }

    // Bounded first pair slice. The host must own the repository and Git
    // configuration exclusively; no model code or arbitrary paths are used.
    // Require a clean checkout, including ignored entries, and compare the
    // four relevant regular tracked UTF-8 files to their exact Git objects.
    internal static async Task<OrderGitBaseline> ObserveOrderBaselineAsync(string repository,
        OrderBinding? binding = null)
    {
        string repo = Path.GetFullPath(repository);
        async Task<string> RequireCleanAsync()
        {
            string root = (await GitAsync(repo, "--no-replace-objects", "rev-parse", "--show-toplevel")).Trim();
            string branch = (await GitAsync(repo, "branch", "--show-current")).Trim();
            string commit = (await GitAsync(repo, "--no-replace-objects", "rev-parse", "--verify", "HEAD")).Trim();
            string main = (await GitAsync(repo, "--no-replace-objects", "rev-parse", "--verify", "refs/heads/main")).Trim();
            OrderCheck.Require(SamePath(repo, root) && branch == "main" && OrderCheck.Commit(commit)
                && commit == main && (binding is null || commit == binding.BaseCommit), "ORDER_GIT_BASELINE_COMMIT");
            string status = await GitAsync(repo, "--no-optional-locks", "-c", "core.fsmonitor=false",
                "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=matching", "--ignore-submodules=none");
            string tracked = await GitAsync(repo, "ls-files", "-v", "-z");
            OrderCheck.Require(status.Length == 0 && tracked.Length > 0 && tracked.EndsWith('\0')
                && tracked.Split('\0')[..^1].All(entry => entry.StartsWith("H ", StringComparison.Ordinal)),
                "ORDER_GIT_BASELINE_DIRTY");
            return commit;
        }

        string baseCommit = await RequireCleanAsync();
        async Task<byte[]> ReadTrackedAsync(string relative)
        {
            string entry = await GitAsync(repo, "--no-replace-objects", "ls-tree", "-z", baseCommit, "--", relative);
            string prefix = "100644 blob ";
            string suffix = "\t" + relative + '\0';
            OrderCheck.Require(entry.StartsWith(prefix, StringComparison.Ordinal)
                && entry.EndsWith(suffix, StringComparison.Ordinal)
                && entry.Length == prefix.Length + 40 + suffix.Length, "ORDER_GIT_BASELINE_TREE");
            string objectId = entry.Substring(prefix.Length, 40);
            OrderCheck.Require(OrderCheck.Commit(objectId), "ORDER_GIT_BASELINE_TREE");
            string sizeText = (await GitAsync(repo, "--no-replace-objects", "cat-file", "-s", objectId)).Trim();
            OrderCheck.Require(int.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out int size)
                && size is > 0 and <= OrderCandidateCheckpoint.MaxBytes, "ORDER_GIT_BASELINE_SIZE");
            byte[] checkout = DevelopmentJobEvidence.ReadBoundedFile(
                Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar)),
                OrderCandidateCheckpoint.MaxBytes);
            string objectText = await GitAsync(repo, "--no-replace-objects", "cat-file", "blob", objectId);
            byte[] objectBytes = new UTF8Encoding(false, true).GetBytes(objectText);
            // The object-ID comparison independently excludes lossy decoding,
            // newline conversion and checkout filters in this bounded slice.
            string checkoutObject = (await GitAsync(repo, "hash-object", "--no-filters", "--", relative)).Trim();
            OrderCheck.Require(checkout.Length == size && checkoutObject == objectId
                && checkout.AsSpan().SequenceEqual(objectBytes), "ORDER_GIT_BASELINE_BYTES");
            return checkout;
        }

        var observed = new OrderGitBaseline(baseCommit,
            await ReadTrackedAsync(DevelopmentAgentContract.PairPath),
            await ReadTrackedAsync(DevelopmentAgentContract.PairScopePath),
            await ReadTrackedAsync(DevelopmentAgentContract.PairFirst),
            await ReadTrackedAsync(DevelopmentAgentContract.PairSecond));
        var scope = DevelopmentContract.Parse(new UTF8Encoding(false, true).GetString(observed.Scope));
        OrderCheck.Require(scope.TaskId == "sandbox-pair"
            && scope.AllowedWritePaths.SequenceEqual(
                [DevelopmentAgentContract.PairFirst, DevelopmentAgentContract.PairSecond], StringComparer.Ordinal),
            "ORDER_GIT_BASELINE_SCOPE");
        OrderCheck.Require(await RequireCleanAsync() == baseCommit, "ORDER_GIT_BASELINE_CHANGED");
        if (binding is not null)
            observed.RequireBinding(binding);
        return observed;
    }

    internal static string[] VerifyChanges(string status, DevelopmentContract contract)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (status.Length == 0) return [];
        string[] entries = status.Split('\0');
        if (entries[^1].Length != 0)
            throw new InvalidOperationException("Unterminated Git status entry.");

        foreach (string entry in entries[..^1])
        {
            if (entry.Length < 4 || entry[2] != ' ')
                throw new InvalidOperationException("Malformed Git status.");
            string kind = entry[..2];
            if (kind is not (" M" or "M " or "MM" or "A " or "AM" or "??"))
                throw new InvalidOperationException("Deletion, rename, conflict or type change is not authorized.");

            string path = entry[3..];
            if (!contract.AllowedWritePaths.Contains(path, StringComparer.Ordinal)
                || !paths.Add(path))
                throw new InvalidOperationException("Changed path is outside the exact contract allowlist.");
        }
        return paths.OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }

    private static void RejectReparsePoints(string worktree, string relative)
    {
        string current = worktree;
        string[] parts = relative.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Symlinks and reparse points are not allowed in changed paths.");
            }
            else if (i != parts.Length - 1)
                throw new InvalidOperationException("A changed path has a missing parent directory.");
        }
    }

    private static bool IsNestedWithin(string parent, string child)
    {
        string relative = Path.GetRelativePath(parent, child);
        return relative != "."
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string ResolveGitPath(string workingDirectory, string gitPath) =>
        Path.GetFullPath(gitPath.Trim(), workingDirectory);

    internal static async Task<string> GitAsync(string dir, params string[] arguments)
    {
        GitResult result = await GitTryAsync(dir, arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Fixed Git inspection failed: "
                + result.Error.Trim());
        return result.Output;
    }

    private static async Task<GitResult> GitTryAsync(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment.Remove(DevelopmentPublicationGateway.TokenVariable);
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("Git executable not available.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new InvalidOperationException("Fixed Git inspection timed out.");
        }
        return new GitResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);
}
