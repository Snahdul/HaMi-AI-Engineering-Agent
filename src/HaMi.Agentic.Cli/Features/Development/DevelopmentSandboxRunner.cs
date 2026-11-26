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
    private const int MaxLogPreviewChars = 4096;
    private const int MaxLogPreviewLines = 80;
    // Host credentials must not reach sbx or the guest. The named set matches
    // our guest policy probe. Never log secret values or environment dumps.
    private static readonly string[] SensitiveEnvNames =
    [
        "OPENAI_API_KEY", "GITHUB_TOKEN", "GH_TOKEN", "HAMI_AGENT_GITHUB_TOKEN",
        "HAMI_AGENT_GITHUB_PUBLICATION_TOKEN", "AZURE_CLIENT_SECRET",
        "AWS_SECRET_ACCESS_KEY", "SSH_AUTH_SOCK",
        // Windows User API-key alias; not a POSIX shell identifier.
        // Remove only from the sbx CHILD process environment.
        "HaMi-AI-Engineering-Agent-Key"
    ];

    private const string GuestEnvCheckScript =
        "for n in OPENAI_API_KEY GITHUB_TOKEN GH_TOKEN HAMI_AGENT_GITHUB_TOKEN HAMI_AGENT_GITHUB_PUBLICATION_TOKEN AZURE_CLIENT_SECRET AWS_SECRET_ACCESS_KEY SSH_AUTH_SOCK; do " +
        "case \"$n\" in " +
        "OPENAI_API_KEY) v=${OPENAI_API_KEY-};; " +
        "GITHUB_TOKEN) v=${GITHUB_TOKEN-};; " +
        "GH_TOKEN) v=${GH_TOKEN-};; " +
        "HAMI_AGENT_GITHUB_TOKEN) v=${HAMI_AGENT_GITHUB_TOKEN-};; " +
        "HAMI_AGENT_GITHUB_PUBLICATION_TOKEN) v=${HAMI_AGENT_GITHUB_PUBLICATION_TOKEN-};; " +
        "AZURE_CLIENT_SECRET) v=${AZURE_CLIENT_SECRET-};; " +
        "AWS_SECRET_ACCESS_KEY) v=${AWS_SECRET_ACCESS_KEY-};; " +
        "SSH_AUTH_SOCK) v=${SSH_AUTH_SOCK-};; " +
        "esac; " +
        "if [ -n \"$v\" ]; then printf 'NONEMPTY_CREDENTIAL_ENV=%s\\n' \"$n\"; exit 19; fi; " +
        "done; printf 'CREDENTIAL_ENV=clean\\n';";

    // Fixed trusted template utilities, before any candidate build or code.
    // The only argument is the host-derived guest directory. Nonregular
    // entries yield an invalid record; any failure omits the end marker.
    private const string GuestSnapshotCheckScript =
        "set -eu; export LC_ALL=C; test ! -L \"$1\"; cd \"$1\"; " +
        "find . ! -type d ! -type f -printf 'INVALID\\0'; " +
        "find . -type f -exec sha256sum --zero -- {} +; " +
        "printf 'HAMI_SNAPSHOT_END\\0'";

    internal sealed record SandboxRunReport(
        int SchemaVersion, string RunId, string Project, string Mode,
        int RestoreExitCode, int BuildExitCode, int ExecutionExitCode,
        bool TimedOut, long CreateMilliseconds, long CopyMilliseconds,
        long RestoreMilliseconds, long BuildMilliseconds, long ExecutionMilliseconds,
        long CleanupMilliseconds, long TotalMilliseconds, bool SandboxRemovalSucceeded, string? Error,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? SnapshotHash = null);

    public static Task<int> RunAsync(
        string repo, string source, string project, string mode, string resultsDirectory,
        Action<string, string, long, int>? phaseObserver = null) =>
        RunCoreAsync(repo, source, project, mode, resultsDirectory, phaseObserver, null, SbxAsync);

    // Only a trusted order host may call this after a fresh journal ack,
    // current authorization and budget admission. Never call during replay.
    // The caller supplies an existing private, exclusively controlled
    // results root and a reviewed immutable source snapshot.
    internal static Task<int> RunReservedAsync(string repo, string source, string resultsDirectory,
        DevelopmentJobStateStore.Lease owner, DevelopmentOrderOperationLedger acknowledged,
        OrderOperationReservation reservation, Action<string, string, long, int>? phaseObserver = null)
    {
        // Consume once in the private job root, independently of any chosen
        // output directory. Failure after this point must never redispatch.
        owner.ConsumeOrderGuestDispatch(acknowledged, reservation);
        return RunCoreAsync(repo, source, "Demo.csproj", "run", resultsDirectory, phaseObserver, reservation, SbxAsync);
    }

    private static async Task<int> RunCoreAsync(
        string repo, string source, string project, string mode, string resultsDirectory,
        Action<string, string, long, int>? phaseObserver, OrderOperationReservation? reservation,
        Func<TimeSpan, string[], Task<SandboxProcess>> transport)
    {
        Task<SandboxProcess> InvokeAsync(TimeSpan limit, params string[] arguments) => transport(limit, arguments);
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
        string id = reservation is null ? Guid.NewGuid().ToString("N")
            : DevelopmentOrderGuestReconciliation.RunId(reservation);
        if (reservation is not null)
            DevelopmentOrderSnapshot.Require(source, project, reservation.SnapshotHash);
        string sandbox = RecoverySandboxName(id);
        string stage = Path.Combine(resultsDirectory, "staging", "hami-code-" + id);
        string reportDir = Path.Combine(resultsDirectory, "reports", "hami-code-" + id);
        string guest = "/home/agent/workspace/hami-code-" + id;
        string guestProject = project.Replace('\\', '/');
        if (reservation is not null)
            ReserveDispatchSlot(resultsDirectory, id, stage, reportDir);
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(reportDir);

        string? verifiedSnapshotHash = null;
        string[]? expectedGuestRecords = null;
        bool createAttempted = false;
        bool createConfirmed = false;
        bool sandboxRemovalSucceeded = false;
        bool timedOut = false;
        int restore = -1, build = -1, execution = -1;
        long createMs = 0, copyMs = 0, restoreMs = 0, buildMs = 0, executionMs = 0;
        long cleanupMs = 0;
        string? error = null;

        // Standalone sandbox runs retain their console output. Agent jobs pass a
        // trusted typed phase observer instead: raw guest diagnostics never
        // enter the structured logger or appear twice on the console.
        void ProgressMessage(string message, string? phaseCode = null, int phaseExit = 0)
        {
            if (phaseObserver is null)
                Progress(lifecycle, message);
            else if (phaseCode is not null)
                phaseObserver(phaseCode, sandbox, lifecycle.ElapsedMilliseconds, phaseExit);
        }

        ProgressMessage($"Auftrag {id[..12]} | Projekt: {guestProject} | Modus: {mode}");
        ProgressMessage($"Snapshot: {sourceFiles.Length} Dateien | Ziel: {sandbox}");

        try
        {
            foreach (string relative in sourceFiles)
            {
                string from = Path.Combine(source, relative);
                string to = Path.Combine(stage, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to);
            }

            if (reservation is not null)
            {
                // Hash the actual copied files, not just the input directory.
                // Never label a mismatched staging snapshot as verified.
                expectedGuestRecords = DevelopmentOrderSnapshot.RequireGuestRecords(stage, project, reservation.SnapshotHash);
            }

            Stopwatch watch = Stopwatch.StartNew();
            ProgressMessage($"Erstelle MicroVM {sandbox} aus {Template} (4 vCPU, 4 GiB) ...");
            if (reservation is not null)
            {
                // Exact read-only absence check immediately before create.
                // Existing name or ambiguous inspect is not ours to remove.
                SandboxProcess existing = await InvokeAsync(TimeSpan.FromSeconds(30), "inspect", sandbox);
                if (!IsConfirmedAbsent(id, existing.ExitCode, existing.Output, existing.Error))
                    throw new InvalidOperationException("Reserved sandbox name absence not confirmed.");
            }
            createAttempted = true;
            EnsureSuccess(await InvokeAsync(TimeSpan.FromSeconds(90),
                "create", "--name", sandbox, "--template", Template,
                "--pull", "never", "--cpus", "4", "--memory", "4g",
                "--skills", "off", "--static-mcp", "none",
                "--env", "OPENAI_API_KEY=", "--env", "GITHUB_TOKEN=",
                "--env", "GH_TOKEN=", "--env", "HAMI_AGENT_GITHUB_TOKEN=",
                "--env", "HAMI_AGENT_GITHUB_PUBLICATION_TOKEN=",
                "--env", "AZURE_CLIENT_SECRET=",
                "--env", "AWS_SECRET_ACCESS_KEY=", "--env", "SSH_AUTH_SOCK=",
                "shell"), "Create sandbox");
            createConfirmed = true;
            createMs = watch.ElapsedMilliseconds;
            ProgressMessage($"MicroVM bereit ({createMs / 1000.0:F2} s).", "MICROVM_READY");

            ProgressMessage("Prüfe Isolation: Mounts, Secrets, MCP, Ports, Netzwerk ...");
            SandboxProcess inspect = await InvokeAsync(TimeSpan.FromSeconds(30),
                "inspect", sandbox);
            ValidateInspect(inspect, sandbox);
            // No guest network approvals, no source/package retrieval from network.
            foreach (string endpoint in new[] { "api.github.com:443", "1.1.1.1:443", "10.0.0.1:443" })
            {
                SandboxProcess policy = await InvokeAsync(TimeSpan.FromSeconds(20),
                    "policy", "check", "network", "--sandbox", sandbox, endpoint);
                ValidateDeny(policy, endpoint);
            }

            ProgressMessage("Prüfe Gast-Umgebung auf nichtleere Credential-Variablen ...");
            SandboxProcess credentialCheck = await InvokeAsync(TimeSpan.FromSeconds(20),
                "exec", sandbox, "/bin/sh", "-c", GuestEnvCheckScript);
            ValidateGuestCredentialPreflight(credentialCheck);
            ProgressMessage("Gast-Umgebung bestätigt: keine nichtleeren Credential-Variablen.");
            ProgressMessage("Isolation bestätigt: kein Host-Mount, keine Secrets, Netzwerk gesperrt.", "ISOLATION_VERIFIED");
            ProgressMessage("Prüfe .NET SDK 11.0.100 im Gast ...");
            SandboxProcess sdk = await InvokeAsync(TimeSpan.FromSeconds(40),
                "exec", sandbox, "/home/agent/bin/dotnet", "--version");
            if (sdk.ExitCode != 0
                || !sdk.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Contains("11.0.100", StringComparer.Ordinal))
                throw new InvalidOperationException("Expected offline .NET SDK 11.0.100 in template.");
            ProgressMessage(".NET SDK 11.0.100 einsatzbereit.", "DOTNET_SDK_READY");

            ProgressMessage($"Übertrage {sourceFiles.Length} Snapshot-Dateien in die MicroVM ...");
            watch.Restart();
            EnsureSuccess(await InvokeAsync(TimeSpan.FromSeconds(75),
                "cp", stage, sandbox + ":/home/agent/workspace/"), "Transfer snapshot");
            EnsureSuccess(await InvokeAsync(TimeSpan.FromSeconds(35),
                "exec", "--user", "root", sandbox, "chown", "-R", "agent:agent", guest),
                "Set guest snapshot owner");
            copyMs = watch.ElapsedMilliseconds;
            ProgressMessage($"Snapshot übertragen und Rechte gesetzt ({copyMs / 1000.0:F2} s).", "SNAPSHOT_TRANSFERRED");

            if (reservation is not null)
            {
                SandboxProcess observedSnapshot = await InvokeAsync(TimeSpan.FromSeconds(30),
                    "exec", sandbox, "/bin/sh", "-c", GuestSnapshotCheckScript, "hami-snapshot", guest);
                DevelopmentOrderSnapshot.RequireGuestRecords(
                    expectedGuestRecords ?? throw new InvalidOperationException("Expected snapshot missing."),
                    observedSnapshot.ExitCode, observedSnapshot.Output, observedSnapshot.Error);
                verifiedSnapshotHash = reservation.SnapshotHash;
                ProgressMessage("Gast-Snapshot bestätigt.", "SNAPSHOT_VERIFIED");
            }

            // No shell interpolation: the project name is a validated relative
            // path and each component of the dotnet command is a fixed argv item.
            EnsureSuccess(await InvokeAsync(TimeSpan.FromSeconds(20),
                "exec", sandbox, "mkdir", "-p", "/home/agent/empty-nuget-source"),
                "Prepare offline NuGet source");

            ProgressMessage("Offline-Restore gestartet ...");
            watch.Restart();
            SandboxProcess restored = await InvokeAsync(TimeSpan.FromSeconds(75),
                "exec", "--workdir", guest, sandbox, "/home/agent/bin/dotnet",
                "restore", guestProject, "--source", "/home/agent/empty-nuget-source",
                "-p:NuGetAudit=false", "--nologo");
            restoreMs = watch.ElapsedMilliseconds;
            restore = restored.ExitCode;
            await WriteOutputAsync(reportDir, "restore.log", restored);
            ProgressMessage($"Restore beendet: Exitcode {restore} ({restoreMs / 1000.0:F2} s).", "RESTORE_COMPLETED", restore);

            if (restore == 0)
            {
                ProgressMessage("Release-Build gestartet ...");
                watch.Restart();
                SandboxProcess built = await InvokeAsync(TimeSpan.FromSeconds(120),
                    "exec", "--workdir", guest, sandbox, "/home/agent/bin/dotnet",
                    "build", guestProject, "-c", "Release", "--no-restore", "--nologo");
                buildMs = watch.ElapsedMilliseconds;
                build = built.ExitCode;
                await WriteOutputAsync(reportDir, "build.log", built);
                ProgressMessage($"Build beendet: Exitcode {build} ({buildMs / 1000.0:F2} s).", "BUILD_COMPLETED", build);
            }

            if (build == 0 && mode != "build")
            {
                ProgressMessage(mode == "run"
                    ? "Starte das C#-Programm im Gast (max. 60 s) ..."
                    : "Starte Tests im Gast (max. 60 s) ...");
                watch.Restart();
                string[] arguments = mode == "run"
                    ? ["run", "--project", guestProject, "-c", "Release", "--no-restore", "--no-build"]
                    : ["test", guestProject, "-c", "Release", "--no-restore", "--no-build", "--nologo"];
                SandboxProcess checkedCode;
                try
                {
                    checkedCode = await InvokeAsync(TimeSpan.FromSeconds(60),
                        ["exec", "--workdir", guest, sandbox, "/home/agent/bin/dotnet", .. arguments]);
                }
                finally
                {
                    // Also capture the elapsed execution time when sbx exec times out.
                    executionMs = watch.ElapsedMilliseconds;
                }
                execution = checkedCode.ExitCode;
                await WriteOutputAsync(reportDir, "execution.log", checkedCode);
                ProgressMessage($"Ausführung beendet: Exitcode {execution} ({executionMs / 1000.0:F2} s).", "EXECUTION_COMPLETED", execution);
            }
        }
        catch (TimeoutException ex)
        {
            timedOut = true;
            error = ex.Message;
            ProgressMessage("Timeout: " + ex.GetType().Name, "OPERATION_TIMEOUT", -1);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            ProgressMessage("Fehler: " + ex.GetType().Name, "OPERATION_FAULT", -1);
        }
        finally
        {
            ProgressMessage($"Bereinige MicroVM {sandbox} und lokale Staging-Dateien ...");
            Stopwatch cleanup = Stopwatch.StartNew();
            // V1 preserves its existing cleanup behavior. For an order,
            // an ambiguous create may refer to a competing name: leave it
            // pending for reconciliation instead of deleting by name.
            if (reservation is null ? createAttempted : createConfirmed)
            {
                try
                {
                    EnsureSuccess(await InvokeAsync(TimeSpan.FromSeconds(90),
                        "rm", "--force", sandbox), "Remove ephemeral sandbox");
                    sandboxRemovalSucceeded = true;
                }
                catch (Exception ex)
                {
                    error = (error is null ? "" : error + "; ") + "CLEANUP FAILED: " + ex.Message;
                }
            }
            cleanupMs = cleanup.ElapsedMilliseconds;
            if (Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
            ProgressMessage(sandboxRemovalSucceeded
                ? $"MicroVM entfernt: sbx rm Exitcode 0 ({cleanupMs / 1000.0:F2} s)."
                : $"ACHTUNG: MicroVM-Entfernung nicht bestätigt ({cleanupMs / 1000.0:F2} s).",
                sandboxRemovalSucceeded ? "CLEANUP_CONFIRMED" : "CLEANUP_UNCONFIRMED",
                sandboxRemovalSucceeded ? 0 : 1);
        }

        var report = new SandboxRunReport(reservation is null ? 1 : 3, id, guestProject, mode, restore, build,
            execution, timedOut, createMs, copyMs, restoreMs, buildMs, executionMs, cleanupMs, lifecycle.ElapsedMilliseconds,
            sandboxRemovalSucceeded, error, verifiedSnapshotHash);
        string reportPath = Path.Combine(reportDir, "report.json");
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            }));
        ProgressMessage($"Auftrag beendet ({report.TotalMilliseconds / 1000.0:F2} s), Bericht erstellt.");
        if (phaseObserver is null)
        {
            Console.WriteLine($"Sandbox report: {reportPath}");
            Console.WriteLine($"Restore: {restore}; build: {build}; {mode}: {execution}; timeout: {timedOut}");
            if (error is not null) Console.Error.WriteLine(error);
            ShowResultLog(reportDir, mode, restore, build, timedOut);
        }
        return error is null && sandboxRemovalSucceeded && restore == 0 && build == 0
            && (mode == "build" || execution == 0) ? 0 : 1;
    }

    private static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void ReserveDispatchSlot(string resultsDirectory, string id, string stage, string reportDir)
    {
        if (!Directory.Exists(resultsDirectory))
            throw new InvalidOperationException("Reserved sandbox requires an existing private results root.");
        CheckNoReparseAncestors(resultsDirectory);
        foreach (string path in new[] { stage, reportDir })
        {
            string parent = Path.GetDirectoryName(path)!;
            if (EntryExists(parent))
            {
                if (!Directory.Exists(parent))
                    throw new InvalidOperationException("Reserved sandbox output parent is not a directory.");
                CheckNoReparseAncestors(parent);
            }
            if (EntryExists(path))
                throw new InvalidOperationException("Reserved sandbox output already exists.");
        }

        // Persistent one-shot guard for this trusted results root and short
        // name. Not a workflow journal or proof of global name ownership.
        // Never remove/reuse the slot after a fault or successful run.
        string slot = Path.Combine(resultsDirectory, "." + RecoverySandboxName(id) + ".dispatch");
        using var stream = new FileStream(slot, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(Encoding.UTF8.GetBytes(id));
        stream.Flush(flushToDisk: true);
    }

    private static void Progress(Stopwatch elapsed, string message)
    {
        Console.WriteLine($"[HaMi Sandbox +{elapsed.Elapsed.TotalSeconds:F2}s] {message}");
        Console.Out.Flush();
    }

    // Guest programs and compiler diagnostics are untrusted. Display only a
    // bounded and control-character-safe preview; retain the full log on disk.
    private static void ShowResultLog(string directory, string mode,
        int restoreExit, int buildExit, bool timedOut)
    {
        string logName = restoreExit != 0 ? "restore.log"
            : buildExit != 0 || mode == "build" ? "build.log"
            : "execution.log";
        string logPath = Path.Combine(directory, logName);
        if (!File.Exists(logPath))
        {
            if (timedOut)
                Console.WriteLine("Keine Ausführungsdiagnose gespeichert (sbx exec durch Timeout abgebrochen).");
            return;
        }

        try
        {
            string contents = File.ReadAllText(logPath);
            Console.WriteLine($"--- Sandbox-Ergebnis ({logName}, begrenzte Vorschau) ---");
            Console.WriteLine(string.IsNullOrWhiteSpace(contents)
                ? "(keine Ausgabe)" : SafeLogPreview(contents));
            Console.WriteLine($"Vollständiges Log: {logPath}");
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Log-Vorschau nicht verfügbar: {ex.GetType().Name}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Log-Vorschau nicht verfügbar: {ex.GetType().Name}");
        }
    }

    internal static string SafeLogPreview(string text)
    {
        var buffer = new StringBuilder(Math.Min(text.Length, MaxLogPreviewChars));
        int lines = 0;
        bool truncated = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') continue;
                c = '\n';
            }
            if (buffer.Length >= MaxLogPreviewChars ||
                (c == '\n' && lines >= MaxLogPreviewLines))
            {
                truncated = true;
                break;
            }
            if (c == '\n') lines++;
            // Avoid terminal escape sequences, carriage-return overwrites,
            // Unicode direction overrides and other formatting controls.
            if ((char.IsControl(c) && c is not ('\n' or '\t')) ||
                char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ||
                c is '\u2028' or '\u2029')
                c = '?';
            buffer.Append(c);
        }
        if (truncated)
            buffer.Append("\n[Ausgabe gekürzt; vollständige Diagnose in der Logdatei]");
        return buffer.ToString();
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

    // Reentry must independently confirm absence, not merely trust the old
    // rm result. This is a read-only command; never create, exec or remove.
    internal static async Task RequireRemovedAsync(string runId)
    {
        string name = RecoverySandboxName(runId);
        SandboxProcess result = await SbxAsync(TimeSpan.FromSeconds(30), "inspect", name);
        if (!IsConfirmedAbsent(runId, result.ExitCode, result.Output, result.Error))
            throw new InvalidOperationException("Recovery sandbox absence not confirmed.");
    }

    // The caller supplies an already authenticated historical descriptor
    // under the exclusive order lease. This adapter cannot adopt arbitrary
    // report files, create a VM, or append an event.
    internal static Task<OrderGuestOutcome> RequireProtectedGuestAsync(
        DevelopmentJobStateStore.Lease owner, OrderGuestOutcome recorded) =>
        RequireProtectedGuestCoreAsync(owner, recorded, SbxAsync);

    internal static async Task<OrderGuestOutcome> RequireProtectedGuestCoreAsync(
        DevelopmentJobStateStore.Lease owner, OrderGuestOutcome recorded,
        Func<TimeSpan, string[], Task<SandboxProcess>> transport)
    {
        byte[] bytes = owner.RequireOrderGuestReport(recorded, recorded.Reservation);
        string runId = DevelopmentOrderGuestReconciliation.RunId(recorded.Reservation);
        SandboxProcess result = await transport(TimeSpan.FromSeconds(30), ["inspect", RecoverySandboxName(runId)]);
        // Recheck the private manifest/report after the external observation.
        // Report identity must not change across the await.
        byte[] readback = owner.RequireOrderGuestReport(recorded, recorded.Reservation);
        OrderCheck.Require(bytes.AsSpan().SequenceEqual(readback), "ORDER_GUEST_REPORT_CHANGED");
        var observed = DevelopmentOrderGuestReconciliation.Observe(recorded.ArchiveHash, recorded.Reservation,
            readback, result.ExitCode, result.Output, result.Error, DateTime.UtcNow.Ticks);
        DevelopmentOrderGuestReconciliation.RequireObserved(recorded, observed);
        return observed ?? throw new InvalidOperationException("ORDER_GUEST_RECONCILIATION");
    }

    private static string RecoverySandboxName(string runId)
    {
        if (!DevelopmentPublicationPolicy.IsSha(runId, 32) || runId != runId.ToLowerInvariant())
            throw new InvalidOperationException("Recovery sandbox run identity rejected.");
        return "hami-code-" + runId[..12];
    }

    internal static bool IsConfirmedAbsent(string runId, int exitCode, string output, string error)
    {
        string name = RecoverySandboxName(runId);
        if (exitCode == 0 || output.Length >= MaxOutputChars || error.Length >= MaxOutputChars)
            return false;
        // Observed on HaMi11: sbx reports the exact sandbox name followed
        // by 'not found', with optional wrapping and the fixed CLI hint.
        // Reject other errors (backend unavailable, permission, timeout).
        string diagnostic = (output + "\n" + error).Trim();
        return Regex.IsMatch(diagnostic, @"\Aerror:\s+sandbox\s+'" + Regex.Escape(name)
            + @"'\s+not\s+found(?:\s+try:\s+sbx\s+ls)?\s*\z",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

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

    private static void ValidateGuestCredentialPreflight(SandboxProcess result)
    {
        if (result.ExitCode != 0
            || !string.Equals(result.Output.Trim(), "CREDENTIAL_ENV=clean", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(result.Error))
        {
            // Deliberately never include stderr or arbitrary command output:
            // a modified guest can return text containing credentials.
            string names = string.Join(", ", SensitiveEnvNames.Where(name =>
                result.Output.Contains("NONEMPTY_CREDENTIAL_ENV=" + name, StringComparison.Ordinal)));
            throw new InvalidOperationException(
                "Guest credential environment rejected (nonempty or invalid preflight)" +
                (names.Length == 0 ? "." : ": " + names));
        }
    }

    private static void EnsureSuccess(SandboxProcess result, string action)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{action} failed ({result.ExitCode}): {result.Error.Trim()}");
    }

    private static void ScrubHostCredentials(ProcessStartInfo info)
    {
        DevelopmentChildEnvironment.RestrictSandboxProcess(info);
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
        ScrubHostCredentials(info);
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

        string sanitized = SafeLogPreview("Hello\r\n\u001b[31mFAIL\u001b[0m\roverride\u202e");
        if (sanitized.Contains('\u001b') || sanitized.Contains('\r')
            || sanitized.Contains('\u202e')
            || !sanitized.Contains("Hello\n?[31mFAIL?[0m\noverride?", StringComparison.Ordinal))
            throw new InvalidOperationException("Untrusted log control characters were not neutralized.");
        string oversized = SafeLogPreview(new string('x', MaxLogPreviewChars + 10));
        if (!oversized.Contains("[Ausgabe gekürzt;", StringComparison.Ordinal)
            || oversized.Length > MaxLogPreviewChars + 100)
            throw new InvalidOperationException("Log preview character limit is not enforced.");
        string tooManyLines = SafeLogPreview(string.Concat(Enumerable.Repeat("line\n", MaxLogPreviewLines + 3)));
        if (!tooManyLines.Contains("[Ausgabe gekürzt;", StringComparison.Ordinal))
            throw new InvalidOperationException("Log preview line limit is not enforced.");

        var scrubTest = new ProcessStartInfo("sbx");
        foreach (string name in SensitiveEnvNames)
            scrubTest.Environment[name] = "DUMMY-CI-VALUE";
        scrubTest.Environment["HAMI_UNKNOWN_FUTURE_SECRET"] = "DUMMY-CI-VALUE";
        scrubTest.Environment["PATH"] = "dummy-path";
        ScrubHostCredentials(scrubTest);
        if (SensitiveEnvNames.Any(scrubTest.Environment.ContainsKey)
            || scrubTest.Environment.ContainsKey("HAMI_UNKNOWN_FUTURE_SECRET")
            || scrubTest.Environment["PATH"] != "dummy-path")
            throw new InvalidOperationException("Sensitive host variables were not removed from sbx environment.");

        ValidateGuestCredentialPreflight(new SandboxProcess(0, "CREDENTIAL_ENV=clean\n", ""));
        ExpectRejected(() => ValidateGuestCredentialPreflight(
            new SandboxProcess(19, "NONEMPTY_CREDENTIAL_ENV=GH_TOKEN\n", "")));
        ExpectRejected(() => ValidateGuestCredentialPreflight(
            new SandboxProcess(0, "CREDENTIAL_ENV=clean\n", "unexpected stderr")));

        ValidateDeny(new SandboxProcess(1,
            "Denied: api.github.com:443\nReason: no matching allow rule (default deny)", ""),
            "api.github.com:443");
        ExpectRejected(() => ValidateDeny(new SandboxProcess(0,
            "Denied: api.github.com:443\nReason: no matching allow rule (default deny)", ""),
            "api.github.com:443"));
        ExpectRejected(() => ValidateDeny(new SandboxProcess(1, "Allowed: api.github.com:443", ""),
            "api.github.com:443"));
        ReservedRunnerSelfTestAsync().GetAwaiter().GetResult();
    }

    private static async Task ReservedRunnerSelfTestAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "hami-reserved-runner-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(root, "repo");
        string source = Path.Combine(root, "source");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(source);
        try
        {
            File.WriteAllText(Path.Combine(source, "Demo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
            File.WriteAllText(Path.Combine(source, "Program.cs"), "Console.WriteLine(42);");
            long now = DateTime.UtcNow.Ticks;
            string snapshotHash = DevelopmentOrderSnapshot.Capture(source, "Demo.csproj");
            var reservation = new OrderOperationReservation(4, new string('a', 32) + ":1",
                "S01", "guest", 30, now, null, snapshotHash);
            DevelopmentOrderSnapshot.SelfTest(source);
            string rejectedResults = Path.Combine(root, "wrong-snapshot");
            Directory.CreateDirectory(rejectedResults);
            bool snapshotRejected = false;
            try
            {
                _ = await RunCoreAsync(repo, source, "Demo.csproj", "run", rejectedResults,
                    (_, _, _, _) => { }, reservation with { SnapshotHash = new string('F', 64) },
                    (_, _) => throw new InvalidOperationException("Unexpected transport invocation."));
            }
            catch (InvalidOperationException ex) when (ex.Message == "ORDER_SNAPSHOT_MISMATCH") { snapshotRejected = true; }
            if (!snapshotRejected || Directory.EnumerateFileSystemEntries(rejectedResults).Any())
                throw new InvalidOperationException("Wrong snapshot reached dispatch.");
            string id = DevelopmentOrderGuestReconciliation.RunId(reservation);
            string name = RecoverySandboxName(id);
            string absent = "error: sandbox '" + name + "' not found";
            string healthy = string.Join('\n', new[]
            {
                "Name: " + name, "Agent: shell", "Workspace: -", "Clone source: -",
                "Configured host mounts: none", "Secrets: none", "MCP mode: static",
                "MCP servers: none", "Ports: none published", "Network policy: global (local policy)",
                "Auth mode: not configured", "State: running"
            });

            string[] expectedRecords = DevelopmentOrderSnapshot.RequireGuestRecords(source, "Demo.csproj", snapshotHash);
            string guestOutput = string.Join('\0', expectedRecords.Reverse()) + "\0"
                + DevelopmentOrderSnapshot.GuestEndMarker + "\0";
            foreach (string scenario in new[]
            {
                "existing", "backend", "ambiguous-create", "guest-modified", "guest-extra",
                "guest-missing", "guest-link", "guest-truncated", "guest-stderr", "success"
            })
            {
                string results = Path.Combine(root, scenario);
                Directory.CreateDirectory(results);
                var calls = new List<string[]>();
                Task<SandboxProcess> FakeTransport(TimeSpan _, string[] args)
                {
                    calls.Add(args);
                    SandboxProcess response = new(0, "", "");
                    switch (args[0])
                    {
                        case "inspect":
                            response = calls.Count == 1
                                ? scenario == "existing" ? new(0, healthy, "")
                                    : scenario == "backend" ? new(1, "", "backend unavailable")
                                    : new(1, "", absent)
                                : new(0, healthy, "");
                            break;
                        case "create":
                            if (scenario == "ambiguous-create")
                                response = new(1, "", "unconfirmed create");
                            break;
                        case "policy":
                            response = new(1, "Denied: " + args[^1] + " default deny", "");
                            break;
                        case "exec":
                            if (args.Contains(GuestEnvCheckScript, StringComparer.Ordinal))
                                response = new(0, "CREDENTIAL_ENV=clean", "");
                            else if (args.Contains("--version", StringComparer.Ordinal))
                                response = new(0, "11.0.100", "");
                            else if (args.Contains(GuestSnapshotCheckScript, StringComparer.Ordinal))
                            {
                                string observed = scenario switch
                                {
                                    "guest-modified" => guestOutput.Replace(expectedRecords[0],
                                        new string('f', 64) + expectedRecords[0][64..], StringComparison.Ordinal),
                                    "guest-extra" => expectedRecords[0] + "\0" + guestOutput,
                                    "guest-missing" => guestOutput.Replace(expectedRecords[0] + "\0", "", StringComparison.Ordinal),
                                    "guest-link" => "INVALID\0" + guestOutput,
                                    "guest-truncated" => guestOutput[..^1],
                                    _ => guestOutput
                                };
                                response = new(0, observed, scenario == "guest-stderr" ? "unexpected stderr" : "");
                            }
                            break;
                    }
                    return Task.FromResult(response);
                }

                int exit = await RunCoreAsync(repo, source, "Demo.csproj", "run", results,
                    (_, _, _, _) => { }, reservation, FakeTransport);
                bool success = scenario == "success";
                bool guestChecked = success || scenario.StartsWith("guest-", StringComparison.Ordinal);
                int creates = calls.Count(a => a[0] == "create");
                int removes = calls.Count(a => a[0] == "rm");
                int checkIndex = calls.FindIndex(a => a.Contains(GuestSnapshotCheckScript, StringComparer.Ordinal));
                int restoreIndex = calls.FindIndex(a => a.Contains("restore", StringComparer.Ordinal));
                if ((exit == 0) != success || creates != (guestChecked || scenario == "ambiguous-create" ? 1 : 0)
                    || removes != (guestChecked ? 1 : 0)
                    || (checkIndex >= 0) != guestChecked
                    || (success ? restoreIndex <= checkIndex : restoreIndex != -1)
                    || calls[0][0] != "inspect" || calls[0][1] != name
                    || calls.Any(a => a[0] == "create" && a[2] != name)
                    || calls.Any(a => a[0] == "rm" && a[^1] != name))
                    throw new InvalidOperationException("Reserved runner dispatch/cleanup differed.");

                string reportPath = Path.Combine(results, "reports", "hami-code-" + id, "report.json");
                byte[] bytes = File.ReadAllBytes(reportPath);
                var report = JsonSerializer.Deserialize<SandboxRunReport>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidOperationException("Reserved runner report missing.");
                if (report.SchemaVersion != 3 || report.RunId != id
                    || report.SnapshotHash != (success ? snapshotHash : null)
                    || report.SandboxRemovalSucceeded != guestChecked)
                    throw new InvalidOperationException("Reserved report identity or cleanup differs.");
                var observed = DevelopmentOrderGuestReconciliation.Observe(new string('A', 64),
                    reservation, bytes, 1, "", absent, DateTime.UtcNow.Ticks);
                if ((observed is not null) != success)
                    throw new InvalidOperationException("Reserved report reconciliation differs.");

                // Remove test report/staging to exercise the durable one-shot
                // slot itself; never a repair action in the real path.
                Directory.Delete(Path.GetDirectoryName(reportPath)!, recursive: true);
                int before = calls.Count;
                bool rejected = false;
                try
                {
                    _ = await RunCoreAsync(repo, source, "Demo.csproj", "run", results,
                        (_, _, _, _) => { }, reservation, FakeTransport);
                }
                catch (IOException) { rejected = true; }
                if (!rejected || calls.Count != before)
                    throw new InvalidOperationException("Reserved run was dispatched twice.");
            }
            Console.WriteLine("PASS: reserved sandbox runner uses pre-bound identity, rejects collisions and ambiguous create, retains one-shot slot.");
            Console.WriteLine("PASS: guest snapshot observation precedes restore; changed/extra/missing/nonregular/truncated/diagnostic evidence blocks execution.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ExpectRejected(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe input was accepted.");
    }

    internal sealed record SandboxProcess(int ExitCode, string Output, string Error);
}
