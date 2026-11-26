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
    private static readonly byte[] CalculatorScope = Encoding.UTF8.GetBytes(
        """
        {"schemaVersion":1,"taskId":"sandbox-pair","description":"Reviewed offline pair fixture","targetFramework":"net11.0","allowedWritePaths":["src/HaMi.Agentic.Cli/Features/Development/PairCalculator.cs","src/HaMi.Agentic.Cli/Features/Development/PairFormatter.cs"],"acceptanceCriteria":["Exact fixture scope"]}
        """);

    private static string CalculatorRepository(string root) => Path.Combine(root, "calculator-repo");

    private static byte[] ReadCalculatorResource(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Calculator Git fixture resource missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // The only Git writes are to this disposable fixture repository. The
    // actual project checkout is neither modified nor used as mutable input.
    private static async Task CreateCalculatorRepositoryAsync(string root)
    {
        string repo = CalculatorRepository(root);
        Directory.CreateDirectory(repo);
        _ = await DevelopmentWorktreeGuard.GitAsync(repo, "init", "--initial-branch=main", "--template=");
        _ = await DevelopmentWorktreeGuard.GitAsync(repo, "config", "core.autocrlf", "false");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [DevelopmentAgentContract.PairPath] = ReadCalculatorResource("HaMi.Probe.PairContract"),
            [DevelopmentAgentContract.PairScopePath] = ReadCalculatorResource("HaMi.Probe.PairScope"),
            [DevelopmentAgentContract.PairFirst] = ModelBaseline,
            [DevelopmentAgentContract.PairSecond] = ModelBaseline
        };
        foreach (var file in files)
        {
            string path = Path.Combine(repo, file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Value);
        }
        _ = await DevelopmentWorktreeGuard.GitAsync(repo, ["add", "-f", "--", .. files.Keys]);
        _ = await DevelopmentWorktreeGuard.GitAsync(repo, "-c", "user.name=HaMi Offline Fixture",
            "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false",
            "-c", "core.hooksPath=" + Path.Combine(repo, ".git", "disabled-hooks"),
            "commit", "-m", "Reviewed calculator recovery fixture");
        _ = await DevelopmentWorktreeGuard.ObserveOrderBaselineAsync(repo);
    }

    internal static async Task<DevelopmentOrderApproval> CalculatorRecoveryApprovalAsync(string root) =>
        FixtureApproval(gitBaseline: await DevelopmentWorktreeGuard.ObserveOrderBaselineAsync(CalculatorRepository(root)));

    // The existing fixture actor is synchronous at its evidence boundary.
    // Production supervision must own cancellation and the execution deadline.
    private static OrderGitBaseline ObserveCalculatorBaseline(string root, OrderBinding binding) =>
        DevelopmentWorktreeGuard.ObserveOrderBaselineAsync(CalculatorRepository(root), binding).GetAwaiter().GetResult();

    // Disposable host-controlled stage, not a production checkout reader.
    // Materialize the exact protected candidate, not an expected model string.
    private static string CalculatorStage(string root) => Path.Combine(root, "calculator-stage");

    private static void CreateCalculatorStage(string root, DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger model)
    {
        string stage = CalculatorStage(root);
        if (Directory.Exists(stage))
            throw new InvalidOperationException("Calculator stage already exists.");
        Directory.CreateDirectory(stage);
        var candidate = model.LatestCandidate ?? throw new InvalidOperationException("Candidate missing.");
        File.WriteAllBytes(Path.Combine(stage, "PairCalculator.cs"), owner.RequireOrderCandidate(candidate));
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Claim missing.");
        File.WriteAllBytes(Path.Combine(stage, "PairFormatter.cs"), ObserveCalculatorBaseline(root, claim.Binding).Formatter);
    }

    private static Dictionary<string, byte[]> CalculatorRecoveryPair(string root) =>
        DevelopmentOrderCalculatorVerifier.ReadStagedPair(CalculatorStage(root));

    private static OrderGuestOutcome ObserveCalculatorFixture(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderClaim claim, OrderGuestOutcome outcome)
    {
        byte[] report = owner.RequireOrderGuestReport(outcome, outcome.Reservation);
        if (!DevelopmentOrderGuestReconciliation.IsCalculatorReport(outcome.Reservation, report))
            throw new InvalidOperationException("Calculator fixture report required.");
        return ObserveFixtureGuest(claim, outcome.Reservation, report)
            ?? throw new InvalidOperationException("Calculator fixture observation missing.");
    }

    private static OrderAssignment ModelFixtureAssignment(DevelopmentOrderClaim claim, DevelopmentOrderArchive archive,
        string step = "S01")
    {
        using var fake = new DevelopmentAiGenerator.ScriptedChatClient("{}");
        var generator = new DevelopmentAiGenerator(fake, ModelName);
        var original = FixtureAssignment(claim, archive, step);
        return original with
        {
            Agent = original.Agent with
            {
                Provider = "openai",
                Model = generator.ModelId,
                ConfigurationHash = step == "S01" ? generator.S01ConfigurationHash : generator.S02ConfigurationHash
            }
        };
    }

    private static void VerifyModelFixtureAssignment(DevelopmentOrderClaim claim, DevelopmentOrderArchive archive,
        OrderOperationReservation reservation) =>
        (reservation.Assignment ?? throw new InvalidOperationException("Missing model assignment."))
            .RequireAssignment(ModelFixtureAssignment(claim, archive, reservation.Step));

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
        foreach (string scenario in new[] { "success", "raw-replaced", "nonzero", "late-inspect-unknown", "late-inspect-timeout",
            "calculator-success", "calculator-wrong-output", "calculator-stderr", "calculator-extra-output" })
        {
            string root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            var store = DevelopmentJobStateRoot.OpenOrderProbeFixture(root, create: true);
            try
            {
                using var owner = store.AcquireOrder(Issue);
                bool calculator = scenario.StartsWith("calculator-", StringComparison.Ordinal);
                byte[] baseline = Encoding.UTF8.GetBytes("internal static class PairCalculator { }");
                var approval = FixtureApproval(scenario == "success" ? 40 : null,
                    calculator ? DevelopmentJobState.Hash(baseline) : null,
                    calculator ? DevelopmentJobState.Hash(CalculatorScope) : null);
                owner.ClaimOrder(Guid.NewGuid().ToString("N"), approval);
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
                long now = DateTime.UtcNow.Ticks;
                var ledger = new DevelopmentOrderOperationLedger().Start(new(1, claim.ArchiveHash, now),
                    claim.ArchiveHash, limits, policy);
                DevelopmentOrderOperationLedger? completedModel = null;
                if (calculator)
                {
                    var assignment = FixtureAssignment(claim, archive, "S01");
                    var model = ledger.Propose(claim.JobId, "S01", "model", 30, now,
                        OrderOperationAgentBinding.FromAssignment(assignment));
                    var pending = ledger.Reserve(claim.JobId, limits, model);
                    var generated = await DevelopmentOrderModelDispatch.RunAsync(owner, pending, model, approval,
                        assignment, [new(assignment.Agent, true, ["csharp"])], baseline,
                        (_, _) => Task.FromResult("internal static class PairCalculator { /* fixture */ }"));
                    completedModel = pending.Checkpoint(claim, generated.Candidate
                        ?? throw new InvalidOperationException("Fixture candidate missing.")).Complete(generated.Completion);
                    ledger = completedModel;
                    foreach (var file in DevelopmentOrderCalculatorVerifier.Prepare(owner, completedModel))
                        File.WriteAllBytes(Path.Combine(source, file.Key), file.Value);
                }
                else
                {
                    File.WriteAllText(Path.Combine(source, "Demo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
                    File.WriteAllText(Path.Combine(source, "Program.cs"), "Console.WriteLine(42);");
                }

                byte[] manifest = DevelopmentOrderSnapshot.CaptureBytes(source, "Demo.csproj");
                string hash = DevelopmentJobState.Hash(manifest);
                var reservation = completedModel is null
                    ? ledger.Propose(claim.JobId, "S01", "guest", 30, now, null, hash)
                    : DevelopmentOrderCalculatorVerifier.ProposeGuest(owner, completedModel, 30, DateTime.UtcNow.Ticks);
                if (completedModel is null)
                    owner.StoreOrderSnapshotManifest(reservation, manifest);
                else
                    DevelopmentOrderCalculatorVerifier.StoreGuestSnapshot(owner, completedModel, reservation, source);
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
                            else if (args.Contains("run", StringComparer.Ordinal))
                            {
                                string marker = DevelopmentOrderCalculatorVerifier.ExpectedStdout + "\n";
                                response = scenario switch
                                {
                                    "nonzero" => new(1, "", ""),
                                    "calculator-success" => new(0, marker, ""),
                                    "calculator-wrong-output" => new(0, "HaMi pair acceptance passed\n", ""),
                                    "calculator-stderr" => new(0, marker, "unexpected diagnostic"),
                                    "calculator-extra-output" => new(0, marker + "extra\n", ""),
                                    _ => response
                                };
                            }
                            break;
                    }
                    return Task.FromResult(response);
                }

                if (completedModel is not null)
                {
                    string program = Path.Combine(source, "Program.cs");
                    byte[] originalProgram = File.ReadAllBytes(program);
                    File.WriteAllBytes(program, [.. originalProgram, 0x20]);
                    bool rejectedSnapshot = false;
                    try
                    {
                        _ = await DevelopmentSandboxRunner.RunReservedCoreAsync(repo, source, results, owner,
                            ledger, reservation, Transport, (_, _, _, _) => { }, completedCalculatorModel: completedModel);
                    }
                    catch (InvalidOperationException ex) when (ex.Message == "ORDER_CALCULATOR_VERIFIER_SNAPSHOT")
                    { rejectedSnapshot = true; }
                    finally { File.WriteAllBytes(program, originalProgram); }
                    if (!rejectedSnapshot || calls != 0 || File.Exists(journal + ".guest-dispatch-2"))
                        throw new InvalidOperationException("Wrong calculator snapshot reached transport.");
                }

                OrderGuestOutcome? outcome = null;
                bool timeout = false;
                try
                {
                    outcome = await DevelopmentSandboxRunner.RunReservedCoreAsync(repo, source, results, owner,
                        ledger, reservation, Transport, (_, _, _, _) => { }, completedCalculatorModel: completedModel);
                }
                catch (TimeoutException ex) when (scenario == "late-inspect-timeout" && ex.Message == "fixture late inspect")
                {
                    timeout = true;
                }

                bool success = scenario is "success" or "raw-replaced" or "calculator-success";
                bool unsuccessfulExecution = scenario == "nonzero" || calculator && !success;
                if ((outcome is not null) != success || timeout != (scenario == "late-inspect-timeout")
                    || !created || !removed || afterReturn != (unsuccessfulExecution ? 0 : 1)
                    || !File.Exists(raw) || ledger.Pending != reservation || ledger.Guests != 1)
                    throw new InvalidOperationException("Live guest capture outcome differs: " + scenario);
                owner.RequireOrderGuestDispatch(reservation);
                string slot = journal + (calculator ? ".guest-report-2" : ".guest-report-1");
                if (success)
                {
                    byte[] captured = owner.RequireOrderGuestReport(outcome!, reservation);
                    if (original is null || !captured.SequenceEqual(original)
                        || DevelopmentJobState.Hash(captured) != outcome!.ReportHash
                        || !DevelopmentOrderGuestReconciliation.IsSuccessfulReport(reservation, captured)
                        || DevelopmentOrderGuestReconciliation.IsCalculatorReport(reservation, captured) != calculator
                        || (scenario == "raw-replaced" && captured.SequenceEqual(File.ReadAllBytes(raw))))
                        throw new InvalidOperationException("Protected report differs from live bytes.");
                    if (completedModel is not null)
                    {
                        var completed = ledger.CompleteGuest(outcome!);
                        var observed = ObserveFixtureGuest(claim, reservation, captured);
                        var candidate = DevelopmentOrderCalculatorVerifier.RequireCompletedGuest(owner,
                            completedModel, completed, observed);
                        if (candidate != completedModel.LatestCandidate || candidate.Quality != "Generated"
                            || completed.Pending is not null || completed.Guests != 1
                            || completed.ReservedSeconds != 60)
                            throw new InvalidOperationException("Correlated calculator evidence changed history.");

                        Reject(() => DevelopmentOrderCalculatorVerifier.RequireCompletedGuest(owner,
                            completedModel, ledger, observed));
                        Reject(() => DevelopmentOrderCalculatorVerifier.RequireCompletedGuest(owner,
                            completedModel, completed, null));
                        Reject(() => DevelopmentOrderCalculatorVerifier.RequireCompletedGuest(owner,
                            completedModel, completed, outcome! with { ObservedUtcTicks = outcome!.ObservedUtcTicks - 1 }));
                        Reject(() => DevelopmentOrderCalculatorVerifier.RequireCompletedGuest(owner,
                            completedModel, completed with { LastEvidenceHash = new string('0', 64) }, observed));
                        Reject(() => DevelopmentOrderCalculatorVerifier.RequireCompletedGuest(owner,
                            completedModel, completed with { ReservedSeconds = 0 }, observed));

                        byte[] changed = captured.ToArray();
                        changed[0] ^= 1;
                        File.WriteAllBytes(slot, changed);
                        try
                        {
                            Reject(() => DevelopmentOrderCalculatorVerifier.RequireCompletedGuest(owner,
                                completedModel, completed, observed));
                        }
                        finally { File.WriteAllBytes(slot, captured); }

                        string candidateSlot = journal + ".candidate-1";
                        byte[] candidateBytes = owner.RequireOrderCandidate(candidate);
                        changed = candidateBytes.ToArray();
                        changed[0] ^= 1;
                        File.WriteAllBytes(candidateSlot, changed);
                        try
                        {
                            Reject(() => DevelopmentOrderCalculatorVerifier.RequireCompletedGuest(owner,
                                completedModel, completed, observed));
                        }
                        finally { File.WriteAllBytes(candidateSlot, candidateBytes); }

                        if (DevelopmentOrderCalculatorVerifier.RequireCompletedGuest(owner,
                            completedModel, completed, observed) != candidate)
                            throw new InvalidOperationException("Read-only calculator correlation changed.");
                        Console.WriteLine("PASS: historical calculator guest correlation requires adjacent completed ledger, exact protected evidence and fresh observation; Generated remains unaccepted.");

                        var pair = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                        {
                            [DevelopmentAgentContract.PairFirst] = candidateBytes,
                            [DevelopmentAgentContract.PairSecond] = ModelBaseline.ToArray()
                        };
                        long acceptedAt = DateTime.UtcNow.Ticks;
                        OrderS01Accepted Propose() => DevelopmentOrderCalculatorVerifier.ProposeAcceptance(owner,
                            completedModel, completed, observed, claim.Binding.BaseCommit, CalculatorScope,
                            baseline, ModelBaseline, pair, acceptedAt);
                        var acceptance = Propose();
                        string evt = DevelopmentOrderOperationLedger.EncodeS01Accepted(acceptance);
                        var accepted = completed.AcceptS01(claim, DevelopmentOrderOperationLedger.DecodeS01Accepted(evt));
                        if (accepted.AcceptedS01 != acceptance || accepted.LatestCandidate != candidate
                            || accepted.ReservedSeconds != completed.ReservedSeconds || accepted.Sequence != completed.Sequence
                            || accepted.StartedUtcTicks != completed.StartedUtcTicks)
                            throw new InvalidOperationException("S01 acceptance changed operation history.");

                        Reject(() => accepted.AcceptS01(claim, acceptance));
                        Reject(() => completed.AcceptS01(claim, acceptance with { GuestReportHash = new string('0', 64) }));
                        Reject(() => completed.AcceptS01(claim, acceptance with { ScopeHash = new string('0', 64) }));
                        Reject(() => completed.AcceptS01(claim, acceptance with { ObservedUtcTicks = acceptedAt - TimeSpan.TicksPerMinute }));
                        Reject(() => completed.AcceptS01(claim, acceptance with
                        {
                            ObservedUtcTicks = completed.StartedUtcTicks + TimeSpan.TicksPerDay
                        }));
                        Reject(() => accepted.Reserve(claim.JobId, limits, accepted.Propose(claim.JobId,
                            "S01", "guest", 1, acceptedAt, null, reservation.SnapshotHash)));
                        Reject(() => DevelopmentOrderCalculatorVerifier.ProposeAcceptance(owner,
                            completedModel, completed, observed, new string('0', 40), CalculatorScope,
                            baseline, ModelBaseline, pair, acceptedAt));
                        Reject(() => DevelopmentOrderCalculatorVerifier.ProposeAcceptance(owner,
                            completedModel, completed, observed, claim.Binding.BaseCommit, [.. CalculatorScope, 0x20],
                            baseline, ModelBaseline, pair, acceptedAt));
                        Reject(() => DevelopmentOrderCalculatorVerifier.ProposeAcceptance(owner,
                            completedModel, completed, observed, claim.Binding.BaseCommit, CalculatorScope,
                            [.. baseline, 0x20], ModelBaseline, pair, acceptedAt));
                        pair[DevelopmentAgentContract.PairSecond] = [.. ModelBaseline, 0x20];
                        Reject(() => Propose());
                        pair[DevelopmentAgentContract.PairSecond] = ModelBaseline.ToArray();
                        pair[DevelopmentAgentContract.PairFirst] = baseline;
                        Reject(() => Propose());
                        pair[DevelopmentAgentContract.PairFirst] = candidateBytes;
                        pair.Add("unexpected.cs", [1]);
                        Reject(() => Propose());
                        pair.Remove("unexpected.cs");
                        pair.Remove(DevelopmentAgentContract.PairSecond);
                        Reject(() => Propose());
                        pair[DevelopmentAgentContract.PairSecond] = ModelBaseline.ToArray();
                        if (Propose() != acceptance)
                            throw new InvalidOperationException("Rejected scope checks changed accepted evidence.");
                        Console.WriteLine("PASS: bounded S01 baseline and scope acceptance proposal, canonical event fold, no duplicate acceptance or S01 redispatch; tampering rejected.");
                    }
                }
                else if (File.Exists(slot))
                    throw new InvalidOperationException("Failed runner or inspect wrote protected report.");

                int before = calls;
                bool rejected = false;
                try
                {
                    _ = await DevelopmentSandboxRunner.RunReservedCoreAsync(repo, source, results, owner,
                        ledger, reservation, Transport, (_, _, _, _) => { }, completedCalculatorModel: completedModel);
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
        Console.WriteLine("PASS: calculator-specific runner binds model and guest ledgers, exact snapshot, semantic stdout and clean stderr to protected report; mismatches and redispatch blocked.");
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
                    TestCalculatorSnapshotBinding(owner, completed, root, journal);
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

    private static void TestCalculatorSnapshotBinding(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger completed, string root, string journal)
    {
        string source = Path.Combine(root, "calculator-verification");
        Directory.CreateDirectory(source);
        var files = DevelopmentOrderCalculatorVerifier.Prepare(owner, completed);
        foreach (var file in files)
            File.WriteAllBytes(Path.Combine(source, file.Key), file.Value);

        var proposal = DevelopmentOrderCalculatorVerifier.ProposeGuest(owner, completed, 30, DateTime.UtcNow.Ticks);
        var limits = completed.CurrentLimits ?? throw new InvalidOperationException("Missing limits.");
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Missing claim.");
        // Synthetic admission only; no journal append or transport.
        var admitted = completed.ReserveWithRequiredWork(claim.JobId, limits, proposal,
            [new("S02", "model", 10), new("S02", "guest", 10), new("FINAL", "guest", completed.FinalReserveSeconds)]);
        string slot = journal + ".snapshot-2";
        if (proposal.Step != "S01" || proposal.Kind != "guest" || admitted.Pending != proposal
            || proposal.SnapshotHash != DevelopmentOrderSnapshot.Capture(source, "Demo.csproj")
            || completed.Pending is not null || completed.Guests != 0 || admitted.Guests != 1
            || admitted.ReservedSeconds != completed.ReservedSeconds + 30 || File.Exists(slot))
            throw new InvalidOperationException("Calculator guest proposal differs.");

        Reject(() => DevelopmentOrderCalculatorVerifier.RequireGuestSnapshot(owner, completed, proposal, source));
        Reject(() => DevelopmentOrderCalculatorVerifier.StoreGuestSnapshot(owner, completed,
            proposal with { Step = "S02" }, source));
        Reject(() => DevelopmentOrderCalculatorVerifier.StoreGuestSnapshot(owner, completed,
            proposal with { SnapshotHash = new string('A', 64) }, source));
        Reject(() => DevelopmentOrderCalculatorVerifier.StoreGuestSnapshot(owner, completed,
            proposal with { OperationId = claim.JobId + ":3" }, source));

        foreach (var file in files)
        {
            string path = Path.Combine(source, file.Key);
            byte[] changed = file.Value.ToArray();
            changed[0] ^= 1;
            File.WriteAllBytes(path, changed);
            try { Reject(() => DevelopmentOrderCalculatorVerifier.StoreGuestSnapshot(owner, completed, proposal, source)); }
            finally { File.WriteAllBytes(path, file.Value); }
        }

        string extra = Path.Combine(source, "Extra.cs");
        File.WriteAllText(extra, "// forbidden extra");
        try { Reject(() => DevelopmentOrderCalculatorVerifier.StoreGuestSnapshot(owner, completed, proposal, source)); }
        finally { File.Delete(extra); }
        string program = Path.Combine(source, "Program.cs");
        File.Delete(program);
        try { Reject(() => DevelopmentOrderCalculatorVerifier.StoreGuestSnapshot(owner, completed, proposal, source)); }
        finally { File.WriteAllBytes(program, files["Program.cs"]); }
        if (File.Exists(slot))
            throw new InvalidOperationException("Rejected snapshot created protected evidence.");

        DevelopmentOrderCalculatorVerifier.StoreGuestSnapshot(owner, completed, proposal, source);
        DevelopmentOrderCalculatorVerifier.RequireGuestSnapshot(owner, completed, proposal, source);
        Reject(() => DevelopmentOrderCalculatorVerifier.StoreGuestSnapshot(owner, completed, proposal, source));
        byte[] manifest = File.ReadAllBytes(slot);
        File.WriteAllBytes(slot, [.. manifest, 0x20]);
        try { Reject(() => DevelopmentOrderCalculatorVerifier.RequireGuestSnapshot(owner, completed, proposal, source)); }
        finally { File.WriteAllBytes(slot, manifest); }
        DevelopmentOrderCalculatorVerifier.RequireGuestSnapshot(owner, completed, proposal, source);
        if (File.Exists(journal + ".guest-dispatch-2") || File.Exists(journal + ".s01")
            || completed.Pending is not null || completed.Guests != 0)
            throw new InvalidOperationException("Verifier preparation dispatched or accepted work.");
        Console.WriteLine("PASS: calculator guest proposal binds exactly four trusted input files and protected candidate bytes; substituted, missing, extra and tampered snapshots rejected.");
    }

    private static void TestIntegrationSnapshot(DevelopmentJobStateStore.Lease owner,
        OrderPairHistory history, OrderPairObservation observed, string root, string journal)
    {
        var files = DevelopmentOrderIntegrationVerifier.Prepare(owner, history, observed);
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        var accepted = history.AcceptedPair.AcceptedS02 ?? throw new InvalidOperationException("Pair acceptance missing.");
        string[] names = ["Demo.csproj", "PairCalculator.cs", "PairFormatter.cs", "Program.cs"];
        if (!files.Keys.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(names)
            || !files["PairCalculator.cs"].SequenceEqual(owner.RequireOrderCandidate(accepted.Predecessor.Candidate))
            || !files["PairFormatter.cs"].SequenceEqual(owner.RequireOrderCandidate(accepted.Candidate))
            || !files["Program.cs"].SequenceEqual(DevelopmentOrderIntegrationVerifier.ReadProgram())
            || !Encoding.UTF8.GetString(files["Program.cs"]).Contains(
                DevelopmentOrderIntegrationVerifier.ExpectedStdout, StringComparison.Ordinal))
            throw new InvalidOperationException("Integration verifier input differs.");
        Reject(() => DevelopmentOrderIntegrationVerifier.Prepare(owner,
            history with { AcceptedPair = history.FormatterGuest }, observed));
        Reject(() => DevelopmentOrderIntegrationVerifier.Prepare(owner,
            history with { AcceptedCalculator = history.CalculatorGuest }, observed));
        Reject(() => DevelopmentOrderIntegrationVerifier.Prepare(owner,
            history with { AcceptedPair = history.AcceptedPair with { ReservedSeconds = 0 } }, observed));
        Reject(() => DevelopmentOrderIntegrationVerifier.Prepare(owner, history, observed with { Calculator = null }));
        Reject(() => DevelopmentOrderIntegrationVerifier.Prepare(owner, history, observed with { Formatter = null }));
        var changedPair = observed.PairStage.ToDictionary(file => file.Key, file => file.Value.ToArray(), StringComparer.Ordinal);
        changedPair[DevelopmentAgentContract.PairFirst][0] ^= 1;
        Reject(() => DevelopmentOrderIntegrationVerifier.Prepare(owner, history, observed with { PairStage = changedPair }));
        changedPair[DevelopmentAgentContract.PairFirst][0] ^= 1;
        changedPair[DevelopmentAgentContract.PairSecond][0] ^= 1;
        Reject(() => DevelopmentOrderIntegrationVerifier.Prepare(owner, history, observed with { PairStage = changedPair }));
        foreach (string suffix in new[] { ".guest-report-2", ".guest-report-4", ".candidate-1", ".candidate-3" })
        {
            string evidence = journal + suffix;
            byte[] original = File.ReadAllBytes(evidence);
            File.WriteAllBytes(evidence, [.. original, 0x20]);
            try { Reject(() => DevelopmentOrderIntegrationVerifier.Prepare(owner, history, observed)); }
            finally { File.WriteAllBytes(evidence, original); }
        }

        string source = Path.Combine(root, "integration-verification");
        Directory.CreateDirectory(source);
        foreach (var file in files)
            File.WriteAllBytes(Path.Combine(source, file.Key), file.Value);
        var ledger = history.AcceptedPair;
        int seconds = ledger.FinalReserveSeconds;
        Reject(() => DevelopmentOrderIntegrationVerifier.ProposeGuest(owner, history, observed, seconds - 1, DateTime.UtcNow.Ticks));
        var proposal = DevelopmentOrderIntegrationVerifier.ProposeGuest(owner, history, observed, seconds, DateTime.UtcNow.Ticks);
        var limits = ledger.CurrentLimits ?? throw new InvalidOperationException("Missing limits.");
        var admitted = ledger.ReserveWithRequiredWork(claim.JobId, limits, proposal, []);
        string slot = journal + ".snapshot-5";
        if (proposal.Step != "FINAL" || proposal.Kind != "guest" || admitted.Pending != proposal
            || admitted.Sequence != 5 || admitted.Models != 2 || admitted.Guests != 3 || admitted.FinalGuests != 1
            || admitted.ReservedSeconds != ledger.ReservedSeconds + seconds
            || admitted.AcceptedS01 != ledger.AcceptedS01 || admitted.AcceptedS02 != accepted
            || proposal.SnapshotHash != DevelopmentOrderSnapshot.Capture(source, "Demo.csproj")
            || File.Exists(slot))
            throw new InvalidOperationException("Integration guest proposal differs.");

        Reject(() => DevelopmentOrderIntegrationVerifier.RequireGuestSnapshot(owner, history, observed, proposal, source));
        Reject(() => DevelopmentOrderIntegrationVerifier.StoreGuestSnapshot(owner, history, observed,
            proposal with { Step = "S02" }, source));
        Reject(() => DevelopmentOrderIntegrationVerifier.StoreGuestSnapshot(owner, history, observed,
            proposal with { SnapshotHash = new string('A', 64) }, source));
        foreach (var file in files)
        {
            string path = Path.Combine(source, file.Key);
            File.WriteAllBytes(path, [.. file.Value, 0x20]);
            try { Reject(() => DevelopmentOrderIntegrationVerifier.StoreGuestSnapshot(owner, history, observed, proposal, source)); }
            finally { File.WriteAllBytes(path, file.Value); }
        }
        string extra = Path.Combine(source, "Extra.cs");
        File.WriteAllText(extra, "// forbidden");
        try { Reject(() => DevelopmentOrderIntegrationVerifier.StoreGuestSnapshot(owner, history, observed, proposal, source)); }
        finally { File.Delete(extra); }
        string program = Path.Combine(source, "Program.cs");
        File.Delete(program);
        try { Reject(() => DevelopmentOrderIntegrationVerifier.StoreGuestSnapshot(owner, history, observed, proposal, source)); }
        finally { File.WriteAllBytes(program, files["Program.cs"]); }
        if (File.Exists(slot))
            throw new InvalidOperationException("Rejected integration snapshot created protected evidence.");
        DevelopmentOrderIntegrationVerifier.StoreGuestSnapshot(owner, history, observed, proposal, source);
        DevelopmentOrderIntegrationVerifier.RequireGuestSnapshot(owner, history, observed, proposal, source);
        Reject(() => DevelopmentOrderIntegrationVerifier.StoreGuestSnapshot(owner, history, observed, proposal, source));
        byte[] manifest = File.ReadAllBytes(slot);
        File.WriteAllBytes(slot, [.. manifest, 0x20]);
        try { Reject(() => DevelopmentOrderIntegrationVerifier.RequireGuestSnapshot(owner, history, observed, proposal, source)); }
        finally { File.WriteAllBytes(slot, manifest); }
        DevelopmentOrderIntegrationVerifier.RequireGuestSnapshot(owner, history, observed, proposal, source);
        if (!DevelopmentOrderIntegrationVerifier.MatchesOutput(DevelopmentOrderIntegrationVerifier.ExpectedStdout + "\n", "")
            || !DevelopmentOrderIntegrationVerifier.MatchesOutput(DevelopmentOrderIntegrationVerifier.ExpectedStdout + "\r\n", "")
            || DevelopmentOrderIntegrationVerifier.MatchesOutput(DevelopmentOrderFormatterVerifier.ExpectedStdout + "\n", "")
            || DevelopmentOrderIntegrationVerifier.MatchesOutput(DevelopmentOrderIntegrationVerifier.ExpectedStdout + "\n\n", "")
            || DevelopmentOrderIntegrationVerifier.MatchesOutput(DevelopmentOrderIntegrationVerifier.ExpectedStdout + "\n", "diagnostic")
            || File.Exists(journal + ".guest-dispatch-5") || File.Exists(journal + ".guest-report-5")
            || ledger.Pending is not null || ledger.FinalGuests != 0)
            throw new InvalidOperationException("Integration snapshot preparation dispatched or accepted work.");
        Console.WriteLine("BRIDGE_PHASE: exact four-file FINAL snapshot binds both accepted candidates and original integration harness; substitutions rejected.");
    }

    private static async Task TestFormatterSnapshotAsync(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger accepted, DevelopmentOrderOperationLedger completed,
        OrderFormatterInputs inputs, string root, string journal, bool expectSuccess,
        Func<OrderOperationReservation, Task<DevelopmentOrderOperationLedger>> admit,
        Func<OrderGuestOutcome, Task<DevelopmentOrderOperationLedger>> complete)
    {
        var files = DevelopmentOrderFormatterVerifier.Prepare(owner, accepted, completed, inputs);
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        var candidate = completed.LatestCandidate ?? throw new InvalidOperationException("Formatter candidate missing.");
        string[] names = ["Demo.csproj", "PairCalculator.cs", "PairFormatter.cs", "PairFormatterVerifier.cs", "Program.cs"];
        if (!files.Keys.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(names)
            || !files["PairCalculator.cs"].SequenceEqual(owner.RequireOrderCandidate(inputs.CalculatorAcceptance.Candidate))
            || !files["PairFormatter.cs"].SequenceEqual(owner.RequireOrderCandidate(candidate))
            || !Encoding.UTF8.GetString(files["Program.cs"]).Contains(
                DevelopmentOrderFormatterVerifier.ExpectedStdout, StringComparison.Ordinal))
            throw new InvalidOperationException("Formatter verifier input differs.");
        Reject(() => DevelopmentOrderCalculatorVerifier.Prepare(owner, completed));
        Reject(() => DevelopmentOrderFormatterVerifier.Prepare(owner, accepted, accepted, inputs));
        Reject(() => DevelopmentOrderFormatterVerifier.Prepare(owner, accepted with { AcceptedS01 = null }, completed, inputs));
        Reject(() => DevelopmentOrderFormatterVerifier.Prepare(owner, accepted, completed with { ReservedSeconds = 0 }, inputs));
        Reject(() => DevelopmentOrderFormatterVerifier.Prepare(owner, accepted, completed,
            inputs with { Calculator = [.. inputs.Calculator, 0x20] }));
        Reject(() => DevelopmentOrderFormatterVerifier.Prepare(owner, accepted, completed,
            inputs with { FormatterBaseline = [.. inputs.FormatterBaseline, 0x20] }));

        string source = Path.Combine(root, "formatter-verification");
        Directory.CreateDirectory(source);
        foreach (var file in files)
            File.WriteAllBytes(Path.Combine(source, file.Key), file.Value);
        var proposal = DevelopmentOrderFormatterVerifier.ProposeGuest(owner, accepted, completed, inputs, 10, DateTime.UtcNow.Ticks);
        var limits = completed.CurrentLimits ?? throw new InvalidOperationException("Missing limits.");
        var admitted = completed.ReserveWithRequiredWork(claim.JobId, limits, proposal,
            [new("FINAL", "guest", completed.FinalReserveSeconds)]);
        string slot = journal + ".snapshot-4";
        if (proposal.Step != "S02" || proposal.Kind != "guest" || admitted.Pending != proposal
            || proposal.SnapshotHash != DevelopmentOrderSnapshot.Capture(source, "Demo.csproj")
            || completed.Pending is not null || completed.Guests != 1 || admitted.Guests != 2
            || admitted.ReservedSeconds != completed.ReservedSeconds + 10 || File.Exists(slot))
            throw new InvalidOperationException("Formatter guest proposal differs.");

        Reject(() => DevelopmentOrderFormatterVerifier.RequireGuestSnapshot(owner, accepted, completed, inputs, proposal, source));
        Reject(() => DevelopmentOrderFormatterVerifier.StoreGuestSnapshot(owner, accepted, completed, inputs,
            proposal with { Step = "S01" }, source));
        Reject(() => DevelopmentOrderFormatterVerifier.StoreGuestSnapshot(owner, accepted, completed, inputs,
            proposal with { SnapshotHash = new string('A', 64) }, source));
        Reject(() => DevelopmentOrderFormatterVerifier.StoreGuestSnapshot(owner, accepted, completed, inputs,
            proposal with { OperationId = claim.JobId + ":5" }, source));
        foreach (var file in files)
        {
            string path = Path.Combine(source, file.Key);
            byte[] changed = file.Value.ToArray();
            changed[0] ^= 1;
            File.WriteAllBytes(path, changed);
            try { Reject(() => DevelopmentOrderFormatterVerifier.StoreGuestSnapshot(owner, accepted, completed, inputs, proposal, source)); }
            finally { File.WriteAllBytes(path, file.Value); }
        }
        string extra = Path.Combine(source, "Extra.cs");
        File.WriteAllText(extra, "// forbidden extra");
        try { Reject(() => DevelopmentOrderFormatterVerifier.StoreGuestSnapshot(owner, accepted, completed, inputs, proposal, source)); }
        finally { File.Delete(extra); }
        string predecessor = Path.Combine(source, "PairCalculator.cs");
        File.Delete(predecessor);
        try { Reject(() => DevelopmentOrderFormatterVerifier.StoreGuestSnapshot(owner, accepted, completed, inputs, proposal, source)); }
        finally { File.WriteAllBytes(predecessor, files["PairCalculator.cs"]); }
        if (File.Exists(slot))
            throw new InvalidOperationException("Rejected formatter snapshot created protected evidence.");

        DevelopmentOrderFormatterVerifier.StoreGuestSnapshot(owner, accepted, completed, inputs, proposal, source);
        DevelopmentOrderFormatterVerifier.RequireGuestSnapshot(owner, accepted, completed, inputs, proposal, source);
        Reject(() => DevelopmentOrderFormatterVerifier.StoreGuestSnapshot(owner, accepted, completed, inputs, proposal, source));
        byte[] manifest = File.ReadAllBytes(slot);
        File.WriteAllBytes(slot, [.. manifest, 0x20]);
        try { Reject(() => DevelopmentOrderFormatterVerifier.RequireGuestSnapshot(owner, accepted, completed, inputs, proposal, source)); }
        finally { File.WriteAllBytes(slot, manifest); }
        DevelopmentOrderFormatterVerifier.RequireGuestSnapshot(owner, accepted, completed, inputs, proposal, source);
        if (File.Exists(journal + ".guest-dispatch-4") || File.Exists(journal + ".s02"))
            throw new InvalidOperationException("Formatter snapshot dispatched or accepted work.");
        Console.WriteLine("BRIDGE_PHASE: exact five-file S02 verifier snapshot binds accepted predecessor and generated formatter; substitutions rejected.");

        // The controlled transport must not run until the actual actor
        // has acknowledged the exact journal reservation.
        var acknowledged = await admit(proposal);
        if (acknowledged != admitted)
            throw new InvalidOperationException("Formatter guest admission acknowledgement differs.");
        string results = Path.Combine(root, "formatter-results");
        Directory.CreateDirectory(results);
        string id = DevelopmentOrderGuestReconciliation.RunId(proposal);
        string name = "hami-code-" + id[..12];
        string absent = "error: sandbox '" + name + "' not found";
        string healthy = string.Join('\n', new[]
        {
            "Name: " + name, "Agent: shell", "Workspace: -", "Clone source: -",
            "Configured host mounts: none", "Secrets: none", "MCP mode: static",
            "MCP servers: none", "Ports: none published", "Network policy: global (local policy)",
            "Auth mode: not configured", "State: running"
        });
        string guestOutput = string.Join('\0', DevelopmentOrderSnapshot.RequireGuestRecords(source, "Demo.csproj", proposal.SnapshotHash))
            + "\0" + DevelopmentOrderSnapshot.GuestEndMarker + "\0";
        string raw = Path.Combine(results, "reports", "hami-code-" + id, "report.json");
        bool created = false, removed = false;
        int calls = 0, executions = 0, afterReturn = 0;
        byte[]? originalReport = null;
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
                        response = new(0, System.Text.Json.JsonSerializer.Serialize(new { name, id = "formatter-backend-id" }), "");
                    else if (!created || removed)
                    {
                        response = new(1, "", absent);
                        if (File.Exists(raw))
                        {
                            afterReturn++;
                            originalReport = File.ReadAllBytes(raw);
                            File.WriteAllText(raw, "not the captured report");
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
                    else if (args.Contains("run", StringComparer.Ordinal))
                    {
                        executions++;
                        response = new(0, (expectSuccess ? DevelopmentOrderFormatterVerifier.ExpectedStdout
                            : DevelopmentOrderCalculatorVerifier.ExpectedStdout) + "\n", "");
                    }
                    break;
            }
            return Task.FromResult(response);
        }

        Task<OrderGuestOutcome?> Run(DevelopmentOrderOperationLedger ack, OrderFormatterInputs handover) =>
            DevelopmentSandboxRunner.RunReservedCoreAsync(CalculatorRepository(root), source, results, owner,
                ack, proposal, Transport, (_, _, _, _) => { }, completedFormatterModel: completed,
                acceptedCalculatorLedger: accepted, formatterInputs: handover);

        async Task RejectRunAsync(DevelopmentOrderOperationLedger ack, OrderFormatterInputs handover)
        {
            bool rejected = false;
            try { _ = await Run(ack, handover); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected)
                throw new InvalidOperationException("Invalid formatter runner context accepted.");
        }

        await RejectRunAsync(admitted with { ReservedSeconds = 0 }, inputs);
        await RejectRunAsync(admitted, inputs with { Calculator = [.. inputs.Calculator, 0x20] });
        string program = Path.Combine(source, "Program.cs");
        byte[] programBytes = File.ReadAllBytes(program);
        File.WriteAllBytes(program, [.. programBytes, 0x20]);
        try { await RejectRunAsync(admitted, inputs); }
        finally { File.WriteAllBytes(program, programBytes); }
        if (calls != 0 || File.Exists(journal + ".guest-dispatch-4"))
            throw new InvalidOperationException("Invalid formatter context reached dispatch.");

        var outcome = await Run(acknowledged, inputs);
        if ((outcome is not null) != expectSuccess || !created || !removed || executions != 1
            || afterReturn != (expectSuccess ? 1 : 0) || !File.Exists(raw))
            throw new InvalidOperationException("Formatter runner outcome differs.");
        owner.RequireOrderGuestDispatch(proposal);
        string reportSlot = journal + ".guest-report-4";
        if (outcome is not null)
        {
            byte[] captured = owner.RequireOrderGuestReport(outcome, proposal);
            if (originalReport is null || !captured.SequenceEqual(originalReport)
                || captured.SequenceEqual(File.ReadAllBytes(raw))
                || !DevelopmentOrderGuestReconciliation.IsFormatterReport(proposal, captured)
                || DevelopmentOrderGuestReconciliation.IsCalculatorReport(proposal, captured))
                throw new InvalidOperationException("Formatter report provenance differs.");
            var report = System.Text.Json.JsonSerializer.Deserialize<DevelopmentSandboxRunner.SandboxRunReport>(captured,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("Formatter report missing.");
            byte[] Encode(DevelopmentSandboxRunner.SandboxRunReport value) =>
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true });
            if (DevelopmentOrderGuestReconciliation.IsSuccessfulReport(proposal,
                    Encode(report with { VerifiedBy = DevelopmentOrderCalculatorVerifier.Id }))
                || DevelopmentOrderGuestReconciliation.IsFormatterReport(proposal,
                    Encode(report with { SchemaVersion = 4, VerifiedBy = null }))
                || DevelopmentOrderGuestReconciliation.IsSuccessfulReport(proposal,
                    Encode(report with { SnapshotHash = new string('A', 64) })))
                throw new InvalidOperationException("Incorrect formatter report accepted.");

            // Compare the expected pure fold before accepting actual
            // persistence; the actor independently checks the evidence.
            var completedGuest = acknowledged.CompleteGuest(outcome);
            var observed = ObserveFixtureGuest(claim, proposal, captured)
                ?? throw new InvalidOperationException("Formatter fixture absence missing.");
            OrderCandidateCheckpoint Correlate(DevelopmentOrderOperationLedger guest, OrderGuestOutcome? fresh) =>
                DevelopmentOrderFormatterVerifier.RequireCompletedGuest(owner, accepted, completed, guest, inputs, fresh);
            if (Correlate(completedGuest, observed) != candidate
                || completedGuest.AcceptedS01 != inputs.CalculatorAcceptance
                || completedGuest.Pending is not null || completedGuest.Sequence != 4
                || completedGuest.ReservedSeconds != 80)
                throw new InvalidOperationException("Formatter guest history differs.");
            Reject(() => Correlate(admitted, observed));
            Reject(() => Correlate(completed, observed));
            Reject(() => Correlate(completedGuest with { ReservedSeconds = 0 }, observed));
            Reject(() => Correlate(completedGuest with { AcceptedS01 = null }, observed));
            Reject(() => Correlate(completedGuest, null));
            Reject(() => Correlate(completedGuest, observed with { ReportHash = new string('A', 64) }));
            Reject(() => Correlate(completedGuest, observed with { ObservedUtcTicks = outcome.ObservedUtcTicks - 1 }));
            Reject(() => DevelopmentOrderFormatterVerifier.RequireCompletedGuest(owner, accepted, completed, completedGuest,
                inputs with { Calculator = [.. inputs.Calculator, 0x20] }, observed));

            foreach (string suffix in new[] { ".snapshot-4", ".guest-dispatch-4", ".guest-report-4",
                ".model-dispatch-3", ".candidate-3", ".evidence-3", ".candidate-1" })
            {
                string evidenceFile = journal + suffix;
                byte[] original = File.ReadAllBytes(evidenceFile);
                File.WriteAllBytes(evidenceFile, [.. original, 0x20]);
                try { Reject(() => Correlate(completedGuest, observed)); }
                finally { File.WriteAllBytes(evidenceFile, original); }
            }

            // Independent stage observation of the exact two candidate files,
            // separate from the five-file verifier input directory.
            var baseline = ObserveCalculatorBaseline(root, claim.Binding);
            string pairStage = Path.Combine(root, "formatter-stage");
            Directory.CreateDirectory(pairStage);
            File.WriteAllBytes(Path.Combine(pairStage, "PairCalculator.cs"), owner.RequireOrderCandidate(inputs.CalculatorAcceptance.Candidate));
            File.WriteAllBytes(Path.Combine(pairStage, "PairFormatter.cs"), owner.RequireOrderCandidate(candidate));
            void VerifyScope() => DevelopmentOrderFormatterVerifier.RequirePairScope(owner, inputs.CalculatorAcceptance,
                Correlate(completedGuest, observed), ObserveCalculatorBaseline(root, claim.Binding),
                DevelopmentOrderCalculatorVerifier.ReadStagedPair(pairStage));
            VerifyScope();
            foreach (string file in new[] { "PairCalculator.cs", "PairFormatter.cs" })
            {
                string stagedPath = Path.Combine(pairStage, file);
                byte[] original = File.ReadAllBytes(stagedPath);
                File.WriteAllBytes(stagedPath, [.. original, 0x20]);
                try { Reject(VerifyScope); }
                finally { File.WriteAllBytes(stagedPath, original); }
            }
            string stagedFormatter = Path.Combine(pairStage, "PairFormatter.cs");
            File.WriteAllBytes(stagedFormatter, baseline.Formatter);
            try { Reject(VerifyScope); }
            finally { File.WriteAllBytes(stagedFormatter, owner.RequireOrderCandidate(candidate)); }
            string additional = Path.Combine(pairStage, "Extra.cs");
            File.WriteAllText(additional, "// forbidden");
            try { Reject(VerifyScope); }
            finally { File.Delete(additional); }
            File.Delete(stagedFormatter);
            try { Reject(VerifyScope); }
            finally { File.WriteAllBytes(stagedFormatter, owner.RequireOrderCandidate(candidate)); }
            Reject(() => DevelopmentOrderFormatterVerifier.RequirePairScope(owner, inputs.CalculatorAcceptance, candidate,
                baseline with { Scope = [.. baseline.Scope, 0x20] }, DevelopmentOrderCalculatorVerifier.ReadStagedPair(pairStage)));
            VerifyScope();
            if (await complete(outcome) != completedGuest)
                throw new InvalidOperationException("Formatter guest completion acknowledgement differs.");
            Console.WriteLine("BRIDGE_PHASE: S02 historical guest correlation and exact two-file scope verified; changed history, evidence, predecessor and stage rejected.");
        }
        else if (File.Exists(reportSlot))
            throw new InvalidOperationException("Wrong semantic output produced protected evidence.");
        int before = calls;
        await RejectRunAsync(admitted, inputs);
        if (calls != before || File.Exists(journal + ".s02") || completed.Pending is not null)
            throw new InvalidOperationException("Formatter guest repeated or accepted a step.");
        Console.WriteLine("BRIDGE_PHASE: S02 controlled runner " + (expectSuccess ? "success" : "wrong marker")
            + "; exact input, one-shot dispatch, semantic report and cleanup checked.");
    }

    internal static async Task RunAsync(string prefix, Func<string, string, Process> start,
        Func<string, string, Task> expectOwnership)
    {
        await DevelopmentAiGenerator.SelfTestS01Async();
        await DevelopmentAiGenerator.SelfTestS02Async();
        await TestModelDispatchAsync(prefix);
        await TestLiveGuestCaptureAsync(prefix);
        foreach (string scenario in new[] { "accepted", "unknown", "failed", "guest", "guest-unknown", "guest-orphan",
            "model-receipt", "model-orphan", "model-checkpoint", "model-generated", "model-rejected", "model-rejected-orphan", "model-accepted", "model-verified", "soft-stop",
            "model-formatter-receipt", "model-formatter-orphan", "model-formatter-checkpoint",
            "model-formatter-rejected", "model-formatter-rejected-orphan" })
        {
            bool unknown = scenario == "unknown";
            bool failed = scenario == "failed";
            bool guest = scenario == "guest";
            bool guestUnknown = scenario == "guest-unknown";
            bool guestOrphan = scenario == "guest-orphan";
            bool guestScenario = guest || guestUnknown || guestOrphan;
            bool modelScenario = scenario.StartsWith("model-", StringComparison.Ordinal);
            bool softScenario = scenario == "soft-stop";
            bool formatterScenario = scenario.StartsWith("model-formatter-", StringComparison.Ordinal);
            bool calculatorScenario = formatterScenario || scenario is "model-accepted" or "model-verified";
            bool formatterHasCandidate = scenario is not ("model-formatter-receipt" or "model-formatter-rejected" or "model-formatter-rejected-orphan");
            bool formatterHasEvidence = scenario != "model-formatter-receipt";
            bool modelGenerated = scenario == "model-generated";
            bool modelRejected = scenario == "model-rejected";
            bool modelCandidate = calculatorScenario || scenario is "model-orphan" or "model-checkpoint" or "model-generated";
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
                "model-accepted" => ("bridge-model-accepted", "BRIDGE_CALCULATOR_ACCEPTED",
                    "bridge-model-resume-accepted", "BRIDGE_CALCULATOR_RECOVERED"),
                "model-verified" => ("bridge-model-verified", "BRIDGE_CALCULATOR_VERIFIED",
                    "bridge-model-resume-verified", "BRIDGE_CALCULATOR_RECOVERED"),
                _ when formatterScenario => ("bridge-model-accepted", "BRIDGE_CALCULATOR_ACCEPTED",
                    "bridge-model-resume-formatter-" + scenario["model-formatter-".Length..], "BRIDGE_CALCULATOR_RECOVERED"),
                _ when modelScenario => ("bridge-" + scenario, "BRIDGE_MODEL_CRASH_READY",
                    modelGenerated ? "bridge-model-resume-generated" : modelRejected ? "bridge-model-resume-rejected"
                        : "bridge-model-resume-blocked", "BRIDGE_MODEL_RECOVERED"),
                _ => throw new InvalidOperationException("Unknown bridge scenario.")
            };
            string root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            var store = DevelopmentJobStateRoot.OpenOrderProbeFixture(root, create: true);
            try
            {
                if (calculatorScenario)
                    await CreateCalculatorRepositoryAsync(root);
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
                string[] calculatorSlots = ["candidate-1", "evidence-1", "model-dispatch-1", "snapshot-2", "guest-dispatch-2", "guest-report-2"];
                string calculatorPrefix = Path.Combine(root, $"issue-{Issue}.order.db.");
                byte[][] calculatorEvidence = calculatorScenario
                    ? calculatorSlots.Select(slot => File.ReadAllBytes(calculatorPrefix + slot)).ToArray()
                    : [];

                string[] formatterSlots = ["model-dispatch-3", "candidate-3", "evidence-3"];
                bool[] expectedFormatterSlots = [true, formatterHasCandidate, formatterHasEvidence];
                byte[][] formatterEvidence = [];
                if (calculatorScenario)
                {
                    using Process formatterChild = start(resumeMode, root);
                    Task<string> formatterErrors = formatterChild.StandardError.ReadToEndAsync();
                    try
                    {
                        await WaitForMarkerAsync(formatterChild, "BRIDGE_FORMATTER_CAPTURED").WaitAsync(Deadline);
                        if (formatterChild.HasExited)
                            throw new InvalidOperationException("Formatter owner stopped before process kill.");
                        await expectOwnership(root, "BUSY");
                        if (formatterSlots.Where((slot, i) =>
                            File.Exists(calculatorPrefix + slot) != expectedFormatterSlots[i]).Any())
                            throw new InvalidOperationException("Formatter crash slots differ.");
                        formatterEvidence = formatterSlots.Select((slot, i) =>
                            expectedFormatterSlots[i] ? File.ReadAllBytes(calculatorPrefix + slot) : []).ToArray();
                    }
                    finally
                    {
                        if (!formatterChild.HasExited)
                            formatterChild.Kill(entireProcessTree: true);
                        await formatterChild.WaitForExitAsync().WaitAsync(Deadline);
                        string diagnostics = await formatterErrors.WaitAsync(Deadline);
                        if (diagnostics.Length > 0)
                            Console.Error.WriteLine(diagnostics[^Math.Min(diagnostics.Length, 16 * 1024)..]);
                    }

                    if (formatterChild.ExitCode == 0)
                        throw new InvalidOperationException("Formatter owner did not terminate abnormally.");
                    await expectOwnership(root, "ACQUIRED:order");
                }

                // Both generated formatter cases now persist guest admission.
                // Kill the third owner at success or unresolved wrong marker.
                string[] guestSlots = ["snapshot-4", "guest-dispatch-4", "guest-report-4"];
                bool formatterGuestScenario = calculatorScenario && !formatterScenario;
                bool[] expectedGuestSlots = [true, true, scenario == "model-accepted"];
                byte[][] guestEvidence = [];
                if (formatterGuestScenario)
                {
                    using Process guestChild = start(resumeMode, root);
                    Task<string> guestErrors = guestChild.StandardError.ReadToEndAsync();
                    try
                    {
                        await WaitForMarkerAsync(guestChild, "BRIDGE_FORMATTER_GUEST_CAPTURED").WaitAsync(Deadline);
                        if (guestChild.HasExited)
                            throw new InvalidOperationException("Formatter guest owner stopped before process kill.");
                        await expectOwnership(root, "BUSY");
                        if (guestSlots.Where((slot, i) =>
                            File.Exists(calculatorPrefix + slot) != expectedGuestSlots[i]).Any())
                            throw new InvalidOperationException("Formatter guest crash slots differ.");
                        guestEvidence = guestSlots.Select((slot, i) =>
                            expectedGuestSlots[i] ? File.ReadAllBytes(calculatorPrefix + slot) : []).ToArray();
                    }
                    finally
                    {
                        if (!guestChild.HasExited)
                            guestChild.Kill(entireProcessTree: true);
                        await guestChild.WaitForExitAsync().WaitAsync(Deadline);
                        string diagnostics = await guestErrors.WaitAsync(Deadline);
                        if (diagnostics.Length > 0)
                            Console.Error.WriteLine(diagnostics[^Math.Min(diagnostics.Length, 16 * 1024)..]);
                    }

                    if (guestChild.ExitCode == 0)
                        throw new InvalidOperationException("Formatter guest owner did not terminate abnormally.");
                    await expectOwnership(root, "ACQUIRED:order");
                }

                // A successful guest completion is not step acceptance.
                // Fourth owner accepts independently, then dies after ACK.
                if (scenario == "model-accepted")
                {
                    using Process acceptanceChild = start(resumeMode, root);
                    Task<string> acceptanceErrors = acceptanceChild.StandardError.ReadToEndAsync();
                    try
                    {
                        await WaitForMarkerAsync(acceptanceChild, "BRIDGE_FORMATTER_ACCEPTED").WaitAsync(Deadline);
                        if (acceptanceChild.HasExited)
                            throw new InvalidOperationException("Formatter acceptance owner stopped before process kill.");
                        await expectOwnership(root, "BUSY");
                    }
                    finally
                    {
                        if (!acceptanceChild.HasExited)
                            acceptanceChild.Kill(entireProcessTree: true);
                        await acceptanceChild.WaitForExitAsync().WaitAsync(Deadline);
                        string diagnostics = await acceptanceErrors.WaitAsync(Deadline);
                        if (diagnostics.Length > 0)
                            Console.Error.WriteLine(diagnostics[^Math.Min(diagnostics.Length, 16 * 1024)..]);
                    }

                    if (acceptanceChild.ExitCode == 0)
                        throw new InvalidOperationException("Formatter acceptance owner did not terminate abnormally.");
                    await expectOwnership(root, "ACQUIRED:order");
                }

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
                    if (calculatorScenario && (calculatorSlots.Where((slot, i) =>
                        !File.ReadAllBytes(calculatorPrefix + slot).SequenceEqual(calculatorEvidence[i])).Any()
                        || File.Exists(calculatorPrefix + "guest-dispatch-3")
                        || File.Exists(calculatorPrefix + "model-dispatch-4")
                        || File.Exists(calculatorPrefix + "guest-dispatch-4") != !formatterScenario
                        || File.Exists(calculatorPrefix + "guest-report-4") != (scenario == "model-accepted")
                        || File.Exists(calculatorPrefix + "snapshot-4") != !formatterScenario
                        || formatterSlots.Where((slot, i) => File.Exists(calculatorPrefix + slot) != expectedFormatterSlots[i]
                            || expectedFormatterSlots[i] && !File.ReadAllBytes(calculatorPrefix + slot).SequenceEqual(formatterEvidence[i])).Any()
                        || formatterHasCandidate && !File.ReadAllBytes(calculatorPrefix + "candidate-3")
                            .SequenceEqual(Encoding.UTF8.GetBytes("class PairFormatter {}"))))
                        throw new InvalidOperationException("Calculator evidence changed or formatter capture differs.");
                    if (formatterGuestScenario && (guestSlots.Where((slot, i) =>
                            File.Exists(calculatorPrefix + slot) != expectedGuestSlots[i]
                            || expectedGuestSlots[i] && !File.ReadAllBytes(calculatorPrefix + slot).SequenceEqual(guestEvidence[i])).Any()
                        || File.Exists(calculatorPrefix + "guest-dispatch-5")
                        || File.Exists(calculatorPrefix + "snapshot-5") != (scenario == "model-accepted")
                        || File.Exists(calculatorPrefix + "guest-report-5")))
                        throw new InvalidOperationException("Formatter guest recovery changed protected slots.");
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

        Console.WriteLine("PASS: actual calculator acceptance event survives Akka SQL process kill; protected candidate, report, scope and charged budget recovered without redispatch.");
        Console.WriteLine("PASS: calculator guest-complete crash before acceptance replays unaccepted; filesystem stage and evidence rechecked, one acceptance appended without redispatch.");
        Console.WriteLine("PASS: calculator recovery binds real Git commit and four exact tracked objects; dirty checkout, untracked entry, skip-worktree and binding drift rejected.");
        Console.WriteLine("PASS: S02 input handover from actual recovered S01 acceptance; unaccepted or tampered history and input bytes rejected.");
        Console.WriteLine("PASS: S02 versioned candidate fold from recovered S01 acceptance; exact predecessor input hash and source scope enforced.");
        Console.WriteLine("PASS: S02 private model dispatch from recovered S01 acceptance, exact receipt and evidence readback, duplicate dispatch and tampering blocked.");
        Console.WriteLine("PASS: S02 admitted reservation, checkpoint and completion persisted before ack; real process kill recovers generated formatter without redispatch or budget reset.");
        Console.WriteLine("PASS: S02 receipt-only, orphan, checkpoint-only, terminal rejected and rejected-orphan crash windows replay without redispatch, evidence adoption or budget reset.");
        Console.WriteLine("PASS: S02 guest admission and successful outcome persisted before ack; completed and unresolved wrong-marker process kills recover without redispatch or budget reset.");
        Console.WriteLine("PASS: independent S02 acceptance persisted and recovered after process kill; immutable predecessor, exact evidence, budget and no redispatch.");
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

    internal static DevelopmentOrderApproval FixtureApproval(int? safetyReserveSeconds = null, string? baselineHash = null,
        string? scopeHash = null, OrderGitBaseline? gitBaseline = null)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HaMi.Probe.Order")
            ?? throw new InvalidOperationException("Order fixture missing.");
        using var reader = new StreamReader(stream);
        // Exercise the no-explicit-budget path across actual process kill.
        JsonObject node = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        node.Remove("limits");
        if (gitBaseline is not null)
            node["baseCommit"] = gitBaseline.Commit;
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
        var profile = new OrderReviewedProfile(repository, 42, "fixture-owner", commit,
            gitBaseline is null ? hash : DevelopmentJobState.Hash(gitBaseline.Contract),
            gitBaseline is null ? scopeHash ?? hash : DevelopmentJobState.Hash(gitBaseline.Scope), policy.Sha256,
            gitBaseline is null ? baselineHash ?? hash : DevelopmentJobState.Hash(gitBaseline.Calculator),
            gitBaseline is null ? hash : DevelopmentJobState.Hash(gitBaseline.Formatter));
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
        const string formatterModePrefix = "bridge-model-resume-formatter-";
        string formatterCrash = mode.StartsWith(formatterModePrefix, StringComparison.Ordinal)
            ? mode[formatterModePrefix.Length..] : "generated";
        bool calculatorScenario = mode is "bridge-model-accepted" or "bridge-model-resume-accepted"
            or "bridge-model-verified" or "bridge-model-resume-verified" || mode.StartsWith(formatterModePrefix, StringComparison.Ordinal);
        bool first = mode is "bridge-hold" or "bridge-unknown" or "bridge-failed" or "bridge-guest"
            or "bridge-guest-unknown" or "bridge-guest-orphan"
            or "bridge-model-receipt" or "bridge-model-orphan" or "bridge-model-checkpoint"
            or "bridge-model-generated" or "bridge-model-rejected" or "bridge-model-rejected-orphan" or "bridge-soft-stop"
            or "bridge-model-accepted" or "bridge-model-verified";
        if (first)
        {
            var fixtureApproval = calculatorScenario ? await CalculatorRecoveryApprovalAsync(root) : FixtureApproval();
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
            IActorRef actor = system.ActorOf(Props.Create(() => new BridgeActor(claim, archive, limits, policy, owner, modelScenario, softScenario, calculatorScenario, root)), "bridge");
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
                    var approval = calculatorScenario ? await CalculatorRecoveryApprovalAsync(root) : FixtureApproval();
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
                            reservation, approval, assignment, catalog,
                            calculatorScenario ? ObserveCalculatorBaseline(root, claim.Binding).Calculator : ModelBaseline, generator);
                        if (fake.Calls != 1 || (result.Candidate is null) != reject
                            || result.Completion.Outcome != (reject ? "failed" : "succeeded"))
                            throw new InvalidOperationException("Model crash fixture returned wrong outcome.");
                        if (mode is "bridge-model-checkpoint" or "bridge-model-generated" || calculatorScenario)
                        {
                            var candidate = result.Candidate ?? throw new InvalidOperationException("Candidate missing.");
                            current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeCandidate(candidate), 2);
                        }

                        if (mode is "bridge-model-generated" or "bridge-model-rejected" || calculatorScenario)
                            current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeCompletion(result.Completion), 7);
                    }

                    if (calculatorScenario)
                    {
                        var model = current.Ledger;
                        CreateCalculatorStage(root, owner, model);
                        var guest = DevelopmentOrderCalculatorVerifier.ProposeGuest(owner, model, 30, DateTime.UtcNow.Ticks);
                        string source = Path.Combine(Path.GetTempPath(), "hami-calculator-recovery-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(source);
                        try
                        {
                            foreach (var file in DevelopmentOrderCalculatorVerifier.Prepare(owner, model))
                                File.WriteAllBytes(Path.Combine(source, file.Key), file.Value);
                            DevelopmentOrderCalculatorVerifier.StoreGuestSnapshot(owner, model, guest, source);
                        }
                        finally { Directory.Delete(source, recursive: true); }
                        current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeReservation(guest), 2);
                        owner.ConsumeOrderGuestDispatch(current.Ledger, guest);
                        // Controlled transport observation, not a real sandbox run.
                        var report = new DevelopmentSandboxRunner.SandboxRunReport(5,
                            DevelopmentOrderGuestReconciliation.RunId(guest), "Demo.csproj", "run",
                            0, 0, 0, false, 1, 1, 1, 1, 1, 1, 6, true, null,
                            guest.SnapshotHash, DevelopmentOrderCalculatorVerifier.Id);
                        byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report,
                            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true });
                        var outcome = ObserveFixtureGuest(claim, guest, bytes)
                            ?? throw new InvalidOperationException("Calculator fixture guest outcome missing.");
                        owner.StoreOrderGuestReport(outcome, guest, bytes);
                        current = await AdvanceAsync(DevelopmentOrderOperationLedger.EncodeGuestOutcome(outcome), 6);
                        if (current.Ledger.AcceptedS01 is not null)
                            throw new InvalidOperationException("Guest completion accepted calculator prematurely.");
                        if (mode == "bridge-model-verified")
                        {
                            owner.ValidateOrderJournalFiles();
                            Console.WriteLine("BRIDGE_CALCULATOR_VERIFIED");
                            Console.Out.Flush();
                            await Task.Delay(Timeout.InfiniteTimeSpan);
                            return 1;
                        }

                        Task<BridgeState> acceptance = actor.Ask<BridgeState>(new AcceptCalculator(), Deadline);
                        await guard.AwaitAsync(acceptance);
                        current = await acceptance;
                        if (current.Stage != 8 || current.Ledger.AcceptedS01 is null
                            || current.Ledger.Models != 1 || current.Ledger.Guests != 1
                            || current.Ledger.ReservedSeconds != 60 || current.Ledger.Pending is not null)
                            throw new InvalidOperationException("Calculator acceptance was not acknowledged.");
                        owner.ValidateOrderJournalFiles();
                        Console.WriteLine("BRIDGE_CALCULATOR_ACCEPTED");
                        Console.Out.Flush();
                        await Task.Delay(Timeout.InfiniteTimeSpan);
                        return 1;
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

            if (calculatorScenario)
            {
                if (mode == "bridge-model-resume-verified" && current.Stage == 6)
                {
                    if (current.Stage != 6 || current.Ledger.AcceptedS01 is not null
                        || current.Ledger.Pending is not null || current.Ledger.Sequence != 2
                        || current.Ledger.ReservedSeconds != 60)
                        throw new InvalidOperationException("Guest completion was mistaken for acceptance.");
                    var unaccepted = current;
                    string formatter = Path.Combine(CalculatorStage(root), "PairFormatter.cs");
                    byte[] originalFormatter = File.ReadAllBytes(formatter);
                    async Task RejectAcceptanceAsync()
                    {
                        bool rejected = false;
                        try { _ = await actor.Ask<BridgeState>(new AcceptCalculator(), Deadline); }
                        catch (InvalidOperationException) { rejected = true; }
                        if (!rejected || await ReadAsync() != unaccepted)
                            throw new InvalidOperationException("Invalid scope changed journal state.");
                    }

                    File.WriteAllBytes(formatter, [.. originalFormatter, 0x20]);
                    try { await RejectAcceptanceAsync(); }
                    finally { File.WriteAllBytes(formatter, originalFormatter); }
                    string extra = Path.Combine(CalculatorStage(root), "extra.cs");
                    File.WriteAllText(extra, "unexpected");
                    try { await RejectAcceptanceAsync(); }
                    finally { File.Delete(extra); }
                    File.Move(formatter, formatter + ".saved");
                    try { await RejectAcceptanceAsync(); }
                    finally { File.Move(formatter + ".saved", formatter); }

                    string repo = CalculatorRepository(root);
                    string baselinePath = Path.Combine(repo, DevelopmentAgentContract.PairFirst);
                    byte[] baselineBytes = File.ReadAllBytes(baselinePath);
                    File.WriteAllBytes(baselinePath, [.. baselineBytes, 0x20]);
                    try { await RejectAcceptanceAsync(); }
                    finally { File.WriteAllBytes(baselinePath, baselineBytes); }
                    string untracked = Path.Combine(repo, "unexpected.txt");
                    File.WriteAllText(untracked, "unexpected");
                    try { await RejectAcceptanceAsync(); }
                    finally { File.Delete(untracked); }
                    _ = await DevelopmentWorktreeGuard.GitAsync(repo, "update-index", "--skip-worktree",
                        "--", DevelopmentAgentContract.PairSecond);
                    try { await RejectAcceptanceAsync(); }
                    finally
                    {
                        _ = await DevelopmentWorktreeGuard.GitAsync(repo, "update-index", "--no-skip-worktree",
                            "--", DevelopmentAgentContract.PairSecond);
                    }
                    Reject(() => ObserveCalculatorBaseline(root, claim.Binding with { BaseCommit = new string('0', 40) }));
                    Reject(() => ObserveCalculatorBaseline(root, claim.Binding with { ScopeHash = new string('0', 64) }));
                    Reject(() => ObserveCalculatorBaseline(root, claim.Binding with { ContractHash = new string('0', 64) }));
                    Task<BridgeState> acceptance = actor.Ask<BridgeState>(new AcceptCalculator(), Deadline);
                    await guard.AwaitAsync(acceptance);
                    current = await acceptance;
                    if (current.Ledger.AcceptedS01 is null || current.Ledger.ReservedSeconds != unaccepted.Ledger.ReservedSeconds
                        || current.Ledger.Sequence != unaccepted.Ledger.Sequence
                        || current.Ledger.StartedUtcTicks != unaccepted.Ledger.StartedUtcTicks)
                        throw new InvalidOperationException("Recovered acceptance changed history.");
                }
                var accepted = current.Ledger.AcceptedS01
                    ?? throw new InvalidOperationException("Calculator acceptance missing after replay.");
                var model = current.CalculatorModel ?? throw new InvalidOperationException("Model history missing.");
                var guest = current.CalculatorGuest ?? throw new InvalidOperationException("Guest history missing.");
                var outcome = guest.LatestGuestOutcome ?? throw new InvalidOperationException("Guest outcome missing.");
                OrderFormatterInputs ReadFormatterInputs(DevelopmentOrderOperationLedger state,
                    OrderGitBaseline baseline, IReadOnlyDictionary<string, byte[]> pair) =>
                    DevelopmentOrderFormatterVerifier.RequireInputs(owner, model, guest, state,
                        ObserveCalculatorFixture(owner, claim, outcome), baseline, pair);

                if (current.Stage == 13)
                {
                    var beforeFormatterAcceptance = current.FormatterInputState
                        ?? throw new InvalidOperationException("Accepted input history missing.");
                    var historicalFormatterModel = current.FormatterModel
                        ?? throw new InvalidOperationException("Formatter model history missing.");
                    var historicalFormatterGuest = current.FormatterGuest
                        ?? throw new InvalidOperationException("Formatter guest history missing.");
                    var formatterAcceptance = current.Ledger.AcceptedS02
                        ?? throw new InvalidOperationException("Formatter acceptance missing.");
                    var acceptedInputs = ReadFormatterInputs(beforeFormatterAcceptance,
                        ObserveCalculatorBaseline(root, claim.Binding), CalculatorRecoveryPair(root));
                    var historicalOutcome = historicalFormatterGuest.LatestGuestOutcome
                        ?? throw new InvalidOperationException("Formatter outcome missing.");
                    byte[] protectedFormatterReport = owner.RequireOrderGuestReport(historicalOutcome, historicalOutcome.Reservation);
                    var freshFormatterObservation = ObserveFixtureGuest(claim, historicalOutcome.Reservation, protectedFormatterReport)
                        ?? throw new InvalidOperationException("Formatter observation missing.");
                    var acceptedFormatter = DevelopmentOrderFormatterVerifier.RequireCompletedGuest(owner, beforeFormatterAcceptance,
                        historicalFormatterModel, historicalFormatterGuest, acceptedInputs, freshFormatterObservation);
                    DevelopmentOrderFormatterVerifier.RequirePairScope(owner, accepted, acceptedFormatter,
                        ObserveCalculatorBaseline(root, claim.Binding),
                        DevelopmentOrderCalculatorVerifier.ReadStagedPair(Path.Combine(root, "formatter-stage")));
                    if (formatterAcceptance.Predecessor != accepted || formatterAcceptance.Candidate != acceptedFormatter
                        || current.Ledger != historicalFormatterGuest.AcceptS02(claim, formatterAcceptance)
                        || current.Ledger.Sequence != 4 || current.Ledger.ReservedSeconds != 80
                        || current.Ledger.Models != 2 || current.Ledger.Guests != 2
                        || current.Ledger.StartedUtcTicks != beforeFormatterAcceptance.StartedUtcTicks
                        || current.Ledger.Pending is not null || File.Exists(database + ".s02"))
                        throw new InvalidOperationException("Replayed formatter acceptance differs.");
                    var repeatedFormatterRequest = ProposeModel(current.Ledger, "S02", 1, DateTime.UtcNow.Ticks);
                    Reject(() => current.Ledger.Reserve(claim.JobId, limits, repeatedFormatterRequest));
                    Reject(() => current.Ledger.AcceptS02(claim, formatterAcceptance));
                    try
                    {
                        _ = await actor.Ask<BridgeState>(new AcceptFormatter(), Deadline);
                        throw new InvalidOperationException("Duplicate formatter acceptance returned success.");
                    }
                    catch (InvalidOperationException ex) when (ex.Message == "Formatter acceptance blocked.") { }
                    var pairHistory = new OrderPairHistory(model, guest, beforeFormatterAcceptance,
                        historicalFormatterModel, historicalFormatterGuest, current.Ledger);
                    var pairObservation = new OrderPairObservation(ObserveCalculatorFixture(owner, claim, outcome),
                        freshFormatterObservation, ObserveCalculatorBaseline(root, claim.Binding), CalculatorRecoveryPair(root),
                        DevelopmentOrderCalculatorVerifier.ReadStagedPair(Path.Combine(root, "formatter-stage")));
                    TestIntegrationSnapshot(owner, pairHistory, pairObservation, root, database);
                    if (await ReadAsync() != current || File.Exists(database + ".guest-dispatch-5"))
                        throw new InvalidOperationException("Formatter acceptance recovery mutated journal.");
                    Console.WriteLine("BRIDGE_PHASE: persisted S02 acceptance recovered after process kill; exact predecessor, candidate, guest evidence and budget unchanged.");
                    Console.WriteLine("BRIDGE_CALCULATOR_RECOVERED");
                    return 0;
                }

                if (current.Stage is 11 or 12)
                {
                    var beforeFormatter = current.FormatterInputState
                        ?? throw new InvalidOperationException("Accepted input history missing.");
                    var completedFormatter = current.FormatterModel
                        ?? throw new InvalidOperationException("Formatter model history missing.");
                    var inputs = ReadFormatterInputs(beforeFormatter,
                        ObserveCalculatorBaseline(root, claim.Binding), CalculatorRecoveryPair(root));
                    bool success = mode == "bridge-model-resume-accepted";
                    if (current.Stage != (success ? 12 : 11)
                        || (current.Ledger.Pending is not null) == success
                        || current.Ledger.Sequence != 4 || current.Ledger.Models != 2 || current.Ledger.Guests != 2
                        || current.Ledger.ReservedSeconds != 80 || current.Ledger.AcceptedS01 != accepted
                        || current.Ledger.StartedUtcTicks != beforeFormatter.StartedUtcTicks
                        || File.Exists(database + ".guest-report-4") != success || File.Exists(database + ".s02"))
                        throw new InvalidOperationException("Recovered formatter guest journal differs.");
                    var reservation = success
                        ? current.Ledger.LatestGuestOutcome?.Reservation : current.Ledger.Pending;
                    if (reservation is not { Step: "S02", Kind: "guest" })
                        throw new InvalidOperationException("Recovered formatter guest reservation missing.");
                    owner.RequireOrderGuestDispatch(reservation);
                    DevelopmentOrderFormatterVerifier.RequireGuestSnapshot(owner, beforeFormatter, completedFormatter,
                        inputs, reservation, Path.Combine(root, "formatter-verification"));
                    if (success)
                    {
                        var recordedFormatterGuest = current.Ledger.LatestGuestOutcome
                            ?? throw new InvalidOperationException("Formatter outcome missing.");
                        byte[] report = owner.RequireOrderGuestReport(recordedFormatterGuest, reservation);
                        var observed = ObserveFixtureGuest(claim, reservation, report)
                            ?? throw new InvalidOperationException("Formatter fixture absence missing.");
                        var formatter = DevelopmentOrderFormatterVerifier.RequireCompletedGuest(owner, beforeFormatter,
                            completedFormatter, current.Ledger, inputs, observed);
                        DevelopmentOrderFormatterVerifier.RequirePairScope(owner, accepted, formatter,
                            ObserveCalculatorBaseline(root, claim.Binding),
                            DevelopmentOrderCalculatorVerifier.ReadStagedPair(Path.Combine(root, "formatter-stage")));
                        Reject(() => DevelopmentOrderFormatterVerifier.RequireCompletedGuest(owner, beforeFormatter,
                            completedFormatter, current.Ledger, inputs, null));

                        var guestBeforeAcceptance = current;
                        var formatterBaseline = ObserveCalculatorBaseline(root, claim.Binding);
                        string formatterStage = Path.Combine(root, "formatter-stage");
                        var proposedAcceptance = DevelopmentOrderFormatterVerifier.ProposeAcceptance(owner, beforeFormatter,
                            completedFormatter, current.Ledger, inputs, observed, formatterBaseline,
                            DevelopmentOrderCalculatorVerifier.ReadStagedPair(formatterStage), DateTime.UtcNow.Ticks);
                        var expectedAcceptance = current.Ledger.AcceptS02(claim, proposedAcceptance);
                        string acceptedEvent = DevelopmentOrderOperationLedger.EncodeS02Accepted(proposedAcceptance);
                        if (DevelopmentOrderOperationLedger.DecodeS02Accepted(acceptedEvent) != proposedAcceptance
                            || expectedAcceptance.AcceptedS02 != proposedAcceptance || current.Ledger.AcceptedS02 is not null
                            || expectedAcceptance.Sequence != current.Ledger.Sequence
                            || expectedAcceptance.ReservedSeconds != current.Ledger.ReservedSeconds)
                            throw new InvalidOperationException("Formatter acceptance fold differs.");
                        Reject(() => current.Ledger.AcceptS02(claim, proposedAcceptance with { Version = 2 }));
                        Reject(() => current.Ledger.AcceptS02(claim, proposedAcceptance with { GuestReportHash = new string('A', 64) }));
                        Reject(() => current.Ledger.AcceptS02(claim, proposedAcceptance with { ScopeHash = new string('A', 64) }));
                        Reject(() => current.Ledger.AcceptS02(claim, proposedAcceptance with
                        {
                            Predecessor = accepted with
                            { GuestReportHash = new string('A', 64) }
                        }));
                        Reject(() => completedFormatter.AcceptS02(claim, proposedAcceptance));
                        Reject(() => expectedAcceptance.AcceptS02(claim, proposedAcceptance));
                        Reject(() => DevelopmentOrderOperationLedger.DecodeS02Accepted(
                            "s02-accepted-v2:" + acceptedEvent["s02-accepted-v1:".Length..]));
                        string formatterStageFile = Path.Combine(formatterStage, "PairCalculator.cs");
                        byte[] formatterStageBytes = File.ReadAllBytes(formatterStageFile);
                        File.WriteAllBytes(formatterStageFile, [.. formatterStageBytes, 0x20]);
                        try
                        {
                            bool rejectedAcceptance = false;
                            try { _ = await actor.Ask<BridgeState>(new AcceptFormatter(), Deadline); }
                            catch (InvalidOperationException) { rejectedAcceptance = true; }
                            if (!rejectedAcceptance || await ReadAsync() != guestBeforeAcceptance)
                                throw new InvalidOperationException("Tampered formatter stage accepted.");
                        }
                        finally { File.WriteAllBytes(formatterStageFile, formatterStageBytes); }
                        Task<BridgeState> formatterAck = actor.Ask<BridgeState>(new AcceptFormatter(), Deadline);
                        await guard.AwaitAsync(formatterAck);
                        current = await formatterAck;
                        if (current.Stage != 13 || current.FormatterGuest != guestBeforeAcceptance.Ledger
                            || current.Ledger.AcceptedS02 is not { } actualAcceptance
                            || (actualAcceptance with { ObservedUtcTicks = proposedAcceptance.ObservedUtcTicks }) != proposedAcceptance
                            || current.Ledger != guestBeforeAcceptance.Ledger.AcceptS02(claim, actualAcceptance)
                            || await ReadAsync() != current)
                            throw new InvalidOperationException("Formatter acceptance acknowledgement differs.");
                        owner.ValidateOrderJournalFiles();
                        Console.WriteLine("BRIDGE_FORMATTER_ACCEPTED");
                        Console.Out.Flush();
                        await Task.Delay(Timeout.InfiniteTimeSpan);
                        return 1;
                    }
                    else
                    {
                        Reject(() => owner.ConsumeOrderGuestDispatch(current.Ledger, reservation));
                        Reject(() => DevelopmentOrderFormatterVerifier.RequireCompletedGuest(owner, beforeFormatter,
                            completedFormatter, current.Ledger, inputs, null));
                    }

                    if (await ReadAsync() != current || File.Exists(database + ".guest-dispatch-5"))
                        throw new InvalidOperationException("Formatter guest recovery mutated journal.");
                    Console.WriteLine("BRIDGE_PHASE: S02 guest " + (success ? "completed" : "pending blocked")
                        + " recovered after process kill; 80s charge and exact protected evidence retained.");
                    Console.WriteLine("BRIDGE_CALCULATOR_RECOVERED");
                    return 0;
                }

                if (current.Stage is 9 or 10)
                {
                    var beforeFormatter = current.FormatterInputState
                        ?? throw new InvalidOperationException("Accepted input history missing.");
                    var recoveredInputs = ReadFormatterInputs(beforeFormatter,
                        ObserveCalculatorBaseline(root, claim.Binding), CalculatorRecoveryPair(root));
                    var recovered = DevelopmentOrderModelReconciliation.Observe(owner, current.Ledger);
                    var expectedStatus = formatterCrash switch
                    {
                        "generated" => OrderModelRecoveryStatus.Generated,
                        "rejected" => OrderModelRecoveryStatus.Rejected,
                        "receipt" or "orphan" or "checkpoint" or "rejected-orphan" => OrderModelRecoveryStatus.PendingBlocked,
                        _ => throw new InvalidOperationException("Unknown formatter crash mode.")
                    };
                    bool blocked = expectedStatus == OrderModelRecoveryStatus.PendingBlocked;
                    bool hasCandidate = formatterCrash is "generated" or "orphan" or "checkpoint";
                    bool hasEvidence = formatterCrash != "receipt";
                    var candidate = recovered.Candidate;
                    if (recovered.Status != expectedStatus || (candidate is not null) != (expectedStatus == OrderModelRecoveryStatus.Generated)
                        || (recovered.Completion is null) != blocked || (current.Ledger.Pending is not null) != blocked
                        || current.Stage != (blocked ? 9 : 10) || recovered.Reservation.Step != "S02"
                        || (current.Ledger.LatestCandidate?.Step == "S02") != (formatterCrash is "checkpoint" or "generated")
                        || beforeFormatter.AcceptedS01 != accepted || current.Ledger.Sequence != 3
                        || current.Ledger.Models != 2 || current.Ledger.Guests != 1 || current.Ledger.ReservedSeconds != 70
                        || current.Ledger.StartedUtcTicks != beforeFormatter.StartedUtcTicks
                        || File.Exists(database + ".candidate-3") != hasCandidate
                        || File.Exists(database + ".evidence-3") != hasEvidence)
                        throw new InvalidOperationException("Recovered formatter journal differs.");
                    if (candidate is not null && (candidate.Step != "S02"
                        || candidate.InputHash != DevelopmentOrderFormatterVerifier.RequireInputHash(claim, accepted, recoveredInputs)
                        || !owner.RequireOrderCandidate(candidate).SequenceEqual(Encoding.UTF8.GetBytes("class PairFormatter {}"))))
                        throw new InvalidOperationException("Recovered formatter input differs.");

                    owner.RequireOrderModelDispatch(recovered.Reservation, accepted);
                    if (blocked)
                    {
                        Reject(() => owner.ConsumeOrderModelDispatch(current.Ledger, recovered.Reservation));
                        var retry = current.Ledger.Propose(claim.JobId, "S02", "model", 1, DateTime.UtcNow.Ticks,
                            recovered.Reservation.Assignment);
                        Reject(() => current.Ledger.Reserve(claim.JobId, limits, retry));
                    }
                    else
                    {
                        string[] suffixes = candidate is null ? [".model-dispatch-3", ".evidence-3"]
                            : [".model-dispatch-3", ".candidate-3", ".evidence-3"];
                        foreach (string suffix in suffixes)
                        {
                            string evidenceFile = database + suffix;
                            byte[] bytes = File.ReadAllBytes(evidenceFile);
                            File.WriteAllBytes(evidenceFile, [.. bytes, 0x20]);
                            try { Reject(() => DevelopmentOrderModelReconciliation.Observe(owner, current.Ledger)); }
                            finally { File.WriteAllBytes(evidenceFile, bytes); }
                        }
                    }

                    if (candidate is null)
                    {
                        Reject(() => DevelopmentOrderFormatterVerifier.Prepare(owner, beforeFormatter, current.Ledger, recoveredInputs));
                    }
                    else
                    {
                        var completedFormatter = current;
                        async Task<DevelopmentOrderOperationLedger> AdmitGuest(OrderOperationReservation request)
                        {
                            Task<BridgeState> ack = actor.Ask<BridgeState>(new AdmitFormatterGuest(request), Deadline);
                            await guard.AwaitAsync(ack);
                            current = await ack;
                            if (current.Stage != 11 || current.FormatterModel != completedFormatter.Ledger)
                                throw new InvalidOperationException("Formatter guest admission stage differs.");
                            return current.Ledger;
                        }

                        async Task<DevelopmentOrderOperationLedger> CompleteGuest(OrderGuestOutcome guestOutcome)
                        {
                            Task<BridgeState> ack = actor.Ask<BridgeState>(new RecordFormatterGuest(guestOutcome), Deadline);
                            await guard.AwaitAsync(ack);
                            current = await ack;
                            if (current.Stage != 12)
                                throw new InvalidOperationException("Formatter guest completion stage differs.");
                            return current.Ledger;
                        }

                        await TestFormatterSnapshotAsync(owner, beforeFormatter, completedFormatter.Ledger, recoveredInputs,
                            root, database, mode == "bridge-model-resume-accepted", AdmitGuest, CompleteGuest);
                        if (await ReadAsync() != current || current.Stage != (mode == "bridge-model-resume-accepted" ? 12 : 11))
                            throw new InvalidOperationException("Formatter guest journal differs before kill.");
                        owner.ValidateOrderJournalFiles();
                        Console.WriteLine("BRIDGE_FORMATTER_GUEST_CAPTURED");
                        Console.Out.Flush();
                        await Task.Delay(Timeout.InfiniteTimeSpan);
                        return 1;
                    }

                    if (DevelopmentOrderModelReconciliation.Observe(owner, current.Ledger) != recovered
                        || await ReadAsync() != current || File.Exists(database + ".model-dispatch-4"))
                        throw new InvalidOperationException("Formatter recovery changed history.");
                    Console.WriteLine("BRIDGE_PHASE: S02 " + formatterCrash + " recovered after process kill; exact predecessor, journal status and private slots rechecked.");
                    Console.WriteLine("BRIDGE_CALCULATOR_RECOVERED");
                    return 0;
                }

                void VerifyAccepted()
                {
                    var baseline = ObserveCalculatorBaseline(root, claim.Binding);
                    var inputs = ReadFormatterInputs(current.Ledger, baseline, CalculatorRecoveryPair(root));
                    if (inputs.CalculatorAcceptance != accepted
                        || !inputs.Calculator.AsSpan().SequenceEqual(owner.RequireOrderCandidate(accepted.Candidate))
                        || !inputs.FormatterBaseline.AsSpan().SequenceEqual(baseline.Formatter))
                        throw new InvalidOperationException("Replayed calculator acceptance differs.");
                }

                if (current.Stage != 8 || current.Ledger.Sequence != 2 || current.Ledger.ReservedSeconds != 60
                    || current.Ledger.Pending is not null || current.Ledger.StartedUtcTicks >= processStart)
                    throw new InvalidOperationException("Replayed calculator budget differs.");
                VerifyAccepted();
                string path = database + ".guest-report-2";
                byte[] original = File.ReadAllBytes(path);
                File.WriteAllBytes(path, [.. original, 0x20]);
                try { Reject(VerifyAccepted); }
                finally { File.WriteAllBytes(path, original); }
                VerifyAccepted();
                var reviewedBaseline = ObserveCalculatorBaseline(root, claim.Binding);
                var stagedPair = CalculatorRecoveryPair(root);
                var handover = ReadFormatterInputs(current.Ledger, reviewedBaseline, stagedPair);
                Reject(() => ReadFormatterInputs(guest, reviewedBaseline, stagedPair));
                Reject(() => ReadFormatterInputs(current.Ledger with
                {
                    AcceptedS01 = accepted with { GuestReportHash = new string('0', 64) }
                }, reviewedBaseline, stagedPair));
                Reject(() => ReadFormatterInputs(current.Ledger with { ReservedSeconds = 0 }, reviewedBaseline, stagedPair));
                Reject(() => ReadFormatterInputs(current.Ledger,
                    reviewedBaseline with { Formatter = [.. reviewedBaseline.Formatter, 0x20] }, stagedPair));
                stagedPair[DevelopmentAgentContract.PairFirst][0] ^= 1;
                Reject(() => ReadFormatterInputs(current.Ledger, reviewedBaseline, stagedPair));
                stagedPair[DevelopmentAgentContract.PairFirst][0] ^= 1;
                handover.Calculator[0] ^= 1;
                handover.FormatterBaseline[0] ^= 1;
                reviewedBaseline.RequireBinding(claim.Binding);
                var unchangedInputs = ReadFormatterInputs(current.Ledger, reviewedBaseline, stagedPair);
                if (!unchangedInputs.Calculator.AsSpan().SequenceEqual(owner.RequireOrderCandidate(accepted.Candidate))
                    || !unchangedInputs.FormatterBaseline.AsSpan().SequenceEqual(reviewedBaseline.Formatter))
                    throw new InvalidOperationException("Caller mutated trusted input evidence.");
                VerifyAccepted();
                string inputHash = DevelopmentOrderFormatterVerifier.RequireInputHash(claim, accepted, unchangedInputs);
                Reject(() => DevelopmentOrderFormatterVerifier.RequireInputHash(claim, accepted, handover));
                Reject(() => DevelopmentOrderFormatterVerifier.RequireInputHash(claim,
                    accepted with { ObservedUtcTicks = accepted.ObservedUtcTicks + 1 }, unchangedInputs));
                var formatterRequest = ProposeModel(current.Ledger, "S02", 1, DateTime.UtcNow.Ticks);
                var formatterPending = current.Ledger.Reserve(claim.JobId, limits, formatterRequest);
                byte[] formatterBytes = Encoding.UTF8.GetBytes("class PairFormatter {}");
                var formatterCandidate = new OrderCandidateCheckpoint(2, claim.ArchiveHash, formatterRequest.OperationId,
                    "S02", DevelopmentAgentContract.PairSecond, claim.Binding.FormatterBaselineHash,
                    inputHash, formatterBytes.Length, DevelopmentJobState.Hash(formatterBytes), "Generated",
                    formatterRequest.ObservedUtcTicks);
                var folded = formatterPending.Checkpoint(claim, formatterCandidate);
                string encoded = DevelopmentOrderOperationLedger.EncodeCandidate(formatterCandidate);
                if (!encoded.StartsWith("candidate-v2:", StringComparison.Ordinal)
                    || DevelopmentOrderOperationLedger.DecodeCandidate(encoded) != formatterCandidate
                    || folded.AcceptedS01 != accepted || folded.Pending != formatterRequest
                    || folded.LatestCandidate != formatterCandidate || folded.Sequence != 3)
                    throw new InvalidOperationException("S02 checkpoint fold differs.");
                Reject(() => formatterPending.Checkpoint(claim, formatterCandidate with { InputHash = new string('0', 64) }));
                Reject(() => formatterPending.Checkpoint(claim, formatterCandidate with { SourcePath = DevelopmentAgentContract.PairFirst }));
                Reject(() => formatterPending.Checkpoint(claim, formatterCandidate with { Version = 1 }));
                Reject(() => (formatterPending with { AcceptedS01 = null }).Checkpoint(claim, formatterCandidate));
                Reject(() => DevelopmentOrderOperationLedger.DecodeCandidate("candidate-v1:" + encoded["candidate-v2:".Length..]));
                if (DevelopmentOrderOperationLedger.DecodeCandidate(
                    DevelopmentOrderOperationLedger.EncodeCandidate(accepted.Candidate)) != accepted.Candidate)
                    throw new InvalidOperationException("Historical S01 candidate encoding changed.");
                Console.WriteLine("BRIDGE_PHASE: S02 versioned checkpoint binds accepted predecessor; changed input, path, version and missing acceptance rejected.");
                Console.WriteLine("BRIDGE_PHASE: accepted S01 bytes handed to S02 read-only input gate; missing or changed acceptance, budget, baseline and predecessor rejected.");

                // Admit and acknowledge the S02 operation before dispatch.
                // All actual model/guest effects in this fixture are controlled.
                var formatterAssignment = ModelFixtureAssignment(claim, archive, "S02");
                OrderAgentProfile[] formatterCatalog = [new(formatterAssignment.Agent, true, ["csharp"])];
                var formatterApproval = await CalculatorRecoveryApprovalAsync(root);
                var actualRequest = current.Ledger.Propose(claim.JobId, "S02", "model", 10,
                    DateTime.UtcNow.Ticks, OrderOperationAgentBinding.FromAssignment(formatterAssignment));
                var acceptedState = current;
                Task<BridgeState> formatterAdmission = actor.Ask<BridgeState>(new AdmitFormatter(actualRequest), Deadline);
                await guard.AwaitAsync(formatterAdmission);
                current = await formatterAdmission;
                var actualPending = current.Ledger;
                if (current.Stage != 9 || actualPending.Pending != actualRequest || actualPending.Sequence != 3
                    || actualPending.ReservedSeconds != 70 || current.FormatterInputState != acceptedState.Ledger)
                    throw new InvalidOperationException("Formatter admission was not persisted and acknowledged.");
                async Task<BridgeState> RecordFormatterAsync(string evt)
                {
                    Task<BridgeState> ack = actor.Ask<BridgeState>(new RecordFormatter(evt), Deadline);
                    await guard.AwaitAsync(ack);
                    return await ack;
                }
                async Task<int> HoldFormatterAsync()
                {
                    owner.ValidateOrderJournalFiles();
                    Console.WriteLine("BRIDGE_FORMATTER_CAPTURED");
                    Console.Out.Flush();
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                    return 1;
                }

                if (formatterCrash == "receipt")
                {
                    owner.ConsumeOrderModelDispatch(actualPending, actualRequest);
                    return await HoldFormatterAsync();
                }

                bool rejectedFormatter = formatterCrash is "rejected" or "rejected-orphan";
                using var formatterClient = new DevelopmentAiGenerator.ScriptedChatClient(
                    rejectedFormatter ? """{"source":""}"""
                        : System.Text.Json.JsonSerializer.Serialize(new { source = "class PairFormatter {}" }));
                var formatterGenerator = new DevelopmentAiGenerator(formatterClient, ModelName);
                Task<OrderModelDispatchResult> DispatchFormatter(OrderFormatterInputs inputs) =>
                    DevelopmentOrderModelDispatch.RunS02CoreAsync(owner, actualPending, actualRequest,
                        formatterApproval, formatterAssignment, formatterCatalog, inputs, formatterGenerator);
                bool invalidInput = false;
                try { _ = await DispatchFormatter(handover); }
                catch (InvalidOperationException) { invalidInput = true; }
                if (!invalidInput || formatterClient.Calls != 0)
                    throw new InvalidOperationException("Changed formatter input reached provider.");
                var result = await DispatchFormatter(unchangedInputs);
                if (formatterClient.Calls != 1 || (result.Candidate is null) != rejectedFormatter
                    || result.Completion.Outcome != (rejectedFormatter ? "failed" : "succeeded"))
                    throw new InvalidOperationException("Formatter crash fixture outcome differs.");
                if (formatterCrash is "orphan" or "rejected-orphan")
                    return await HoldFormatterAsync();
                if (rejectedFormatter)
                {
                    current = await RecordFormatterAsync(DevelopmentOrderOperationLedger.EncodeCompletion(result.Completion));
                    if (current.Stage != 10 || current.Ledger != actualPending.Complete(result.Completion)
                        || DevelopmentOrderModelReconciliation.Observe(owner, current.Ledger).Status != OrderModelRecoveryStatus.Rejected)
                        throw new InvalidOperationException("Rejected formatter completion differs.");
                    return await HoldFormatterAsync();
                }

                var generated = result.Candidate ?? throw new InvalidOperationException("Formatter candidate missing.");
                var generatedState = actualPending.Checkpoint(claim, generated);
                current = await RecordFormatterAsync(DevelopmentOrderOperationLedger.EncodeCandidate(generated));
                if (current.Stage != 9 || current.Ledger != generatedState)
                    throw new InvalidOperationException("Formatter checkpoint acknowledgement differs.");
                if (formatterCrash == "checkpoint")
                    return await HoldFormatterAsync();

                var completedState = generatedState.Complete(result.Completion);
                current = await RecordFormatterAsync(DevelopmentOrderOperationLedger.EncodeCompletion(result.Completion));
                if (current.Stage != 10 || current.Ledger != completedState)
                    throw new InvalidOperationException("Formatter completion acknowledgement differs.");
                var recoveredModel = DevelopmentOrderModelReconciliation.Observe(owner, completedState);
                if (formatterClient.Calls != 1 || generated.InputHash != inputHash
                    || recoveredModel.Status != OrderModelRecoveryStatus.Generated || recoveredModel.Candidate != generated
                    || completedState.AcceptedS01 != accepted
                    || DevelopmentOrderModelReconciliation.Observe(owner, generatedState).Status != OrderModelRecoveryStatus.PendingBlocked)
                    throw new InvalidOperationException("Formatter dispatch or reconciliation differs.");
                bool duplicate = false;
                try { _ = await DispatchFormatter(unchangedInputs); }
                catch (InvalidOperationException) { duplicate = true; }
                if (!duplicate || formatterClient.Calls != 1)
                    throw new InvalidOperationException("Formatter dispatch repeated.");
                Reject(() => DevelopmentOrderModelReconciliation.Observe(owner, completedState with { AcceptedS01 = null }));
                Reject(() => DevelopmentOrderModelReconciliation.Observe(owner, completedState with
                {
                    AcceptedS01 = accepted with { ObservedUtcTicks = accepted.ObservedUtcTicks + 1 }
                }));
                string formatterReceipt = database + ".model-dispatch-3";
                byte[] receiptBytes = File.ReadAllBytes(formatterReceipt);
                File.WriteAllBytes(formatterReceipt, [.. receiptBytes, 0x20]);
                try { Reject(() => DevelopmentOrderModelReconciliation.Observe(owner, completedState)); }
                finally { File.WriteAllBytes(formatterReceipt, receiptBytes); }
                string formatterEvidence = database + ".evidence-3";
                byte[] evidenceBytes = File.ReadAllBytes(formatterEvidence);
                File.WriteAllBytes(formatterEvidence, [.. evidenceBytes, 0x20]);
                try { Reject(() => DevelopmentOrderModelReconciliation.Observe(owner, completedState)); }
                finally { File.WriteAllBytes(formatterEvidence, evidenceBytes); }
                string formatterPath = database + ".candidate-3";
                byte[] candidateBytes = File.ReadAllBytes(formatterPath);
                File.WriteAllBytes(formatterPath, [.. candidateBytes, 0x20]);
                try { Reject(() => DevelopmentOrderModelReconciliation.Observe(owner, completedState)); }
                finally { File.WriteAllBytes(formatterPath, candidateBytes); }
                if (DevelopmentOrderModelReconciliation.Observe(owner, completedState).Candidate != generated)
                    throw new InvalidOperationException("Formatter evidence readback differs.");
                Console.WriteLine("BRIDGE_PHASE: S02 private dispatch, receipt, candidate and completion evidence bound; duplicate call and tampering rejected.");
                Reject(() => current.Ledger.Reserve(claim.JobId, limits,
                    ProposeModel(current.Ledger, "S01", 1, DateTime.UtcNow.Ticks)));
                try
                {
                    _ = await actor.Ask<BridgeState>(new AcceptCalculator(), Deadline);
                    throw new InvalidOperationException("Duplicate acceptance was acknowledged.");
                }
                catch (InvalidOperationException ex) when (ex.Message == "Calculator acceptance blocked.") { }
                if (await ReadAsync() != current)
                    throw new InvalidOperationException("Calculator reentry mutated journal state.");
                return await HoldFormatterAsync();
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
    private sealed record AcceptCalculator;
    private sealed record AdmitFormatter(OrderOperationReservation Request);
    private sealed record RecordFormatter(string Event);
    private sealed record AdmitFormatterGuest(OrderOperationReservation Request);
    private sealed record RecordFormatterGuest(OrderGuestOutcome Outcome);
    private sealed record AcceptFormatter;

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
        RecordedOperation? S01 = null, RecordedOperation? S02 = null, RecordedOperation? FailedS01 = null,
        DevelopmentOrderOperationLedger? CalculatorModel = null, DevelopmentOrderOperationLedger? CalculatorGuest = null,
        DevelopmentOrderOperationLedger? FormatterInputState = null, DevelopmentOrderOperationLedger? FormatterModel = null,
        DevelopmentOrderOperationLedger? FormatterGuest = null);

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
        private readonly bool calculatorScenario;

        public override string PersistenceId { get; }

        public BridgeActor(DevelopmentOrderClaim claim, DevelopmentOrderArchive archive,
            OrderOperationLimits limits, DevelopmentOrderBudgetPolicy policy, DevelopmentJobStateStore.Lease owner,
            bool modelScenario, bool softScenario, bool calculatorScenario, string root)
        {
            this.calculatorScenario = calculatorScenario;
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
            Command<AcceptCalculator>(command =>
            {
                if (!recovered || !calculatorScenario || state.Stage != 6 || state.Ledger.AcceptedS01 is not null)
                {
                    Sender.Tell(new Status.Failure(new InvalidOperationException("Calculator acceptance blocked.")));
                    return;
                }

                try
                {
                    var model = state.CalculatorModel ?? throw new InvalidOperationException("Model history missing.");
                    var outcome = state.Ledger.LatestGuestOutcome ?? throw new InvalidOperationException("Guest outcome missing.");
                    var observed = ObserveCalculatorFixture(owner, claim, outcome);
                    var baseline = ObserveCalculatorBaseline(root, claim.Binding);
                    var proposal = DevelopmentOrderCalculatorVerifier.ProposeAcceptance(owner, model, state.Ledger,
                        observed, baseline.Commit, baseline.Scope, baseline.Calculator, baseline.Formatter,
                        CalculatorRecoveryPair(root), DateTime.UtcNow.Ticks);
                    string evt = DevelopmentOrderOperationLedger.EncodeS01Accepted(proposal);
                    BridgeState next = Next(state, evt);
                    IActorRef reply = Sender;
                    Persist(evt, _ =>
                    {
                        state = next;
                        reply.Tell(state);
                    });
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    Sender.Tell(new Status.Failure(ex));
                }
            });
            Command<AdmitFormatter>(command =>
            {
                try
                {
                    if (!recovered || !calculatorScenario || state.Stage != 8
                        || command.Request is not { Step: "S02", Kind: "model" })
                        throw new InvalidOperationException("Formatter admission blocked.");
                    var model = state.CalculatorModel ?? throw new InvalidOperationException("Model history missing.");
                    var guest = state.CalculatorGuest ?? throw new InvalidOperationException("Guest history missing.");
                    var outcome = guest.LatestGuestOutcome ?? throw new InvalidOperationException("Guest outcome missing.");
                    _ = DevelopmentOrderFormatterVerifier.RequireInputs(owner, model, guest, state.Ledger,
                        ObserveCalculatorFixture(owner, claim, outcome), ObserveCalculatorBaseline(root, claim.Binding),
                        CalculatorRecoveryPair(root));
                    string evt = DevelopmentOrderOperationLedger.EncodeReservation(command.Request);
                    BridgeState next = Next(state, evt);
                    IActorRef reply = Sender;
                    Persist(evt, _ =>
                    {
                        state = next;
                        reply.Tell(state);
                    });
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    Sender.Tell(new Status.Failure(ex));
                }
            });
            Command<RecordFormatter>(command =>
            {
                try
                {
                    if (!recovered || !calculatorScenario || state.Stage != 9)
                        throw new InvalidOperationException("Formatter recording blocked.");
                    BridgeState next = Next(state, command.Event);
                    var reservation = state.Ledger.Pending ?? throw new InvalidOperationException("Formatter reservation missing.");
                    var accepted = state.Ledger.AcceptedS01 ?? throw new InvalidOperationException("Calculator acceptance missing.");
                    owner.RequireOrderModelDispatch(reservation, accepted);
                    if (command.Event.StartsWith("candidate-v2:", StringComparison.Ordinal))
                        _ = owner.RequireOrderCandidate(next.Ledger.LatestCandidate
                            ?? throw new InvalidOperationException("Formatter candidate missing."));
                    else if (DevelopmentOrderModelReconciliation.Observe(owner, next.Ledger).Status
                        == OrderModelRecoveryStatus.PendingBlocked)
                        throw new InvalidOperationException("Formatter completion has no verified evidence.");

                    IActorRef reply = Sender;
                    Persist(command.Event, _ =>
                    {
                        state = next;
                        reply.Tell(state);
                    });
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    Sender.Tell(new Status.Failure(ex));
                }
            });
            OrderFormatterInputs RequireFormatterInputs()
            {
                var model = state.CalculatorModel ?? throw new InvalidOperationException("Calculator model history missing.");
                var guest = state.CalculatorGuest ?? throw new InvalidOperationException("Calculator guest history missing.");
                var accepted = state.FormatterInputState ?? throw new InvalidOperationException("Accepted input history missing.");
                var outcome = guest.LatestGuestOutcome ?? throw new InvalidOperationException("Calculator outcome missing.");
                return DevelopmentOrderFormatterVerifier.RequireInputs(owner, model, guest, accepted,
                    ObserveCalculatorFixture(owner, claim, outcome), ObserveCalculatorBaseline(root, claim.Binding),
                    CalculatorRecoveryPair(root));
            }

            Command<AdmitFormatterGuest>(command =>
            {
                try
                {
                    if (!recovered || !calculatorScenario || state.Stage != 10)
                        throw new InvalidOperationException("Formatter guest admission blocked.");
                    var accepted = state.FormatterInputState ?? throw new InvalidOperationException("Accepted input history missing.");
                    DevelopmentOrderFormatterVerifier.RequireGuestSnapshot(owner, accepted, state.Ledger,
                        RequireFormatterInputs(), command.Request, Path.Combine(root, "formatter-verification"));
                    string evt = DevelopmentOrderOperationLedger.EncodeReservation(command.Request);
                    BridgeState next = Next(state, evt);
                    IActorRef reply = Sender;
                    Persist(evt, _ =>
                    {
                        state = next;
                        reply.Tell(state);
                    });
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    Sender.Tell(new Status.Failure(ex));
                }
            });
            Command<RecordFormatterGuest>(command =>
            {
                try
                {
                    if (!recovered || !calculatorScenario || state.Stage != 11)
                        throw new InvalidOperationException("Formatter guest recording blocked.");
                    var accepted = state.FormatterInputState ?? throw new InvalidOperationException("Accepted input history missing.");
                    var model = state.FormatterModel ?? throw new InvalidOperationException("Formatter model history missing.");
                    string evt = DevelopmentOrderOperationLedger.EncodeGuestOutcome(command.Outcome);
                    BridgeState next = Next(state, evt);
                    byte[] report = owner.RequireOrderGuestReport(command.Outcome, command.Outcome.Reservation);
                    _ = DevelopmentOrderFormatterVerifier.RequireCompletedGuest(owner, accepted, model, next.Ledger,
                        RequireFormatterInputs(), ObserveFixtureGuest(claim, command.Outcome.Reservation, report));
                    IActorRef reply = Sender;
                    Persist(evt, _ =>
                    {
                        state = next;
                        reply.Tell(state);
                    });
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    Sender.Tell(new Status.Failure(ex));
                }
            });
            Command<AcceptFormatter>(command =>
            {
                if (!recovered || !calculatorScenario || state.Stage != 12 || state.Ledger.AcceptedS02 is not null)
                {
                    Sender.Tell(new Status.Failure(new InvalidOperationException("Formatter acceptance blocked.")));
                    return;
                }
                try
                {
                    var accepted = state.FormatterInputState ?? throw new InvalidOperationException("Accepted input history missing.");
                    var model = state.FormatterModel ?? throw new InvalidOperationException("Formatter model history missing.");
                    var outcome = state.Ledger.LatestGuestOutcome ?? throw new InvalidOperationException("Formatter outcome missing.");
                    byte[] report = owner.RequireOrderGuestReport(outcome, outcome.Reservation);
                    var proposal = DevelopmentOrderFormatterVerifier.ProposeAcceptance(owner, accepted, model, state.Ledger,
                        RequireFormatterInputs(), ObserveFixtureGuest(claim, outcome.Reservation, report),
                        ObserveCalculatorBaseline(root, claim.Binding),
                        DevelopmentOrderCalculatorVerifier.ReadStagedPair(Path.Combine(root, "formatter-stage")), DateTime.UtcNow.Ticks);
                    string evt = DevelopmentOrderOperationLedger.EncodeS02Accepted(proposal);
                    BridgeState next = Next(state, evt);
                    IActorRef reply = Sender;
                    Persist(evt, _ =>
                    {
                        state = next;
                        reply.Tell(state);
                    });
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    Sender.Tell(new Status.Failure(ex));
                }
            });
            Command<Advance>(command =>
            {
                if (!recovered)
                    throw new InvalidOperationException("Recovery incomplete.");
                if (calculatorScenario && state.Stage >= 8)
                {
                    Sender.Tell(new Status.Failure(new InvalidOperationException("Use formatter commands.")));
                    return;
                }
                if (command.Event.StartsWith("s01-accepted-v1:", StringComparison.Ordinal)
                    || command.Event.StartsWith("s02-accepted-v1:", StringComparison.Ordinal))
                {
                    Sender.Tell(new Status.Failure(new InvalidOperationException("Use calculator acceptance command.")));
                    return;
                }

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
                {
                    if (calculatorScenario)
                    {
                        byte[] manifest = DevelopmentOrderSnapshot.CaptureBytes(
                            DevelopmentOrderCalculatorVerifier.Prepare(owner, state.Ledger), "Demo.csproj");
                        if (!owner.RequireOrderSnapshotManifest(reservedGuest).SequenceEqual(manifest))
                            throw new InvalidOperationException("Calculator snapshot differs.");
                    }
                    else
                        VerifyFixtureSnapshot(owner, reservedGuest);
                }
                if (command.Event.StartsWith("guest-outcome-v1:", StringComparison.Ordinal))
                {
                    var outcome = next.Ledger.LatestGuestOutcome
                        ?? throw new InvalidOperationException("Guest outcome missing.");
                    if (calculatorScenario)
                        _ = DevelopmentOrderCalculatorVerifier.RequireCompletedGuest(owner,
                            next.CalculatorModel ?? throw new InvalidOperationException("Model history missing."),
                            next.Ledger, ObserveCalculatorFixture(owner, claim, outcome));
                    else
                        VerifyFixtureGuest(owner, claim, outcome);
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
            if (calculatorScenario && previous.Stage == 8)
            {
                var reservation = DevelopmentOrderOperationLedger.DecodeReservation(evt);
                if (reservation is not { Step: "S02", Kind: "model" })
                    throw new InvalidOperationException("Formatter model reservation required.");
                VerifyModelFixtureAssignment(claim, archive, reservation);
                var effective = previous.Ledger.CurrentLimits ?? throw new InvalidOperationException("Effective budget missing.");
                return previous with
                {
                    Stage = 9,
                    FormatterInputState = previous.Ledger,
                    Ledger = previous.Ledger.ReserveWithRequiredWork(claim.JobId, effective, reservation,
                        [new("S02", "guest", 10), new("FINAL", "guest", policy.FinalVerificationSeconds)])
                };
            }
            if (calculatorScenario && previous.Stage == 9)
            {
                var reservation = previous.Ledger.Pending ?? throw new InvalidOperationException("Formatter reservation missing.");
                if (reservation is not { Step: "S02", Kind: "model" })
                    throw new InvalidOperationException("Unexpected formatter operation.");
                if (evt.StartsWith("candidate-v2:", StringComparison.Ordinal))
                    return previous with
                    {
                        Ledger = previous.Ledger.Checkpoint(claim, DevelopmentOrderOperationLedger.DecodeCandidate(evt))
                    };
                var completion = DevelopmentOrderOperationLedger.DecodeCompletion(evt);
                if ((completion.Outcome == "succeeded") != (previous.Ledger.LatestCandidate?.OperationId == reservation.OperationId))
                    throw new InvalidOperationException("Unexpected formatter completion.");
                return previous with { Stage = 10, Ledger = previous.Ledger.Complete(completion) };
            }
            if (calculatorScenario && previous.Stage == 10)
            {
                var reservation = DevelopmentOrderOperationLedger.DecodeReservation(evt);
                if (reservation is not { Step: "S02", Kind: "guest" } || previous.Ledger.LastOutcome != "succeeded")
                    throw new InvalidOperationException("Formatter guest reservation required.");
                var effective = previous.Ledger.CurrentLimits ?? throw new InvalidOperationException("Effective budget missing.");
                return previous with
                {
                    Stage = 11,
                    FormatterModel = previous.Ledger,
                    Ledger = previous.Ledger.ReserveWithRequiredWork(claim.JobId, effective, reservation,
                        [new("FINAL", "guest", policy.FinalVerificationSeconds)])
                };
            }
            if (calculatorScenario && previous.Stage == 11)
            {
                var outcome = DevelopmentOrderOperationLedger.DecodeGuestOutcome(evt);
                if (outcome.Reservation is not { Step: "S02", Kind: "guest" })
                    throw new InvalidOperationException("Formatter guest outcome required.");
                return previous with { Stage = 12, Ledger = previous.Ledger.CompleteGuest(outcome) };
            }
            if (calculatorScenario && previous.Stage == 12)
                return previous with
                {
                    Stage = 13,
                    FormatterGuest = previous.Ledger,
                    Ledger = previous.Ledger.AcceptS02(claim, DevelopmentOrderOperationLedger.DecodeS02Accepted(evt))
                };
            if (calculatorScenario && previous.Stage == 7)
            {
                var reservation = DevelopmentOrderOperationLedger.DecodeReservation(evt);
                if (reservation is not { Step: "S01", Kind: "guest" })
                    throw new InvalidOperationException("Calculator guest reservation required.");
                return previous with
                {
                    Stage = 2,
                    CalculatorModel = previous.Ledger,
                    Ledger = previous.Ledger.Reserve(claim.JobId, limits, reservation)
                };
            }
            if (calculatorScenario && previous.Stage == 6)
                return previous with
                {
                    Stage = 8,
                    CalculatorGuest = previous.Ledger,
                    Ledger = previous.Ledger.AcceptS01(claim, DevelopmentOrderOperationLedger.DecodeS01Accepted(evt))
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
