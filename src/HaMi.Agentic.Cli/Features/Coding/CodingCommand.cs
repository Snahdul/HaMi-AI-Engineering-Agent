using HaMi.Agentic.Cli.Runtime;
using HaMi.Agentic.Cli.Runtime.Logging;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
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
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("Usage: coding --repo <local-clone> --unattended [--task clamp-kata|retry-budget] [--model gpt-5.4-mini] [--ai-extensions]");
            Console.WriteLine("       coding --self-test --repo <local-clone> (offline, compiles and runs trusted tests)");
            Console.WriteLine("       coding --self-test-ai --repo <local-clone> (offline, fake IChatClient, no API)");
            Console.WriteLine("       coding --policy-check --repo <local-clone> (offline, validated permissions and limits)");
            Console.WriteLine("       coding --self-test-logging --repo <local-clone> (offline, Serilog and decision logging checks)");
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
        string? taskOption;
        try { taskOption = Option(args, "--task"); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        if (!CodingTaskDefinition.TryResolve(taskOption, out CodingTaskDefinition task))
        {
            Console.Error.WriteLine("Unsupported coding task; choose clamp-kata or retry-budget.");
            return 2;
        }

        if (args.Contains("--policy-check", StringComparer.Ordinal))
        {
            try
            {
                CodingExecutionPolicy policy = CodingExecutionPolicy.Load(repo, task);
                Console.WriteLine($"Task: {task.Id}");
                Console.WriteLine($"Policy: {policy.AssetPath}");
                Console.WriteLine($"Allowed write path: {policy.OnlyWritablePath}");
                Console.WriteLine($"Allowed model tools: {policy.AllowedModelTools.Length}");
                Console.WriteLine($"Shell/GitHub writes: {policy.AllowShellCommands}/{policy.AllowGitHubWrites}");
                Console.WriteLine($"Model calls: {policy.MaxModelCalls}; output tokens: {policy.MaxOutputTokens}");
                Console.WriteLine($"Process/API timeout: {policy.ProcessTimeoutSeconds}/{policy.ApiTimeoutSeconds} seconds");
                Console.WriteLine($"Untrusted feedback budget: {policy.MaxFeedbackCharacters} characters");
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

        if (args.Contains("--self-test-logging", StringComparer.Ordinal))
        {
            try
            {
                AgentLoggingSelfTest.Run(repo);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Logging self-test failed: {ex.GetType().Name}: {ex.Message}");
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
        string runId = $"mvp03-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        AgentLoggingSession? loggingSession = null;
        ILogger? auditLog = null;
        IDisposable? correlation = null;
        try
        {
            loggingSession = AgentLoggingSession.Create(repo);
            auditLog = loggingSession.CreateLogger("HaMi.Agentic.Cli.Features.Coding");
            // Keep the scope alive through catch: terminal failures must carry RunId.
            correlation = auditLog.BeginScope(new Dictionary<string, object>
            {
                ["RunId"] = runId
            });
            string origin = (await GitAsync(repo, "remote", "get-url", "origin")).Trim();
            if (!IsAllowedOrigin(origin))
                throw new InvalidOperationException("Coding mode is restricted to the Agent repository.");

            CodingExecutionPolicy policy = CodingExecutionPolicy.Load(repo, task);
            string targetPath = policy.OnlyWritablePath;
            CodingPromptTemplate template = CodingPromptTemplate.Load(repo, task);
            string baseline = (await GitAsync(repo, "rev-parse", "refs/heads/main")).Trim();
            runDirectory = Path.Combine(RuntimeSettings.GetWorktreeRoot(repo), "hami-agent-coding",
                runId);
            Directory.CreateDirectory(runDirectory);
            if (loggingSession.Settings.LogDecisionSummaries)
                CodingLogEvents.DecisionRecorded(auditLog,
                    task.Id == "clamp-kata" ? "CLAMP_SCOPE" : "RETRY_BUDGET_SCOPE",
                    "Trusted task and fixed verifier", "Generate one bounded C# expression",
                    "Versioned policy permits no model tools or other write paths");
            string worktree = Path.Combine(runDirectory, "worktree");
            await GitAsync(repo, "worktree", "add", "--detach", worktree, baseline);

            string statusBefore = await GitAsync(worktree, "status", "--porcelain", "--untracked-files=all");
            string head = (await GitAsync(worktree, "rev-parse", "HEAD")).Trim();
            string fixtureRoot = Path.Combine(worktree,
                task.FixtureDirectory.Replace('/', Path.DirectorySeparatorChar));
            string targetFile = Path.Combine(worktree, targetPath.Replace('/', Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(statusBefore)
                || !string.Equals(head, baseline, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(targetFile)
                || !File.Exists(Path.Combine(fixtureRoot, "Program.cs"))
                || !File.Exists(Path.Combine(fixtureRoot, task.ProjectFile)))
                throw new InvalidOperationException("Coding worktree is not clean or fixed fixture is missing.");

            Console.WriteLine($"Isolated coding worktree: {worktree}");
            Console.WriteLine($"Model supplies only a bounded C# expression; fixed .NET build/test commands. Maximum {policy.MaxModelCalls} model calls.");

            using Microsoft.Extensions.AI.IChatClient? chatClient = useAiExtensions
                ? CodingAiClient.CreateOpenAi(key) : null;
            var aiGateway = chatClient is null ? null : new CodingAiClient(chatClient, policy);
            CodingRunResult outcome = await ExecuteAttemptsAsync(fixtureRoot, targetFile, task,
                template, policy, prompt => aiGateway is null
                    ? GenerateAsync(prompt, model, key, policy)
                    : GenerateWithAiAsync(aiGateway, prompt, model),
                auditLog, loggingSession.Settings);

            string gitStatus = await GitAsync(worktree, "status", "--short", "--untracked-files=all");
            await WriteReportAsync(runDirectory, model, baseline, worktree, outcome, gitStatus,
                useAiExtensions ? "microsoft-extensions-ai-responses" : "direct-responses",
                template, policy, task);
            CodingRunReport outcomeReport = CodingRunReport.From(outcome);
            if (outcomeReport.Success && loggingSession.Settings.LogSuccessfulSteps)
                CodingLogEvents.RunSucceeded(auditLog, outcomeReport.ModelCalls);
            Console.WriteLine($"Coding run: {outcomeReport.Status}");
            Console.WriteLine($"Model calls: {outcomeReport.ModelCalls}; modified file: {targetPath}");
            Console.WriteLine(gitStatus);
            Console.WriteLine($"Local report: {Path.Combine(runDirectory, "report.json")}");
            return outcomeReport.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            if (auditLog is not null)
                CodingLogEvents.RunFailed(auditLog, ex.GetType().Name);
            Console.Error.WriteLine($"Coding run failed ({ex.GetType().Name}): {ex.Message}");
            if (runDirectory is not null)
                await File.WriteAllTextAsync(Path.Combine(runDirectory, "error.txt"),
                    $"Error at {DateTimeOffset.UtcNow:O}: {ex.GetType().Name}");
            return 1;
        }
        finally
        {
            correlation?.Dispose();
            loggingSession?.Dispose();
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

    private static SourceValidationResult ValidateDraft(
        CodingDraft draft, CodingTaskDefinition? task = null)
    {
        task ??= CodingTaskDefinition.Clamp;
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(draft.Plan) || draft.Plan.Length > 2000)
            errors.Add("Implementation plan is missing or exceeds 2000 characters.");
        if (string.IsNullOrWhiteSpace(draft.Expression)
            || draft.Expression.Length > 350
            || draft.Expression.Contains('\n') || draft.Expression.Contains('\r'))
        {
            errors.Add("C# expression must be a single nonempty line with at most 350 characters.");
            return new SourceValidationRejected(errors.ToImmutableArray());
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
            if (!task.AllowedIdentifiers.Contains(identifier.Value))
                errors.Add($"Expression contains a forbidden identifier: {identifier.Value}");

        return errors.Count == 0
            ? new SourceValidationPassed(expression.Trim())
            : new SourceValidationRejected(errors.ToImmutableArray());
    }

    private static async Task<CodingRunResult> ExecuteAttemptsAsync(
        string fixtureRoot, string source, CodingTaskDefinition task,
        CodingPromptTemplate promptTemplate, CodingExecutionPolicy policy,
        Func<CodingPrompt, Task<CodingDraft>> generate, ILogger? logger = null,
        AgentLoggingSettings? loggingSettings = null)
    {
        string original = await File.ReadAllTextAsync(source);
        var attempts = new List<CodingAttemptResult>();
        string feedback = "";
        bool completed = false;
        int calls = 0;

        try
        {
            for (int attempt = 1; attempt <= policy.MaxModelCalls; attempt++)
            {
                CodingPrompt prompt = promptTemplate.Render(feedback, policy.MaxFeedbackCharacters);
                CodingDraft draft = await generate(prompt);
                calls++;
                SourceValidationResult validation = ValidateDraft(draft, task);
                if (validation is SourceValidationRejected rejected)
                {
                    feedback = string.Join("\n", rejected.Diagnostics);
                    attempts.Add(new ExpressionRejectedAttempt(attempt, rejected.Diagnostics));
                    if (logger is not null)
                    {
                        CodingLogEvents.ExpressionRejected(logger, attempt, "invalid_expression");
                        if (loggingSettings?.LogDecisionSummaries == true
                            && RetryBudget.HasAnotherAttempt(attempt, policy.MaxModelCalls))
                            CodingLogEvents.DecisionRecorded(logger, "RETRY_VALIDATION",
                                "Expression rejected by validator", "Retry with bounded feedback",
                                "Remaining model-call budget allows another attempt");
                    }
                    Console.WriteLine($"Attempt {attempt}: unsafe or invalid C# expression rejected.");
                    continue;
                }

                if (validation is not SourceValidationPassed valid)
                    throw new InvalidOperationException("Source validation returned no outcome.");

                // Rebase each model attempt on the original trusted file, not
                // the preceding failed candidate. Preserve all source trivia.
                string updatedSource = task.ReplaceExpression(original, valid.Expression);
                if (!string.Equals(await File.ReadAllTextAsync(source), updatedSource,
                    StringComparison.Ordinal))
                    await File.WriteAllTextAsync(source, updatedSource, new UTF8Encoding(false));
                CompilationResult compilation = await CompileAsync(fixtureRoot, task, policy);
                if (compilation is CompilationFailed compilationFailed)
                {
                    feedback = "Compiler failed:\n" + compilationFailed.Diagnostics;
                    attempts.Add(new CompilationFailedAttempt(attempt, compilationFailed.Diagnostics));
                    if (logger is not null)
                    {
                        CodingLogEvents.CompilationFailed(logger, attempt, "compile_failed");
                        if (loggingSettings?.LogDecisionSummaries == true
                            && RetryBudget.HasAnotherAttempt(attempt, policy.MaxModelCalls))
                            CodingLogEvents.DecisionRecorded(logger, "RETRY_COMPILATION",
                                "Compilation failed", "Retry with bounded compiler feedback",
                                "Remaining model-call budget allows another attempt");
                    }
                    Console.WriteLine($"Attempt {attempt}: compiler rejected generated C#.");
                    continue;
                }

                if (compilation is not CompilationSucceeded)
                    throw new InvalidOperationException("Compilation returned no outcome.");

                TestExecutionResult testExecution = await ExecuteTestsAsync(fixtureRoot, task, policy);
                if (testExecution is TestExecutionFailed testFailed)
                {
                    feedback = "Fixed tests failed:\n" + testFailed.Diagnostics;
                    attempts.Add(new TestsFailedAttempt(attempt, testFailed.Diagnostics));
                    if (logger is not null)
                    {
                        CodingLogEvents.TestsFailed(logger, attempt, "tests_failed");
                        if (loggingSettings?.LogDecisionSummaries == true
                            && RetryBudget.HasAnotherAttempt(attempt, policy.MaxModelCalls))
                            CodingLogEvents.DecisionRecorded(logger, "RETRY_TESTS",
                                "Fixed tests failed", "Retry with bounded test feedback",
                                "Remaining model-call budget allows another attempt");
                    }
                    Console.WriteLine($"Attempt {attempt}: tests failed; requesting correction.");
                    continue;
                }

                if (testExecution is not TestExecutionSucceeded testsPassed)
                    throw new InvalidOperationException("Test execution returned no outcome.");

                attempts.Add(new CodingSucceededAttempt(attempt));
                if (logger is not null && loggingSettings?.LogSuccessfulSteps == true)
                    CodingLogEvents.AttemptSucceeded(logger, attempt);
                Console.WriteLine($"Attempt {attempt}: build and fixed tests passed.");
                Console.WriteLine(testsPassed.Output);
                completed = true;
                return new CodingRunSucceeded(calls, attempts.ToImmutableArray());
            }
            if (logger is not null)
                CodingLogEvents.RunExhausted(logger, calls);
            return new CodingRunExhausted(calls, attempts.ToImmutableArray());
        }
        finally
        {
            // Restore the tracked fixture when every attempt fails or an exception occurs.
            // Build artifacts are excluded by .gitignore; the detached worktree stays clean.
            if (!completed)
                await File.WriteAllTextAsync(source, original, new UTF8Encoding(false));
        }
    }

    private static async Task<CompilationResult> CompileAsync(
        string fixtureRoot, CodingTaskDefinition task, CodingExecutionPolicy policy)
    {
        ProcessResult build = await RunDotnetAsync(fixtureRoot, policy,
            "build", task.ProjectFile, "--configuration", "Release", "--nologo",
            "-p:NuGetAudit=false");
        if (build.ExitCode != 0)
            return new CompilationFailed(build.Output);
        return new CompilationSucceeded();
    }

    private static async Task<TestExecutionResult> ExecuteTestsAsync(
        string fixtureRoot, CodingTaskDefinition task, CodingExecutionPolicy policy)
    {
        ProcessResult tests = await RunDotnetAsync(fixtureRoot, policy,
            "run", "--project", task.ProjectFile, "--configuration", "Release",
            "--no-build", "--no-restore");
        if (tests.ExitCode != 0)
            return new TestExecutionFailed(tests.Output);
        return new TestExecutionSucceeded(tests.Output);
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
        return new ProcessResult(process.ExitCode,
            text.Length <= policy.MaxFeedbackCharacters
                ? text : text[^policy.MaxFeedbackCharacters..]);
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
        string worktree, CodingRunResult outcome, string gitStatus, string transport,
        CodingPromptTemplate template, CodingExecutionPolicy policy, CodingTaskDefinition task)
    {
        CodingRunReport outcomeReport = CodingRunReport.From(outcome);
        var report = new
        {
            task = task.Id,
            status = outcomeReport.Status,
            model,
            transport,
            promptAsset = template.AssetPath,
            promptSha256 = template.Sha256,
            policyAsset = policy.AssetPath,
            policySha256 = policy.Sha256,
            modelCalls = outcomeReport.ModelCalls,
            approvalMode = "unattended-isolated",
            baseline,
            worktree,
            // A correct no-op expression is a passed task, not a changed file.
            modifiedPath = outcomeReport.Success
                && gitStatus.Contains(policy.OnlyWritablePath, StringComparison.Ordinal)
                    ? policy.OnlyWritablePath : null,
            gitStatus,
            timestampUtc = DateTimeOffset.UtcNow,
            attempts = outcomeReport.Attempts
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
            if (ValidateDraft(new CodingDraft("Plan", forbidden)) is not SourceValidationRejected)
                throw new InvalidOperationException($"Unsafe expression accepted: {forbidden}");
        }

        if (!IsAllowedOrigin("https://github.com/Snahdul/HaMi-AI-Engineering-Agent.git")
            || !IsAllowedOrigin("git@github.com:Snahdul/HaMi-AI-Engineering-Agent.git")
            || IsAllowedOrigin("https://evilgithub.com/Snahdul/HaMi-AI-Engineering-Agent.git")
            || IsAllowedOrigin("https://github.com/Attacker/HaMi-AI-Engineering-Agent.git"))
            throw new InvalidOperationException("Remote origin verification allowed an unexpected repository.");

        CodingTaskDefinition.SelfTest();
        CodingRunReport.SelfTest();
        CodingExecutionPolicy.SelfTest(repo);
        CodingExecutionPolicy policy = CodingExecutionPolicy.Load(repo);
        CodingPromptTemplate.SelfTest(repo);
        CodingPromptTemplate template = CodingPromptTemplate.Load(repo);

        const string correct = "value < min ? min : value > max ? max : value";
        if (ValidateDraft(new CodingDraft("Plan", correct)) is not SourceValidationPassed)
            throw new InvalidOperationException("Valid expression was rejected.");
        const string retryExpression = "attempt >= 1 && maxCalls >= 1 && attempt < maxCalls";
        if (ValidateDraft(new CodingDraft("Plan", retryExpression),
                CodingTaskDefinition.RetryBudget) is not SourceValidationPassed
            || ValidateDraft(new CodingDraft("Plan", "value < maxCalls"),
                CodingTaskDefinition.RetryBudget) is not SourceValidationRejected
            || ValidateDraft(new CodingDraft("Plan", "Math.Clamp(attempt, 0, maxCalls)"),
                CodingTaskDefinition.RetryBudget) is not SourceValidationRejected)
            throw new InvalidOperationException("Retry task expression bounds were not enforced.");

        string sourceDir = Path.Combine(repo, FixtureDirectory.Replace('/', Path.DirectorySeparatorChar));
        string temp = Path.Combine(RuntimeSettings.GetWorktreeRoot(repo), "hami-agent-coding-selftest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            foreach (string filename in new[] { "Clamp.cs", "ClampKata.csproj", "Program.cs" })
                File.Copy(Path.Combine(sourceDir, filename), Path.Combine(temp, filename));

            int calls = 0;
            CodingRunResult result = await ExecuteAttemptsAsync(
                temp, Path.Combine(temp, "Clamp.cs"), CodingTaskDefinition.Clamp,
                template, policy, prompt =>
            {
                calls++;
                if (calls == 2 && !prompt.User.Contains("Fixed tests failed:", StringComparison.Ordinal))
                    throw new InvalidOperationException("Feedback from failed tests did not reach correction attempt.");
                return Task.FromResult(new CodingDraft("Implement Clamp", calls == 1 ? "value" : correct));
            });
            CodingRunReport passedReport = CodingRunReport.From(result);
            if (result is not CodingRunSucceeded
                || !passedReport.Success || passedReport.ModelCalls != 2
                || passedReport.Attempts.Count != 2
                || passedReport.Attempts[0].Status != "tests_failed"
                || passedReport.Attempts[1].Status != "passed")
                throw new InvalidOperationException("Coding retry / real build+test self-test failed.");

            int compilerCalls = 0;
            CodingRunResult compilerRecovery = await ExecuteAttemptsAsync(
                temp, Path.Combine(temp, "Clamp.cs"), CodingTaskDefinition.Clamp,
                template, policy, prompt =>
            {
                compilerCalls++;
                if (compilerCalls == 2 && !prompt.User.Contains("Compiler failed:", StringComparison.Ordinal))
                    throw new InvalidOperationException("Compiler diagnostics did not reach correction attempt.");
                return Task.FromResult(new CodingDraft("Implement Clamp",
                    compilerCalls == 1 ? "value < min ? min :" : correct));
            });
            CodingRunReport compilerReport = CodingRunReport.From(compilerRecovery);
            if (compilerRecovery is not CodingRunSucceeded
                || !compilerReport.Success || compilerReport.ModelCalls != 2
                || compilerReport.Attempts[0].Status != "build_failed"
                || compilerReport.Attempts[1].Status != "passed")
                throw new InvalidOperationException("Coding compiler failure / recovery self-test failed.");

            // No partial code remains after a completely unsuccessful run.
            string target = Path.Combine(temp, "Clamp.cs");
            string original = await File.ReadAllTextAsync(Path.Combine(sourceDir, "Clamp.cs"));
            await File.WriteAllTextAsync(target, original);
            CodingRunResult exhausted = await ExecuteAttemptsAsync(
                temp, Path.Combine(temp, "Clamp.cs"), CodingTaskDefinition.Clamp, template, policy,
                _ => Task.FromResult(new CodingDraft("Plan", "value")));
            CodingRunReport exhaustedReport = CodingRunReport.From(exhausted);
            if (exhausted is not CodingRunExhausted
                || exhaustedReport.Success || exhaustedReport.ModelCalls != policy.MaxModelCalls
                || await File.ReadAllTextAsync(target) != original)
                throw new InvalidOperationException("Exhausted attempt budget did not restore original source.");

            // The second task compiles a *linked production source file* with a
            // trusted test project. Model output can only modify that one file.
            CodingTaskDefinition retryTask = CodingTaskDefinition.RetryBudget;
            CodingExecutionPolicy retryPolicy = CodingExecutionPolicy.Load(repo, retryTask);
            CodingPromptTemplate retryPrompt = CodingPromptTemplate.Load(repo, retryTask);
            string retryRoot = Path.Combine(temp, "retry-worktree");
            string retryFixture = Path.Combine(retryRoot,
                retryTask.FixtureDirectory.Replace('/', Path.DirectorySeparatorChar));
            string retrySource = Path.Combine(retryRoot,
                retryTask.TargetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(retryFixture);
            Directory.CreateDirectory(Path.GetDirectoryName(retrySource)!);
            File.Copy(Path.Combine(repo, retryTask.TargetPath.Replace('/', Path.DirectorySeparatorChar)),
                retrySource);
            foreach (string filename in new[] { retryTask.ProjectFile, "Program.cs" })
                File.Copy(Path.Combine(repo, retryTask.FixtureDirectory.Replace(
                    '/', Path.DirectorySeparatorChar), filename),
                    Path.Combine(retryFixture, filename));

            int retryCalls = 0;
            CodingRunResult retryResult = await ExecuteAttemptsAsync(
                retryFixture, retrySource, retryTask, retryPrompt, retryPolicy, prompt =>
                {
                    retryCalls++;
                    if (retryCalls == 2 && !prompt.User.Contains("Fixed tests failed:",
                        StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            "Trusted RetryBudget tests did not trigger bounded correction feedback.");
                    return Task.FromResult(new CodingDraft("Implement retry eligibility",
                        retryCalls == 1 ? "true" : retryExpression));
                });
            CodingRunReport retryReport = CodingRunReport.From(retryResult);
            if (retryResult is not CodingRunSucceeded
                || !retryReport.Success || retryReport.ModelCalls != 2
                || retryReport.Attempts.Count != 2
                || retryReport.Attempts[0].Status != "tests_failed"
                || retryReport.Attempts[1].Status != "passed"
                || await File.ReadAllTextAsync(retrySource) != await File.ReadAllTextAsync(
                    Path.Combine(repo, retryTask.TargetPath.Replace('/',
                        Path.DirectorySeparatorChar))))
                throw new InvalidOperationException(
                    "Production RetryBudget coding task did not recover using real compiler/tests.");

            string reportDir = Path.Combine(temp, "retry-report");
            Directory.CreateDirectory(reportDir);
            await WriteReportAsync(reportDir, "offline-stub", "trusted-baseline", retryRoot,
                retryResult, " M " + retryTask.TargetPath, "offline-fake",
                retryPrompt, retryPolicy, retryTask);
            using (JsonDocument report = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(reportDir, "report.json"))))
            {
                JsonElement resultJson = report.RootElement;
                if (resultJson.GetProperty("task").GetString() != retryTask.Id
                    || resultJson.GetProperty("promptAsset").GetString() != retryTask.PromptAsset
                    || resultJson.GetProperty("policyAsset").GetString() != retryTask.PolicyAsset
                    || resultJson.GetProperty("modifiedPath").GetString() != retryTask.TargetPath
                    || resultJson.GetProperty("modelCalls").GetInt32() != 2
                    || resultJson.GetProperty("status").GetString() != "generated"
                    || resultJson.GetProperty("attempts").GetArrayLength() != 2)
                    throw new InvalidOperationException("RetryBudget report task/asset mapping changed.");
            }

            // A hostile/unrelated model expression must not survive a failed run.
            string pristineRetrySource = File.ReadAllText(Path.Combine(repo,
                retryTask.TargetPath.Replace('/', Path.DirectorySeparatorChar)));
            await File.WriteAllTextAsync(retrySource, pristineRetrySource);
            CodingRunResult retryExhausted = await ExecuteAttemptsAsync(
                retryFixture, retrySource, retryTask, retryPrompt, retryPolicy,
                _ => Task.FromResult(new CodingDraft("Plan",
                    "System.IO.File.Delete(\"anything\")")));
            if (retryExhausted is not CodingRunExhausted
                || await File.ReadAllTextAsync(retrySource) != pristineRetrySource)
                throw new InvalidOperationException("Retry task did not fail closed and restore source.");

            Console.WriteLine("Coding self-test passed: C# 15 unions, two bounded tasks (ClampKata "
                + "and production RetryBudget), strict path/policy contracts, real build/test "
                + "corrections and exhausted-attempt rollback.");
            return 0;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException) { /* Leave temp if dotnet files are locked. */ }
        }
    }

    private sealed record CodingDraft(string Plan, string Expression);
    private sealed record ProcessResult(int ExitCode, string Output);
}
