using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>
/// Deliberately fixed, trusted main-branch RetryBudget smoke test.
/// Does not accept candidate code, repository mounts, custom shell commands,
/// model tools, credentials, or an arbitrary sandbox name.
/// </summary>
internal static class DevelopmentSandboxPoC
{
    internal const string SandboxName = "hami-dotnet11-check";
    private static readonly string[] TrustedFiles =
    [
        "global.json",
        "samples/RetryBudget/RetryBudget.csproj",
        "samples/RetryBudget/Program.cs",
        "src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs"
    ];

    private const string BuildScript = """
        #!/bin/sh
        set -eu
        cd "$1"
        out="$PWD/_results"
        mkdir -p "$out" /home/agent/empty-nuget-source
        : > "$out/build.log"
        : > "$out/test.log"
        start=$(date +%s)

        if /home/agent/bin/dotnet restore samples/RetryBudget/RetryBudget.csproj \
              --source /home/agent/empty-nuget-source -p:NuGetAudit=false --nologo \
              > "$out/build.log" 2>&1 &&
           /home/agent/bin/dotnet build samples/RetryBudget/RetryBudget.csproj \
              -c Release --no-restore --nologo >> "$out/build.log" 2>&1; then
            build=0
        else
            build=$?
        fi

        test=-1
        if [ "$build" -eq 0 ]; then
            if /home/agent/bin/dotnet run \
                  --project samples/RetryBudget/RetryBudget.csproj \
                  -c Release --no-restore --no-build \
                  > "$out/test.log" 2>&1; then
                test=0
            else
                test=$?
            fi
        fi

        elapsed=$(( $(date +%s) - start ))
        printf '{"schemaVersion":1,"buildExitCode":%d,"testExitCode":%d,"elapsedSeconds":%d}\n' \
            "$build" "$test" "$elapsed" > "$out/report.json"
        echo "Sandbox report written."
        """;

    public static async Task<int> RunAsync(string repo, string resultDirectory)
    {
        if (!Path.IsPathFullyQualified(resultDirectory))
            throw new ArgumentException("Sandbox results directory must be an absolute host path.");

        repo = Path.GetFullPath(repo);
        resultDirectory = Path.GetFullPath(resultDirectory);
        if (IsWithin(repo, resultDirectory) || IsWithin(resultDirectory, repo))
            throw new InvalidOperationException("Sandbox reports cannot reside inside or contain the repository.");

        string mainRoot = (await DevelopmentWorktreeGuard.GitAsync(
            repo, "rev-parse", "--show-toplevel")).Trim();
        if (!SamePath(mainRoot, repo))
            throw new InvalidOperationException("PoC requires a Git checkout root.");

        // The runner may itself be tested from a detached review worktree
        // *before* the PR is merged. Only the four trusted fixture files are
        // ever read from refs/heads/main, never from the review worktree.
        string mainSha = (await DevelopmentWorktreeGuard.GitAsync(
            repo, "rev-parse", "--verify", "refs/heads/main")).Trim();
        string fetchedMainSha = (await DevelopmentWorktreeGuard.GitAsync(
            repo, "rev-parse", "--verify", "refs/remotes/origin/main")).Trim();
        string sharedBase = (await DevelopmentWorktreeGuard.GitAsync(
            repo, "merge-base", "HEAD", "refs/heads/main")).Trim();
        if (!string.Equals(mainSha, fetchedMainSha, StringComparison.Ordinal)
            || !string.Equals(mainSha, sharedBase, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Main must be up to date and an ancestor of the review checkout.");

        string localChanges = await DevelopmentWorktreeGuard.GitAsync(
            repo, "status", "--porcelain", "--untracked-files=all");
        if (!string.IsNullOrEmpty(localChanges))
            throw new InvalidOperationException(
                "PoC must be run from a clean, reviewed checkout.");

        string origin = (await DevelopmentWorktreeGuard.GitAsync(
            repo, "remote", "get-url", "origin")).Trim();
        if (!IsAllowedOrigin(origin))
            throw new InvalidOperationException("PoC is restricted to the HaMi Agent repository.");

        // Fail closed before copying anything to the guest.
        SandboxProcessResult inspect = await SbxAsync(TimeSpan.FromSeconds(30),
            "inspect", SandboxName);
        ValidateInspection(inspect);
        SandboxProcessResult policy = await SbxAsync(TimeSpan.FromSeconds(30),
            "policy", "check", "network", "--sandbox", SandboxName, "api.github.com");
        ValidateNetworkPolicy(policy);
        SandboxProcessResult sdk = await SbxAsync(TimeSpan.FromSeconds(30),
            "exec", SandboxName, "/home/agent/bin/dotnet", "--version");
        if (sdk.ExitCode != 0 || !sdk.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("11.0.100", StringComparer.Ordinal))
            throw new InvalidOperationException("Expected the reviewed .NET SDK 11.0.100 template.");

        string runId = "hami-retry-poc-" + Guid.NewGuid().ToString("N");
        string stage = Path.Combine(resultDirectory, runId);
        string output = Path.Combine(resultDirectory, "reports", runId);
        string guest = "/home/agent/workspace/" + runId;
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(output);
        try
        {
            // Fetch exactly four versioned files from trusted refs/heads/main:
            // no working directory content, untracked files or model changes.
            foreach (string relative in TrustedFiles)
            {
                string content = await DevelopmentWorktreeGuard.GitAsync(
                    repo, "show", "refs/heads/main:" + relative);
                if (Encoding.UTF8.GetByteCount(content) > 128 * 1024)
                    throw new InvalidOperationException("Trusted fixture exceeds source size budget.");
                string hostFile = Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(hostFile)!);
                await File.WriteAllTextAsync(hostFile, content, new UTF8Encoding(false));
            }
            // C# raw string literals inherit the source file's line endings.
            // Windows checkouts may use CRLF, which Debian /bin/sh interprets
            // as an invalid option in 'set -eu\r'. Always emit LF-only shell code.
            await File.WriteAllTextAsync(Path.Combine(stage, "run.sh"),
                ToUnixLineEndings(BuildScript), new UTF8Encoding(false));

            EnsureSuccess(await SbxAsync(TimeSpan.FromMinutes(2),
                "cp", stage, SandboxName + ":/home/agent/workspace/"), "Copy trusted snapshot");
            EnsureSuccess(await SbxAsync(TimeSpan.FromSeconds(45),
                "exec", "--user", "root", SandboxName, "chown", "-R", "agent:agent", guest),
                "Fix Windows copy ownership");

            Stopwatch elapsed = Stopwatch.StartNew();
            SandboxProcessResult execution = await SbxAsync(TimeSpan.FromMinutes(3),
                "exec", SandboxName, "sh", guest + "/run.sh", guest);
            elapsed.Stop();
            EnsureSuccess(execution, "Execute fixed build/test script");

            string guestResults = SandboxName + ":" + guest + "/_results/";
            foreach (string name in new[] { "report.json", "build.log", "test.log" })
                EnsureSuccess(await SbxAsync(TimeSpan.FromSeconds(45),
                    "cp", guestResults + name, Path.Combine(output, name)),
                    "Retrieve " + name);

            string json = await File.ReadAllTextAsync(Path.Combine(output, "report.json"));
            SandboxReport report = SandboxReport.Parse(json);
            foreach (string name in new[] { "build.log", "test.log" })
                if (new FileInfo(Path.Combine(output, name)).Length > 256 * 1024)
                    throw new InvalidOperationException("Guest diagnostic log exceeds 256 KiB.");

            Console.WriteLine($"Trusted RetryBudget sandbox: {SandboxName}");
            Console.WriteLine($"Build exit: {report.BuildExitCode}; tests exit: {report.TestExitCode}");
            Console.WriteLine($"Guest build/test: {report.ElapsedSeconds}s; host execution: {elapsed.Elapsed.TotalSeconds:F2}s");
            Console.WriteLine($"Reports: {output}");
            Console.WriteLine("No candidate code, host workspace mounts, or model calls were used.");
            return report.Success ? 0 : 1;
        }
        finally
        {
            // The guest remains for manual inspection; this PoC never removes
            // a user-created sandbox. It cleans its own temporary host snapshot.
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    private static string ToUnixLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static void ValidateInspection(SandboxProcessResult result)
    {
        EnsureSuccess(result, "Inspect sandbox");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in result.Output.Split('\n'))
        {
            Match match = Regex.Match(line, @"^\s*([^:]+):\s*(.*?)\s*$");
            if (match.Success) fields[match.Groups[1].Value.Trim()] = match.Groups[2].Value.Trim();
        }

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Name"] = SandboxName,
            ["Agent"] = "shell",
            ["State"] = "running or stopped", // sbx stops local sandboxes when idle
            ["Workspace"] = "-",
            ["Clone source"] = "-",
            ["Configured host mounts"] = "none",
            ["Secrets"] = "none",
            ["MCP mode"] = "static",
            ["MCP servers"] = "none",
            ["Ports"] = "none published",
            ["Network policy"] = "global (local policy)",
            ["Auth mode"] = "not configured"
        };

        foreach ((string key, string value) in expected)
        {
            if (!fields.TryGetValue(key, out string? actual)
                || (key == "State"
                    ? !IsAcceptableSandboxState(actual)
                    : !string.Equals(actual, value, StringComparison.Ordinal)))
                throw new InvalidOperationException("Sandbox security preflight failed: " + key);
        }
    }

    // A local sbx create/exec sandbox may be stopped between commands.
    // A non-responsive or transitional state must still fail closed.
    private static bool IsAcceptableSandboxState(string state) =>
        (state is "running" or "stopped"
            || Regex.IsMatch(state, @"^(running|stopped) \([^)]*\)$",
                RegexOptions.CultureInvariant))
        && !state.Contains("not responding", StringComparison.OrdinalIgnoreCase);

    private static void ValidateNetworkPolicy(SandboxProcessResult result)
    {
        // Docker sbx policy check returns exit code 1 when access is denied.
        // Exit code 0 means allowed and must always fail the security gate.
        if (result.ExitCode != 1
            || !result.Output.Contains("Denied:", StringComparison.Ordinal)
            || !result.Output.Contains("no matching allow rule (default deny)", StringComparison.Ordinal))
            throw new InvalidOperationException("Sandbox network denial could not be confirmed.");
    }

    private static void EnsureSuccess(SandboxProcessResult result, string task)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{task} failed (exit {result.ExitCode}): "
                + result.Error.Trim());
    }

    internal sealed record SandboxReport(int BuildExitCode, int TestExitCode, int ElapsedSeconds)
    {
        public bool Success => BuildExitCode == 0 && TestExitCode == 0;

        public static SandboxReport Parse(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Sandbox report is not a JSON object.");
            var root = doc.RootElement;
            if (root.EnumerateObject().Count() != 4
                || !root.TryGetProperty("schemaVersion", out JsonElement version)
                || version.GetInt32() != 1
                || !root.TryGetProperty("buildExitCode", out JsonElement build)
                || !root.TryGetProperty("testExitCode", out JsonElement test)
                || !root.TryGetProperty("elapsedSeconds", out JsonElement elapsed))
                throw new InvalidOperationException("Sandbox report schema is invalid.");

            int buildExit = build.GetInt32(), testExit = test.GetInt32();
            int duration = elapsed.GetInt32();
            if (buildExit is < 0 or > 255 || testExit is < -1 or > 255
                || duration is < 0 or > 600
                || (buildExit != 0 && testExit != -1))
                throw new InvalidOperationException("Sandbox report values are invalid.");
            return new SandboxReport(buildExit, testExit, duration);
        }
    }

    internal static void SelfTest()
    {
        string inspection = """
            Name: hami-dotnet11-check
            Agent: shell
            State: running (10s)
            Workspace: -
            Clone source: -
            Configured host mounts: none
            Secrets: none
            MCP mode: static
            MCP servers: none
            Ports: none published
            Network policy: global (local policy)
            Auth mode: not configured
            """;
        ValidateInspection(new SandboxProcessResult(0, inspection, ""));
        ValidateInspection(new SandboxProcessResult(0,
            inspection.Replace("State: running (10s)", "State: stopped", StringComparison.Ordinal), ""));
        ValidateInspection(new SandboxProcessResult(0,
            inspection.Replace("State: running (10s)", "State: stopped (15m)", StringComparison.Ordinal), ""));
        ValidateNetworkPolicy(new SandboxProcessResult(1,
            "Denied: api.github.com:443\nReason: no matching allow rule (default deny)", ""));

        foreach (string unsafeInspection in new[]
        {
            inspection.Replace("Workspace: -", "Workspace: C:/Users", StringComparison.Ordinal),
            inspection.Replace("Configured host mounts: none", "Configured host mounts: 1", StringComparison.Ordinal),
            inspection.Replace("MCP servers: none", "MCP servers: github", StringComparison.Ordinal),
            inspection.Replace("Secrets: none", "Secrets: 1", StringComparison.Ordinal),
            inspection.Replace("State: running (10s)", "State: starting", StringComparison.Ordinal),
            inspection.Replace("State: running (10s)", "State: running (not responding)", StringComparison.Ordinal)
        })
            ExpectRejected(() => ValidateInspection(new SandboxProcessResult(0, unsafeInspection, "")));

        ExpectRejected(() => ValidateNetworkPolicy(new SandboxProcessResult(0,
            "Allowed: api.github.com:443", "")));
        // Even text claiming denial must fail if the CLI returned success (allowed).
        ExpectRejected(() => ValidateNetworkPolicy(new SandboxProcessResult(0,
            "Denied: api.github.com:443\nReason: no matching allow rule (default deny)", "")));
        ExpectRejected(() => ValidateNetworkPolicy(new SandboxProcessResult(1,
            "Denied: api.github.com:443", "")));
        ExpectRejected(() => ValidateNetworkPolicy(new SandboxProcessResult(2,
            "Denied: api.github.com:443\nReason: no matching allow rule (default deny)", "")));

        if (!SandboxReport.Parse("""{"schemaVersion":1,"buildExitCode":0,"testExitCode":0,"elapsedSeconds":1}""").Success
            || SandboxReport.Parse("""{"schemaVersion":1,"buildExitCode":0,"testExitCode":1,"elapsedSeconds":1}""").Success)
            throw new InvalidOperationException("Sandbox report success mapping invalid.");

        foreach (string invalidReport in new[]
        {
            """{"schemaVersion":2,"buildExitCode":0,"testExitCode":0,"elapsedSeconds":1}""",
            """{"schemaVersion":1,"buildExitCode":1,"testExitCode":0,"elapsedSeconds":1}""",
            """{"schemaVersion":1,"buildExitCode":0,"testExitCode":0,"elapsedSeconds":1,"shell":"true"}"""
        })
            ExpectRejected(() => SandboxReport.Parse(invalidReport));

        if (!IsAllowedOrigin("https://github.com/Snahdul/HaMi-AI-Engineering-Agent.git")
            || IsAllowedOrigin("https://github.com/untrusted/project.git"))
            throw new InvalidOperationException("Untrusted repository origin accepted.");

        // Simulate a Windows CRLF checkout even when CI runs on Linux.
        string windowsScript = BuildScript.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);
        string portableScript = ToUnixLineEndings(windowsScript);
        if (portableScript.Contains('\r')
            || !portableScript.StartsWith("#!/bin/sh\nset -eu\n", StringComparison.Ordinal)
            || !string.Equals(portableScript, ToUnixLineEndings(BuildScript), StringComparison.Ordinal))
            throw new InvalidOperationException("Shell script must use LF-only Unix line endings.");

        if (!BuildScript.Contains("--source /home/agent/empty-nuget-source", StringComparison.Ordinal)
            || !BuildScript.Contains("> \"$out/build.log\"", StringComparison.Ordinal)
            || !BuildScript.Contains("> \"$out/test.log\"", StringComparison.Ordinal))
            throw new InvalidOperationException("Fixed build script no longer captures offline diagnostics.");
    }

    private static void ExpectRejected(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe sandbox input was accepted.");
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

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsWithin(string parent, string child)
    {
        string relative = Path.GetRelativePath(parent, child);
        return relative == "."
            || (relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !Path.IsPathRooted(relative));
    }

    private static async Task<SandboxProcessResult> SbxAsync(
        TimeSpan timeout, params string[] arguments)
    {
        var start = new ProcessStartInfo("sbx")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        DevelopmentChildEnvironment.RestrictSandboxProcess(start);
        start.ArgumentList.Add("--no-input");
        foreach (string arg in arguments) start.ArgumentList.Add(arg);

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Docker Sandboxes CLI (sbx) is not installed.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new InvalidOperationException("Docker Sandbox operation exceeded its time limit.");
        }
        return new SandboxProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record SandboxProcessResult(int ExitCode, string Output, string Error);
}
