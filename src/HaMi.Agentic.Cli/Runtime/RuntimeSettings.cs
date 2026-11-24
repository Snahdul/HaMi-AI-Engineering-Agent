namespace HaMi.Agentic.Cli.Runtime;

/// <summary>
/// Resolves operational settings without writing secrets or changing system environment.
/// Deliberately does not persist or print the API key.
/// </summary>
internal static class RuntimeSettings
{
    private const string PrimaryApiKey = "OPENAI_API_KEY";
    private const string WindowsKeyAlias = "HaMi-AI-Engineering-Agent-Key";
    private const string WorktreeRootVariable = "HAMI_AGENT_WORKTREE_ROOT";

    public static string? GetApiKey() => FindApiKey().Value;

    public static string GetWorktreeRoot(string repository)
    {
        string? configured = FindVariable(WorktreeRootVariable).Value;
        string candidate = string.IsNullOrWhiteSpace(configured) ? Path.GetTempPath() : configured;

        if (!Path.IsPathFullyQualified(candidate))
            throw new InvalidOperationException(
                $"{WorktreeRootVariable} must be an absolute path. No worktree was created.");

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        string repo = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository));
        string relative = Path.GetRelativePath(repo, root);

        // Never place generated worktrees inside the production repository.
        // Separate siblings are allowed (e.g. I:\git\HaMi\_agent-worktrees).
        if (relative == "." || (!Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"{WorktreeRootVariable} must be outside the production repository.");

        return root;
    }

    public static int RunConfigurationCommand(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("Usage: config --check [--repo <local-checkout>] (offline; no key value printed)");
            Console.WriteLine("       config --check-github-issues-token (presence only, no secret output)");
            Console.WriteLine("       config --self-test (offline; no API or Git access)");
            Console.WriteLine("Windows User settings: HaMi-AI-Engineering-Agent-Key and HAMI_AGENT_WORKTREE_ROOT.");
            return 0;
        }

        if (args.SequenceEqual(["--check-github-issues-token"]))
        {
            try
            {
                Console.WriteLine("Dedicated GitHub Issues credential: "
                    + (WindowsIssueCredentialStore.Exists() ? "available" : "missing"));
                return WindowsIssueCredentialStore.Exists() ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Credential inspection failed: " + ex.GetType().Name);
                return 2;
            }
        }

        if (args.SequenceEqual(["--self-test"]))
            return SelfTest();

        if (!args.Contains("--check", StringComparer.Ordinal))
        {
            Console.Error.WriteLine("Use config --check, config --check-github-issues-token or config --self-test.");
            return 2;
        }

        try
        {
            string repository = Path.GetFullPath(Option(args, "--repo") ?? Directory.GetCurrentDirectory());
            string root = GetWorktreeRoot(repository);
            var key = FindApiKey();
            Console.WriteLine($"Worktree root: {root}");
            Console.WriteLine($"Root exists: {Directory.Exists(root)}");
            Console.WriteLine($"Worktree root source: {FindVariable(WorktreeRootVariable).Source}");
            Console.WriteLine($"API key: {(key.Value is null ? "missing" : "available")} ({key.Source})");
            Console.WriteLine("This check makes no network requests and prints no credential values.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Configuration check failed: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static (string? Value, string Source) FindApiKey()
    {
        string? primary = Environment.GetEnvironmentVariable(PrimaryApiKey);
        if (!string.IsNullOrWhiteSpace(primary))
            return (primary, "OPENAI_API_KEY (process)");

        return FindVariable(WindowsKeyAlias);
    }

    private static (string? Value, string Source) FindVariable(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(value))
            return (value, $"{variable} (process)");

        if (OperatingSystem.IsWindows())
        {
            value = Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(value))
                return (value, $"{variable} (Windows User)");

            value = Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrWhiteSpace(value))
                return (value, $"{variable} (Windows Machine)");
        }

        return (null, "not configured");
    }

    private static string? Option(string[] args, string option)
    {
        int index = Array.IndexOf(args, option);
        if (index < 0) return null;
        if (index == args.Length - 1 || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Missing value for {option}.");
        return args[index + 1];
    }

    private static int SelfTest()
    {
        // Process-level fixtures are cross-platform and never access the API.
        // Windows User/Machine registry lookup is covered by manual host verification.
        var keys = new[] { PrimaryApiKey, WindowsKeyAlias, WorktreeRootVariable };
        var previous = keys.Select(Environment.GetEnvironmentVariable).ToArray();

        try
        {
            Environment.SetEnvironmentVariable(PrimaryApiKey, null);
            Environment.SetEnvironmentVariable(WindowsKeyAlias, "test-alias-token");
            if (GetApiKey() != "test-alias-token")
                throw new InvalidOperationException("Named API key alias was not resolved.");

            Environment.SetEnvironmentVariable(PrimaryApiKey, "test-primary-token");
            if (GetApiKey() != "test-primary-token")
                throw new InvalidOperationException("OPENAI_API_KEY must take precedence.");
            Environment.SetEnvironmentVariable(PrimaryApiKey, null);

            string fakeRepo = Path.Combine(Path.GetTempPath(), "hami-settings-test", "repo");
            string siblingRoot = Path.Combine(Path.GetTempPath(), "hami-settings-test", "worktrees");
            Environment.SetEnvironmentVariable(WorktreeRootVariable, siblingRoot);
            if (GetWorktreeRoot(fakeRepo) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(siblingRoot)))
                throw new InvalidOperationException("Worktree root resolution failed.");

            Environment.SetEnvironmentVariable(WorktreeRootVariable, "relative/worktrees");
            if (!RootIsRejected(fakeRepo))
                throw new InvalidOperationException("Relative worktree root was accepted.");

            Environment.SetEnvironmentVariable(WorktreeRootVariable, Path.Combine(fakeRepo, "nested"));
            if (!RootIsRejected(fakeRepo))
                throw new InvalidOperationException("Worktree root inside repository was accepted.");

            Environment.SetEnvironmentVariable(WorktreeRootVariable, fakeRepo);
            if (!RootIsRejected(fakeRepo))
                throw new InvalidOperationException("Repository root was accepted as worktree root.");

            Console.WriteLine("Runtime settings self-test passed: key alias/priority, absolute ReFS-capable root and repository separation.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Runtime settings self-test failed: {ex.GetType().Name}");
            return 1;
        }
        finally
        {
            for (int i = 0; i < keys.Length; i++)
                Environment.SetEnvironmentVariable(keys[i], previous[i]);
        }
    }

    private static bool RootIsRejected(string repo)
    {
        try
        {
            _ = GetWorktreeRoot(repo);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
