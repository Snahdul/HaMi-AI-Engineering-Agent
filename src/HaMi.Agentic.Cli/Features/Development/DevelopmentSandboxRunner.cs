using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>
/// Executes a bounded snapshot of arbitrary C# in an ephemeral, mountless local
/// Docker MicroVM. Never runs candidate code or MSBuild on the host.
/// The agent supplies a prepared source snapshot, not a host shell command.
/// </summary>
internal static class DevelopmentSandboxRunner
{
    private const string Template = "hami-dotnet11:0.1";
    private const int MaxFiles = 512;
    private const long MaxBytes = 16 * 1024 * 1024;
    private const int MaxOutputChars = 256 * 1024;

    internal sealed record SandboxRunReport(
        int SchemaVersion, string RunId, string Project, string Mode,
        int RestoreExitCode, int BuildExitCode, int ExecutionExitCode,
        bool TimedOut, long CreateMilliseconds, long CopyMilliseconds,
        long RestoreMilliseconds, long BuildMilliseconds, long ExecutionMilliseconds,
        long CleanupMilliseconds, long TotalMilliseconds, string? Error);

    public static async Task<int> RunAsync(
        string repo, string source, string project, string mode, string resultsDirectory)
    {
        ValidateMode(mode);
        ValidateProjectPath(project);
        if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(resultsDirectory))
            throw new InvalidOperationException("Snapshot and results directories must be absolute.");
        Stopwatch lifecycle = Stopwatch.StartNew();
        source = Path.GetFullPath(source);
        repo = Path.GetFullPath(repo);
        resultsDirectory = Path.GetFullPath(resultsDirectory);
        if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(resultsDirectory)
            || PathsOverlap(source, repo) || PathsOverlap(source, resultsDirectory)
            || PathsOverlap(resultsDirectory, repo))
            throw new InvalidOperationException("Source, repository and results must be separate directories.");
        if (!Directory.Exists(source))
            throw new InvalidOperationException("Snapshot source directory does not exist.");

        string[] sourceFiles = InspectSnapshot(source, project);
        string id = Guid.NewGuid().ToString("N");
        string sandbox = "hami-code-" + id[..12];
        string stage = Path.Combine(resultsDirectory, "staging", "hami-code-" + id);
        string reportDir = Path.Combine(resultsDirectory, "reports", "hami-code-" + id);
        string guest = "/home/agent/workspace/hami-code-" + id;
        string guestProject = project.Replace('\\', '/');
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(reportDir);

        bool createAttempted = false;
        bool timedOut = false;
        int restore = -1, build = -1, execution = -1;
        long createMs = 0, copyMs = 0, restoreMs = 0, buildMs = 0, executionMs = 0;
        long cleanupMs = 0;
        string? error = null;

        try
        {
            foreach (string relative in sourceFiles)
            {
                string from = Path.Combine(source, relative);
                string to = Path.Combine(stage, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to);
            }

            Stopwatch watch = Stopwatch.StartNew();
            createAttempted = true;
            EnsureSuccess(await SbxAsync(TimeSpan.FromSeconds(90),
                "create", "--name", sandbox, "--template", Template,
                "--pull", "never", "--cpus", "4", "--memory", "4g",
                "--skills", "off", "--static-mcp", "none", "shell"), "Create sandbox");
            createMs = watch.ElapsedMilliseconds;

            SandboxProcess inspect = await SbxAsync(TimeSpan.FromSeconds(30),
                "inspect", sandbox);
            ValidateInspect(inspect, sandbox);
            // No guest network approvals, no source/package retrieval from network.
            foreach (string endpoint in new[] { "api.github.com:443", "1.1.1.1:443", "10.0.0.1:443" })
            {
                SandboxProcess policy = await SbxAsync(TimeSpan.FromSeconds(20),
                    "policy", "check", "network", "--sandbox", sandbox, endpoint);
                ValidateDeny(policy, endpoint);
            }

            SandboxProcess sdk = await SbxAsync(TimeSpan.FromSeconds(40),
                "exec", sandbox, "/home/agent/bin/dotnet", "--version");
            if (sdk.ExitCode != 0
                || !sdk.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Contains("11.0.100", StringComparer.Ordinal))
                throw new InvalidOperationException("Expected offline .NET SDK 11.0.100 in template.");

            watch.Restart();
            EnsureSuccess(await SbxAsync(TimeSpan.FromSeconds(75),
                "cp", stage, sandbox + ":/home/agent/workspace/"), "Transfer snapshot");
            EnsureSuccess(await SbxAsync(TimeSpan.FromSeconds(35),
                "exec", "--user", "root", sandbox, "chown", "-R", "agent:agent", guest),
                "Set guest snapshot owner");
            copyMs = watch.ElapsedMilliseconds;

            // No shell interpolation: the project name is a validated relative
            // path and each component of the dotnet command is a fixed argv item.
            EnsureSuccess(await SbxAsync(TimeSpan.FromSeconds(20),
                "exec", sandbox, "mkdir", "-p", "/home/agent/empty-nuget-source"),
                "Prepare offline NuGet source");

            watch.Restart();
            SandboxProcess restored = await SbxAsync(TimeSpan.FromSeconds(75),
                "exec", "--workdir", guest, sandbox, "/home/agent/bin/dotnet",
                "restore", guestProject, "--source", "/home/agent/empty-nuget-source",
                "-p:NuGetAudit=false", "--nologo");
            restoreMs = watch.ElapsedMilliseconds;
            restore = restored.ExitCode;
            await WriteOutputAsync(reportDir, "restore.log", restored);

            if (restore == 0)
            {
                watch.Restart();
                SandboxProcess built = await SbxAsync(TimeSpan.FromSeconds(120),
                    "exec", "--workdir", guest, sandbox, "/home/agent/bin/dotnet",
                    "build", guestProject, "-c", "Release", "--no-restore", "--nologo");
                buildMs = watch.ElapsedMilliseconds;
                build = built.ExitCode;
                await WriteOutputAsync(reportDir, "build.log", built);
            }

            if (build == 0 && mode != "build")
            {
                watch.Restart();
                string[] arguments = mode == "run"
                    ? ["run", "--project", guestProject, "-c", "Release", "--no-restore", "--no-build"]
                    : ["test", guestProject, "-c", "Release", "--no-restore", "--no-build", "--nologo"];
                SandboxProcess checkedCode = await SbxAsync(TimeSpan.FromSeconds(60),
                    ["exec", "--workdir", guest, sandbox, "/home/agent/bin/dotnet", .. arguments]);
                executionMs = watch.ElapsedMilliseconds;
                execution = checkedCode.ExitCode;
                await WriteOutputAsync(reportDir, "execution.log", checkedCode);
            }
        }
        catch (TimeoutException ex)
        {
            timedOut = true;
            error = ex.Message;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        finally
        {
            Stopwatch cleanup = Stopwatch.StartNew();
            if (createAttempted)
            {
                try
                {
                    EnsureSuccess(await SbxAsync(TimeSpan.FromSeconds(90),
                        "rm", "--force", sandbox), "Remove ephemeral sandbox");
                }
                catch (Exception ex)
                {
                    error = (error is null ? "" : error + "; ") + "CLEANUP FAILED: " + ex.Message;
                }
            }
            cleanupMs = cleanup.ElapsedMilliseconds;
            if (Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
        }

        var report = new SandboxRunReport(1, id, guestProject, mode, restore, build,
            execution, timedOut, createMs, copyMs, restoreMs, buildMs, executionMs, cleanupMs, lifecycle.ElapsedMilliseconds, error);
        string reportPath = Path.Combine(reportDir, "report.json");
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            }));
        Console.WriteLine($"Sandbox report: {reportPath}");
        Console.WriteLine($"Restore: {restore}; build: {build}; {mode}: {execution}; timeout: {timedOut}");
        if (error is not null) Console.Error.WriteLine(error);
        return error is null && restore == 0 && build == 0
            && (mode == "build" || execution == 0) ? 0 : 1;
    }

    private static async Task WriteOutputAsync(string dir, string filename, SandboxProcess process)
    {
        string output = process.Output + (process.Error.Length == 0 ? "" : "\nSTDERR:\n" + process.Error);
        await File.WriteAllTextAsync(Path.Combine(dir, filename), output);
    }

    internal static void ValidateMode(string mode)
    {
        if (mode is not ("build" or "run" or "test"))
            throw new ArgumentException("Mode must be build, run or test.");
    }

    internal static void ValidateProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || !path.EndsWith(".csproj", StringComparison.Ordinal)
            || path.StartsWith('/') || path.Contains('\\') || !Regex.IsMatch(path, @"^[a-zA-Z0-9._/-]+$", RegexOptions.CultureInvariant)
            || path.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidOperationException("Project must be a safe relative *.csproj path.");
    }

    internal static string[] InspectSnapshot(string source, string project)
    {
        ValidateProjectPath(project);
        if (!Directory.Exists(source)) throw new InvalidOperationException("Snapshot does not exist.");
        var files = new List<string>();
        var paths = new Stack<string>();
        paths.Push(source);
        CheckNoReparseAncestors(source);
        long total = 0;
        while (paths.Count > 0)
        {
            string dir = paths.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Symlinks and junctions are not allowed in snapshots.");
                string relative = Path.GetRelativePath(source, entry).Replace('\\', '/');
                string name = Path.GetFileName(entry);
                if (name is ".git" or ".hg" or ".svn" or ".ssh" or ".vs" or ".idea"
                    or "obj" or "bin" or ".nuget" or ".env"
                    || name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Snapshot contains a prohibited metadata or credential path.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    paths.Push(entry);
                    continue;
                }
                if (relative.Contains(':') || relative.StartsWith('/') || relative.Split('/').Any(p => p is ".." or "."))
                    throw new InvalidOperationException("Invalid snapshot file path.");
                total += new FileInfo(entry).Length;
                files.Add(relative.Replace('/', Path.DirectorySeparatorChar));
                if (files.Count > MaxFiles || total > MaxBytes)
                    throw new InvalidOperationException("Snapshot exceeds 512 files or 16 MiB.");
            }
        }
        if (!files.Any(f => string.Equals(f.Replace('\\', '/'), project, StringComparison.Ordinal)))
            throw new InvalidOperationException("Requested C# project is not included in the snapshot.");
        return files.OrderBy(f => f, StringComparer.Ordinal).ToArray();
    }

    private static void CheckNoReparseAncestors(string source)
    {
        string? cursor = Path.GetFullPath(source);
        while (cursor is not null)
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Snapshot path passes through a junction or symlink.");
            string? parent = Path.GetDirectoryName(cursor);
            if (parent is null || parent == cursor) break;
            cursor = parent;
        }
    }

    private static bool PathsOverlap(string left, string right)
    {
        string relative = Path.GetRelativePath(left, right);
        string reverse = Path.GetRelativePath(right, left);
        return IsInside(relative) || IsInside(reverse);
    }

    private static bool IsInside(string path) =>
        path == "." || (path != ".."
            && !path.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(path));

    private static void ValidateInspect(SandboxProcess result, string sandbox)
    {
        EnsureSuccess(result, "Inspect sandbox");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in result.Output.Split('\n'))
        {
            Match match = Regex.Match(line, @"^\s*([^:]+):\s*(.*?)\s*$");
            if (match.Success) fields[match.Groups[1].Value.Trim()] = match.Groups[2].Value.Trim();
        }
        var required = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Name"] = sandbox,
            ["Agent"] = "shell",
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
        foreach ((string key, string value) in required)
            if (!fields.TryGetValue(key, out string? actual)
                || !string.Equals(actual, value, StringComparison.Ordinal))
                throw new InvalidOperationException("Sandbox security preflight failed: " + key);
        if (!fields.TryGetValue("State", out string? state)
            || !(state is "running" or "stopped"
                || Regex.IsMatch(state, @"^(running|stopped) \([^)]*\)$",
                    RegexOptions.CultureInvariant))
            || state.Contains("not responding", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Sandbox state is not healthy.");
    }

    private static void ValidateDeny(SandboxProcess result, string endpoint)
    {
        if (result.ExitCode != 1 || !result.Output.Contains("Denied:", StringComparison.Ordinal)
            || !result.Output.Contains(endpoint, StringComparison.Ordinal)
            || !result.Output.Contains("default deny", StringComparison.Ordinal))
            throw new InvalidOperationException("Sandbox network is not default-deny for " + endpoint);
    }

    private static void EnsureSuccess(SandboxProcess result, string action)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{action} failed ({result.ExitCode}): {result.Error.Trim()}");
    }

    private static async Task<SandboxProcess> SbxAsync(TimeSpan limit, params string[] arguments)
    {
        var info = new ProcessStartInfo("sbx")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("--no-input");
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("sbx CLI is not available.");
        using var cts = new CancellationTokenSource(limit);
        Task<string> stdout = ReadBoundedAsync(process.StandardOutput, cts.Token);
        Task<string> stderr = ReadBoundedAsync(process.StandardError, cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return new SandboxProcess(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("sbx operation timed out: " + arguments[0]);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader input, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        char[] buffer = new char[4096];
        while (true)
        {
            int count = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            int kept = Math.Min(count, Math.Max(0, MaxOutputChars - sb.Length));
            if (kept > 0) sb.Append(buffer, 0, kept);
        }
        return sb.ToString();
    }

    internal static void SelfTest()
    {
        foreach (string invalid in new[]
        {
            "../Other.csproj", "/root/a.csproj", "C:/a.csproj", "a\\b.csproj",
            "src//App.csproj", "src/../App.csproj", "src/App.cs"
        })
            ExpectRejected(() => ValidateProjectPath(invalid));
        ValidateProjectPath("src/MyProject/App.csproj");
        foreach (string invalid in new[] { "", "build and run", "shell" })
            ExpectRejected(() => ValidateMode(invalid));
        foreach (string valid in new[] { "build", "run", "test" }) ValidateMode(valid);

        string root = Path.Combine(Path.GetTempPath(), "hami-snapshot-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Demo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
            File.WriteAllText(Path.Combine(root, "Program.cs"), "Console.WriteLine(42);");
            if (InspectSnapshot(root, "Demo.csproj").Length != 2)
                throw new InvalidOperationException("Valid C# snapshot was rejected.");
            File.WriteAllText(Path.Combine(root, ".env"), "PROHIBITED");
            ExpectRejected(() => InspectSnapshot(root, "Demo.csproj"));
            File.Delete(Path.Combine(root, ".env"));
            ExpectRejected(() => InspectSnapshot(root, "Missing.csproj"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        ValidateDeny(new SandboxProcess(1,
            "Denied: api.github.com:443\nReason: no matching allow rule (default deny)", ""),
            "api.github.com:443");
        ExpectRejected(() => ValidateDeny(new SandboxProcess(0,
            "Denied: api.github.com:443\nReason: no matching allow rule (default deny)", ""),
            "api.github.com:443"));
        ExpectRejected(() => ValidateDeny(new SandboxProcess(1, "Allowed: api.github.com:443", ""),
            "api.github.com:443"));
    }

    private static void ExpectRejected(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe input was accepted.");
    }

    private sealed record SandboxProcess(int ExitCode, string Output, string Error);
}
