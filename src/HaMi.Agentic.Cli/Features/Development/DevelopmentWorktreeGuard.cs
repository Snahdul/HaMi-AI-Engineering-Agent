using System.Diagnostics;

namespace HaMi.Agentic.Cli.Features.Development;

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
