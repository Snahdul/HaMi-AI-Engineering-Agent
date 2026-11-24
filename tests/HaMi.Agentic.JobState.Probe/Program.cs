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

        if (args.Length == 2 && args[0] is "hold" or "probe")
        {
            string root = args[1];
            string name = Path.GetFileName(root);
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(name[Prefix.Length..], "N", out _)
                || Path.Combine(Path.GetTempPath(), name) != root)
                throw new InvalidOperationException("Probe root rejected.");
            var store = new DevelopmentJobStateStore(root);
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
            return 0;
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
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

    private static async Task ExpectProbeAsync(string root, string expected)
    {
        using var child = Start("probe", root);
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
