using System.Diagnostics;
using System.Reflection;
using HaMi.Agentic.Cli.Features.Development;

internal static class Program
{
    private const int Issue = 62;
    private const string Prefix = "hami-job-probe-";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private static async Task<int> Main(string[] args)
    {
        // Explicit operator acceptance only. The default CI probe stays
        // entirely offline and never accesses the real account state root.
        if (args.Length == 5 && args[0] == "host-check")
        {
            string repo = Path.GetFullPath(args[1]);
            string results = Path.GetFullPath(args[2]);
            string[] runIds = args[3..];
            if (runIds.Any(id => !DevelopmentPublicationPolicy.IsSha(id, 32)
                || id != id.ToLowerInvariant())
                || runIds[0][..12] == runIds[1][..12])
                throw new InvalidOperationException("Two distinct historical sandbox identities required.");

            await DevelopmentIssueWorkflow.VerifyOriginAsync(repo);
            DevelopmentIsolatedRunner.ValidateStoragePaths(repo,
                Path.Combine(repo, "samples", "RetryBudget"), results);
            _ = DevelopmentJobStateRoot.Open(repo, results);
            Console.WriteLine("PASS: actual private account recovery root validated.");
            foreach (string id in runIds)
            {
                await DevelopmentSandboxRunner.RequireRemovedAsync(id);
                Console.WriteLine("PASS: read-only absence check hami-code-" + id[..12]);
            }
            Console.WriteLine("PASS: HaMi11 recovery host prerequisites; no claim, model, guest or GitHub write.");
            return 0;
        }

        if (args.Length == 2 && args[0] is "transport-parent" or "transport-leaf")
        {
            string root = args[1];
            string name = Path.GetFileName(root);
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(name[Prefix.Length..], "N", out _)
                || Path.Combine(Path.GetTempPath(), name) != root || !Directory.Exists(root))
                throw new InvalidOperationException("Transport probe root rejected.");

            if (args[0] == "transport-leaf")
            {
                Console.WriteLine("TRANSPORT_LEAF_READY");
                Console.Out.Flush();
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 1;
            }

            using var leaf = Start("transport-leaf", root);
            try
            {
                if (await leaf.StandardOutput.ReadLineAsync().WaitAsync(Deadline) != "TRANSPORT_LEAF_READY"
                    || leaf.HasExited)
                    throw new InvalidOperationException("Transport leaf did not become ready.");
                Console.WriteLine("TRANSPORT_READY:" + leaf.Id);
                Console.Out.Flush();
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 1;
            }
            finally
            {
                if (!leaf.HasExited)
                    leaf.Kill(entireProcessTree: true);
                await leaf.WaitForExitAsync().WaitAsync(Deadline);
            }
        }

        if (args.Length == 2 && args[0] is "backend-hold" or "backend-probe")
        {
            var backendStore = DevelopmentJobStateRoot.OpenOrderProbeFixture(args[1], create: false);
            FileStream backend;
            try { backend = backendStore.AcquireSandboxBackend(); }
            catch (IOException) when (args[0] == "backend-probe")
            {
                Console.WriteLine("BUSY");
                return 0;
            }
            using (backend)
            {
                Console.WriteLine("BACKEND_ACQUIRED");
                Console.Out.Flush();
                if (args[0] == "backend-probe")
                    return 0;
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 1;
            }
        }

        if (args.Length == 2 && args[0] is "hold" or "probe" or "bridge-hold" or "bridge-unknown" or "bridge-resume" or "bridge-blocked" or "bridge-failed" or "bridge-retry" or "bridge-guest" or "bridge-guest-resume" or "bridge-guest-unknown" or "bridge-guest-blocked" or "bridge-guest-orphan" or "bridge-guest-orphan-resume" or "bridge-probe"
            or "bridge-model-receipt" or "bridge-model-orphan" or "bridge-model-checkpoint"
            or "bridge-model-generated" or "bridge-model-rejected" or "bridge-model-rejected-orphan"
            or "bridge-model-resume-blocked" or "bridge-model-resume-generated" or "bridge-model-resume-rejected"
            or "bridge-soft-stop" or "bridge-soft-resume" or "bridge-model-accepted" or "bridge-model-resume-accepted"
            or "bridge-model-verified" or "bridge-model-resume-verified"
            or "bridge-model-resume-formatter-receipt" or "bridge-model-resume-formatter-orphan"
            or "bridge-model-resume-formatter-checkpoint" or "bridge-model-resume-formatter-rejected"
            or "bridge-model-resume-formatter-rejected-orphan"
            or "bridge-model-resume-formatter-budget-hard" or "bridge-model-resume-formatter-budget-soft"
            or "bridge-model-resume-final-pre-dispatch" or "bridge-model-resume-final-wrong-output"
            or "bridge-model-resume-final-orphan")
        {
            string root = args[1];
            string name = Path.GetFileName(root);
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(name[Prefix.Length..], "N", out _)
                || Path.Combine(Path.GetTempPath(), name) != root)
                throw new InvalidOperationException("Probe root rejected.");
            var store = new DevelopmentJobStateStore(root);
            if (args[0].StartsWith("bridge-", StringComparison.Ordinal))
            {
                var orderStore = DevelopmentJobStateRoot.OpenOrderProbeFixture(root, create: false);
                DevelopmentJobStateStore.Lease owner;
                try { owner = orderStore.AcquireOrder(PersistenceBridgeProbe.Issue); }
                catch (IOException) when (args[0] == "bridge-probe")
                {
                    Console.WriteLine("BUSY");
                    return 0;
                }
                using (owner)
                {
                    if (args[0] == "bridge-probe")
                    {
                        Console.WriteLine(owner.CurrentOrder is null ? "ACQUIRED:none" : "ACQUIRED:order");
                        return 0;
                    }
                    if (args[0] is "bridge-hold" or "bridge-unknown" or "bridge-failed" or "bridge-guest"
                        or "bridge-guest-unknown" or "bridge-guest-orphan"
                        or "bridge-model-receipt" or "bridge-model-orphan" or "bridge-model-checkpoint"
                        or "bridge-model-generated" or "bridge-model-rejected" or "bridge-model-rejected-orphan"
                        or "bridge-soft-stop" or "bridge-model-accepted" or "bridge-model-verified")
                        owner.ClaimOrder(Guid.NewGuid().ToString("N"), args[0] is "bridge-model-accepted" or "bridge-model-verified"
                            ? await PersistenceBridgeProbe.CalculatorRecoveryApprovalAsync(root) : PersistenceBridgeProbe.FixtureApproval());
                    _ = owner.RequireArchivedOrder();
                    return await PersistenceBridgeProbe.RunChildAsync(args[0], root, owner);
                }
            }

            if (args[0] == "probe")
            {
                DevelopmentJobStateStore.Lease owner;
                try { owner = store.Acquire(Issue); }
                catch (IOException)
                {
                    Console.WriteLine("BUSY");
                    return 0;
                }
                using (owner)
                    Console.WriteLine("ACQUIRED:" + (owner.Current?.Stage ?? "none"));
                return 0;
            }
            using var held = store.Acquire(Issue);
            held.Claim(Initial(root));
            held.Commit(held.Current!.Start());
            Console.WriteLine("CLAIMED");
            Console.Out.Flush();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 1;
        }

        if (args.Length != 0)
            return 2;
        string fixture = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var store = new DevelopmentJobStateStore(fixture);
            using (var child = Start("hold", fixture))
            {
                try
                {
                    string? ready = await child.StandardOutput.ReadLineAsync().WaitAsync(Deadline);
                    if (ready != "CLAIMED" || child.HasExited)
                        throw new InvalidOperationException("Owner subprocess did not claim.");
                    await ExpectProbeAsync(fixture, "BUSY");
                    Reject(() => store.Acquire(Issue).Dispose());
                }
                finally
                {
                    // Simulate abrupt process termination; no Dispose/cleanup
                    // from the owning process can release the durable claim.
                    if (!child.HasExited)
                        child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(Deadline);
                }
                if (child.ExitCode == 0)
                    throw new InvalidOperationException("Owner process did not terminate abnormally.");
            }

            await ExpectProbeAsync(fixture, "ACQUIRED:executing");
            using (var owner = store.Acquire(Issue))
            {
                if (owner.Current?.Stage != DevelopmentJobState.Executing
                    || owner.Current.Identity.JobId != Initial(fixture).Identity.JobId)
                    throw new InvalidOperationException("Crash lost the original claim.");
                Reject(() => owner.Claim(Initial(fixture)));
                owner.Commit(owner.Current.BlockUnknownExecution());
                await ExpectProbeAsync(fixture, "BUSY");
            }
            await ExpectProbeAsync(fixture, "ACQUIRED:recovery-blocked");
            Console.WriteLine("PASS: cross-process exclusive ownership and abrupt termination retain claim.");
            await RunReservedTransportProbeAsync(fixture);
            await RunBackendLeaseProbeAsync();
            await PersistenceBridgeProbe.RunAsync(Prefix, Start,
                (root, expected) => ExpectProbeAsync(root, expected, "bridge-probe"));
            return 0;
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static async Task RunReservedTransportProbeAsync(string root)
    {
        foreach (bool callerCancellation in new[] { false, true })
        {
            using var parent = Start("transport-parent", root);
            Process? leaf = null;
            try
            {
                const string marker = "TRANSPORT_READY:";
                string? ready = await parent.StandardOutput.ReadLineAsync().WaitAsync(Deadline);
                if (ready is null || !ready.StartsWith(marker, StringComparison.Ordinal)
                    || !int.TryParse(ready[marker.Length..], out int pid) || pid <= 0 || pid == parent.Id
                    || pid == Environment.ProcessId || parent.HasExited)
                    throw new InvalidOperationException("Transport parent did not become ready.");
                leaf = Process.GetProcessById(pid);
                if (NoLongerExecuting(leaf))
                    throw new InvalidOperationException("Transport leaf exited before cancellation.");

                using var caller = new CancellationTokenSource();
                using var safety = new DevelopmentOrderSafetyWindow(TimeSpan.FromSeconds(10));
                TimeSpan phaseLimit = callerCancellation ? Deadline : TimeSpan.FromMilliseconds(250);
                Task<DevelopmentSandboxRunner.SandboxProcess> operation =
                    DevelopmentSandboxRunner.ObserveReservedProcessForProbeAsync(parent, phaseLimit, caller.Token, safety);
                if (callerCancellation)
                    caller.Cancel();

                bool expected = false;
                try { _ = await operation.WaitAsync(Deadline); }
                catch (OperationCanceledException ex) when (callerCancellation && ex.CancellationToken == caller.Token)
                { expected = true; }
                catch (TimeoutException ex) when (!callerCancellation && ex.Message == "sbx operation timed out: offline-probe")
                { expected = true; }
                if (!expected || !parent.HasExited || parent.ExitCode == 0)
                    throw new InvalidOperationException("Reserved transport did not terminate with the expected outcome.");
                // The cleanup token remains independent of the cancelled
                // business token, and uses the already-started safety window.
                safety.Check();
                if (safety.Token.IsCancellationRequested)
                    throw new InvalidOperationException("Safety token inherited business cancellation.");

                // A killed Linux grandchild can briefly remain as a zombie
                // until the OS reaps it. A zombie cannot execute instructions.
                var observation = Stopwatch.StartNew();
                while (!NoLongerExecuting(leaf) && observation.Elapsed < TimeSpan.FromSeconds(5))
                    await Task.Delay(25);
                if (!NoLongerExecuting(leaf))
                    throw new InvalidOperationException("Controlled transport descendant still executing.");
                Console.WriteLine("PASS: reserved transport " + (callerCancellation ? "caller cancellation" : "phase timer")
                    + " terminated the controlled process and descendant.");
            }
            finally
            {
                // A failing regression must not leave the controlled fixture
                // running. This fallback does not count towards test success.
                try
                {
                    if (!parent.HasExited)
                        parent.Kill(entireProcessTree: true);
                    await parent.WaitForExitAsync().WaitAsync(Deadline);
                    if (leaf is not null && !NoLongerExecuting(leaf))
                    {
                        leaf.Kill(entireProcessTree: true);
                        var fallback = Stopwatch.StartNew();
                        while (!NoLongerExecuting(leaf) && fallback.Elapsed < Deadline)
                            await Task.Delay(25);
                        if (!NoLongerExecuting(leaf))
                            throw new InvalidOperationException("Transport fixture cleanup failed.");
                    }
                }
                finally
                {
                    leaf?.Dispose();
                }
            }
        }

        // Exercise the real safety timer as well as deterministic clock
        // exhaustion in the existing reserved runner self-tests.
        using var window = new DevelopmentOrderSafetyWindow(TimeSpan.FromSeconds(1));
        CancellationToken safetyToken = window.Token;
        bool expired = false;
        try { await Task.Delay(Timeout.InfiniteTimeSpan, safetyToken).WaitAsync(Deadline); }
        catch (OperationCanceledException ex) when (ex.CancellationToken == safetyToken) { expired = true; }
        if (!expired || !safetyToken.IsCancellationRequested)
            throw new InvalidOperationException("Safety timer did not cancel.");
        try
        {
            window.Check();
            throw new InvalidOperationException("Expired safety window restarted.");
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            Console.WriteLine("PASS: real safety timer cancels without restarting its window.");
        }
    }

    private static bool NoLongerExecuting(Process process)
    {
        if (process.HasExited)
            return true;
        if (!OperatingSystem.IsLinux())
            return false;
        try
        {
            string stat = File.ReadAllText("/proc/" + process.Id + "/stat");
            int nameEnd = stat.LastIndexOf(')');
            return nameEnd >= 0 && stat.Length > nameEnd + 2 && stat[nameEnd + 2] is 'Z' or 'X';
        }
        catch (FileNotFoundException) { return process.HasExited; }
        catch (DirectoryNotFoundException) { return process.HasExited; }
    }

    private static async Task RunBackendLeaseProbeAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        var store = DevelopmentJobStateRoot.OpenOrderProbeFixture(root, create: true);
        try
        {
            string path = Path.Combine(root, "sandbox-backend.lock");
            using (var child = Start("backend-hold", root))
            {
                try
                {
                    if (await child.StandardOutput.ReadLineAsync().WaitAsync(Deadline) != "BACKEND_ACQUIRED"
                        || child.HasExited)
                        throw new InvalidOperationException("Backend owner did not acquire.");
                    await ExpectProbeAsync(root, "BUSY", "backend-probe");
                    Reject(() => store.AcquireSandboxBackend().Dispose());
                    if (!File.Exists(path))
                        throw new InvalidOperationException("Backend lock missing.");
                }
                finally
                {
                    if (!child.HasExited)
                        child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(Deadline);
                }
                if (child.ExitCode == 0)
                    throw new InvalidOperationException("Backend owner did not terminate abnormally.");
            }

            // Do not unlink the lock to recover. The OS releases the handle.
            if (!File.Exists(path))
                throw new InvalidOperationException("Backend lock lost after crash.");
            await ExpectProbeAsync(root, "BACKEND_ACQUIRED", "backend-probe");
            using (var backend = store.AcquireSandboxBackend())
            {
                await ExpectProbeAsync(root, "BUSY", "backend-probe");
            }
            await ExpectProbeAsync(root, "BACKEND_ACQUIRED", "backend-probe");
            File.WriteAllText(path, "unexpected");
            Reject(() => store.AcquireSandboxBackend().Dispose());
            Reject(() => new DevelopmentJobStateStore(root).AcquireSandboxBackend().Dispose());
            Console.WriteLine("PASS: private account-wide sandbox backend lease excludes cooperating processes and releases on process kill without unlink.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static DevelopmentJobState Initial(string root)
    {
        string hash = new('A', 64);
        return DevelopmentJobState.Create(new DevelopmentJobIdentity(new string('a', 32),
            GitHubIssueGateway.Repository, Issue, new string('b', 40),
            hash, hash, hash, hash, Path.Combine(root, "results"),
            "offline-scripted", 2, DevelopmentJobState.PublicationLimit,
            "production-retry-budget", "main", true, false, false));
    }

    private static Process Start(string mode, string root)
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Test process executable unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // The dedicated test executable is not part of production CLI dispatch.
        // Preserve only host runtime location hints needed to launch a child.
        var runtime = start.Environment.Where(x => x.Key is
            "DOTNET_ROOT" or "DOTNET_ROOT_X64" or "DOTNET_ROOT_ARM64" or "DOTNET_ROOT(x86)")
            .ToArray();
        DevelopmentChildEnvironment.RestrictSandboxProcess(start);
        foreach (var item in runtime)
            start.Environment[item.Key] = item.Value;
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(root);
        return Process.Start(start) ?? throw new InvalidOperationException("Probe launch failed.");
    }

    private static async Task ExpectProbeAsync(string root, string expected, string mode = "probe")
    {
        using var child = Start(mode, root);
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(Deadline);
            if (child.ExitCode != 0 || (await stdout).Trim() != expected
                || (await stderr).Length != 0)
                throw new InvalidOperationException("Cross-process ownership outcome differs: " + expected);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(Deadline);
            }
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (IOException) { return; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Conflicting owner or claim was accepted.");
    }
}
