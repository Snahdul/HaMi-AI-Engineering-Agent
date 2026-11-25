using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Akka.Actor;
using Akka.Hosting;
using Akka.Pattern;
using Akka.Persistence;
using Akka.Persistence.Sql.Hosting;
using HaMi.Agentic.Cli.Features.Development;
using LinqToDB;
using LinqToDB.Common;
using LinqToDB.Interceptors;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Disposable offline experiment, not an execution path or production journal.
internal static class PersistenceBridgeProbe
{
    internal const int Issue = 900001;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private static readonly byte[] AcceptedBytes = Encoding.UTF8.GetBytes("synthetic accepted S01");
    private static readonly string AcceptedHash = DevelopmentJobState.Hash(AcceptedBytes);
    private static readonly byte[] PartialBytes = Encoding.UTF8.GetBytes("internal static class PartialCalculator {");
    private static readonly string PartialHash = DevelopmentJobState.Hash(PartialBytes);

    // Controlled host observation, separate from the synthetic generator.
    // This is not an independent real model/guest verifier.
    private static OrderOperationCompletion ObserveFixtureOutcome(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderClaim claim, OrderOperationReservation operation, string outputHash)
    {
        byte[] actual = owner.RequireOrderArtifact(operation.Step, outputHash);
        if (!actual.SequenceEqual(AcceptedBytes))
            throw new InvalidOperationException("Fixture output differs from trusted reference.");
        string baseline = operation.Step == "S01" ? claim.Binding.CalculatorBaselineHash : claim.Binding.FormatterBaselineHash;
        string inputHash = operation.Step == "S01" ? baseline : DevelopmentJobState.Hash(owner.RequireOrderArtifact("S01", AcceptedHash));
        var evidence = new OrderOperationEvidence(1, claim.ArchiveHash, operation, OrderOperationEvidence.FixtureObserver,
            "succeeded", baseline, inputHash, outputHash, DateTime.UtcNow.Ticks);
        var completion = owner.StoreOrderOperationEvidence(evidence);
        Reject(() => owner.StoreOrderOperationEvidence(evidence));
        return completion;
    }

    private static void VerifyFixtureOutcome(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderClaim claim, RecordedOperation recorded)
    {
        var evidence = owner.RequireOrderOperationEvidence(recorded.Reservation, recorded.Completion);
        string expectedInput = recorded.Reservation.Step == "S01"
            ? claim.Binding.CalculatorBaselineHash
            : DevelopmentJobState.Hash(owner.RequireOrderArtifact("S01", AcceptedHash));
        if (evidence.Outcome != "succeeded" || evidence.InputHash != expectedInput
            || evidence.OutputHash != AcceptedHash
            || !owner.RequireOrderArtifact(recorded.Reservation.Step, AcceptedHash).SequenceEqual(AcceptedBytes))
            throw new InvalidOperationException("Fixture observation differs from trusted inputs/output.");
    }

    private static void VerifyFixtureFailure(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderClaim claim, RecordedOperation recorded, OrderCandidateCheckpoint candidate)
    {
        var evidence = owner.RequireOrderOperationEvidence(recorded.Reservation, recorded.Completion);
        byte[] rejected = owner.RequireOrderCandidate(candidate);
        if (evidence.Outcome != "failed" || evidence.OutputHash is not null || recorded.Reservation.Step != "S01"
            || candidate.OperationId != recorded.Reservation.OperationId || candidate.ArchiveHash != claim.ArchiveHash
            || !rejected.SequenceEqual(PartialBytes) || rejected.SequenceEqual(AcceptedBytes))
            throw new InvalidOperationException("Fixture failure differs from trusted rejection.");
    }

    internal static async Task RunAsync(string prefix, Func<string, string, Process> start,
        Func<string, string, Task> expectOwnership)
    {
        foreach (string scenario in new[] { "accepted", "unknown", "failed" })
        {
            bool unknown = scenario == "unknown";
            bool failed = scenario == "failed";
            string root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            var store = DevelopmentJobStateRoot.OpenOrderProbeFixture(root, create: true);
            try
            {
                using (Process child = start(unknown ? "bridge-unknown" : failed ? "bridge-failed" : "bridge-hold", root))
                {
                    Task<string> stderr = child.StandardError.ReadToEndAsync();
                    var startup = Stopwatch.StartNew();
                    try
                    {
                        await WaitForMarkerAsync(child, unknown ? "BRIDGE_RESERVED" : failed ? "BRIDGE_FAILED" : "BRIDGE_ACCEPTED").WaitAsync(Deadline);
                        Console.WriteLine($"Bridge fixture startup/acceptance: {startup.ElapsedMilliseconds} ms.");
                        if (child.HasExited)
                            throw new InvalidOperationException("Bridge owner stopped before the crash test.");
                        await expectOwnership(root, "BUSY");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Bridge startup failed after {startup.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}");
                        throw;
                    }
                    finally
                    {
                        if (!child.HasExited)
                            child.Kill(entireProcessTree: true);
                        await child.WaitForExitAsync().WaitAsync(Deadline);
                        string diagnostics = await stderr.WaitAsync(Deadline);
                        if (diagnostics.Length > 0)
                            Console.Error.WriteLine(diagnostics[^Math.Min(diagnostics.Length, 16 * 1024)..]);
                    }

                    if (child.ExitCode == 0)
                        throw new InvalidOperationException("Bridge owner did not terminate abnormally.");
                }

                string artifact = Path.Combine(root, $"issue-{Issue}.order.db.s01");
                using (var owner = store.AcquireOrder(Issue))
                {
                    _ = owner.RequireArchivedOrder();
                    string journal = owner.RequireOrderJournal();
                    if (unknown || failed)
                    {
                        // The child has journaled the descriptor; the reentry
                        // child must independently recover and validate it.
                        string partial = journal + ".candidate-1";
                        if (!File.ReadAllBytes(partial).SequenceEqual(PartialBytes))
                            throw new InvalidOperationException("Partial candidate not retained.");
                    }
                    Reject(() => owner.CreateOrderJournal());
                    Reject(() => store.Acquire(Issue).Dispose());
                    if (!unknown && !failed && !File.Exists(journal + "-journal"))
                        throw new InvalidOperationException("Crash did not retain the live rollback sidecar.");
                    if (!unknown && !failed)
                    {
                        byte[] original = owner.RequireOrderArtifact("S01", AcceptedHash);
                        File.WriteAllBytes(artifact, [.. original, 0x20]);
                        Reject(() => owner.RequireOrderArtifact("S01", AcceptedHash));
                        File.WriteAllBytes(artifact, original);
                        File.Move(artifact, artifact + ".saved");
                        try { Reject(() => owner.RequireOrderArtifact("S01", AcceptedHash)); }
                        finally { File.Move(artifact + ".saved", artifact); }
                        Reject(() => owner.StoreOrderArtifact("S01", original));
                    }
                    // Disposable fault injection only: no recovery path repairs
                    // or recreates missing/empty databases.
                    File.Move(journal, journal + ".saved");
                    try
                    {
                        Reject(() => owner.RequireOrderJournal());
                        Reject(() => owner.CreateOrderJournal());
                        using (File.Create(journal)) { }
                        Reject(() => owner.RequireOrderJournal());
                        File.Delete(journal);
                    }
                    finally
                    {
                        File.Move(journal + ".saved", journal);
                    }
                }
                await expectOwnership(root, "ACQUIRED:order");

                using Process resumed = start(unknown ? "bridge-blocked" : failed ? "bridge-retry" : "bridge-resume", root);
                Task<string> output = resumed.StandardOutput.ReadToEndAsync();
                Task<string> errors = resumed.StandardError.ReadToEndAsync();
                try
                {
                    await resumed.WaitForExitAsync().WaitAsync(Deadline);
                    string result = await output;
                    foreach (string line in result.Split('\n').Where(line =>
                        line.StartsWith("BRIDGE_PHASE:", StringComparison.Ordinal)))
                        Console.WriteLine("[bridge reentry] " + line.TrimEnd());
                    if (resumed.ExitCode != 0 || !result.Split('\n').Any(line =>
                        line.Trim() == (unknown ? "BRIDGE_BLOCKED" : failed ? "BRIDGE_RETRIED" : "BRIDGE_COMPLETED")))
                        throw new InvalidOperationException("Bridge reentry failed: " + result + await errors);

                    string s02 = Path.Combine(root, $"issue-{Issue}.order.db.s02");
                    string candidatePath = Path.Combine(root, $"issue-{Issue}.order.db.candidate-1");
                    if (unknown ? File.Exists(artifact) || File.Exists(s02)
                        || !File.ReadAllBytes(candidatePath).SequenceEqual(PartialBytes)
                        : failed ? !File.ReadAllBytes(artifact).SequenceEqual(AcceptedBytes) || File.Exists(s02)
                        || !File.ReadAllBytes(candidatePath).SequenceEqual(PartialBytes)
                        || !File.Exists(Path.Combine(root, $"issue-{Issue}.order.db.evidence-1"))
                        || !File.Exists(Path.Combine(root, $"issue-{Issue}.order.db.evidence-2"))
                        : !File.ReadAllBytes(artifact).SequenceEqual(AcceptedBytes)
                        || !File.ReadAllBytes(s02).SequenceEqual(AcceptedBytes))
                        throw new InvalidOperationException("Bridge external effect observation differs.");
                }
                finally
                {
                    if (!resumed.HasExited)
                    {
                        resumed.Kill(entireProcessTree: true);
                        await resumed.WaitForExitAsync().WaitAsync(Deadline);
                    }
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        Console.WriteLine("PASS: Akka SQL journal survives process kill; ownership, accepted artifact reuse and unknown-effect block.");
        Console.WriteLine("PASS: protected artifacts, journal identity, guarded SQL commands and live rollback sidecar recovery.");
        Console.WriteLine("PASS: operation identity and consumed reservation budgets survive journal replay.");
        Console.WriteLine("PASS: original execution window survives process kill and reentry.");
        Console.WriteLine("PASS: journaled budget stop survives process kill; synthetic extension preserves accepted artifacts and charges.");
        Console.WriteLine("PASS: unaccepted source checkpoint survives process kill; pending model remains blocked.");
        Console.WriteLine("PASS: immutable operation outcome receipts survive process kill; missing, tampered and mismatched evidence rejected.");
        Console.WriteLine("PASS: known failed fixture operation survives process kill; verified receipt and retained candidate permit one newly charged retry.");
    }

    private static async Task WaitForMarkerAsync(Process child, string expected)
    {
        while (await child.StandardOutput.ReadLineAsync() is string line)
        {
            // The fixture is synthetic and has no credentials or live model
            // output. Keep child diagnostics visible even if readiness times out.
            Console.WriteLine("[bridge child] " + line);
            if (line == expected)
                return;
        }

        throw new InvalidOperationException("Bridge child stopped before " + expected);
    }

    internal static DevelopmentOrderApproval FixtureApproval()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HaMi.Probe.Order")
            ?? throw new InvalidOperationException("Order fixture missing.");
        using var reader = new StreamReader(stream);
        // Exercise the no-explicit-budget path across actual process kill.
        JsonObject node = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        node.Remove("limits");
        foreach (JsonNode? step in node["steps"]!.AsArray())
        {
            step!.AsObject().Remove("maxModelCalls");
            step.AsObject().Remove("maxSandboxRuns");
        }
        node["finalVerification"]!.AsObject().Remove("maxSandboxRuns");
        string plan = node.ToJsonString() + "\n";
        using Stream policyStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HaMi.Probe.BudgetPolicy")
            ?? throw new InvalidOperationException("Budget policy fixture missing.");
        using var buffer = new MemoryStream();
        policyStream.CopyTo(buffer);
        var policyNode = JsonNode.Parse(Encoding.UTF8.GetString(buffer.ToArray()))!.AsObject();
        // Only this disposable process fixture uses a tighter initial window.
        policyNode["defaults"]!["elapsedSeconds"] = 120;
        var policy = DevelopmentOrderBudgetPolicy.Parse(Encoding.UTF8.GetBytes(policyNode.ToJsonString()));
        byte[] body = Encoding.UTF8.GetBytes("\x60\x60\x60hami-order-json\n" + plan + "\x60\x60\x60\n");
        DevelopmentOrder order = DevelopmentOrder.ParseIssueBody(body);
        var document = order.Document;
        string repository = document.GetProperty("repository").GetString()!;
        string commit = document.GetProperty("baseCommit").GetString()!;
        string hash = DevelopmentJobState.Hash(Encoding.UTF8.GetBytes("synthetic review"));
        var profile = new OrderReviewedProfile(repository, 42, "fixture-owner", commit, hash, hash, policy.Sha256, hash, hash);
        DateTimeOffset time = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var comment = new OrderCommentObservation(1, 42, "fixture-owner",
            $"HaMi-Order-Approval: v1 1 {order.Sha256} {commit}", time, time);
        var observation = new OrderIssueObservation(repository, Issue, Issue,
            document.GetProperty("title").GetString()!, 42, "fixture-owner",
            true, true, body, time.AddMinutes(1), true, [comment]);
        return DevelopmentOrderApproval.Observe(observation, profile, policy);
    }

    internal static async Task<int> RunChildAsync(string mode, string root, DevelopmentJobStateStore.Lease owner)
    {
        long processStart = DateTime.UtcNow.Ticks;
        DevelopmentOrderClaim claim = owner.CurrentOrder
            ?? throw new InvalidOperationException("Order claim missing.");
        DevelopmentOrderArchive archive = owner.RequireArchivedOrder();
        // Recovery uses only the validated immutable archive, not today's policy.
        OrderOperationLimits limits = archive.EffectiveLimits;
        var policy = DevelopmentOrderBudgetPolicy.Parse(Encoding.UTF8.GetBytes(archive.BudgetPolicy));
        bool first = mode is "bridge-hold" or "bridge-unknown" or "bridge-failed";
        string database = first ? owner.CreateOrderJournal() : owner.RequireOrderJournal();

        string connection = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
        // The binding is immutable metadata, not a second workflow state.
        // Validate it before the Akka provider can initialize any tables.
        using (var binding = new SqliteConnection(connection))
        {
            binding.Open();
            CheckDurability(binding);
            CheckJournalIdentity(binding, claim, first);
            if (!first)
            {
                using var check = binding.CreateCommand();
                check.CommandText = "SELECT COUNT(*) FROM hami_crash_probe";
                if (check.ExecuteScalar() is not long count || count != 0)
                    throw new InvalidOperationException("Uncommitted fixture transaction survived recovery.");
                Reject(() => CheckJournalIdentity(binding, claim with { JobId = new string('f', 32) }, false));
            }
            using var version = binding.CreateCommand();
            version.CommandText = "SELECT sqlite_version()";
            if (version.ExecuteScalar() is not string nativeVersion || nativeVersion != "3.54.1")
                throw new InvalidOperationException("Unexpected native SQLite version; reviewed 3.54.1 required.");
            Console.WriteLine("SQLite native version: " + nativeVersion);
        }

        Console.WriteLine("BRIDGE_PHASE: identity validated; initializing provider.");
        var guard = new ProviderConnectionGuard();
        var options = new DataOptions()
            .UseConnectionString(ProviderName.SQLiteMS, connection)
            .UseInterceptor(new ProviderOpenInterceptor(guard))
            .UseInterceptor(guard);
        using IHost host = new HostBuilder()
            .ConfigureServices((_, services) => services.AddAkka("hami-persistence-probe", (builder, provider) =>
                builder.WithSqlPersistence(options)))
            .Build();
        await guard.AwaitAsync(host.StartAsync());
        try
        {
            var system = host.Services.GetRequiredService<ActorSystem>();
            IActorRef actor = system.ActorOf(Props.Create(() => new BridgeActor(claim, limits, policy, owner)), "bridge");
            async Task<BridgeState> ReadAsync()
            {
                Task<BridgeState> read = actor.Ask<BridgeState>(new ReadState(), Deadline);
                await guard.AwaitAsync(read);
                return await read;
            }
            async Task<BridgeState> AdvanceAsync(string evt, int stage)
            {
                Task<BridgeState> pending = actor.Ask<BridgeState>(new Advance(evt), Deadline);
                await guard.AwaitAsync(pending);
                BridgeState state = await pending;
                if (state.Stage != stage)
                    throw new InvalidOperationException("Persisted transition was not acknowledged.");
                return state;
            }

            Console.WriteLine("BRIDGE_PHASE: host started; awaiting replay.");
            var recovery = Stopwatch.StartNew();
            BridgeState current = await ReadAsync();
            guard.RequireObserved();
            Console.WriteLine($"BRIDGE_PHASE: replay state {current.Stage}; {recovery.ElapsedMilliseconds} ms.");
            string artifact = database + ".s01";
            if (!first)
            {
                long now = DateTime.UtcNow.Ticks;
                if (current.Ledger.StartedUtcTicks <= 0 || current.Ledger.StartedUtcTicks >= processStart
                    || current.Ledger.Remaining(limits, now) >= TimeSpan.FromSeconds(limits.Seconds)
                    || current.Ledger.LastUtcTicks < current.Ledger.StartedUtcTicks)
                    throw new InvalidOperationException("Persisted execution window not recovered.");
                Reject(() => current.Ledger.Remaining(limits, current.Ledger.LastUtcTicks - 1));
                Console.WriteLine($"BRIDGE_PHASE: remaining order window {current.Ledger.Remaining(limits, now).TotalMilliseconds:F0} ms.");
            }
            if (first)
            {
                if (current.Stage != 0 || File.Exists(artifact))
                    throw new InvalidOperationException("New journal is not empty.");
                current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeStart(
                    new(1, claim.ArchiveHash, processStart)), 1);
                Console.WriteLine("BRIDGE_PHASE: order accepted.");
                var reserved = current.Ledger.Propose(claim.JobId, "S01", "model", 30, DateTime.UtcNow.Ticks);
                current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeReservation(reserved), 2);
                if (current.Ledger.Pending != reserved || current.Ledger.Models != 1
                    || current.Ledger.ReservedSeconds != 30)
                    throw new InvalidOperationException("Reservation acknowledgement differs.");
                _ = current.Ledger.DispatchAllowance(limits, reserved.OperationId, DateTime.UtcNow.Ticks);
                Console.WriteLine("BRIDGE_PHASE: operation reserved.");
                if (mode is "bridge-unknown" or "bridge-failed")
                {
                    var candidate = new OrderCandidateCheckpoint(1, claim.ArchiveHash, reserved.OperationId,
                        "S01", DevelopmentAgentContract.PairFirst, claim.Binding.CalculatorBaselineHash,
                        claim.Binding.CalculatorBaselineHash, PartialBytes.Length, PartialHash, "Partial", DateTime.UtcNow.Ticks);
                    owner.StoreOrderCandidate(candidate, PartialBytes);
                    _ = owner.RequireOrderCandidate(candidate);
                    current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeCandidate(candidate), 2);
                    if (current.Ledger.LatestCandidate != candidate || current.Ledger.Pending != reserved
                        || current.Ledger.ReservedSeconds != 30 || File.Exists(artifact))
                        throw new InvalidOperationException("Candidate advanced unknown operation.");
                    Reject(() => owner.StoreOrderCandidate(candidate, PartialBytes));
                    if (mode == "bridge-failed")
                    {
                        // The controlled synthetic operation has returned an
                        // invalid result, not timed out. Independently check
                        // exact candidate bytes against the fixture reference.
                        byte[] rejected = owner.RequireOrderCandidate(candidate);
                        if (!rejected.SequenceEqual(PartialBytes) || rejected.SequenceEqual(AcceptedBytes))
                            throw new InvalidOperationException("Expected fixture rejection missing.");
                        var failure = new OrderOperationEvidence(1, claim.ArchiveHash, reserved,
                            OrderOperationEvidence.FixtureObserver, "failed", claim.Binding.CalculatorBaselineHash,
                            claim.Binding.CalculatorBaselineHash, null, DateTime.UtcNow.Ticks);
                        var completion = owner.StoreOrderOperationEvidence(failure);
                        current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeCompletion(completion), 1);
                        if (current.Ledger.Pending is not null || current.Ledger.LastOutcome != "failed"
                            || current.Ledger.Models != 1 || current.Ledger.ReservedSeconds != 30
                            || current.FailedS01?.Completion != completion || current.S01 is not null)
                            throw new InvalidOperationException("Known failure advanced or refunded work.");
                        Console.WriteLine("BRIDGE_FAILED");
                    }
                    else
                        Console.WriteLine("BRIDGE_RESERVED");
                }
                else
                {
                    string hash = owner.StoreOrderArtifact("S01", AcceptedBytes);
                    var completion = ObserveFixtureOutcome(owner, claim, reserved, hash);
                    current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeCompletion(completion), 3);
                    var denied = current.Ledger.Propose(claim.JobId, "S02", "model", 100, DateTime.UtcNow.Ticks);
                    current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeBudgetStop(new(1, denied, "time")), 3);
                    if (current.Ledger.BudgetStop?.Request != denied || current.Ledger.Sequence != 1
                        || File.Exists(database + ".s02"))
                        throw new InvalidOperationException("Budget stop changed consumed work.");
                    Console.WriteLine("BRIDGE_PHASE: S01 persisted; opening rollback transaction.");
                    // Hold an actual native rollback transaction across process kill.
                    // This table is a test fixture; Akka event rows are untouched.
                    using var crashConnection = new SqliteConnection(connection);
                    crashConnection.Open();
                    CheckDurability(crashConnection);
                    using var transaction = crashConnection.BeginTransaction();
                    using var pending = crashConnection.CreateCommand();
                    pending.Transaction = transaction;
                    pending.CommandText = "INSERT INTO hami_crash_probe(value) VALUES (1)";
                    pending.ExecuteNonQuery();
                    if (!File.Exists(database + "-journal"))
                        throw new InvalidOperationException("Live rollback sidecar missing.");
                    owner.ValidateOrderJournalFiles();
                    Console.WriteLine("BRIDGE_ACCEPTED");
                    Console.Out.Flush();
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                    return 1;
                }

                owner.ValidateOrderJournalFiles();
                Console.Out.Flush();
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 1;
            }

            if (mode == "bridge-blocked")
            {
                if (current.Stage != 2 || current.ArtifactHash is not null || File.Exists(artifact)
                    || current.Ledger.Pending is not { Step: "S01", Kind: "model", ReservedSeconds: 30 } pending
                    || pending.OperationId != claim.JobId + ":1" || current.Ledger.Sequence != 1
                    || current.Ledger.Models != 1 || current.Ledger.Guests != 0
                    || current.Ledger.ReservedSeconds != 30)
                    throw new InvalidOperationException("Unknown operation did not recover as reserved.");
                var candidate = current.Ledger.LatestCandidate
                    ?? throw new InvalidOperationException("Candidate checkpoint missing after replay.");
                if (candidate.Quality != "Partial" || candidate.OperationId != pending.OperationId
                    || candidate.ArtifactHash != PartialHash || !owner.RequireOrderCandidate(candidate).SequenceEqual(PartialBytes))
                    throw new InvalidOperationException("Candidate replay differs.");
                string partial = database + ".candidate-1";
                // Disposable fixture fault injection; runtime never repairs or
                // overwrites a referenced candidate.
                File.WriteAllBytes(partial, [.. PartialBytes, 0x20]);
                try { Reject(() => owner.RequireOrderCandidate(candidate)); }
                finally { File.WriteAllBytes(partial, PartialBytes); }
                File.Move(partial, partial + ".saved");
                try { Reject(() => owner.RequireOrderCandidate(candidate)); }
                finally { File.Move(partial + ".saved", partial); }
                Reject(() => owner.RequireOrderCandidate(candidate with { SourcePath = "../Other.cs" }));
                Reject(() => owner.StoreOrderCandidate(candidate, PartialBytes));
                _ = owner.RequireOrderCandidate(candidate);
                Console.WriteLine("BRIDGE_PHASE: unaccepted candidate retained; unknown model still pending.");
                // A candidate hash alone is not an outcome receipt.
                Reject(() => owner.RequireOrderOperationEvidence(pending,
                    new(2, pending.OperationId, PartialHash, "succeeded", DateTime.UtcNow.Ticks)));
                // Do not retry or release the reservation: outcome is unknown.
                Reject(() => current.Ledger.Reserve(claim.JobId, limits,
                    current.Ledger.Propose(claim.JobId, "S01", "model", 30, DateTime.UtcNow.Ticks)));
                Console.WriteLine("BRIDGE_BLOCKED");
                return 0;
            }

            if (mode == "bridge-retry")
            {
                var failed = current.FailedS01 ?? throw new InvalidOperationException("Failed operation missing.");
                var candidate = current.Ledger.LatestCandidate ?? throw new InvalidOperationException("Failed candidate missing.");
                if (current.Stage != 1 || current.Ledger.Pending is not null || current.Ledger.LastOutcome != "failed"
                    || current.Ledger.LastEvidenceHash != failed.Completion.EvidenceHash
                    || current.Ledger.Sequence != 1 || current.Ledger.Models != 1 || current.Ledger.ReservedSeconds != 30
                    || current.S01 is not null || current.S02 is not null || current.ArtifactHash is not null || File.Exists(artifact))
                    throw new InvalidOperationException("Known failure did not recover.");
                VerifyFixtureFailure(owner, claim, failed, candidate);
                string receipt = database + ".evidence-1";
                byte[] originalReceipt = File.ReadAllBytes(receipt);
                File.WriteAllBytes(receipt, [.. originalReceipt, 0x20]);
                try { Reject(() => VerifyFixtureFailure(owner, claim, failed, candidate)); }
                finally { File.WriteAllBytes(receipt, originalReceipt); }
                VerifyFixtureFailure(owner, claim, failed, candidate);

                long originalStart = current.Ledger.StartedUtcTicks;
                var retry = current.Ledger.Propose(claim.JobId, "S01", "model", 30, DateTime.UtcNow.Ticks);
                current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeReservation(retry), 2);
                if (retry.OperationId != claim.JobId + ":2" || current.Ledger.Models != 2
                    || current.Ledger.ReservedSeconds != 60 || current.Ledger.StartedUtcTicks != originalStart
                    || current.Ledger.LatestCandidate != candidate)
                    throw new InvalidOperationException("Retry reset original reservation or candidate.");
                Reject(() => owner.RequireOrderOperationEvidence(retry, failed.Completion));
                _ = current.Ledger.DispatchAllowance(limits, retry.OperationId, DateTime.UtcNow.Ticks);
                string hash = owner.StoreOrderArtifact("S01", AcceptedBytes);
                var succeeded = ObserveFixtureOutcome(owner, claim, retry, hash);
                current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeCompletion(succeeded), 3);
                VerifyFixtureOutcome(owner, claim, current.S01 ?? throw new InvalidOperationException("Retry outcome missing."));
                if (current.FailedS01 != failed || current.Ledger.Pending is not null || current.Ledger.Models != 2
                    || current.Ledger.ReservedSeconds != 60 || current.Ledger.LastOutcome != "succeeded")
                    throw new InvalidOperationException("Retry lost failed operation history.");
                // The old failure remains a distinct receipt even after success.
                _ = owner.RequireOrderOperationEvidence(failed.Reservation, failed.Completion);
                Reject(() => current.Ledger.Reserve(claim.JobId, limits,
                    current.Ledger.Propose(claim.JobId, "S01", "model", 1, DateTime.UtcNow.Ticks)));
                guard.RequireObserved();
                Console.WriteLine("BRIDGE_PHASE: verified failure; fresh retry consumed second S01 model reservation.");
                Console.WriteLine("BRIDGE_RETRIED");
                return 0;
            }

            if (mode != "bridge-resume" || current.Stage != 3 || current.ArtifactHash != AcceptedHash
                || current.Ledger.Pending is not null || current.Ledger.Sequence != 1
                || current.Ledger.Models != 1 || current.Ledger.Guests != 0
                || current.Ledger.ReservedSeconds != 30)
                throw new InvalidOperationException("Accepted step not recovered.");
            var recorded = current.S01 ?? throw new InvalidOperationException("Recorded S01 outcome missing.");
            if (current.S02 is not null || current.Ledger.LastEvidenceHash != recorded.Completion.EvidenceHash)
                throw new InvalidOperationException("Replayed outcome identity differs.");
            VerifyFixtureOutcome(owner, claim, recorded);
            string receipt = database + ".evidence-1";
            byte[] originalReceipt = File.ReadAllBytes(receipt);
            // Disposable fixture fault injection only; a real reentry must
            // stop on either failure and never repair evidence automatically.
            File.WriteAllBytes(receipt, [.. originalReceipt, 0x20]);
            try { Reject(() => VerifyFixtureOutcome(owner, claim, recorded)); }
            finally { File.WriteAllBytes(receipt, originalReceipt); }
            File.Move(receipt, receipt + ".saved");
            try { Reject(() => VerifyFixtureOutcome(owner, claim, recorded)); }
            finally { File.Move(receipt + ".saved", receipt); }
            Reject(() => owner.RequireOrderOperationEvidence(recorded.Reservation,
                recorded.Completion with { EvidenceHash = AcceptedHash }));
            Reject(() => owner.RequireOrderOperationEvidence(recorded.Reservation with { ReservedSeconds = 31 }, recorded.Completion));
            VerifyFixtureOutcome(owner, claim, recorded);
            Console.WriteLine("BRIDGE_PHASE: exact S01 outcome evidence and artifact verified before continuation.");
            byte[] input = owner.RequireOrderArtifact("S01", AcceptedHash);
            if (current.Ledger.BudgetStop is not { Reason: "time", Request: { Step: "S02", ReservedSeconds: 100 } }
                || current.Ledger.BudgetRevision != 0 || current.Ledger.CurrentLimits != limits)
                throw new InvalidOperationException("Persistent budget exhaustion missing.");
            Reject(() => current.Ledger.Reserve(claim.JobId, limits,
                current.Ledger.Propose(claim.JobId, "S02", "model", 1, DateTime.UtcNow.Ticks)));
            // Synthetic trusted decision only; no real approval transport.
            string decision = DevelopmentJobState.Hash(Encoding.UTF8.GetBytes("synthetic extension fixture"));
            var extension = new OrderBudgetExtensionApproved(1, claim.JobId, claim.ArchiveHash, 0,
                policy.Maximums, decision, DateTime.UtcNow.Ticks);
            long originalStart = current.Ledger.StartedUtcTicks;
            current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeBudgetExtension(extension), 3);
            if (current.Ledger.BudgetStop is not null || current.Ledger.BudgetRevision != 1
                || current.Ledger.StartedUtcTicks != originalStart || current.Ledger.Models != 1
                || current.Ledger.ReservedSeconds != 30)
                throw new InvalidOperationException("Budget extension reset prior work.");
            limits = current.Ledger.CurrentLimits ?? throw new InvalidOperationException("Effective budget missing.");
            Console.WriteLine("BRIDGE_PHASE: recovered budget stop; synthetic approved extension persisted.");

            // Reusing S01 is not a second write to its exclusive effect file.
            var next = current.Ledger.Propose(claim.JobId, "S02", "model", 100, DateTime.UtcNow.Ticks);
            current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeReservation(next), 4);
            if (current.Ledger.Pending != next || current.Ledger.Models != 2
                || current.Ledger.ReservedSeconds != 130)
                throw new InvalidOperationException("S02 reservation acknowledgement differs.");
            _ = current.Ledger.DispatchAllowance(limits, next.OperationId, DateTime.UtcNow.Ticks);
            string outputHash = owner.StoreOrderArtifact("S02", input);
            _ = owner.RequireOrderArtifact("S02", outputHash);
            var finalCompletion = ObserveFixtureOutcome(owner, claim, next, outputHash);
            current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeCompletion(finalCompletion), 5);
            VerifyFixtureOutcome(owner, claim, current.S02 ?? throw new InvalidOperationException("Recorded S02 outcome missing."));
            if (current.Ledger.Pending is not null || current.Ledger.Models != 2
                || current.Ledger.ReservedSeconds != 130)
                throw new InvalidOperationException("S02 completion refunded budget.");
            guard.RequireObserved();
            Console.WriteLine("BRIDGE_COMPLETED");
            return 0;
        }
        finally
        {
            await host.StopAsync().WaitAsync(Deadline);
            owner.ValidateOrderJournalFiles();
        }
    }

    private static void CheckDurability(DbConnection connection)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        if (command.ExecuteScalar() is not string mode || !mode.Equals("delete", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQLite rollback journal mode required.");
        command.CommandText = "PRAGMA synchronous=FULL";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA synchronous";
        if (command.ExecuteScalar() is not long value || value < 2)
            throw new InvalidOperationException("SQLite FULL synchronization required.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return;
        }
        throw new InvalidOperationException("Invalid journal or claim was accepted.");
    }

    private static void CheckJournalIdentity(SqliteConnection connection, DevelopmentOrderClaim claim, bool first)
    {
        if (first)
        {
            using var initialize = connection.CreateCommand();
            initialize.CommandText = "CREATE TABLE hami_order_identity (persistence_id TEXT NOT NULL, archive_hash TEXT NOT NULL); "
                + "CREATE TABLE hami_crash_probe (value INTEGER NOT NULL); "
                + "INSERT INTO hami_order_identity VALUES ($id, $hash)";
            initialize.Parameters.AddWithValue("$id", claim.PersistenceId);
            initialize.Parameters.AddWithValue("$hash", claim.ArchiveHash);
            initialize.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT persistence_id, archive_hash FROM hami_order_identity";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != claim.PersistenceId
            || reader.GetString(1) != claim.ArchiveHash || reader.Read())
            throw new InvalidOperationException("Journal identity differs from claim.");
    }

    // LinqToDB 6 creates commands before opening their connection.
    // Configure at connection-open, then guard the actual execution boundary.
    // Both interceptors are registered in DataOptions for provider clones.
    private sealed class ProviderOpenInterceptor(ProviderConnectionGuard guard) : ConnectionInterceptor
    {
        public override void ConnectionOpened(ConnectionEventData eventData, DbConnection connection) =>
            guard.Configure(connection);

        public override Task ConnectionOpenedAsync(ConnectionEventData eventData, DbConnection connection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            guard.Configure(connection);
            return Task.CompletedTask;
        }
    }

    private sealed class ProviderConnectionGuard : CommandInterceptor
    {
        private readonly ConditionalWeakTable<DbConnection, object> configured = new();
        private readonly TaskCompletionSource<Exception> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int commands;

        internal void Configure(DbConnection connection)
        {
            try
            {
                CheckDurability(connection);
                _ = configured.GetValue(connection, _ => new object());
            }
            catch (Exception ex)
            {
                Fail(ex);
                throw;
            }
        }

        private void Fail(Exception error)
        {
            if (failure.TrySetResult(error))
                Console.Error.WriteLine("BRIDGE_GUARD: " + error.Message);
        }

        private void RequireConfigured(DbCommand command)
        {
            if (command.Connection is not DbConnection connection
                || connection.State != ConnectionState.Open || !configured.TryGetValue(connection, out _))
            {
                var error = new InvalidOperationException("Provider attempted SQL execution without an open, configured connection.");
                Fail(error);
                throw error;
            }
            Interlocked.Increment(ref commands);
        }

        public override Option<int> ExecuteNonQuery(CommandEventData eventData, DbCommand command, Option<int> result)
        {
            RequireConfigured(command);
            return result;
        }

        public override Task<Option<int>> ExecuteNonQueryAsync(CommandEventData eventData, DbCommand command,
            Option<int> result, CancellationToken cancellationToken) =>
            Task.FromResult(ExecuteNonQuery(eventData, command, result));

        public override Option<object?> ExecuteScalar(CommandEventData eventData, DbCommand command, Option<object?> result)
        {
            RequireConfigured(command);
            return result;
        }

        public override Task<Option<object?>> ExecuteScalarAsync(CommandEventData eventData, DbCommand command,
            Option<object?> result, CancellationToken cancellationToken) =>
            Task.FromResult(ExecuteScalar(eventData, command, result));

        public override Option<DbDataReader> ExecuteReader(CommandEventData eventData, DbCommand command,
            CommandBehavior commandBehavior, Option<DbDataReader> result)
        {
            RequireConfigured(command);
            return result;
        }

        public override Task<Option<DbDataReader>> ExecuteReaderAsync(CommandEventData eventData, DbCommand command,
            CommandBehavior commandBehavior, Option<DbDataReader> result, CancellationToken cancellationToken) =>
            Task.FromResult(ExecuteReader(eventData, command, commandBehavior, result));

        internal async Task AwaitAsync(Task operation)
        {
            await Task.WhenAny(operation, failure.Task).WaitAsync(Deadline);
            if (failure.Task.IsCompletedSuccessfully)
                throw new InvalidOperationException("Provider durability guard rejected an operation.", await failure.Task);
            await operation;
        }

        internal void RequireObserved()
        {
            if (failure.Task.IsCompletedSuccessfully || Volatile.Read(ref commands) == 0)
                throw new InvalidOperationException("No valid guarded actual provider execution observed.");
        }
    }

    private sealed record ReadState;
    private sealed record Advance(string Event);
    private sealed record RecordedOperation(OrderOperationReservation Reservation, OrderOperationCompletion Completion);
    private sealed record BridgeState(int Stage, string? ArtifactHash, DevelopmentOrderOperationLedger Ledger,
        RecordedOperation? S01 = null, RecordedOperation? S02 = null, RecordedOperation? FailedS01 = null);

    private sealed class BridgeActor : ReceivePersistentActor
    {
        private BridgeState state = new(0, null, new());
        private bool recovered;
        private readonly DevelopmentOrderClaim claim;
        private readonly OrderOperationLimits limits;
        private readonly DevelopmentOrderBudgetPolicy policy;

        public override string PersistenceId { get; }

        public BridgeActor(DevelopmentOrderClaim claim, OrderOperationLimits limits, DevelopmentOrderBudgetPolicy policy,
            DevelopmentJobStateStore.Lease owner)
        {
            PersistenceId = claim.PersistenceId;
            this.claim = claim;
            this.limits = limits;
            this.policy = policy;
            // Recovery only folds acknowledged journal events; no effects.
            Recover<string>(evt => state = Next(state, evt));
            Recover<RecoveryCompleted>(_ => recovered = true);
            Command<ReadState>(_ => Sender.Tell(recovered ? state :
                throw new InvalidOperationException("Recovery incomplete.")));
            Command<Advance>(command =>
            {
                if (!recovered)
                    throw new InvalidOperationException("Recovery incomplete.");
                BridgeState next = Next(state, command.Event);
                // Only the live command boundary reads protected evidence.
                // Replay remains a pure fold and the host verifies recovered
                // receipts again before permitting any further work.
                if (command.Event.StartsWith("complete-v2:", StringComparison.Ordinal))
                {
                    if (next.Ledger.LastOutcome == "failed")
                    {
                        VerifyFixtureFailure(owner, claim,
                            next.FailedS01 ?? throw new InvalidOperationException("Failure missing."),
                            next.Ledger.LatestCandidate ?? throw new InvalidOperationException("Failed candidate missing."));
                    }
                    else
                    {
                        var observed = state.Stage == 2 ? next.S01 : next.S02;
                        VerifyFixtureOutcome(owner, claim, observed ?? throw new InvalidOperationException("Outcome missing."));
                    }
                }
                IActorRef reply = Sender;
                Persist(command.Event, _ =>
                {
                    state = next;
                    reply.Tell(state);
                });
            });
        }

        private BridgeState Next(BridgeState previous, string evt)
        {
            if (previous.Stage == 0)
                return previous with
                {
                    Stage = 1,
                    Ledger = previous.Ledger.Start(DevelopmentOrderOperationLedger.DecodeStart(evt), claim.ArchiveHash, limits, policy)
                };
            if (previous.Stage is 1 or 3)
            {
                var effective = previous.Ledger.CurrentLimits ?? throw new InvalidOperationException("Effective budget missing.");
                if (evt.StartsWith("budget-stop-v1:", StringComparison.Ordinal))
                {
                    var stop = DevelopmentOrderOperationLedger.DecodeBudgetStop(evt);
                    if (stop.Request.Step != (previous.Stage == 1 ? "S01" : "S02") || stop.Request.Kind != "model")
                        throw new InvalidOperationException("Unexpected synthetic stop.");
                    return previous with { Ledger = previous.Ledger.StopForBudget(claim.JobId, effective, stop) };
                }
                if (evt.StartsWith("budget-extension-v1:", StringComparison.Ordinal))
                    return previous with
                    {
                        Ledger = previous.Ledger.Extend(claim.JobId, policy,
                            DevelopmentOrderOperationLedger.DecodeBudgetExtension(evt))
                    };
                var reservation = DevelopmentOrderOperationLedger.DecodeReservation(evt);
                if (reservation.Step != (previous.Stage == 1 ? "S01" : "S02") || reservation.Kind != "model")
                    throw new InvalidOperationException("Unexpected synthetic step operation.");
                return previous with
                {
                    Stage = previous.Stage + 1,
                    Ledger = previous.Ledger.Reserve(claim.JobId, effective, reservation)
                };
            }
            if (previous.Stage is 2 or 4)
            {
                if (evt.StartsWith("candidate-v1:", StringComparison.Ordinal))
                    return previous with
                    {
                        Ledger = previous.Ledger.Checkpoint(claim, DevelopmentOrderOperationLedger.DecodeCandidate(evt))
                    };
                var completion = DevelopmentOrderOperationLedger.DecodeCompletion(evt);
                var reservation = previous.Ledger.Pending ?? throw new InvalidOperationException("Operation missing.");
                var recorded = new RecordedOperation(reservation, completion);
                if (completion.Outcome == "failed")
                {
                    if (previous.Stage != 2 || previous.FailedS01 is not null
                        || previous.Ledger.LatestCandidate?.OperationId != reservation.OperationId)
                        throw new InvalidOperationException("Unexpected fixture failure.");
                    return previous with
                    {
                        Stage = 1,
                        Ledger = previous.Ledger.Complete(completion),
                        FailedS01 = recorded
                    };
                }
                if (completion.Outcome != "succeeded")
                    throw new InvalidOperationException("Synthetic outcome mismatch.");
                return previous with
                {
                    Stage = previous.Stage + 1,
                    ArtifactHash = AcceptedHash,
                    Ledger = previous.Ledger.Complete(completion),
                    S01 = previous.Stage == 2 ? recorded : previous.S01,
                    S02 = previous.Stage == 4 ? recorded : previous.S02
                };
            }
            throw new InvalidOperationException("Bridge transition rejected.");
        }
    }
}
