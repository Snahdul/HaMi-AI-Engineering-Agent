using HaMi.Agentic.Cli.Runtime;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HaMi.Agentic.Cli.Features.Coding;

// MVP 0.3: a deliberately narrow C# coding agent.
// The model supplies ONLY a bounded arithmetic expression for an existing method.
// The fixed repository-owned harness is the only executable test program.
internal static class CodingCommand
{
    private const string ExpectedRepository = "Snahdul/HaMi-AI-Engineering-Agent";
    private const string FixtureDirectory = "samples/ClampKata";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex AllowedCharacters =
        new(@"^[A-Za-z0-9_+\-*/%<>=!&|^~?:() \t]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Identifiers =
        new(@"[A-Za-z_][A-Za-z_0-9]*", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AllowedIdentifiers =
        new(["value", "min", "max", "true", "false"], StringComparer.Ordinal);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("Usage: coding --repo <local-clone> --unattended [--model gpt-5.4-mini] [--ai-extensions]");
            Console.WriteLine("       coding --self-test --repo <local-clone> (offline, compiles and runs trusted tests)");
            Console.WriteLine("       coding --self-test-ai --repo <local-clone> (offline, fake IChatClient, no API)");
            Console.WriteLine("       coding --policy-check --repo <local-clone> (offline, validated permissions and limits)");
            Console.WriteLine("Produces only a checked C# expression in a new detached worktree; no GitHub writes.");
            return 0;
        }

        string? repoOption = Option(args, "--repo");
        if (string.IsNullOrWhiteSpace(repoOption))
        {
            Console.Error.WriteLine("Coding mode requires explicit --repo <local-clone>.");
            return 2;
        }

        string repo = Path.GetFullPath(repoOption);
        if (args.Contains("--policy-check", StringComparer.Ordinal))
        {
            try
            {
                CodingExecutionPolicy policy = CodingExecutionPolicy.Load(repo);
                Console.WriteLine($"Policy: {CodingExecutionPolicy.RelativePath}");
                Console.WriteLine($"Allowed write path: {policy.OnlyWritablePath}");
                Console.WriteLine($"Allowed model tools: {policy.AllowedModelTools.Length}");
                Console.WriteLine($"Shell/GitHub writes: {policy.AllowShellCommands}/{policy.AllowGitHubWrites}");
                Console.WriteLine($"Model calls: {policy.MaxModelCalls}; output tokens: {policy.MaxOutputTokens}");
                Console.WriteLine($"Process/API timeout: {policy.ProcessTimeoutSeconds}/{policy.ApiTimeoutSeconds} seconds");
                Console.WriteLine($"Policy SHA-256: {policy.Sha256}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Coding policy check failed: {ex.GetType().Name}: {ex.Message}");
                return 2;
            }
        }

        if (args.Contains("--self-test", StringComparer.Ordinal))
        {
            try { return await SelfTestAsync(repo); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Coding self-test failed: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        if (args.Contains("--self-test-ai", StringComparer.Ordinal))
        {
            try
            {
                await CodingAiClient.SelfTestAsync(repo);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"AI adapter self-test failed: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        if (!args.Contains("--unattended", StringComparer.Ordinal))
        {
            Console.Error.WriteLine("Coding mode requires explicit --unattended.");
            return 2;
        }

        string? key = RuntimeSettings.GetApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            Console.Error.WriteLine("No API key configured (OPENAI_API_KEY or HaMi-AI-Engineering-Agent-Key); no coding run started.");
            return 2;
        }

        string model = Option(args, "--model")
            ?? Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-5.4-mini";
        bool useAiExtensions = args.Contains("--ai-extensions", StringComparer.Ordinal);
        string? runDirectory = null;
        try
        {
            string origin = (await GitAsync(repo, "remote", "get-url", "origin")).Trim();
            if (!IsAllowedOrigin(origin))
                throw new InvalidOperationException("Coding mode is restricted to the Agent repository.");

            CodingExecutionPolicy policy = CodingExecutionPolicy.Load(repo);
            string targetPath = policy.OnlyWritablePath;
            CodingPromptTemplate template = CodingPromptTemplate.Load(repo);
            string baseline = (await GitAsync(repo, "rev-parse", "refs/heads/main")).Trim();
            runDirectory = Path.Combine(RuntimeSettings.GetWorktreeRoot(repo), "hami-agent-coding",
                $"mvp03-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(runDirectory);
            string worktree = Path.Combine(runDirectory, "worktree");
            await GitAsync(repo, "worktree", "add", "--detach", worktree, baseline);

            string statusBefore = await GitAsync(worktree, "status", "--porcelain", "--untracked-files=all");
            string head = (await GitAsync(worktree, "rev-parse", "HEAD")).Trim();
            string fixtureRoot = Path.Combine(worktree, FixtureDirectory.Replace('/', Path.DirectorySeparatorChar));
            string targetFile = Path.Combine(worktree, targetPath.Replace('/', Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(statusBefore)
                || !string.Equals(head, baseline, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(targetFile)
                || !File.Exists(Path.Combine(fixtureRoot, "Program.cs"))
                || !File.Exists(Path.Combine(fixtureRoot, "ClampKata.csproj")))
                throw new InvalidOperationException("Coding worktree is not clean or fixed fixture is missing.");

            Console.WriteLine($"Isolated coding worktree: {worktree}");
            Console.WriteLine($"Model supplies only a bounded C# expression; fixed .NET build/test commands. Maximum {policy.MaxModelCalls} model calls.");

            using Microsoft.Extensions.AI.IChatClient? chatClient = useAiExtensions
                ? CodingAiClient.CreateOpenAi(key) : null;
            var aiGateway = chatClient is null ? null : new CodingAiClient(chatClient, policy);
            CodingOutcome outcome = await ExecuteAttemptsAsync(fixtureRoot, template, policy,
                prompt => aiGateway is null
                    ? GenerateAsync(prompt, model, key, policy)
                    : GenerateWithAiAsync(aiGateway, prompt, model));

            string gitStatus = await GitAsync(worktree, "status", "--short", "--untracked-files=all");
            await WriteReportAsync(runDirectory, model, baseline, worktree, outcome, gitStatus,
                useAiExtensions ? "microsoft-extensions-ai-responses" : "direct-responses",
                template.Sha256, policy);
            Console.WriteLine($"Coding run: {(outcome.Success ? "generated" : "validation_failed")}");
            Console.WriteLine($"Model calls: {outcome.ModelCalls}; modified file: {targetPath}");
            Console.WriteLine(gitStatus);
            Console.WriteLine($"Local report: {Path.Combine(runDirectory, "report.json")}");
            return outcome.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Coding run failed ({ex.GetType().Name}): {ex.Message}");
            if (runDirectory is not null)
                await File.WriteAllTextAsync(Path.Combine(runDirectory, "error.txt"),
                    $"Error at {DateTimeOffset.UtcNow:O}: {ex.GetType().Name}");
            return 1;
        }
    }

    private static bool IsAllowedOrigin(string origin) =>
        new[]
        {
            "https://github.com/Snahdul/HaMi-AI-Engineering-Agent",
            "https://github.com/Snahdul/HaMi-AI-Engineering-Agent.git",
            "git@github.com:Snahdul/HaMi-AI-Engineering-Agent",
            "git@github.com:Snahdul/HaMi-AI-Engineering-Agent.git",
            "ssh://git@github.com/Snahdul/HaMi-AI-Engineering-Agent",
            "ssh://git@github.com/Snahdul/HaMi-AI-Engineering-Agent.git"
        }.Contains(origin, StringComparer.OrdinalIgnoreCase);

    private static async Task<CodingDraft> GenerateWithAiAsync(
        CodingAiClient gateway, CodingPrompt prompt, string model)
    {
        CodingAiClient.CodingSuggestion result = await gateway.GenerateAsync(prompt, model);
        return new CodingDraft(result.Plan, result.Expression);
    }

    private static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Missing value for {name}");
        return args[index + 1];
    }

    private static List<string> ValidateDraft(CodingDraft draft)
    {
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(draft.Plan) || draft.Plan.Length > 2000)
            errors.Add("Implementation plan is missing or exceeds 2000 characters.");
        if (string.IsNullOrWhiteSpace(draft.Expression)
            || draft.Expression.Length > 350
            || draft.Expression.Contains('\n') || draft.Expression.Contains('\r'))
        {
            errors.Add("C# expression must be a single nonempty line with at most 350 characters.");
            return errors;
        }

        string expression = draft.Expression;
        if (!AllowedCharacters.IsMatch(expression)
            || expression.Contains("//", StringComparison.Ordinal)
            || expression.Contains("/*", StringComparison.Ordinal)
            || expression.Contains("*/", StringComparison.Ordinal)
            || expression.Contains("++", StringComparison.Ordinal)
            || expression.Contains("--", StringComparison.Ordinal)
            || expression.Contains("=>", StringComparison.Ordinal))
            errors.Add("Expression contains forbidden syntax; only bounded arithmetic, comparisons and ternary operators are allowed.");

        foreach (Match identifier in Identifiers.Matches(expression))
            if (!AllowedIdentifiers.Contains(identifier.Value))
                errors.Add($"Expression contains a forbidden identifier: {identifier.Value}");

        return errors;
    }

    private static string RenderSource(string expression) =>
        "// Fixed C# source scaffold. Only the expression after => is model-generated.\n"
        + "public static class ClampKata\n{\n"
        + "    public static int Clamp(int value, int min, int max) => " + expression.Trim() + ";\n"
        + "}\n";

    private static async Task<CodingOutcome> ExecuteAttemptsAsync(string fixtureRoot,
        CodingPromptTemplate promptTemplate, CodingExecutionPolicy policy,
        Func<CodingPrompt, Task<CodingDraft>> generate)
    {
        string source = Path.Combine(fixtureRoot, "Clamp.cs");
        string original = await File.ReadAllTextAsync(source);
        var attempts = new List<CodingAttempt>();
        string feedback = "";
        bool completed = false;
        int calls = 0;

        try
        {
            for (int attempt = 1; attempt <= policy.MaxModelCalls; attempt++)
            {
                CodingPrompt prompt = promptTemplate.Render(feedback);
                CodingDraft draft = await generate(prompt);
                calls++;
                List<string> errors = ValidateDraft(draft);
                if (errors.Count > 0)
                {
                    feedback = string.Join("\n", errors);
                    attempts.Add(new CodingAttempt(attempt, false, false, false, "expression_rejected"));
                    Console.WriteLine($"Attempt {attempt}: unsafe or invalid C# expression rejected.");
                    continue;
                }

                await File.WriteAllTextAsync(source, RenderSource(draft.Expression), new UTF8Encoding(false));
                ProcessResult build = await RunDotnetAsync(fixtureRoot, policy,
                    "build", "ClampKata.csproj", "--configuration", "Release", "--nologo",
                    "-p:NuGetAudit=false");

                if (build.ExitCode != 0)
                {
                    feedback = "Compiler failed:\n" + build.Output;
                    attempts.Add(new CodingAttempt(attempt, true, false, false, "build_failed"));
                    Console.WriteLine($"Attempt {attempt}: compiler rejected generated C#.");
                    continue;
                }

                ProcessResult tests = await RunDotnetAsync(fixtureRoot, policy,
                    "run", "--project", "ClampKata.csproj", "--configuration", "Release",
                    "--no-build", "--no-restore");
                if (tests.ExitCode != 0)
                {
                    feedback = "Fixed tests failed:\n" + tests.Output;
                    attempts.Add(new CodingAttempt(attempt, true, true, false, "tests_failed"));
                    Console.WriteLine($"Attempt {attempt}: tests failed; requesting correction.");
                    continue;
                }

                attempts.Add(new CodingAttempt(attempt, true, true, true, "passed"));
                Console.WriteLine($"Attempt {attempt}: build and fixed tests passed.");
                Console.WriteLine(tests.Output);
                completed = true;
                return new CodingOutcome(true, calls, attempts);
            }
            return new CodingOutcome(false, calls, attempts);
        }
        finally
        {
            // Restore the tracked fixture when every attempt fails or an exception occurs.
            // Build artifacts are excluded by .gitignore; the detached worktree stays clean.
            if (!completed)
                await File.WriteAllTextAsync(source, original, new UTF8Encoding(false));
        }
    }

    private static async Task<ProcessResult> RunDotnetAsync(string directory,
        CodingExecutionPolicy policy, params string[] args)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("Could not launch fixed dotnet command.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(policy.ProcessTimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            return new ProcessResult(124, "Fixed dotnet command timed out.");
        }
        string text = await stdout + "\n" + await stderr;
        // No arbitrary model-provided command line or arguments are ever used here.
        return new ProcessResult(process.ExitCode, text.Length <= 1600 ? text : text[^1600..]);
    }

    private static async Task<string> GitAsync(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("Git could not be launched.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string result = await stdout;
        string error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Git command failed (exit {process.ExitCode}): {error.Trim()}");
        return result;
    }

    private static async Task<CodingDraft> GenerateAsync(CodingPrompt prompt, string model,
        string apiKey, CodingExecutionPolicy policy)
    {
        var schema = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                plan = new { type = "string" },
                expression = new { type = "string" }
            },
            required = new[] { "plan", "expression" }
        };
        var request = new
        {
            model,
            store = policy.StoreModelResponses,
            max_output_tokens = policy.MaxOutputTokens,
            instructions = prompt.System,
            input = prompt.User,
            text = new { format = new { type = "json_schema", name = "hami_clamp_coding", strict = true, schema } }
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(policy.ApiTimeoutSeconds) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var payload = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("https://api.openai.com/v1/responses", payload);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Model API returned HTTP {(int)response.StatusCode}; body is not logged.");

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;
        if (root.GetProperty("status").GetString() != "completed")
            throw new InvalidOperationException("Model response not completed.");
        string? output = null;
        foreach (JsonElement item in root.GetProperty("output").EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "message") continue;
            foreach (JsonElement part in item.GetProperty("content").EnumerateArray())
                if (part.GetProperty("type").GetString() == "output_text")
                    output = part.GetProperty("text").GetString();
        }
        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("Model returned no structured output.");
        return JsonSerializer.Deserialize<CodingDraft>(output, JsonOptions)
            ?? throw new InvalidOperationException("Invalid structured model response.");
    }

    private static Task WriteReportAsync(string dir, string model, string baseline,
        string worktree, CodingOutcome outcome, string gitStatus, string transport,
        string promptSha256, CodingExecutionPolicy policy)
    {
        var report = new
        {
            task = "clamp-kata",
            status = outcome.Success ? "generated" : "validation_failed",
            model,
            transport,
            promptAsset = CodingPromptTemplate.RelativePath,
            promptSha256,
            policyAsset = CodingExecutionPolicy.RelativePath,
            policySha256 = policy.Sha256,
            modelCalls = outcome.ModelCalls,
            approvalMode = "unattended-isolated",
            baseline,
            worktree,
            modifiedPath = outcome.Success ? policy.OnlyWritablePath : null,
            gitStatus,
            timestampUtc = DateTimeOffset.UtcNow,
            attempts = outcome.Attempts
        };
        return File.WriteAllTextAsync(Path.Combine(dir, "report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<int> SelfTestAsync(string repo)
    {
        foreach (string forbidden in new[]
        {
            "Math.Clamp(value, min, max)",
            "System.IO.File.Delete(value)",
            "value; Console.WriteLine(0)",
            "value /* injected comment */",
            "value\n+1",
            "value++",
            "value => value"
        })
        {
            if (ValidateDraft(new CodingDraft("Plan", forbidden)).Count == 0)
                throw new InvalidOperationException($"Unsafe expression accepted: {forbidden}");
        }

        if (!IsAllowedOrigin("https://github.com/Snahdul/HaMi-AI-Engineering-Agent.git")
            || !IsAllowedOrigin("git@github.com:Snahdul/HaMi-AI-Engineering-Agent.git")
            || IsAllowedOrigin("https://evilgithub.com/Snahdul/HaMi-AI-Engineering-Agent.git")
            || IsAllowedOrigin("https://github.com/Attacker/HaMi-AI-Engineering-Agent.git"))
            throw new InvalidOperationException("Remote origin verification allowed an unexpected repository.");

        CodingExecutionPolicy.SelfTest(repo);
        CodingExecutionPolicy policy = CodingExecutionPolicy.Load(repo);
        CodingPromptTemplate.SelfTest(repo);
        CodingPromptTemplate template = CodingPromptTemplate.Load(repo);

        const string correct = "value < min ? min : value > max ? max : value";
        if (ValidateDraft(new CodingDraft("Plan", correct)).Count != 0)
            throw new InvalidOperationException("Valid expression was rejected.");

        string sourceDir = Path.Combine(repo, FixtureDirectory.Replace('/', Path.DirectorySeparatorChar));
        string temp = Path.Combine(RuntimeSettings.GetWorktreeRoot(repo), "hami-agent-coding-selftest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            foreach (string filename in new[] { "Clamp.cs", "ClampKata.csproj", "Program.cs" })
                File.Copy(Path.Combine(sourceDir, filename), Path.Combine(temp, filename));

            int calls = 0;
            CodingOutcome result = await ExecuteAttemptsAsync(temp, template, policy, prompt =>
            {
                calls++;
                if (calls == 2 && !prompt.User.Contains("Fixed tests failed:", StringComparison.Ordinal))
                    throw new InvalidOperationException("Feedback from failed tests did not reach correction attempt.");
                return Task.FromResult(new CodingDraft("Implement Clamp", calls == 1 ? "value" : correct));
            });
            if (!result.Success || result.ModelCalls != 2 || result.Attempts.Count != 2
                || result.Attempts[0].Status != "tests_failed"
                || result.Attempts[1].Status != "passed")
                throw new InvalidOperationException("Coding retry / real build+test self-test failed.");

            int compilerCalls = 0;
            CodingOutcome compilerRecovery = await ExecuteAttemptsAsync(temp, template, policy, prompt =>
            {
                compilerCalls++;
                if (compilerCalls == 2 && !prompt.User.Contains("Compiler failed:", StringComparison.Ordinal))
                    throw new InvalidOperationException("Compiler diagnostics did not reach correction attempt.");
                return Task.FromResult(new CodingDraft("Implement Clamp",
                    compilerCalls == 1 ? "value < min ? min :" : correct));
            });
            if (!compilerRecovery.Success || compilerRecovery.ModelCalls != 2
                || compilerRecovery.Attempts[0].Status != "build_failed"
                || compilerRecovery.Attempts[1].Status != "passed")
                throw new InvalidOperationException("Coding compiler failure / recovery self-test failed.");

            // No partial code remains after a completely unsuccessful run.
            string target = Path.Combine(temp, "Clamp.cs");
            string original = "// Clean placeholder after failed run.\n";
            await File.WriteAllTextAsync(target, original);
            CodingOutcome exhausted = await ExecuteAttemptsAsync(temp, template, policy,
                _ => Task.FromResult(new CodingDraft("Plan", "value")));
            if (exhausted.Success || exhausted.ModelCalls != policy.MaxModelCalls
                || await File.ReadAllTextAsync(target) != original)
                throw new InvalidOperationException("Exhausted attempt budget did not restore original source.");

            Console.WriteLine("Coding self-test passed: strict repository origin, expression grammar, "
                + "real compiler/test failure corrections and rollback after exhausted attempts.");
            return 0;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException) { /* Leave temp if dotnet files are locked. */ }
        }
    }

    private sealed record CodingDraft(string Plan, string Expression);
    private sealed record CodingAttempt(int Attempt, bool ValidationPassed,
        bool BuildPassed, bool TestsPassed, string Status);
    private sealed record CodingOutcome(bool Success, int ModelCalls, List<CodingAttempt> Attempts);
    private sealed record ProcessResult(int ExitCode, string Output);
}
