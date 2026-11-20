using System.Text;
using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

internal static class DevelopmentCommand
{
    internal const string ExampleContract = "contracts/development/retry-budget-refinement.json";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 1 && args[0] == "--help")
        {
            Console.WriteLine("Usage: development --repo <checkout> --contract-check --contract <contracts/development/*.json>");
            Console.WriteLine("       development --repo <main-checkout> --worktree-check --contract <contracts/development/*.json> --worktree <detached-worktree>");
            Console.WriteLine("       development --repo <checkout> --self-test");
            Console.WriteLine("Read-only checks only: no model calls, generated code execution, or Git writes.");
            return 0;
        }

        try
        {
            string? repo = null;
            string? contractPath = null;
            string? worktree = null;
            string? action = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--self-test":
                    case "--contract-check":
                    case "--worktree-check":
                        if (action is not null) throw new ArgumentException("Exactly one development action required.");
                        action = args[i];
                        break;
                    case "--repo":
                    case "--contract":
                    case "--worktree":
                        string option = args[i];
                        if (i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                            throw new ArgumentException("Missing value for " + option);
                        string value = args[++i];
                        switch (option)
                        {
                            case "--repo":
                                if (repo is not null) throw new ArgumentException("Duplicate --repo.");
                                repo = value;
                                break;
                            case "--contract":
                                if (contractPath is not null) throw new ArgumentException("Duplicate --contract.");
                                contractPath = value;
                                break;
                            case "--worktree":
                                if (worktree is not null) throw new ArgumentException("Duplicate --worktree.");
                                worktree = value;
                                break;
                        }
                        break;
                    default:
                        throw new ArgumentException("Unknown development argument: " + args[i]);
                }
            }

            if (repo is null || action is null)
                throw new ArgumentException("Development mode requires --repo and exactly one action.");
            repo = Path.GetFullPath(repo);

            if (action == "--self-test")
            {
                if (contractPath is not null || worktree is not null)
                    throw new ArgumentException("Self-test does not accept a candidate or contract override.");
                await SelfTestAsync(repo);
                Console.WriteLine("Development contract/worktree self-test passed.");
                return 0;
            }

            if (!DevelopmentContract.IsContractPath(contractPath))
                throw new ArgumentException("Expected a safe contracts/development/*.json path.");

            if (action == "--contract-check")
            {
                if (worktree is not null) throw new ArgumentException("Contract check does not accept --worktree.");
                DevelopmentContract contract = DevelopmentContract.Parse(
                    await File.ReadAllTextAsync(Path.Combine(repo,
                        contractPath!.Replace('/', Path.DirectorySeparatorChar))));
                Console.WriteLine($"Valid development task: {contract.TaskId}; allowed paths: {contract.AllowedWritePaths.Length}");
                Console.WriteLine("Validation only; no execution permission granted.");
                return 0;
            }

            if (worktree is null)
                throw new ArgumentException("Worktree check requires --worktree.");
            string[] modified = await DevelopmentWorktreeGuard.CheckAsync(repo, worktree, contractPath!);
            Console.WriteLine($"Worktree scope valid; changed allowlisted C# paths: {modified.Length}");
            foreach (string path in modified)
                Console.WriteLine(path);
            Console.WriteLine("No generated code was compiled, run or integrated.");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Development validation rejected: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task SelfTestAsync(string repo)
    {
        string json = await File.ReadAllTextAsync(Path.Combine(repo,
            ExampleContract.Replace('/', Path.DirectorySeparatorChar)));
        DevelopmentContract contract = DevelopmentContract.Parse(json);
        string compact = JsonSerializer.Serialize(contract, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        string first = contract.AllowedWritePaths[0];
        string second = contract.AllowedWritePaths[1];

        foreach (string invalid in new[]
        {
            compact.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
            compact.Replace(first, "../escape.cs", StringComparison.Ordinal),
            compact[..^1] + ",\"taskId\":\"duplicate\"}",
            compact[..^1] + ",\"allowShellCommands\":true}",
            JsonSerializer.Serialize(contract with { AllowedWritePaths = [first, first] },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            JsonSerializer.Serialize(contract with { AcceptanceCriteria = [] },
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
        })
            ExpectRejected(() => DevelopmentContract.Parse(invalid));

        if (DevelopmentContract.IsContractPath("../contracts/development/escape.json")
            || DevelopmentContract.IsContractPath("contracts/development/../escape.json")
            || DevelopmentContract.IsAllowedSourcePath("src/HaMi.Agentic.Cli/../../secrets.cs"))
            throw new InvalidOperationException("Unsafe contract/path was accepted.");

        string status = " M " + first + '\0' + "?? " + second + '\0';
        if (DevelopmentWorktreeGuard.VerifyChanges(status, contract).Length != 2)
            throw new InvalidOperationException("Allowed multi-file change was rejected.");
        foreach (string invalidStatus in new[]
        {
            " M src/HaMi.Agentic.Cli/Features/Coding/Program.cs\0",
            " D " + first + '\0',
            "R  " + first + '\0' + second + '\0',
            " M " + first,
            " M " + first + '\0' + " M " + first + '\0'
        })
            ExpectRejected(() => DevelopmentWorktreeGuard.VerifyChanges(invalidStatus, contract));

        // End-to-end integration test in a disposable local repository.
        // No model, no CI credentials, no mutations to the actual checkout.
        string root = Path.Combine(Path.GetTempPath(), "hami-development-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string candidate = Path.Combine(root, "candidate");
        Directory.CreateDirectory(source);
        try
        {
            await DevelopmentWorktreeGuard.GitAsync(source, "init", "-b", "main");
            await DevelopmentWorktreeGuard.GitAsync(source, "config", "user.email", "tests@example.invalid");
            await DevelopmentWorktreeGuard.GitAsync(source, "config", "user.name", "HaMi Offline Test");
            foreach (string relative in new[] { first, second, "src/HaMi.Agentic.Cli/Features/Coding/Program.cs" })
                WriteFile(source, relative, "// trusted baseline\n");
            WriteFile(source, ExampleContract, json);
            await DevelopmentWorktreeGuard.GitAsync(source, "add", "-A");
            await DevelopmentWorktreeGuard.GitAsync(source, "commit", "-m", "trusted baseline");
            await DevelopmentWorktreeGuard.GitAsync(source, "worktree", "add", "--detach", candidate, "HEAD");

            if ((await DevelopmentWorktreeGuard.CheckAsync(source, candidate, ExampleContract)).Length != 0)
                throw new InvalidOperationException("Clean candidate returned changes.");

            File.AppendAllText(Path.Combine(candidate, first.Replace('/', Path.DirectorySeparatorChar)),
                "// approved change\n", Encoding.UTF8);
            File.AppendAllText(Path.Combine(candidate, second.Replace('/', Path.DirectorySeparatorChar)),
                "// second approved change\n", Encoding.UTF8);
            if ((await DevelopmentWorktreeGuard.CheckAsync(source, candidate, ExampleContract)).Length != 2)
                throw new InvalidOperationException("Two approved production edits were rejected.");

            string unauthorized = Path.Combine(candidate,
                "src/HaMi.Agentic.Cli/Features/Coding/Program.cs".Replace('/', Path.DirectorySeparatorChar));
            File.AppendAllText(unauthorized, "// unauthorized\n", Encoding.UTF8);
            await ExpectRejectedAsync(() => DevelopmentWorktreeGuard.CheckAsync(source, candidate, ExampleContract));
            File.Delete(unauthorized);
            await ExpectRejectedAsync(() => DevelopmentWorktreeGuard.CheckAsync(source, candidate, ExampleContract));
        }
        finally
        {
            if (Directory.Exists(candidate))
            {
                try { await DevelopmentWorktreeGuard.GitAsync(source, "worktree", "remove", "--force", candidate); }
                catch (InvalidOperationException) { /* Best-effort cleanup of disposable fixture. */ }
            }
            if (Directory.Exists(root))
            {
                // Git for Windows may leave read-only loose Git objects in the
                // disposable .git directory; normalize attributes for cleanup.
                foreach (string file in Directory.EnumerateFiles(root, "*",
                    SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void WriteFile(string root, string relative, string value)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    private static void ExpectRejected(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        catch (JsonException) { return; }
        throw new InvalidOperationException("Invalid development scope was accepted.");
    }

    private static async Task ExpectRejectedAsync(Func<Task<string[]>> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe worktree changes were accepted.");
    }
}
