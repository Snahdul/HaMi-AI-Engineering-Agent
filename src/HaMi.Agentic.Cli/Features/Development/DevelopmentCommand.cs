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
            Console.WriteLine("       development --repo <main-checkout> --production-verify --contract contracts/development/retry-budget-refinement.json --worktree <detached-worktree> --results-dir <absolute-external-dir>");
            Console.WriteLine("       development --repo <checkout> --self-test");
            Console.WriteLine("       development --repo <main-checkout> --sandbox-poc --results-dir <absolute-local-dir>");
            Console.WriteLine("       development --repo <checkout> --sandbox-poc-self-test");
            Console.WriteLine("       development --repo <checkout> --sandbox-run --source <snapshot-dir> --project <relative.csproj> --mode <build|run|test> --results-dir <absolute-dir>");
            Console.WriteLine("       development --repo <checkout> --sandbox-run-self-test");
            Console.WriteLine("       development --repo <checkout> --agent-self-test (offline, Akka + CLI parser)");
            Console.WriteLine("       development --repo <checkout> --agent-run --results-dir <absolute-dir> [--contract contracts/development/agent-console-retry.json] [--live --model <model>] [--issue <number> [--publish-issue [--allow-followups]]]");
            Console.WriteLine("Issue opt-in: --issue <number> [--publish-issue [--allow-followups]]. Requires approved owner-authored Issue and matching contract marker.");
            Console.WriteLine("No GitHub writes without explicit --publish-issue; no issue permissions in sandbox/model. Host token: HAMI_AGENT_GITHUB_TOKEN.");
            Console.WriteLine("Agent run: reviewed console, pair or production expression task; independent ephemeral Docker MicroVM per eligible attempt.");
            Console.WriteLine("Contract checks are read-only. sandbox-poc runs the trusted RetryBudget fixture; sandbox-run executes a bounded project snapshot only in a fresh local MicroVM.");
            return 0;
        }

        try
        {
            string? repo = null;
            string? contractPath = null;
            string? worktree = null;
            string? resultsDir = null;
            string? source = null;
            string? project = null;
            string? mode = null;
            string? model = null;
            bool live = false;
            int? issueNumber = null;
            bool publishIssue = false;
            bool allowFollowUps = false;
            string? action = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--self-test":
                    case "--sandbox-poc-self-test":
                    case "--sandbox-poc":
                    case "--sandbox-run":
                    case "--sandbox-run-self-test":
                    case "--agent-run":
                    case "--agent-self-test":
                    case "--contract-check":
                    case "--worktree-check":
                    case "--production-verify":
                        if (action is not null) throw new ArgumentException("Exactly one development action required.");
                        action = args[i];
                        break;
                    case "--live":
                        if (live) throw new ArgumentException("Duplicate --live.");
                        live = true;
                        break;
                    case "--publish-issue":
                        if (publishIssue) throw new ArgumentException("Duplicate --publish-issue.");
                        publishIssue = true;
                        break;
                    case "--allow-followups":
                        if (allowFollowUps) throw new ArgumentException("Duplicate --allow-followups.");
                        allowFollowUps = true;
                        break;
                    case "--model":
                    case "--repo":
                    case "--results-dir":
                    case "--source":
                    case "--project":
                    case "--mode":
                    case "--contract":
                    case "--worktree":
                    case "--issue":
                        string option = args[i];
                        if (i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                            throw new ArgumentException("Missing value for " + option);
                        string value = args[++i];
                        switch (option)
                        {
                            case "--model":
                                if (model is not null) throw new ArgumentException("Duplicate --model.");
                                model = value;
                                break;
                            case "--source":
                                if (source is not null) throw new ArgumentException("Duplicate --source.");
                                source = value;
                                break;
                            case "--project":
                                if (project is not null) throw new ArgumentException("Duplicate --project.");
                                project = value;
                                break;
                            case "--mode":
                                if (mode is not null) throw new ArgumentException("Duplicate --mode.");
                                mode = value;
                                break;
                            case "--results-dir":
                                if (resultsDir is not null) throw new ArgumentException("Duplicate --results-dir.");
                                resultsDir = value;
                                break;
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
                            case "--issue":
                                if (issueNumber is not null || !int.TryParse(value, out int parsed) || parsed < 1)
                                    throw new ArgumentException("Issue number must be a unique positive integer.");
                                issueNumber = parsed;
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
            if ((issueNumber is not null || publishIssue || allowFollowUps) && action != "--agent-run")
                throw new ArgumentException("GitHub Issue options require --agent-run.");
            if (publishIssue && issueNumber is null || allowFollowUps && !publishIssue)
                throw new ArgumentException("--publish-issue requires --issue; --allow-followups requires --publish-issue.");

            if (action == "--agent-self-test")
            {
                if (source is not null || project is not null || mode is not null
                    || contractPath is not null || worktree is not null
                    || resultsDir is not null || model is not null || live)
                    throw new ArgumentException("Agent self-test accepts only --repo.");
                await DevelopmentAgentCommand.SelfTestAsync(repo);
                return 0;
            }

            if (action == "--agent-run")
            {
                if (resultsDir is null || source is not null || project is not null
                    || mode is not null || worktree is not null
                    || (model is not null && !live))
                    throw new ArgumentException("Agent run requires --results-dir and the reviewed fixture; --source is not authorized; --model only with --live.");
                return await DevelopmentAgentCommand.RunAsync(repo,
                    contractPath, source, resultsDir, live, model,
                    issueNumber, publishIssue, allowFollowUps);
            }

            if (model is not null || live)
                throw new ArgumentException("--model and --live require --agent-run.");

            if (action == "--production-verify")
            {
                if (worktree is null || contractPath is null || resultsDir is null
                    || source is not null || project is not null || mode is not null)
                    throw new ArgumentException(
                        "Production verification needs --contract, --worktree and --results-dir.");
                return await DevelopmentProductionVerifier.RunAsync(
                    repo, worktree, contractPath, resultsDir);
            }

            if (action == "--sandbox-run-self-test")
            {
                if (source is not null || project is not null || mode is not null
                    || contractPath is not null || worktree is not null || resultsDir is not null)
                    throw new ArgumentException("Sandbox runner self-test accepts only --repo.");
                DevelopmentSandboxRunner.SelfTest();
                Console.WriteLine("General C# sandbox runner offline validation passed.");
                return 0;
            }

            if (action == "--sandbox-run")
            {
                if (source is null || project is null || mode is null || resultsDir is null
                    || contractPath is not null || worktree is not null)
                    throw new ArgumentException(
                        "Sandbox runner requires --source, --project, --mode and --results-dir.");
                return await DevelopmentSandboxRunner.RunAsync(repo, source, project, mode, resultsDir);
            }

            if (source is not null || project is not null || mode is not null)
                throw new ArgumentException("--source, --project and --mode require --sandbox-run.");

            if (action == "--sandbox-poc-self-test")
            {
                if (contractPath is not null || worktree is not null || resultsDir is not null)
                    throw new ArgumentException("Sandbox PoC self-test accepts only --repo.");
                DevelopmentSandboxPoC.SelfTest();
                Console.WriteLine("Sandbox PoC offline validation passed.");
                return 0;
            }

            if (action == "--sandbox-poc")
            {
                if (contractPath is not null || worktree is not null || resultsDir is null)
                    throw new ArgumentException("Sandbox PoC requires --results-dir and no candidate or contract.");
                return await DevelopmentSandboxPoC.RunAsync(repo, resultsDir);
            }

            if (resultsDir is not null)
                throw new ArgumentException("--results-dir requires --sandbox-poc or --sandbox-run.");

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
        DevelopmentProductionVerifier.SelfTest();
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
