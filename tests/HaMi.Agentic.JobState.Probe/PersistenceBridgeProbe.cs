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
    private static readonly byte[] ModelBaseline = Encoding.UTF8.GetBytes("synthetic review");
    private const string ModelCandidate = "internal static class PairCalculator { /* generated fixture */ }";
    private const string ModelName = "fixture-model";

    private static OrderAssignment ModelFixtureAssignment(DevelopmentOrderClaim claim, DevelopmentOrderArchive archive)
    {
        using var fake = new DevelopmentAiGenerator.ScriptedChatClient("{}");
        var generator = new DevelopmentAiGenerator(fake, ModelName);
        var original = FixtureAssignment(claim, archive, "S01");
        return original with
        {
            Agent = original.Agent with
            {
                Provider = "openai",
                Model = generator.ModelId,
                ConfigurationHash = generator.S01ConfigurationHash
            }
        };
    }

    private static void VerifyModelFixtureAssignment(DevelopmentOrderClaim claim, DevelopmentOrderArchive archive,
        OrderOperationReservation reservation) =>
        (reservation.Assignment ?? throw new InvalidOperationException("Missing model assignment."))
            .RequireAssignment(ModelFixtureAssignment(claim, archive));

    // Fixed reviewed offline catalog; not runtime agent selection. Construct
    // the historical decision from immutable archived inputs, never from a
    // mutable current model profile during replay.
    private static OrderAssignment FixtureAssignment(DevelopmentOrderClaim claim,
        DevelopmentOrderArchive archive, string step)
    {
        if (step is not ("S01" or "S02"))
            throw new InvalidOperationException("Unknown fixture assignment step.");
        var agent = new OrderAgentIdentity("fixture-" + step, "1",
            DevelopmentJobState.Hash(Encoding.UTF8.GetBytes("fixture profile " + step)),
            "offline", "synthetic",
            DevelopmentJobState.Hash(Encoding.UTF8.GetBytes("fixture configuration " + step)));
        return new(claim.Binding, Guid.ParseExact(claim.JobId, "N"), step, 1, agent,
            "Reviewed offline C# fixture assignment.", archive.ObservedAt);
    }

    private static void VerifyFixtureAssignment(DevelopmentOrderClaim claim, DevelopmentOrderArchive archive,
        OrderOperationReservation operation)
    {
        (operation.Assignment ?? throw new InvalidOperationException("Missing model assignment."))
            .RequireAssignment(FixtureAssignment(claim, archive, operation.Step));
    }

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
        var evidence = new OrderOperationEvidence(2, claim.ArchiveHash, operation, OrderOperationEvidence.FixtureObserver,
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

    private static readonly Lazy<byte[]> FixtureSnapshotManifest = new(() =>
    {
        string source = Path.Combine(Path.GetTempPath(), "hami-order-snapshot-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        try
        {
            File.WriteAllText(Path.Combine(source, "Demo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
            File.WriteAllText(Path.Combine(source, "Program.cs"), "Console.WriteLine(42);");
            return DevelopmentOrderSnapshot.CaptureBytes(source, "Demo.csproj");
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    });
    private static string FixtureSnapshotHash => DevelopmentJobState.Hash(FixtureSnapshotManifest.Value);

    private static void VerifyFixtureSnapshot(DevelopmentJobStateStore.Lease owner, OrderOperationReservation reservation)
    {
        if (reservation.SnapshotHash != FixtureSnapshotHash
            || !owner.RequireOrderSnapshotManifest(reservation).SequenceEqual(FixtureSnapshotManifest.Value))
            throw new InvalidOperationException("Protected fixture snapshot differs.");
    }

    private static void TestFixtureSnapshotFaults(DevelopmentJobStateStore.Lease owner,
        OrderOperationReservation reservation, string database)
    {
        VerifyFixtureSnapshot(owner, reservation);
        string path = database + ".snapshot-1";
        byte[] original = owner.RequireOrderSnapshotManifest(reservation);
        File.WriteAllBytes(path, [.. original, 0x20]);
        try { Reject(() => VerifyFixtureSnapshot(owner, reservation)); }
        finally { File.WriteAllBytes(path, original); }
        File.Move(path, path + ".saved");
        try { Reject(() => VerifyFixtureSnapshot(owner, reservation)); }
        finally { File.Move(path + ".saved", path); }
        Reject(() => owner.RequireOrderSnapshotManifest(reservation with { SnapshotHash = new string('F', 64) }));
        Reject(() => owner.RequireOrderSnapshotManifest(reservation with { OperationId = new string('f', 32) + ":1" }));
        Reject(() => owner.StoreOrderSnapshotManifest(reservation, original));
        VerifyFixtureSnapshot(owner, reservation);
    }

    private static void TestGuestDispatchFaults(DevelopmentJobStateStore.Lease owner,
        OrderOperationReservation reservation, string database)
    {
        owner.RequireOrderGuestDispatch(reservation);
        string path = database + ".guest-dispatch-1";
        byte[] original = File.ReadAllBytes(path);
        File.WriteAllBytes(path, [.. original, 0x20]);
        try { Reject(() => owner.RequireOrderGuestDispatch(reservation)); }
        finally { File.WriteAllBytes(path, original); }
        File.Move(path, path + ".saved");
        try { Reject(() => owner.RequireOrderGuestDispatch(reservation)); }
        finally { File.Move(path + ".saved", path); }
        Reject(() => owner.RequireOrderGuestDispatch(reservation with { ReservedSeconds = 31 }));
        Reject(() => owner.RequireOrderGuestDispatch(reservation with { OperationId = new string('f', 32) + ":1" }));
        owner.RequireOrderGuestDispatch(reservation);
    }

    private static byte[] FixtureGuestReport(OrderOperationReservation reservation)
    {
        if (reservation.SnapshotHash != FixtureSnapshotHash)
            throw new InvalidOperationException("Fixture snapshot identity differs.");
        var report = new DevelopmentSandboxRunner.SandboxRunReport(4,
            DevelopmentOrderGuestReconciliation.RunId(reservation), "Demo.csproj", "run",
            0, 0, 0, false, 1, 1, 1, 1, 1, 1, 6, true, null, FixtureSnapshotHash);
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true });
    }

    // Controlled offline observation only. The real host must independently
    // inspect sbx and validate snapshot/report provenance before this boundary.
    private static OrderGuestOutcome? ObserveFixtureGuest(DevelopmentOrderClaim claim,
        OrderOperationReservation reservation, byte[] bytes)
    {
        string id = DevelopmentOrderGuestReconciliation.RunId(reservation);
        string absent = "error: sandbox 'hami-code-" + id[..12] + "' not found";
        return DevelopmentOrderGuestReconciliation.Observe(claim.ArchiveHash, reservation,
            bytes, 1, "", absent, DateTime.UtcNow.Ticks);
    }

    private static void VerifyFixtureGuest(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderClaim claim, OrderGuestOutcome outcome)
    {
        VerifyFixtureSnapshot(owner, outcome.Reservation);
        byte[] bytes = owner.RequireOrderGuestReport(outcome, outcome.Reservation);
        if (!bytes.SequenceEqual(FixtureGuestReport(outcome.Reservation)))
            throw new InvalidOperationException("Guest report differs from controlled fixture.");
        DevelopmentOrderGuestReconciliation.RequireObserved(outcome,
            ObserveFixtureGuest(claim, outcome.Reservation, bytes));
    }

    // Exercise the real read-only adapter with a controlled transport.
    // No actual sbx process is started in this disposable CI fixture.
    private static async Task TestProtectedGuestInspectionAsync(DevelopmentJobStateStore.Lease owner,
        OrderGuestOutcome recorded, string database)
    {
        string name = "hami-code-" + recorded.RunId[..12];
        string absent = "error: sandbox '" + name + "' not found";
        int calls = 0;
        async Task<OrderGuestOutcome> ObserveAsync(DevelopmentSandboxRunner.SandboxProcess result,
            Action? duringInspect = null)
        {
            var observations = await DevelopmentSandboxRunner.RequireProtectedGuestsCoreAsync(owner, [recorded],
                (limit, token, args, safety) =>
                {
                    if (limit != TimeSpan.FromSeconds(30) || !args.SequenceEqual(new[] { "inspect", name })
                        || !token.CanBeCanceled || safety is null)
                        throw new InvalidOperationException("Unexpected reconciliation command.");
                    calls++;
                    duringInspect?.Invoke();
                    return Task.FromResult(result);
                });
            return observations.Single();
        }

        async Task RejectObservationAsync(DevelopmentSandboxRunner.SandboxProcess result, Action? duringInspect = null)
        {
            try { _ = await ObserveAsync(result, duringInspect); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Unconfirmed guest inspection accepted.");
        }

        var fresh = await ObserveAsync(new(1, "", absent));
        DevelopmentOrderGuestReconciliation.RequireObserved(recorded, fresh);
        foreach (var result in new DevelopmentSandboxRunner.SandboxProcess[]
        {
            new(0, "State: running", ""),
            new(1, "", "backend unavailable"),
            new(1, "", "permission denied"),
            new(1, "", "error: sandbox 'hami-code-ffffffffffff' not found"),
            new(1, "", absent + " unexpected diagnostic"),
            new(1, absent.PadRight(256 * 1024), "")
        })
            await RejectObservationAsync(result);

        string reportPath = database + ".guest-report-1";
        byte[] original = owner.RequireOrderGuestReport(recorded, recorded.Reservation);
        int before = calls;
        File.Move(reportPath, reportPath + ".saved");
        try { await RejectObservationAsync(new(1, "", absent)); }
        finally { File.Move(reportPath + ".saved", reportPath); }
        if (calls != before)
            throw new InvalidOperationException("Missing protected report reached backend.");
        try
        {
            await RejectObservationAsync(new(1, "", absent),
                () => File.WriteAllBytes(reportPath, [.. original, 0x20]));
        }
        finally { File.WriteAllBytes(reportPath, original); }
        if (calls != before + 1)
            throw new InvalidOperationException("Report change during inspection was not exercised.");

        bool timeout = false;
        try
        {
            _ = await DevelopmentSandboxRunner.RequireProtectedGuestsCoreAsync(owner, [recorded], (_, _, args, _) =>
            {
                if (!args.SequenceEqual(new[] { "inspect", name }))
                    throw new InvalidOperationException("Unexpected timeout command.");
                throw new TimeoutException("Synthetic inspect timeout.");
            });
        }
        catch (TimeoutException) { timeout = true; }
        if (!timeout)
            throw new InvalidOperationException("Inspect timeout became successful evidence.");
        using var cancel = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            _ = await DevelopmentSandboxRunner.RequireProtectedGuestsCoreAsync(owner, [recorded],
                (_, token, _, _) =>
                {
                    cancel.Cancel();
                    if (!token.IsCancellationRequested)
                        throw new InvalidOperationException("Reentry transport lost cancellation.");
                    return Task.FromResult(new DevelopmentSandboxRunner.SandboxProcess(1, "", absent));
                }, cancel.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        if (!cancelled)
            throw new InvalidOperationException("Cancelled reentry returned evidence.");

        cancelled = false;
        try
        {
            _ = await DevelopmentSandboxRunner.RequireProtectedGuestsCoreAsync(owner, [recorded],
                (_, _, _, _) => throw new InvalidOperationException("Precancelled reentry reached backend."), cancel.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        if (!cancelled)
            throw new InvalidOperationException("Precancelled reentry accepted.");

        VerifyFixtureSnapshot(owner, recorded.Reservation);
        DevelopmentOrderGuestReconciliation.RequireObserved(recorded, await ObserveAsync(new(1, "", absent)));
        Console.WriteLine("PASS: protected guest read-only adapter requires exact fresh inspect and stable manifest/report; backend failures remain blocked.");
    }

    // Separate disposable storage/transport integration fixture. Existing
    // process-kill cases retain their exact historical reference reports.
    private static async Task TestLiveGuestCaptureAsync(string prefix)
    {
        foreach (string scenario in new[] { "success", "raw-replaced", "nonzero", "late-inspect-unknown", "late-inspect-timeout" })
        {
            string root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            var store = DevelopmentJobStateRoot.OpenOrderProbeFixture(root, create: true);
            try
            {
                using var owner = store.AcquireOrder(Issue);
                owner.ClaimOrder(Guid.NewGuid().ToString("N"), FixtureApproval(scenario == "success" ? 40 : null));
                string journal = owner.CreateOrderJournal();
                DevelopmentOrderClaim claim = owner.CurrentOrder ?? throw new InvalidOperationException("Claim missing.");
                DevelopmentOrderArchive archive = owner.RequireArchivedOrder();
                var policy = DevelopmentOrderBudgetPolicy.Parse(Encoding.UTF8.GetBytes(archive.BudgetPolicy));
                var limits = archive.EffectiveLimits;
                string repo = Path.Combine(root, "repo");
                string source = Path.Combine(root, "source");
                string results = Path.Combine(root, "results");
                Directory.CreateDirectory(repo);
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(results);
                File.WriteAllText(Path.Combine(source, "Demo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
                File.WriteAllText(Path.Combine(source, "Program.cs"), "Console.WriteLine(42);");
                byte[] manifest = DevelopmentOrderSnapshot.CaptureBytes(source, "Demo.csproj");
                string hash = DevelopmentJobState.Hash(manifest);
                long now = DateTime.UtcNow.Ticks;
                var ledger = new DevelopmentOrderOperationLedger().Start(new(1, claim.ArchiveHash, now),
                    claim.ArchiveHash, limits, policy);
                var reservation = ledger.Propose(claim.JobId, "S01", "guest", 30, now, null, hash);
                owner.StoreOrderSnapshotManifest(reservation, manifest);
                // Synthetic acknowledged view only in this integration test;
                // actual Persist/replay is exercised in the six cases below.
                ledger = ledger.Reserve(claim.JobId, limits, reservation);
                string id = DevelopmentOrderGuestReconciliation.RunId(reservation);
                string name = "hami-code-" + id[..12];
                string absent = "error: sandbox '" + name + "' not found";
                string healthy = string.Join('\n', new[]
                {
                    "Name: " + name, "Agent: shell", "Workspace: -", "Clone source: -",
                    "Configured host mounts: none", "Secrets: none", "MCP mode: static",
                    "MCP servers: none", "Ports: none published", "Network policy: global (local policy)",
                    "Auth mode: not configured", "State: running"
                });
                string guestOutput = string.Join('\0', DevelopmentOrderSnapshot.RequireGuestRecords(source, "Demo.csproj", hash))
                    + "\0" + DevelopmentOrderSnapshot.GuestEndMarker + "\0";
                string raw = Path.Combine(results, "reports", "hami-code-" + id, "report.json");
                bool created = false, removed = false;
                int calls = 0, afterReturn = 0;
                byte[]? original = null;

                Task<DevelopmentSandboxRunner.SandboxProcess> Transport(TimeSpan limit, CancellationToken token,
                    string[] args, DevelopmentOrderSafetyWindow? safety)
                {
                    token.ThrowIfCancellationRequested();
                    if (safety is null || limit <= TimeSpan.Zero)
                        throw new InvalidOperationException("Missing reserved transport safety window.");
                    calls++;
                    DevelopmentSandboxRunner.SandboxProcess response = new(0, "", "");
                    switch (args[0])
                    {
                        case "inspect":
                            if (args.Contains("--json", StringComparer.Ordinal))
                                response = new(0, System.Text.Json.JsonSerializer.Serialize(new { name, id = "fixture-backend-id" }), "");
                            else if (!created || removed)
                            {
                                response = new(1, "", absent);
                                if (File.Exists(raw))
                                {
                                    afterReturn++;
                                    original = File.ReadAllBytes(raw);
                                    if (scenario == "raw-replaced")
                                        File.WriteAllText(raw, "not the captured report");
                                    if (scenario == "late-inspect-unknown")
                                        response = new(1, "", "backend unavailable");
                                    if (scenario == "late-inspect-timeout")
                                        throw new TimeoutException("fixture late inspect");
                                }
                            }
                            else
                                response = new(0, healthy, "");
                            break;
                        case "create": created = true; break;
                        case "rm": removed = true; break;
                        case "policy": response = new(1, "Denied: " + args[^1] + " default deny", ""); break;
                        case "exec":
                            if (args.Any(a => a.Contains("CREDENTIAL_ENV=clean", StringComparison.Ordinal)))
                                response = new(0, "CREDENTIAL_ENV=clean", "");
                            else if (args.Contains("--version", StringComparer.Ordinal))
                                response = new(0, "11.0.100", "");
                            else if (args.Contains("hami-snapshot", StringComparer.Ordinal))
                                response = new(0, guestOutput, "");
                            else if (scenario == "nonzero" && args.Contains("run", StringComparer.Ordinal))
                                response = new(1, "", "");
                            break;
                    }
                    return Task.FromResult(response);
                }

                OrderGuestOutcome? outcome = null;
                bool timeout = false;
                try
                {
                    outcome = await DevelopmentSandboxRunner.RunReservedCoreAsync(repo, source, results, owner,
                        ledger, reservation, Transport, (_, _, _, _) => { });
                }
                catch (TimeoutException ex) when (scenario == "late-inspect-timeout" && ex.Message == "fixture late inspect")
                {
                    timeout = true;
                }

                bool success = scenario is "success" or "raw-replaced";
                if ((outcome is not null) != success || timeout != (scenario == "late-inspect-timeout")
                    || !created || !removed || afterReturn != (scenario == "nonzero" ? 0 : 1)
                    || !File.Exists(raw) || ledger.Pending != reservation || ledger.Guests != 1)
                    throw new InvalidOperationException("Live guest capture outcome differs: " + scenario);
                owner.RequireOrderGuestDispatch(reservation);
                string slot = journal + ".guest-report-1";
                if (success)
                {
                    byte[] captured = owner.RequireOrderGuestReport(outcome!, reservation);
                    if (original is null || !captured.SequenceEqual(original)
                        || DevelopmentJobState.Hash(captured) != outcome!.ReportHash
                        || !DevelopmentOrderGuestReconciliation.IsSuccessfulReport(reservation, captured)
                        || (scenario == "raw-replaced" && captured.SequenceEqual(File.ReadAllBytes(raw))))
                        throw new InvalidOperationException("Protected report differs from live bytes.");
                }
                else if (File.Exists(slot))
                    throw new InvalidOperationException("Failed runner or inspect wrote protected report.");

                int before = calls;
                bool rejected = false;
                try
                {
                    _ = await DevelopmentSandboxRunner.RunReservedCoreAsync(repo, source, results, owner,
                        ledger, reservation, Transport, (_, _, _, _) => { });
                }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected || calls != before)
                    throw new InvalidOperationException("Consumed guest was redispatched.");
                if (scenario == "success")
                {
                    // A second synthetic journal descriptor with protected
                    // bytes exercises a shared reentry window across records.
                    // It does not claim a second live runner invocation.
                    OrderGuestOutcome first = outcome!;
                    var completed = ledger.CompleteGuest(first);
                    var next = completed.Propose(claim.JobId, "S01", "guest", 30, DateTime.UtcNow.Ticks, null, hash);
                    owner.StoreOrderSnapshotManifest(next, manifest);
                    var pending = completed.Reserve(claim.JobId, limits, next);
                    owner.ConsumeOrderGuestDispatch(pending, next);
                    byte[] secondBytes = FixtureGuestReport(next);
                    var second = ObserveFixtureGuest(claim, next, secondBytes)
                        ?? throw new InvalidOperationException("Second fixture observation missing.");
                    owner.StoreOrderGuestReport(second, next, secondBytes);
                    var historical = pending.CompleteGuest(second);
                    await TestReentryWindowAsync(owner, first, second, policy.SafetyReserveSeconds, slot);
                    if (historical.Pending is not null || historical.LatestGuestOutcome != second
                        || historical.Guests != 2 || historical.ReservedSeconds != 60)
                        throw new InvalidOperationException("Reentry changed charged operations.");
                }

                owner.ValidateOrderJournalFiles();
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        Console.WriteLine("PASS: live reserved runner captures exact protected bytes only after success and fresh absence; raw reports cannot authorize completion.");
    }

    private sealed class ReentryClock : TimeProvider
    {
        internal long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
    }

    private static async Task TestReentryWindowAsync(DevelopmentJobStateStore.Lease owner,
        OrderGuestOutcome first, OrderGuestOutcome second, int reserveSeconds, string firstSlot)
    {
        foreach (string scenario in new[] { "shared", "exhausted", "earlier-changed", "duplicate" })
        {
            var clock = new ReentryClock();
            int calls = 0;
            byte[] original = owner.RequireOrderGuestReport(first, first.Reservation);
            bool rejected = false;
            try
            {
                var observations = await DevelopmentSandboxRunner.RequireProtectedGuestsCoreAsync(owner,
                    scenario == "duplicate" ? [first, first] : [first, second],
                    (limit, token, args, safety) =>
                    {
                        OrderGuestOutcome expected = calls == 0 ? first : second;
                        string name = "hami-code-" + expected.RunId[..12];
                        TimeSpan expectedLimit = TimeSpan.FromSeconds(calls == 0 ? 30 : 15);
                        if (!args.SequenceEqual(new[] { "inspect", name }) || limit != expectedLimit
                            || !token.CanBeCanceled || safety is null || calls >= 2)
                            throw new InvalidOperationException("Reentry window or command differs.");
                        calls++;
                        clock.Timestamp += TimeSpan.FromSeconds(calls == 1 ? reserveSeconds - 15
                            : scenario == "exhausted" ? 15 : 1).Ticks;
                        if (scenario == "earlier-changed" && calls == 2)
                            File.WriteAllBytes(firstSlot, [.. original, 0x20]);
                        return Task.FromResult(new DevelopmentSandboxRunner.SandboxProcess(1, "",
                            "error: sandbox '" + name + "' not found"));
                    }, clock: clock);
                if (scenario != "shared" || observations.Length != 2 || calls != 2)
                    throw new InvalidOperationException("Unexpected successful reconciliation batch.");
                DevelopmentOrderGuestReconciliation.RequireObserved(first, observations[0]);
                DevelopmentOrderGuestReconciliation.RequireObserved(second, observations[1]);
            }
            catch (TimeoutException ex) when (scenario == "exhausted" && ex.Message == "ORDER_SAFETY_RESERVE_EXHAUSTED")
            { rejected = true; }
            catch (InvalidOperationException ex) when (scenario == "duplicate" && ex.Message == "ORDER_GUEST_RECONCILIATION_COUNT")
            { rejected = true; }
            catch (InvalidOperationException ex) when (scenario == "earlier-changed" && ex.Message == "ORDER_GUEST_REPORT_BYTES")
            { rejected = true; }
            finally
            {
                if (scenario == "earlier-changed")
                    File.WriteAllBytes(firstSlot, original);
            }

            if (rejected != (scenario != "shared") || calls != (scenario == "duplicate" ? 0 : 2))
                throw new InvalidOperationException("Reentry regression did not exercise expected boundary.");
        }

        Console.WriteLine("PASS: one policy-bounded read-only reentry window across reports; expiry, duplicate and earlier tampering block the entire batch.");
    }

    private static async Task TestModelDispatchAsync(string prefix)
    {
        byte[] baseline = Encoding.UTF8.GetBytes("internal static class PairCalculator { }");
        const string candidate = "internal static class PairCalculator { /* generated */ }";
        foreach (string scenario in new[] { "success", "adapter-success", "adapter-rejected", "adapter-empty",
            "adapter-length", "wrong-config", "wrong-input", "wrong-assignment", "cancelled", "unknown", "invalid-source" })
        {
            string root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            var store = DevelopmentJobStateRoot.OpenOrderProbeFixture(root, create: true);
            try
            {
                using var owner = store.AcquireOrder(Issue);
                var approval = FixtureApproval(baselineHash: DevelopmentJobState.Hash(baseline));
                owner.ClaimOrder(Guid.NewGuid().ToString("N"), approval);
                string journal = owner.CreateOrderJournal();
                DevelopmentOrderClaim claim = owner.CurrentOrder ?? throw new InvalidOperationException("Claim missing.");
                var archive = owner.RequireArchivedOrder();
                var assignment = FixtureAssignment(claim, archive, "S01");
                string payload = scenario switch
                {
                    "adapter-rejected" => """{"source":"one","source":"two"}""",
                    "adapter-empty" => """{"source":""}""",
                    _ => System.Text.Json.JsonSerializer.Serialize(new { source = candidate })
                };
                using var scripted = new DevelopmentAiGenerator.ScriptedChatClient(payload,
                    scenario == "adapter-length" ? Microsoft.Extensions.AI.ChatFinishReason.Length : null);
                var generator = new DevelopmentAiGenerator(scripted, "fixture-model");
                bool adapter = scenario.StartsWith("adapter-", StringComparison.Ordinal) || scenario == "wrong-config";
                if (adapter)
                    assignment = assignment with
                    {
                        Agent = assignment.Agent with
                        {
                            Provider = "openai",
                            Model = generator.ModelId,
                            ConfigurationHash = scenario == "wrong-config" ? new string('A', 64) : generator.S01ConfigurationHash
                        }
                    };
                OrderAgentProfile[] catalog = [new(assignment.Agent, true, ["csharp"])];
                long now = DateTime.UtcNow.Ticks;
                var ledger = new DevelopmentOrderOperationLedger().Start(new(1, claim.ArchiveHash, now),
                    claim.ArchiveHash, archive.EffectiveLimits, approval.BudgetPolicy);
                var operation = ledger.Propose(claim.JobId, "S01", "model", 30, now,
                    OrderOperationAgentBinding.FromAssignment(assignment));
                ledger = ledger.Reserve(claim.JobId, archive.EffectiveLimits, operation);
                using var cancel = new CancellationTokenSource();
                int calls = 0;
                Task<string> Generate(string input, CancellationToken token)
                {
                    if (input != Encoding.UTF8.GetString(baseline) || !token.CanBeCanceled)
                        throw new InvalidOperationException("Wrong model input or cancellation.");
                    calls++;
                    if (scenario == "cancelled")
                        cancel.Cancel();
                    if (scenario == "unknown")
                        throw new IOException("Synthetic unknown transport outcome.");
                    return Task.FromResult(scenario == "invalid-source" ? "\0" : candidate);
                }

                OrderModelDispatchResult? generated = null;
                bool rejected = false;
                try
                {
                    if (adapter)
                        generated = await DevelopmentOrderModelDispatch.RunS01CoreAsync(owner, ledger, operation, approval,
                            assignment, catalog, baseline, generator, cancel.Token);
                    else
                        generated = await DevelopmentOrderModelDispatch.RunAsync(owner, ledger, operation, approval,
                            scenario == "wrong-assignment" ? assignment with { Revision = 2 } : assignment,
                            catalog, scenario == "wrong-input" ? [1] : baseline, Generate, cancel.Token);
                }
                catch (InvalidOperationException) when (scenario is "wrong-input" or "wrong-assignment" or "invalid-source" or "wrong-config")
                { rejected = true; }
                catch (InvalidOperationException ex) when (scenario == "adapter-length" && ex.Message == "ORDER_MODEL_RESPONSE_INCOMPLETE")
                { rejected = true; }
                catch (OperationCanceledException) when (scenario == "cancelled") { rejected = true; }
                catch (IOException) when (scenario == "unknown") { rejected = true; }

                bool dispatched = scenario is not ("wrong-input" or "wrong-assignment" or "wrong-config");
                bool success = scenario is "success" or "adapter-success";
                bool knownFailure = scenario is "adapter-rejected" or "adapter-empty";
                bool completedResponse = success || knownFailure;
                string receipt = journal + ".model-dispatch-1";
                string artifact = journal + ".candidate-1";
                string evidencePath = journal + ".evidence-1";
                if (rejected == completedResponse || (generated is not null) != completedResponse
                    || calls + scripted.Calls != (dispatched ? 1 : 0) || File.Exists(receipt) != dispatched
                    || File.Exists(artifact) != success || File.Exists(evidencePath) != completedResponse
                    || ledger.Pending != operation || ledger.Models != 1)
                    throw new InvalidOperationException("Model dispatch outcome differs: " + scenario);
                if (dispatched)
                {
                    owner.RequireOrderModelDispatch(operation);
                    Reject(() => owner.ConsumeOrderModelDispatch(ledger, operation));
                    byte[] original = File.ReadAllBytes(receipt);
                    File.WriteAllBytes(receipt, [.. original, 0x20]);
                    try { Reject(() => owner.RequireOrderModelDispatch(operation)); }
                    finally { File.WriteAllBytes(receipt, original); }
                    Reject(() => owner.RequireOrderModelDispatch(operation with { ReservedSeconds = 31 }));
                    owner.RequireOrderModelDispatch(operation);
                }
                // A recovered pending reservation never becomes a
                // completion through discovery of its private files.
                var blocked = DevelopmentOrderModelReconciliation.Observe(owner, ledger);
                if (blocked.Status != OrderModelRecoveryStatus.PendingBlocked || blocked.Reservation != operation
                    || blocked.Candidate is not null || blocked.Completion is not null)
                    throw new InvalidOperationException("Unjournaled model effects were adopted.");

                if (success)
                {
                    var result = generated ?? throw new InvalidOperationException("Missing generated result.");
                    var recorded = result.Candidate ?? throw new InvalidOperationException("Missing generated candidate.");
                    var completion = result.Completion;
                    var evidence = owner.RequireOrderOperationEvidence(operation, completion, recorded);
                    var checkpointed = ledger.Checkpoint(claim, recorded);
                    var completed = checkpointed.Complete(completion);
                    var checkpointRecovery = DevelopmentOrderModelReconciliation.Observe(owner, checkpointed);
                    var completedRecovery = DevelopmentOrderModelReconciliation.Observe(owner, completed);
                    if (checkpointRecovery.Status != OrderModelRecoveryStatus.PendingBlocked
                        || checkpointRecovery.Candidate is not null || checkpointRecovery.Completion is not null
                        || completedRecovery.Status != OrderModelRecoveryStatus.Generated
                        || completedRecovery.Candidate != recorded || completedRecovery.Completion != completion
                        || completedRecovery.Reservation != operation)
                        throw new InvalidOperationException("Generated model recovery differs from journal.");
                    Reject(() => DevelopmentOrderModelReconciliation.Observe(owner,
                        completed with { LastEvidenceHash = new string('A', 64) }));
                    Reject(() => DevelopmentOrderModelReconciliation.Observe(owner,
                        completed with { LatestModelCompletion = null }));
                    Reject(() => DevelopmentOrderModelReconciliation.Observe(owner,
                        completed with { LatestCandidate = null }));
                    if (!owner.RequireOrderCandidate(recorded).SequenceEqual(Encoding.UTF8.GetBytes(candidate))
                        || evidence.Observer != OrderOperationEvidence.GeneratedObserver
                        || evidence.OutputHash != recorded.ArtifactHash || checkpointed.Pending != operation
                        || completed.Pending is not null || completed.Models != 1 || completed.ReservedSeconds != 30
                        || completed.LatestCandidate != recorded || completed.LastOutcome != "succeeded")
                        throw new InvalidOperationException("Generated model evidence or charge differs.");
                    Reject(() => owner.RequireOrderOperationEvidence(operation, completion));
                    Reject(() => owner.RequireOrderOperationEvidence(operation, completion, recorded with { Quality = "Partial" }));
                    Reject(() => owner.RequireOrderOperationEvidence(operation, completion, recorded with { ArtifactHash = new string('A', 64) }));
                    Reject(() => owner.StoreOrderOperationEvidence(evidence, recorded));
                    Reject(() => owner.StoreOrderOperationEvidence(evidence, null));
                    Reject(() => owner.StoreOrderOperationEvidence(evidence with { Observer = OrderOperationEvidence.FixtureObserver }, recorded));
                    foreach (string path in new[] { receipt, artifact, evidencePath })
                    {
                        byte[] original = File.ReadAllBytes(path);
                        File.Move(path, path + ".saved");
                        try
                        {
                            Reject(() => owner.RequireOrderOperationEvidence(operation, completion, recorded));
                            Reject(() => DevelopmentOrderModelReconciliation.Observe(owner, completed));
                        }
                        finally { File.Move(path + ".saved", path); }
                        File.WriteAllBytes(path, [.. original, 0x20]);
                        try
                        {
                            Reject(() => owner.RequireOrderOperationEvidence(operation, completion, recorded));
                            Reject(() => DevelopmentOrderModelReconciliation.Observe(owner, completed));
                        }
                        finally { File.WriteAllBytes(path, original); }
                    }

                    _ = owner.RequireOrderOperationEvidence(operation, completion, recorded);
                    if (ledger.Pending != operation || File.Exists(journal + ".s01"))
                        throw new InvalidOperationException("Private model evidence became journal or step authority.");
                }
                if (knownFailure)
                {
                    var result = generated ?? throw new InvalidOperationException("Missing rejected-response result.");
                    var completion = result.Completion;
                    var evidence = owner.RequireOrderOperationEvidence(operation, completion);
                    var completed = ledger.Complete(completion);
                    var recovered = DevelopmentOrderModelReconciliation.Observe(owner, completed);
                    if (recovered.Status != OrderModelRecoveryStatus.Rejected || recovered.Candidate is not null
                        || recovered.Completion != completion || recovered.Reservation != operation)
                        throw new InvalidOperationException("Rejected model recovery differs from journal.");
                    if (result.Candidate is not null || evidence.Observer != OrderOperationEvidence.RejectedResponseObserver
                        || evidence.OutputHash is not null || completed.Pending is not null || completed.Models != 1
                        || completed.ReservedSeconds != 30 || completed.LatestCandidate is not null
                        || completed.LastOutcome != "failed" || ledger.Pending != operation)
                        throw new InvalidOperationException("Rejected response became candidate or changed charge.");

                    var next = completed.Propose(claim.JobId, "S01", "model", 30,
                        DateTime.UtcNow.Ticks, OrderOperationAgentBinding.FromAssignment(assignment));
                    var retry = completed.Reserve(claim.JobId, archive.EffectiveLimits, next);
                    var retryRecovery = DevelopmentOrderModelReconciliation.Observe(owner, retry);
                    if (retryRecovery.Status != OrderModelRecoveryStatus.PendingBlocked
                        || retryRecovery.Reservation != next || retryRecovery.Completion is not null)
                        throw new InvalidOperationException("Previous completion settled new pending operation.");
                    if (retry.Models != 2 || retry.ReservedSeconds != 60 || retry.Pending != next
                        || next.OperationId == operation.OperationId || scripted.Calls != 1)
                        throw new InvalidOperationException("Known failure retry was not a new charged reservation.");
                    Reject(() => owner.StoreOrderOperationEvidence(evidence));
                    Reject(() => owner.StoreOrderOperationEvidence(evidence with { Observer = OrderOperationEvidence.GeneratedObserver }));
                    File.WriteAllBytes(artifact, Encoding.UTF8.GetBytes(candidate));
                    try { Reject(() => owner.RequireOrderOperationEvidence(operation, completion)); }
                    finally { File.Delete(artifact); }
                    foreach (string path in new[] { receipt, evidencePath })
                    {
                        byte[] original = File.ReadAllBytes(path);
                        File.Move(path, path + ".saved");
                        try
                        {
                            Reject(() => owner.RequireOrderOperationEvidence(operation, completion));
                            Reject(() => DevelopmentOrderModelReconciliation.Observe(owner, completed));
                        }
                        finally { File.Move(path + ".saved", path); }
                        File.WriteAllBytes(path, [.. original, 0x20]);
                        try
                        {
                            Reject(() => owner.RequireOrderOperationEvidence(operation, completion));
                            Reject(() => DevelopmentOrderModelReconciliation.Observe(owner, completed));
                        }
                        finally { File.WriteAllBytes(path, original); }
                    }

                    _ = owner.RequireOrderOperationEvidence(operation, completion);
                }
                owner.ValidateOrderJournalFiles();
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        Console.WriteLine("PASS: reserved S01 model dispatch, generated and terminal-rejected host outcomes, exact evidence, unknown/cancelled remain pending.");
    }

    internal static async Task RunAsync(string prefix, Func<string, string, Process> start,
        Func<string, string, Task> expectOwnership)
    {
        await DevelopmentAiGenerator.SelfTestS01Async();
        await TestModelDispatchAsync(prefix);
        await TestLiveGuestCaptureAsync(prefix);
        foreach (string scenario in new[] { "accepted", "unknown", "failed", "guest", "guest-unknown", "guest-orphan",
            "model-receipt", "model-orphan", "model-checkpoint", "model-generated", "model-rejected", "model-rejected-orphan", "soft-stop" })
        {
            bool unknown = scenario == "unknown";
            bool failed = scenario == "failed";
            bool guest = scenario == "guest";
            bool guestUnknown = scenario == "guest-unknown";
            bool guestOrphan = scenario == "guest-orphan";
            bool guestScenario = guest || guestUnknown || guestOrphan;
            bool modelScenario = scenario.StartsWith("model-", StringComparison.Ordinal);
            bool softScenario = scenario == "soft-stop";
            bool modelGenerated = scenario == "model-generated";
            bool modelRejected = scenario == "model-rejected";
            bool modelCandidate = scenario is "model-orphan" or "model-checkpoint" or "model-generated";
            bool modelEvidence = modelCandidate || modelRejected || scenario == "model-rejected-orphan";
            (string firstMode, string firstMarker, string resumeMode, string resumeMarker) = scenario switch
            {
                "accepted" => ("bridge-hold", "BRIDGE_ACCEPTED", "bridge-resume", "BRIDGE_COMPLETED"),
                "soft-stop" => ("bridge-soft-stop", "BRIDGE_SOFT_STOPPED", "bridge-soft-resume", "BRIDGE_SOFT_RESUMED"),
                "unknown" => ("bridge-unknown", "BRIDGE_RESERVED", "bridge-blocked", "BRIDGE_BLOCKED"),
                "failed" => ("bridge-failed", "BRIDGE_FAILED", "bridge-retry", "BRIDGE_RETRIED"),
                "guest" => ("bridge-guest", "BRIDGE_GUEST", "bridge-guest-resume", "BRIDGE_GUEST_RECOVERED"),
                "guest-unknown" => ("bridge-guest-unknown", "BRIDGE_GUEST_UNKNOWN", "bridge-guest-blocked", "BRIDGE_GUEST_BLOCKED"),
                "guest-orphan" => ("bridge-guest-orphan", "BRIDGE_GUEST_ORPHAN", "bridge-guest-orphan-resume", "BRIDGE_GUEST_ORPHAN_RECOVERED"),
                _ when modelScenario => ("bridge-" + scenario, "BRIDGE_MODEL_CRASH_READY",
                    modelGenerated ? "bridge-model-resume-generated" : modelRejected ? "bridge-model-resume-rejected"
                        : "bridge-model-resume-blocked", "BRIDGE_MODEL_RECOVERED"),
                _ => throw new InvalidOperationException("Unknown bridge scenario.")
            };
            string root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            var store = DevelopmentJobStateRoot.OpenOrderProbeFixture(root, create: true);
            try
            {
                using (Process child = start(firstMode, root))
                {
                    Task<string> stderr = child.StandardError.ReadToEndAsync();
                    var startup = Stopwatch.StartNew();
                    try
                    {
                        await WaitForMarkerAsync(child, firstMarker).WaitAsync(Deadline);
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
                    if (!unknown && !failed && !guestScenario && !modelScenario && !softScenario && !File.Exists(journal + "-journal"))
                        throw new InvalidOperationException("Crash did not retain the live rollback sidecar.");
                    if (!unknown && !failed && !guestScenario && !modelScenario)
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

                using Process resumed = start(resumeMode, root);
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
                        line.Trim() == resumeMarker))
                        throw new InvalidOperationException("Bridge reentry failed: " + result + await errors);

                    string s02 = Path.Combine(root, $"issue-{Issue}.order.db.s02");
                    string candidatePath = Path.Combine(root, $"issue-{Issue}.order.db.candidate-1");
                    if (softScenario ? !File.ReadAllBytes(artifact).SequenceEqual(AcceptedBytes) || File.Exists(s02)
                        || !File.Exists(Path.Combine(root, $"issue-{Issue}.order.db.evidence-1"))
                        || File.Exists(Path.Combine(root, $"issue-{Issue}.order.db.evidence-2"))
                        : modelScenario ? File.Exists(artifact) || File.Exists(s02)
                        || !File.Exists(Path.Combine(root, $"issue-{Issue}.order.db.model-dispatch-1"))
                        || File.Exists(candidatePath) != modelCandidate
                        || (modelCandidate && !File.ReadAllBytes(candidatePath).SequenceEqual(Encoding.UTF8.GetBytes(ModelCandidate)))
                        || File.Exists(Path.Combine(root, $"issue-{Issue}.order.db.evidence-1")) != modelEvidence
                        || File.Exists(Path.Combine(root, $"issue-{Issue}.order.db.model-dispatch-2"))
                        : guestScenario ? File.Exists(artifact) || File.Exists(s02)
                        || !File.ReadAllBytes(Path.Combine(root, $"issue-{Issue}.order.db.snapshot-1")).SequenceEqual(FixtureSnapshotManifest.Value)
                        || !File.Exists(Path.Combine(root, $"issue-{Issue}.order.db.guest-dispatch-1"))
                        || File.Exists(Path.Combine(root, $"issue-{Issue}.order.db.guest-report-1")) == guestUnknown
                        : unknown ? File.Exists(artifact) || File.Exists(s02)
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
        Console.WriteLine("PASS: exact historical model assignment survives process kill with reservation and receipts.");
        Console.WriteLine("PASS: protected guest report survives process kill; exact bytes and fresh fixture observation required, no step accepted.");
        Console.WriteLine("PASS: unknown guest reservation survives process kill; absent report or unconfirmed sandbox blocks all new reservations.");
        Console.WriteLine("PASS: orphan guest report alone does not settle reservation; explicit fresh fixture observation and journal acknowledgement required.");
        Console.WriteLine("PASS: private canonical snapshot manifests survive guest crash windows; missing/changed/foreign/duplicate manifests rejected.");
        Console.WriteLine("PASS: protected guest dispatch is consumed once across crash/reentry; missing/modified/foreign receipts reject report use.");
        Console.WriteLine("PASS: S01 model dispatch crash windows recover through real Akka SQL replay: receipt-only, orphan, checkpoint-only, generated, rejected and rejected-orphan.");
        Console.WriteLine("PASS: supervisor admission persists soft stop across process kill; insufficient extension remains stopped, sufficient extension permits fresh charged reservation.");
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

    internal static DevelopmentOrderApproval FixtureApproval(int? safetyReserveSeconds = null, string? baselineHash = null)
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
        if (safetyReserveSeconds is int safetySeconds)
            policyNode["safetyReserveSeconds"] = safetySeconds;
        var policy = DevelopmentOrderBudgetPolicy.Parse(Encoding.UTF8.GetBytes(policyNode.ToJsonString()));
        byte[] body = Encoding.UTF8.GetBytes("\x60\x60\x60hami-order-json\n" + plan + "\x60\x60\x60\n");
        DevelopmentOrder order = DevelopmentOrder.ParseIssueBody(body);
        var document = order.Document;
        string repository = document.GetProperty("repository").GetString()!;
        string commit = document.GetProperty("baseCommit").GetString()!;
        string hash = DevelopmentJobState.Hash(Encoding.UTF8.GetBytes("synthetic review"));
        var profile = new OrderReviewedProfile(repository, 42, "fixture-owner", commit, hash, hash, policy.Sha256,
            baselineHash ?? hash, hash);
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
        bool modelScenario = mode.StartsWith("bridge-model-", StringComparison.Ordinal);
        bool softScenario = mode is "bridge-soft-stop" or "bridge-soft-resume";
        bool first = mode is "bridge-hold" or "bridge-unknown" or "bridge-failed" or "bridge-guest"
            or "bridge-guest-unknown" or "bridge-guest-orphan"
            or "bridge-model-receipt" or "bridge-model-orphan" or "bridge-model-checkpoint"
            or "bridge-model-generated" or "bridge-model-rejected" or "bridge-model-rejected-orphan" or "bridge-soft-stop";
        if (first)
        {
            var fixtureApproval = FixtureApproval();
            if (fixtureApproval.Binding != claim.Binding || fixtureApproval.ObservedAt != archive.ObservedAt)
                throw new InvalidOperationException("Fixture assignment approval mismatch.");
            var assignments = new[] { FixtureAssignment(claim, archive, "S01"), FixtureAssignment(claim, archive, "S02") };
            var catalog = assignments.Select(a => new OrderAgentProfile(a.Agent, true, ["csharp"])).ToArray();
            foreach (var assignment in assignments)
                DevelopmentOrderHandover.ValidateAssignment(fixtureApproval, Guid.ParseExact(claim.JobId, "N"), assignment, catalog);
        }

        OrderOperationReservation ProposeModel(DevelopmentOrderOperationLedger state, string step, int seconds, long now) =>
            state.Propose(claim.JobId, step, "model", seconds, now,
                OrderOperationAgentBinding.FromAssignment(FixtureAssignment(claim, archive, step)));

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
            IActorRef actor = system.ActorOf(Props.Create(() => new BridgeActor(claim, archive, limits, policy, owner, modelScenario, softScenario)), "bridge");
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

            async Task<BridgeState> AdmitAsync(OrderOperationReservation request, int stage)
            {
                Task<BridgeState> pending = actor.Ask<BridgeState>(new Admit(request), Deadline);
                await guard.AwaitAsync(pending);
                BridgeState state = await pending;
                if (state.Stage != stage)
                    throw new InvalidOperationException("Supervisor decision was not acknowledged.");
                return state;
            }

            async Task RejectAdmissionAsync(OrderOperationReservation request, bool raw)
            {
                try
                {
                    if (raw)
                        _ = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeReservation(request), 4);
                    else
                        _ = await AdmitAsync(request, 4);
                }
                catch (InvalidOperationException ex) when (ex.Message == "Fixture admission blocked."
                    || ex.Message == "Use supervisor admission.")
                {
                    return;
                }

                throw new InvalidOperationException("Stopped or raw admission was accepted.");
            }

            Console.WriteLine("BRIDGE_PHASE: host started; awaiting replay.");
            var recovery = Stopwatch.StartNew();
            BridgeState current = await ReadAsync();
            guard.RequireObserved();
            Console.WriteLine($"BRIDGE_PHASE: replay state {current.Stage}; {recovery.ElapsedMilliseconds} ms.");
            string artifact = database + ".s01";
            if (!first)
            {
                // The exact assignment is part of the historical reservation,
                // including for unresolved and failed attempts.
                if (current.Ledger.Pending is { Kind: "model" } recoveredOperation)
                {
                    if (modelScenario)
                        VerifyModelFixtureAssignment(claim, archive, recoveredOperation);
                    else
                        VerifyFixtureAssignment(claim, archive, recoveredOperation);
                }
                if (modelScenario && current.Ledger.LatestModelReservation is { } finishedModel)
                    VerifyModelFixtureAssignment(claim, archive, finishedModel);
                if (current.S01 is { } recoveredS01)
                    VerifyFixtureAssignment(claim, archive, recoveredS01.Reservation);
                if (current.FailedS01 is { } recoveredFailure)
                    VerifyFixtureAssignment(claim, archive, recoveredFailure.Reservation);
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
                if (modelScenario)
                {
                    var assignment = ModelFixtureAssignment(claim, archive);
                    var approval = FixtureApproval();
                    OrderAgentProfile[] catalog = [new(assignment.Agent, true, ["csharp"])];
                    var reservation = current.Ledger.Propose(claim.JobId, "S01", "model", 30,
                        DateTime.UtcNow.Ticks, OrderOperationAgentBinding.FromAssignment(assignment));
                    current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeReservation(reservation), 2);
                    if (mode == "bridge-model-receipt")
                        owner.ConsumeOrderModelDispatch(current.Ledger, reservation);
                    else
                    {
                        bool reject = mode is "bridge-model-rejected" or "bridge-model-rejected-orphan";
                        string response = reject ? """{"source":""}"""
                            : System.Text.Json.JsonSerializer.Serialize(new { source = ModelCandidate });
                        using var fake = new DevelopmentAiGenerator.ScriptedChatClient(response);
                        var generator = new DevelopmentAiGenerator(fake, ModelName);
                        var result = await DevelopmentOrderModelDispatch.RunS01CoreAsync(owner, current.Ledger,
                            reservation, approval, assignment, catalog, ModelBaseline, generator);
                        if (fake.Calls != 1 || (result.Candidate is null) != reject
                            || result.Completion.Outcome != (reject ? "failed" : "succeeded"))
                            throw new InvalidOperationException("Model crash fixture returned wrong outcome.");
                        if (mode is "bridge-model-checkpoint" or "bridge-model-generated")
                        {
                            var candidate = result.Candidate ?? throw new InvalidOperationException("Candidate missing.");
                            current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeCandidate(candidate), 2);
                        }

                        if (mode is "bridge-model-generated" or "bridge-model-rejected")
                            current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeCompletion(result.Completion), 7);
                    }

                    if (current.Ledger.Models != 1 || current.Ledger.ReservedSeconds != 30
                        || current.ArtifactHash is not null || current.S01 is not null || File.Exists(artifact))
                        throw new InvalidOperationException("Model crash fixture accepted a step or refunded budget.");
                    owner.ValidateOrderJournalFiles();
                    Console.WriteLine("BRIDGE_MODEL_CRASH_READY");
                    Console.Out.Flush();
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                    return 1;
                }

                if (mode is "bridge-guest" or "bridge-guest-unknown" or "bridge-guest-orphan")
                {
                    var guestReservation = current.Ledger.Propose(claim.JobId, "S01", "guest", 30, DateTime.UtcNow.Ticks, null, FixtureSnapshotHash);
                    Reject(() => owner.RequireOrderSnapshotManifest(guestReservation));
                    owner.StoreOrderSnapshotManifest(guestReservation, FixtureSnapshotManifest.Value);
                    VerifyFixtureSnapshot(owner, guestReservation);
                    Reject(() => owner.StoreOrderSnapshotManifest(guestReservation, FixtureSnapshotManifest.Value));
                    current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeReservation(guestReservation), 2);
                    if (current.Ledger.Pending != guestReservation || current.Ledger.Guests != 1
                        || current.Ledger.ReservedSeconds != 30)
                        throw new InvalidOperationException("Guest reservation acknowledgement differs.");
                    var missingDispatch = ObserveFixtureGuest(claim, guestReservation, FixtureGuestReport(guestReservation))
                        ?? throw new InvalidOperationException("Fixture observation missing.");
                    Reject(() => owner.StoreOrderGuestReport(missingDispatch, guestReservation, FixtureGuestReport(guestReservation)));
                    Reject(() => owner.ConsumeOrderGuestDispatch(current.Ledger,
                        guestReservation with { ReservedSeconds = 31 }));
                    owner.ConsumeOrderGuestDispatch(current.Ledger, guestReservation);
                    owner.RequireOrderGuestDispatch(guestReservation);
                    Reject(() => owner.ConsumeOrderGuestDispatch(current.Ledger, guestReservation));
                    if (mode == "bridge-guest-unknown")
                    {
                        // Simulate interruption after the durable reservation
                        // and before any trustworthy result is available.
                        owner.ValidateOrderJournalFiles();
                        Console.WriteLine("BRIDGE_GUEST_UNKNOWN");
                        Console.Out.Flush();
                        await Task.Delay(Timeout.InfiniteTimeSpan);
                        return 1;
                    }
                    byte[] guestBytes = FixtureGuestReport(guestReservation);
                    var guestOutcome = ObserveFixtureGuest(claim, guestReservation, guestBytes)
                        ?? throw new InvalidOperationException("Fixture guest observation missing.");
                    owner.StoreOrderGuestReport(guestOutcome, guestReservation, guestBytes);
                    Reject(() => owner.StoreOrderGuestReport(guestOutcome, guestReservation, guestBytes));
                    if (mode == "bridge-guest-orphan")
                    {
                        // Crash window: exact private bytes are durable,
                        // but no guest outcome event has been appended.
                        owner.ValidateOrderJournalFiles();
                        Console.WriteLine("BRIDGE_GUEST_ORPHAN");
                        Console.Out.Flush();
                        await Task.Delay(Timeout.InfiniteTimeSpan);
                        return 1;
                    }
                    current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeGuestOutcome(guestOutcome), 6);
                    if (current.Ledger.Pending is not null || current.Ledger.LatestGuestOutcome != guestOutcome
                        || current.Ledger.Guests != 1 || current.Ledger.ReservedSeconds != 30
                        || current.S01 is not null || current.ArtifactHash is not null)
                        throw new InvalidOperationException("Guest outcome became accepted step or refunded budget.");
                    owner.ValidateOrderJournalFiles();
                    Console.WriteLine("BRIDGE_GUEST");
                    Console.Out.Flush();
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                    return 1;
                }
                var reserved = ProposeModel(current.Ledger, "S01", 30, DateTime.UtcNow.Ticks);
                current = softScenario ? await AdmitAsync(reserved, 2)
                    : await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeReservation(reserved), 2);
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
                        var failure = new OrderOperationEvidence(2, claim.ArchiveHash, reserved,
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
                    if (softScenario)
                    {
                        var request = ProposeModel(current.Ledger, "S02", 50, DateTime.UtcNow.Ticks);
                        await RejectAdmissionAsync(request, raw: true);
                        current = await AdmitAsync(request, 3);
                        if (current.Ledger.SoftStop?.Request != request || current.Ledger.SoftStop.Reason != "time"
                            || current.Ledger.BudgetStop is not null || current.Ledger.Pending is not null
                            || current.Ledger.Sequence != 1 || current.Ledger.ReservedSeconds != 30)
                            throw new InvalidOperationException("Supervisor did not durably stop before optional work.");
                        await RejectAdmissionAsync(request, raw: false);
                        owner.ValidateOrderJournalFiles();
                        Console.WriteLine("BRIDGE_SOFT_STOPPED");
                        Console.Out.Flush();
                        await Task.Delay(Timeout.InfiniteTimeSpan);
                        return 1;
                    }

                    var denied = ProposeModel(current.Ledger, "S02", 100, DateTime.UtcNow.Ticks);
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

            if (softScenario)
            {
                var recoveredStop = current.Ledger.SoftStop ?? throw new InvalidOperationException("Soft stop not replayed.");
                var accepted = current.S01 ?? throw new InvalidOperationException("Accepted fixture S01 missing.");
                VerifyFixtureOutcome(owner, claim, accepted);
                if (current.Stage != 3 || current.ArtifactHash != AcceptedHash || current.Ledger.Pending is not null
                    || current.Ledger.BudgetStop is not null || current.Ledger.Sequence != 1
                    || current.Ledger.Models != 1 || current.Ledger.ReservedSeconds != 30
                    || recoveredStop.Request.Step != "S02" || recoveredStop.Reason != "time"
                    || !recoveredStop.RequiredAfter.SequenceEqual(FixtureRequiredAfter("S02", policy)))
                    throw new InvalidOperationException("Replayed soft stop differs.");
                var request = ProposeModel(current.Ledger, "S02", 50, DateTime.UtcNow.Ticks);
                await RejectAdmissionAsync(request, raw: false);
                await RejectAdmissionAsync(request, raw: true);
                long originalSoftStart = current.Ledger.StartedUtcTicks;
                string insufficientHash = DevelopmentJobState.Hash(Encoding.UTF8.GetBytes("synthetic insufficient soft extension"));
                var insufficient = new OrderBudgetExtensionApproved(1, claim.JobId, claim.ArchiveHash, 0,
                    limits with { Seconds = 125 }, insufficientHash, DateTime.UtcNow.Ticks);
                current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeBudgetExtension(insufficient), 3);
                if (current.Ledger.SoftStop is null || current.Ledger.BudgetRevision != 1
                    || current.Ledger.Sequence != 1 || current.Ledger.ReservedSeconds != 30)
                    throw new InvalidOperationException("Insufficient extension cleared soft stop.");
                await RejectAdmissionAsync(request with { ObservedUtcTicks = DateTime.UtcNow.Ticks }, raw: false);
                string sufficientHash = DevelopmentJobState.Hash(Encoding.UTF8.GetBytes("synthetic sufficient soft extension"));
                var sufficient = new OrderBudgetExtensionApproved(1, claim.JobId, claim.ArchiveHash, 1,
                    policy.Maximums, sufficientHash, DateTime.UtcNow.Ticks);
                current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeBudgetExtension(sufficient), 3);
                if (current.Ledger.SoftStop is not null || current.Ledger.BudgetStop is not null
                    || current.Ledger.BudgetRevision != 2 || current.Ledger.StartedUtcTicks != originalSoftStart
                    || current.Ledger.Sequence != 1 || current.Ledger.ReservedSeconds != 30 || current.S01 != accepted)
                    throw new InvalidOperationException("Sufficient extension lost original state.");
                VerifyFixtureOutcome(owner, claim, accepted);
                var resumedRequest = request with { ObservedUtcTicks = DateTime.UtcNow.Ticks };
                current = await AdmitAsync(resumedRequest, 4);
                if (current.Ledger.Pending != resumedRequest || current.Ledger.Sequence != 2 || current.Ledger.Models != 2
                    || current.Ledger.ReservedSeconds != 80 || current.Ledger.StartedUtcTicks != originalSoftStart
                    || current.S01 != accepted || File.Exists(database + ".s02"))
                    throw new InvalidOperationException("Fresh admitted reservation differs.");
                // Stop at the fresh reservation; no transport or S02 acceptance.
                Console.WriteLine("BRIDGE_SOFT_RESUMED");
                return 0;
            }

            if (modelScenario)
            {
                var observation = DevelopmentOrderModelReconciliation.Observe(owner, current.Ledger);
                var expected = mode switch
                {
                    "bridge-model-resume-blocked" => OrderModelRecoveryStatus.PendingBlocked,
                    "bridge-model-resume-generated" => OrderModelRecoveryStatus.Generated,
                    "bridge-model-resume-rejected" => OrderModelRecoveryStatus.Rejected,
                    _ => throw new InvalidOperationException("Unexpected model reentry mode.")
                };
                if (observation.Status != expected || current.Ledger.Models != 1
                    || current.Ledger.ReservedSeconds != 30 || current.ArtifactHash is not null
                    || current.S01 is not null || File.Exists(artifact)
                    || (observation.Candidate is not null) != (expected == OrderModelRecoveryStatus.Generated)
                    || (observation.Completion is null) != (expected == OrderModelRecoveryStatus.PendingBlocked)
                    || (current.Ledger.Pending is not null) != (expected == OrderModelRecoveryStatus.PendingBlocked))
                    throw new InvalidOperationException("Replayed model outcome differs.");
                owner.RequireOrderModelDispatch(observation.Reservation);
                if (expected == OrderModelRecoveryStatus.Generated)
                {
                    var files = DevelopmentOrderCalculatorVerifier.Prepare(owner, current.Ledger);
                    var candidate = observation.Candidate ?? throw new InvalidOperationException("Missing candidate.");
                    if (files.Count != 4 || !files.ContainsKey("Demo.csproj") || !files.ContainsKey("Program.cs")
                        || !files.ContainsKey("PairCalculatorVerifier.cs")
                        || !files["PairCalculator.cs"].SequenceEqual(owner.RequireOrderCandidate(candidate))
                        || !Encoding.UTF8.GetString(files["Program.cs"]).Contains(
                            DevelopmentOrderCalculatorVerifier.ExpectedStdout, StringComparison.Ordinal))
                        throw new InvalidOperationException("Calculator verifier input differs.");
                    files["PairCalculator.cs"][0] ^= 1;
                    if (!DevelopmentOrderCalculatorVerifier.Prepare(owner, current.Ledger)["PairCalculator.cs"]
                        .SequenceEqual(owner.RequireOrderCandidate(candidate)))
                        throw new InvalidOperationException("Verifier preparation changed protected bytes.");
                }
                else
                {
                    Reject(() => DevelopmentOrderCalculatorVerifier.Prepare(owner, current.Ledger));
                }

                if (expected == OrderModelRecoveryStatus.PendingBlocked)
                {
                    Reject(() => owner.ConsumeOrderModelDispatch(current.Ledger, observation.Reservation));
                    var blockedRetry = ProposeModel(current.Ledger, "S01", 30, DateTime.UtcNow.Ticks);
                    Reject(() => current.Ledger.Reserve(claim.JobId, limits, blockedRetry));
                }
                else
                {
                    string evidence = database + ".evidence-1";
                    byte[] original = File.ReadAllBytes(evidence);
                    File.WriteAllBytes(evidence, [.. original, 0x20]);
                    try
                    {
                        Reject(() => DevelopmentOrderModelReconciliation.Observe(owner, current.Ledger));
                        Reject(() => DevelopmentOrderCalculatorVerifier.Prepare(owner, current.Ledger));
                    }
                    finally { File.WriteAllBytes(evidence, original); }
                    _ = DevelopmentOrderModelReconciliation.Observe(owner, current.Ledger);
                }

                if (await ReadAsync() != current)
                    throw new InvalidOperationException("Read-only model recovery mutated the actor.");
                Console.WriteLine("BRIDGE_MODEL_RECOVERED");
                return 0;
            }

            if (mode is "bridge-guest-blocked" or "bridge-guest-orphan-resume")
            {
                var pendingGuest = current.Ledger.Pending
                    ?? throw new InvalidOperationException("Pending guest missing after replay.");
                if (current.Stage != 2 || pendingGuest.Kind != "guest" || pendingGuest.Step != "S01"
                    || pendingGuest.OperationId != claim.JobId + ":1" || current.Ledger.Sequence != 1
                    || current.Ledger.Guests != 1 || current.Ledger.Models != 0 || current.Ledger.ReservedSeconds != 30
                    || current.Ledger.LatestGuestOutcome is not null || current.Ledger.LastOutcome is not null
                    || current.S01 is not null || current.S02 is not null || current.ArtifactHash is not null)
                    throw new InvalidOperationException("Unknown guest reservation changed during replay.");

                TestFixtureSnapshotFaults(owner, pendingGuest, database);
                TestGuestDispatchFaults(owner, pendingGuest, database);
                Reject(() => owner.ConsumeOrderGuestDispatch(current.Ledger, pendingGuest));
                string recoveredRunId = DevelopmentOrderGuestReconciliation.RunId(pendingGuest);
                string absent = "error: sandbox 'hami-code-" + recoveredRunId[..12] + "' not found";
                string orphanPath = database + ".guest-report-1";
                long observedAt = DateTime.UtcNow.Ticks;
                byte[] expectedBytes = FixtureGuestReport(pendingGuest);
                // A descriptor fabricated from expected fixture bytes is not
                // evidence that these bytes were actually persisted.
                var expectedOutcome = ObserveFixtureGuest(claim, pendingGuest, expectedBytes)
                    ?? throw new InvalidOperationException("Fixture observation missing.");
                Reject(() => current.Ledger.Complete(new(2, pendingGuest.OperationId,
                    expectedOutcome.ReportHash, "failed", observedAt)));
                Reject(() => current.Ledger.Reserve(claim.JobId, limits,
                    current.Ledger.Propose(claim.JobId, "S01", "guest", 1, observedAt, null, FixtureSnapshotHash)));
                Reject(() => current.Ledger.Reserve(claim.JobId, limits,
                    ProposeModel(current.Ledger, "S02", 1, observedAt)));

                if (mode == "bridge-guest-blocked")
                {
                    if (File.Exists(orphanPath))
                        throw new InvalidOperationException("Unexpected report for interrupted fixture.");
                    Reject(() => owner.RequireOrderGuestReport(expectedOutcome, pendingGuest));
                    if (DevelopmentOrderGuestReconciliation.Observe(claim.ArchiveHash, pendingGuest,
                        null, 1, "", absent, observedAt) is not null)
                        throw new InvalidOperationException("Absence alone settled unknown guest.");
                    if (await ReadAsync() != current)
                        throw new InvalidOperationException("Blocked reconciliation mutated journal.");
                    guard.RequireObserved();
                    Console.WriteLine("BRIDGE_PHASE: pending guest retained; no report and no new execution.");
                    Console.WriteLine("BRIDGE_GUEST_BLOCKED");
                    return 0;
                }

                // Fixture only: expected report hash derives from known fixed
                // reference bytes, not from arbitrary orphan data. Production
                // must establish trusted dispatch/snapshot/report provenance.
                byte[] orphanBytes = owner.RequireOrderGuestReport(expectedOutcome, pendingGuest);
                if (!orphanBytes.SequenceEqual(expectedBytes))
                    throw new InvalidOperationException("Orphan report differs from fixture reference.");
                if (DevelopmentOrderGuestReconciliation.Observe(claim.ArchiveHash, pendingGuest,
                    orphanBytes, 0, "existing sandbox", "", observedAt) is not null
                    || DevelopmentOrderGuestReconciliation.Observe(claim.ArchiveHash, pendingGuest,
                        orphanBytes, 1, "", "backend unavailable", observedAt) is not null)
                    throw new InvalidOperationException("Unconfirmed sandbox settled orphan report.");
                File.WriteAllBytes(orphanPath, [.. orphanBytes, 0x20]);
                try { Reject(() => owner.RequireOrderGuestReport(expectedOutcome, pendingGuest)); }
                finally { File.WriteAllBytes(orphanPath, orphanBytes); }
                Reject(() => owner.StoreOrderGuestReport(expectedOutcome, pendingGuest, orphanBytes));
                if (await ReadAsync() != current)
                    throw new InvalidOperationException("Orphan file became journal authority.");
                var verifiedGuest = ObserveFixtureGuest(claim, pendingGuest,
                    owner.RequireOrderGuestReport(expectedOutcome, pendingGuest))
                    ?? throw new InvalidOperationException("Fresh fixture observation missing.");
                current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeGuestOutcome(verifiedGuest), 6);
                if (current.Ledger.Pending is not null || current.Ledger.LatestGuestOutcome != verifiedGuest
                    || current.Ledger.Sequence != 1 || current.Ledger.Guests != 1
                    || current.Ledger.Models != 0 || current.Ledger.ReservedSeconds != 30
                    || current.S01 is not null || current.ArtifactHash is not null)
                    throw new InvalidOperationException("Orphan reconciliation changed charge or accepted step.");
                VerifyFixtureGuest(owner, claim, verifiedGuest);
                guard.RequireObserved();
                Console.WriteLine("BRIDGE_PHASE: orphan report reconciled by explicit fixture observation, no redispatch.");
                Console.WriteLine("BRIDGE_GUEST_ORPHAN_RECOVERED");
                return 0;
            }

            if (mode == "bridge-guest-resume")
            {
                var recoveredGuest = current.Ledger.LatestGuestOutcome
                    ?? throw new InvalidOperationException("Guest outcome missing after replay.");
                if (current.Stage != 6 || current.Ledger.Pending is not null || current.Ledger.Guests != 1
                    || current.Ledger.Models != 0 || current.Ledger.ReservedSeconds != 30
                    || current.S01 is not null || current.S02 is not null || current.ArtifactHash is not null)
                    throw new InvalidOperationException("Guest outcome changed acceptance or budget.");
                TestFixtureSnapshotFaults(owner, recoveredGuest.Reservation, database);
                TestGuestDispatchFaults(owner, recoveredGuest.Reservation, database);
                Reject(() => owner.ConsumeOrderGuestDispatch(current.Ledger, recoveredGuest.Reservation));
                VerifyFixtureGuest(owner, claim, recoveredGuest);
                await TestProtectedGuestInspectionAsync(owner, recoveredGuest, database);
                if (await ReadAsync() != current)
                    throw new InvalidOperationException("Read-only guest observation changed journal.");
                string guestPath = database + ".guest-report-1";
                byte[] originalGuest = owner.RequireOrderGuestReport(recoveredGuest, recoveredGuest.Reservation);
                File.WriteAllBytes(guestPath, [.. originalGuest, 0x20]);
                try { Reject(() => VerifyFixtureGuest(owner, claim, recoveredGuest)); }
                finally { File.WriteAllBytes(guestPath, originalGuest); }
                File.Move(guestPath, guestPath + ".saved");
                try { Reject(() => VerifyFixtureGuest(owner, claim, recoveredGuest)); }
                finally { File.Move(guestPath + ".saved", guestPath); }
                Reject(() => owner.RequireOrderGuestReport(recoveredGuest,
                    recoveredGuest.Reservation with { ReservedSeconds = 31 }));
                Reject(() => owner.RequireOrderGuestReport(recoveredGuest with { ArchiveHash = new string('C', 64) },
                    recoveredGuest.Reservation));
                Reject(() => owner.StoreOrderGuestReport(recoveredGuest, recoveredGuest.Reservation, originalGuest));
                Reject(() => current.Ledger.CompleteGuest(recoveredGuest));
                VerifyFixtureGuest(owner, claim, recoveredGuest);
                guard.RequireObserved();
                Console.WriteLine("BRIDGE_PHASE: protected guest report revalidated; no step accepted.");
                Console.WriteLine("BRIDGE_GUEST_RECOVERED");
                return 0;
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
                    ProposeModel(current.Ledger, "S01", 30, DateTime.UtcNow.Ticks)));
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
                string failureReceipt = database + ".evidence-1";
                byte[] originalFailureReceipt = File.ReadAllBytes(failureReceipt);
                File.WriteAllBytes(failureReceipt, [.. originalFailureReceipt, 0x20]);
                try { Reject(() => VerifyFixtureFailure(owner, claim, failed, candidate)); }
                finally { File.WriteAllBytes(failureReceipt, originalFailureReceipt); }
                VerifyFixtureFailure(owner, claim, failed, candidate);

                long originalRetryStart = current.Ledger.StartedUtcTicks;
                var retry = ProposeModel(current.Ledger, "S01", 30, DateTime.UtcNow.Ticks);
                current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeReservation(retry), 2);
                if (retry.OperationId != claim.JobId + ":2" || current.Ledger.Models != 2
                    || current.Ledger.ReservedSeconds != 60 || current.Ledger.StartedUtcTicks != originalRetryStart
                    || current.Ledger.LatestCandidate != candidate)
                    throw new InvalidOperationException("Retry reset original reservation or candidate.");
                Reject(() => owner.RequireOrderOperationEvidence(retry, failed.Completion));
                var retryAssignment = retry.Assignment ?? throw new InvalidOperationException("Retry assignment missing.");
                Reject(() => owner.RequireOrderOperationEvidence(failed.Reservation with
                {
                    Assignment = retryAssignment with { Revision = retryAssignment.Revision + 1 }
                }, failed.Completion));
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
                    ProposeModel(current.Ledger, "S01", 1, DateTime.UtcNow.Ticks)));
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
                ProposeModel(current.Ledger, "S02", 1, DateTime.UtcNow.Ticks)));
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
            var extendedLimits = current.Ledger.CurrentLimits ?? throw new InvalidOperationException("Effective budget missing.");
            Console.WriteLine("BRIDGE_PHASE: recovered budget stop; synthetic approved extension persisted.");

            // Reusing S01 is not a second write to its exclusive effect file.
            var next = ProposeModel(current.Ledger, "S02", 100, DateTime.UtcNow.Ticks);
            current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeReservation(next), 4);
            if (current.Ledger.Pending != next || current.Ledger.Models != 2
                || current.Ledger.ReservedSeconds != 130)
                throw new InvalidOperationException("S02 reservation acknowledgement differs.");
            _ = current.Ledger.DispatchAllowance(extendedLimits, next.OperationId, DateTime.UtcNow.Ticks);
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
    private sealed record Admit(OrderOperationReservation Request);

    // Reviewed disposable fixture only: the existing synthetic S01/S02
    // completion already represents accepted work. No dynamic estimator or
    // production verifier durations are asserted by these values.
    private static OrderRequiredOperation[] FixtureRequiredAfter(string step, DevelopmentOrderBudgetPolicy policy) =>
        step switch
        {
            "S01" => [new("S02", "model", 20), new("S02", "guest", 20), new("FINAL", "guest", policy.FinalVerificationSeconds)],
            "S02" => [new("S02", "guest", 20), new("FINAL", "guest", policy.FinalVerificationSeconds)],
            _ => throw new InvalidOperationException("Unexpected fixture admission step.")
        };
    private sealed record RecordedOperation(OrderOperationReservation Reservation, OrderOperationCompletion Completion);
    private sealed record BridgeState(int Stage, string? ArtifactHash, DevelopmentOrderOperationLedger Ledger,
        RecordedOperation? S01 = null, RecordedOperation? S02 = null, RecordedOperation? FailedS01 = null);

    private sealed class BridgeActor : ReceivePersistentActor
    {
        private BridgeState state = new(0, null, new());
        private bool recovered;
        private readonly DevelopmentOrderClaim claim;
        private readonly DevelopmentOrderArchive archive;
        private readonly OrderOperationLimits limits;
        private readonly DevelopmentOrderBudgetPolicy policy;
        private readonly bool modelScenario;
        private readonly bool softScenario;

        public override string PersistenceId { get; }

        public BridgeActor(DevelopmentOrderClaim claim, DevelopmentOrderArchive archive,
            OrderOperationLimits limits, DevelopmentOrderBudgetPolicy policy, DevelopmentJobStateStore.Lease owner,
            bool modelScenario, bool softScenario)
        {
            this.modelScenario = modelScenario;
            this.softScenario = softScenario;
            PersistenceId = claim.PersistenceId;
            this.claim = claim;
            this.archive = archive;
            this.limits = limits;
            this.policy = policy;
            // Recovery only folds acknowledged journal events; no effects.
            Recover<string>(evt => state = Next(state, evt));
            Recover<RecoveryCompleted>(_ => recovered = true);
            Command<ReadState>(_ => Sender.Tell(recovered ? state :
                throw new InvalidOperationException("Recovery incomplete.")));
            Command<Admit>(command =>
            {
                if (!recovered || !softScenario || state.Ledger.SoftStop is not null
                    || state.Ledger.BudgetStop is not null || state.Ledger.Pending is not null)
                {
                    Sender.Tell(new Status.Failure(new InvalidOperationException("Fixture admission blocked.")));
                    return;
                }

                var request = command.Request;
                var effective = state.Ledger.CurrentLimits ?? throw new InvalidOperationException("Effective budget missing.");
                var required = FixtureRequiredAfter(request.Step, policy);
                string? hard = state.Ledger.BudgetDenial(claim.JobId, effective, request);
                string? soft = state.Ledger.SoftStopDenial(claim.JobId, effective, request, required);
                string evt;
                if (hard is not null)
                    evt = DevelopmentOrderOperationLedger.EncodeBudgetStop(new(1, request, hard));
                else if (soft is not null)
                    evt = DevelopmentOrderOperationLedger.EncodeSoftStop(new(1, request, required, soft));
                else
                {
                    _ = state.Ledger.ReserveWithRequiredWork(claim.JobId, effective, request, required);
                    evt = DevelopmentOrderOperationLedger.EncodeReservation(request);
                }

                BridgeState next = Next(state, evt);
                IActorRef reply = Sender;
                Persist(evt, _ =>
                {
                    state = next;
                    reply.Tell(state);
                });
            });
            Command<Advance>(command =>
            {
                if (!recovered)
                    throw new InvalidOperationException("Recovery incomplete.");
                if (softScenario && (command.Event.StartsWith("reserve-v4:", StringComparison.Ordinal)
                    || command.Event.StartsWith("soft-stop-v1:", StringComparison.Ordinal)
                    || command.Event.StartsWith("budget-stop-v1:", StringComparison.Ordinal)))
                {
                    Sender.Tell(new Status.Failure(new InvalidOperationException("Use supervisor admission.")));
                    return;
                }

                BridgeState next = Next(state, command.Event);
                // Only the live command boundary reads protected evidence.
                // Replay remains a pure fold and the host verifies recovered
                // receipts again before permitting any further work.
                if (modelScenario && command.Event.StartsWith("candidate-v1:", StringComparison.Ordinal))
                {
                    var reservation = state.Ledger.Pending ?? throw new InvalidOperationException("Missing model reservation.");
                    owner.RequireOrderModelDispatch(reservation);
                    _ = owner.RequireOrderCandidate(next.Ledger.LatestCandidate
                        ?? throw new InvalidOperationException("Missing candidate."));
                }
                if (command.Event.StartsWith("complete-v2:", StringComparison.Ordinal))
                {
                    if (modelScenario)
                    {
                        var observation = DevelopmentOrderModelReconciliation.Observe(owner, next.Ledger);
                        if (observation.Status == OrderModelRecoveryStatus.PendingBlocked)
                            throw new InvalidOperationException("Model completion has no verified evidence.");
                    }
                    else if (next.Ledger.LastOutcome == "failed")
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
                if (command.Event.StartsWith("reserve-v4:", StringComparison.Ordinal)
                    && next.Ledger.Pending is { Kind: "guest" } reservedGuest)
                    VerifyFixtureSnapshot(owner, reservedGuest);
                if (command.Event.StartsWith("guest-outcome-v1:", StringComparison.Ordinal))
                    VerifyFixtureGuest(owner, claim, next.Ledger.LatestGuestOutcome
                        ?? throw new InvalidOperationException("Guest outcome missing."));
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
                if (evt.StartsWith("soft-stop-v1:", StringComparison.Ordinal))
                {
                    var stop = DevelopmentOrderOperationLedger.DecodeSoftStop(evt);
                    if (!softScenario || stop.Request.Step != (previous.Stage == 1 ? "S01" : "S02")
                        || stop.Request.Kind != "model"
                        || !stop.RequiredAfter.SequenceEqual(FixtureRequiredAfter(stop.Request.Step, policy)))
                        throw new InvalidOperationException("Unexpected fixture soft stop.");
                    VerifyFixtureAssignment(claim, archive, stop.Request);
                    return previous with { Ledger = previous.Ledger.StopSoft(claim.JobId, effective, stop) };
                }
                if (evt.StartsWith("budget-stop-v1:", StringComparison.Ordinal))
                {
                    var stop = DevelopmentOrderOperationLedger.DecodeBudgetStop(evt);
                    VerifyFixtureAssignment(claim, archive, stop.Request);
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
                if (reservation.Kind == "model")
                {
                    if (modelScenario)
                        VerifyModelFixtureAssignment(claim, archive, reservation);
                    else
                        VerifyFixtureAssignment(claim, archive, reservation);
                }
                if (reservation.Step != (previous.Stage == 1 ? "S01" : "S02")
                    || (reservation.Kind != "model" && !(previous.Stage == 1 && reservation.Kind == "guest")))
                    throw new InvalidOperationException("Unexpected synthetic step operation.");
                return previous with
                {
                    Stage = previous.Stage + 1,
                    Ledger = softScenario
                        ? previous.Ledger.ReserveWithRequiredWork(claim.JobId, effective, reservation,
                            FixtureRequiredAfter(reservation.Step, policy))
                        : previous.Ledger.Reserve(claim.JobId, effective, reservation)
                };
            }
            if (previous.Stage is 2 or 4)
            {
                if (evt.StartsWith("guest-outcome-v1:", StringComparison.Ordinal))
                {
                    if (previous.Stage != 2 || previous.Ledger.Pending is not { Kind: "guest" })
                        throw new InvalidOperationException("Unexpected fixture guest outcome.");
                    return previous with
                    {
                        Stage = 6,
                        Ledger = previous.Ledger.CompleteGuest(DevelopmentOrderOperationLedger.DecodeGuestOutcome(evt))
                    };
                }
                if (evt.StartsWith("candidate-v1:", StringComparison.Ordinal))
                    return previous with
                    {
                        Ledger = previous.Ledger.Checkpoint(claim, DevelopmentOrderOperationLedger.DecodeCandidate(evt))
                    };
                var completion = DevelopmentOrderOperationLedger.DecodeCompletion(evt);
                var reservation = previous.Ledger.Pending ?? throw new InvalidOperationException("Operation missing.");
                if (modelScenario)
                {
                    if (previous.Stage != 2 || reservation.Kind != "model" || reservation.Step != "S01"
                        || (completion.Outcome == "succeeded") != (previous.Ledger.LatestCandidate?.OperationId == reservation.OperationId))
                        throw new InvalidOperationException("Unexpected model-only completion.");
                    return previous with
                    {
                        Stage = 7,
                        Ledger = previous.Ledger.Complete(completion)
                    };
                }

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
