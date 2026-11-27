using System.Diagnostics;
using Akka.Actor;
using Akka.Pattern;
using HaMi.Agentic.Cli.Features.Development;

internal static partial class PersistenceBridgeProbe
{
    // Bounded acceptance composition only. The operator entry point must
    // independently bind the reviewed executable, acquire the canonical
    // account claim/lease, and establish exclusive backend ownership.
    // No default probe/CI path invokes this method.
    internal static async Task<int> RunPairAcceptanceAsync(string hostRepository, string hostCommit,
        string baselineRepository, string root, DevelopmentJobStateStore store,
        DevelopmentJobStateStore.Lease owner, GitHubIssueGateway gateway, string apiKey,
        bool first, CancellationToken ct)
    {
        OrderCheck.Require(OperatingSystem.IsWindows(), "ACCEPTANCE_HAMI11_ONLY");
        long processStart = DateTime.UtcNow.Ticks;
        var profile = PairAcceptanceProfile.Load();
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Acceptance claim missing.");
        profile.RequireBinding(claim.Binding);
        var archive = owner.RequireArchivedOrder();
        OrderCheck.Require(archive.EffectiveLimits == profile.Policy.Defaults
            && archive.Binding == claim.Binding, "ACCEPTANCE_ARCHIVE");
        // Stage data has its own existing protected workspace; never use
        // the workspace store to acquire the actual order/backend lease.
        OrderCheck.Require(root == DevelopmentJobStateRoot.PreparePairAcceptanceWorkspace(claim.JobId, create: false),
            "ACCEPTANCE_WORKSPACE_IDENTITY");
        DevelopmentIsolatedRunner.ValidateStoragePaths(hostRepository, baselineRepository, root);
        string results = Path.Combine(root, "results");
        if (first)
        {
            OrderCheck.Require(!Directory.Exists(results) && !File.Exists(results)
                && new DirectoryInfo(results).LinkTarget is null, "ACCEPTANCE_RESULTS_EXISTS");
            Directory.CreateDirectory(results);
        }
        OrderCheck.Require(Directory.Exists(results) && new DirectoryInfo(results).LinkTarget is null
            && (File.GetAttributes(results) & FileAttributes.ReparsePoint) == 0, "ACCEPTANCE_RESULTS_ROOT");
        await PairAcceptanceEntry.RequireHostAsync(hostRepository, hostCommit);
        store.RequireAcceptanceBackendIdle(PairAcceptanceProfile.IssueNumber);
        await profile.ObserveApprovalAsync(baselineRepository, gateway, claim, hostCommit, ct);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Acceptance model credential missing.");

        using var host = await OrderJournalHost.OpenAsync(owner, first);
        try
        {
            var actor = host.System.ActorOf(Props.Create(() => new BridgeActor(claim, archive,
                archive.EffectiveLimits, profile.Policy, owner, true, false, true, root,
                profile, baselineRepository, ct)), "bridge");
            async Task<BridgeState> AskAsync(object command)
            {
                ct.ThrowIfCancellationRequested();
                Task<BridgeState> pending = actor.Ask<BridgeState>(command, Deadline);
                await host.Guard.AwaitAsync(pending);
                return await pending;
            }

            BridgeState current = await AskAsync(new ReadState());
            host.Guard.RequireObserved();

            async Task<DevelopmentOrderApproval> AuthorityAsync()
            {
                ct.ThrowIfCancellationRequested();
                await PairAcceptanceEntry.RequireHostAsync(hostRepository, hostCommit);
                _ = DevelopmentJobStateRoot.PreparePairAcceptanceWorkspace(claim.JobId, create: false);
                store.RequireAcceptanceBackendIdle(PairAcceptanceProfile.IssueNumber);
                return await profile.ObserveApprovalAsync(baselineRepository, gateway, claim, hostCommit, ct);
            }

            async Task<OrderGitBaseline> BaselineAsync() =>
                await DevelopmentWorktreeGuard.ObserveOrderBaselineAsync(baselineRepository, claim.Binding);

            static DevelopmentOrderOperationLedger RequireLedger(DevelopmentOrderOperationLedger? ledger) =>
                ledger ?? throw new InvalidOperationException("Acceptance journal history missing.");

            static OrderGuestOutcome RequireGuest(DevelopmentOrderOperationLedger ledger) =>
                ledger.LatestGuestOutcome ?? throw new InvalidOperationException("Acceptance guest history missing.");

            string NewStage(string name, IReadOnlyDictionary<string, byte[]> files)
            {
                _ = DevelopmentJobStateRoot.PreparePairAcceptanceWorkspace(claim.JobId, create: false);
                string target = Path.Combine(root, name);
                OrderCheck.Require(!Directory.Exists(target) && !File.Exists(target)
                    && new DirectoryInfo(target).LinkTarget is null, "ACCEPTANCE_STAGE_EXISTS");
                Directory.CreateDirectory(target);
                foreach (var file in files)
                {
                    OrderCheck.Require(Path.GetFileName(file.Key) == file.Key, "ACCEPTANCE_STAGE_FILE");
                    using var stream = new FileStream(Path.Combine(target, file.Key), FileMode.CreateNew,
                        FileAccess.Write, FileShare.None);
                    stream.Write(file.Value);
                    stream.Flush(flushToDisk: true);
                }
                return target;
            }

            void RequireAction(OrderWorkflowAction expected) =>
                OrderCheck.Require(DevelopmentOrderWorkflow.Select(claim, current.Ledger) == expected,
                    "ACCEPTANCE_WORKFLOW_ACTION");

            async Task AdmitAsync(OrderOperationReservation request, object command,
                Action? storeManifest = null)
            {
                var decision = profile.Decide(claim, current.Ledger, request);
                if (decision.Ledger.Pending is not null)
                    storeManifest?.Invoke();
                current = await AskAsync(command);
                DevelopmentOrderAdmission.RequireAcknowledged(claim, decision, current.Ledger);
                OrderCheck.Require(current.Ledger.Pending == request, "ACCEPTANCE_BUDGET_STOP");
            }

            async Task RecordModelAsync(OrderModelDispatchResult result, bool formatter)
            {
                var reserved = current.Ledger;
                if (result.Candidate is { } candidate)
                {
                    string evt = DevelopmentOrderOperationLedger.EncodeCandidate(candidate);
                    var expected = reserved.Checkpoint(claim, candidate);
                    current = await AskAsync(formatter ? new RecordFormatter(evt) : new Advance(evt));
                    OrderCheck.Require(current.Ledger == expected, "ACCEPTANCE_CANDIDATE_ACK");
                }

                _ = await DevelopmentOrderCompletion.RecordModelAsync(owner, current.Ledger, result.Completion,
                    async completion =>
                    {
                        string evt = DevelopmentOrderOperationLedger.EncodeCompletion(completion);
                        current = await AskAsync(formatter ? new RecordFormatter(evt) : new Advance(evt));
                        return current.Ledger;
                    });
                RequireAction(formatter ? OrderWorkflowAction.VerifyFormatter : OrderWorkflowAction.VerifyCalculator);
            }

            async Task RecordGuestAsync(OrderGuestOutcome? result, Func<OrderGuestOutcome, object> command)
            {
                var outcome = result ?? throw new InvalidOperationException("ACCEPTANCE_GUEST_UNCONFIRMED");
                _ = await DevelopmentOrderCompletion.RecordGuestAsync(claim, current.Ledger, outcome,
                    async observed =>
                    {
                        current = await AskAsync(command(observed));
                        return current.Ledger;
                    });
            }

            async Task<OrderFormatterInputs> InputsAsync(DevelopmentOrderOperationLedger accepted)
            {
                var model = RequireLedger(current.CalculatorModel);
                var guest = RequireLedger(current.CalculatorGuest);
                var observed = await DevelopmentSandboxRunner.RequireProtectedGuestAsync(owner, RequireGuest(guest), ct);
                return DevelopmentOrderFormatterVerifier.RequireInputs(owner, model, guest, accepted,
                    observed, await BaselineAsync(), CalculatorRecoveryPair(root));
            }

            async Task<OrderPairObservation> PairObservationAsync(OrderPairHistory history)
            {
                var observed = await DevelopmentSandboxRunner.RequireProtectedGuestsAsync(owner,
                    [RequireGuest(history.CalculatorGuest), RequireGuest(history.FormatterGuest)], ct);
                return new(observed[0], observed[1], await BaselineAsync(), CalculatorRecoveryPair(root),
                    DevelopmentOrderCalculatorVerifier.ReadStagedPair(Path.Combine(root, "formatter-stage")));
            }

            static void Phase(string phase, string sandbox, long elapsed, int exit) =>
                Console.WriteLine(FormattableString.Invariant(
                    $"ACCEPTANCE_GUEST_PHASE: {phase}; {sandbox}; elapsedMs={elapsed}; exit={exit}"));

            if (first)
            {
                OrderCheck.Require(current.Stage == 0 && current.Ledger.Sequence == 0
                    && current.Ledger.StartedUtcTicks == 0, "ACCEPTANCE_NEW_JOURNAL");
                var start = new OrderExecutionStart(1, claim.ArchiveHash, processStart);
                var expected = current.Ledger.Start(start, claim.ArchiveHash, archive.EffectiveLimits, profile.Policy);
                current = await AskAsync(new Advance(DevelopmentOrderOperationLedger.EncodeStart(start)));
                OrderCheck.Require(current.Stage == 1 && current.Ledger == expected, "ACCEPTANCE_START_ACK");
                RequireAction(OrderWorkflowAction.GenerateCalculator);

                var approval = await AuthorityAsync();
                long now = DateTime.UtcNow.Ticks;
                var assignment = profile.Assign(claim, approval, "S01", now);
                var request = current.Ledger.Propose(claim.JobId, "S01", "model", 90, now,
                    OrderOperationAgentBinding.FromAssignment(assignment));
                await AdmitAsync(request, new AdmitPair(request));
                // Recheck current authority after the durable admission,
                // using the original approval for historical assignment validation.
                await AuthorityAsync();
                var generated = await DevelopmentOrderModelDispatch.RunS01Async(owner, current.Ledger, request,
                    approval, assignment, PairAcceptanceProfile.Catalog(), (await BaselineAsync()).Calculator, apiKey, ct);
                await RecordModelAsync(generated, formatter: false);
                var model = current.Ledger;
                var candidate = model.LatestCandidate ?? throw new InvalidOperationException("Acceptance candidate missing.");
                _ = NewStage("calculator-stage", new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["PairCalculator.cs"] = owner.RequireOrderCandidate(candidate),
                    ["PairFormatter.cs"] = (await BaselineAsync()).Formatter
                });
                string source = NewStage("calculator-verification", DevelopmentOrderCalculatorVerifier.Prepare(owner, model));
                await AuthorityAsync();
                var guest = DevelopmentOrderCalculatorVerifier.ProposeGuest(owner, model, 420, DateTime.UtcNow.Ticks);
                await AdmitAsync(guest, new AdmitPair(guest),
                    () => DevelopmentOrderCalculatorVerifier.StoreGuestSnapshot(owner, model, guest, source));
                await AuthorityAsync();
                var outcome = await DevelopmentSandboxRunner.RunCalculatorReservedAsync(baselineRepository, source, results,
                    owner, model, current.Ledger, guest, Phase, ct);
                await RecordGuestAsync(outcome, observed => new Advance(DevelopmentOrderOperationLedger.EncodeGuestOutcome(observed)));
                RequireAction(OrderWorkflowAction.AcceptCalculator);
                await AuthorityAsync();
                current = await AskAsync(new AcceptCalculator());
                RequireAction(OrderWorkflowAction.GenerateFormatter);
                OrderCheck.Require(current.Stage == 8 && current.Ledger.Sequence == 2
                    && current.Ledger.Models == 1 && current.Ledger.Guests == 1
                    && current.Ledger.ReservedSeconds == 510 && current.Ledger.Pending is null
                    && current.Ledger.AcceptedS01 is not null, "ACCEPTANCE_S01_ACK");
                owner.ValidateOrderJournalFiles();
                host.Guard.RequireObserved();
                Console.WriteLine(FormattableString.Invariant(
                    $"ACCEPTANCE_S01_ACK: job={claim.JobId}; start={current.Ledger.StartedUtcTicks}; sequence=2; charged=510; candidate={current.Ledger.AcceptedS01!.Candidate.ArtifactHash}"));
                Console.Out.Flush();
                // Only the designated operator/supervisor terminates this
                // process after the durable acceptance marker. No S02 here.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return 1;
            }

            // Only the specifically approved interruption boundary is a
            // continuation point. Other states, including pending and
            // terminal, are not eligible for dispatch through this entry.
            OrderCheck.Require(current.Stage == 8 && current.Ledger.Pending is null
                && current.Ledger.BudgetStop is null && current.Ledger.SoftStop is null
                && current.Ledger.AcceptedS01 is not null && current.Ledger.Sequence == 2
                && current.Ledger.Models == 1 && current.Ledger.Guests == 1
                && current.Ledger.ReservedSeconds == 510 && current.Ledger.StartedUtcTicks < processStart,
                "ACCEPTANCE_RECOVERY_BOUNDARY");
            RequireAction(OrderWorkflowAction.GenerateFormatter);
            var accepted = current.Ledger;
            long recoveryReady = Stopwatch.GetTimestamp();
            _ = profile.RequireAssignment(claim, RequireLedger(current.CalculatorModel).LatestModelReservation
                ?? throw new InvalidOperationException("Acceptance S01 assignment missing."));
            var inputs = await InputsAsync(accepted);
            OrderCheck.Require(accepted.Remaining(archive.EffectiveLimits, DateTime.UtcNow.Ticks) > TimeSpan.Zero,
                "ACCEPTANCE_RECOVERY_WINDOW");
            Console.WriteLine(FormattableString.Invariant(
                $"ACCEPTANCE_RECOVERED: job={claim.JobId}; start={accepted.StartedUtcTicks}; sequence=2; charged=510; candidate={inputs.CalculatorAcceptance.Candidate.ArtifactHash}; evidenceMs={Stopwatch.GetElapsedTime(recoveryReady).TotalMilliseconds:F3}"));

            var formatterApproval = await AuthorityAsync();
            long formatterNow = DateTime.UtcNow.Ticks;
            var formatterAssignment = profile.Assign(claim, formatterApproval, "S02", formatterNow);
            var formatterRequest = accepted.Propose(claim.JobId, "S02", "model", 90, formatterNow,
                OrderOperationAgentBinding.FromAssignment(formatterAssignment));
            await AdmitAsync(formatterRequest, new AdmitFormatter(formatterRequest));
            await AuthorityAsync();
            var formatterResult = await DevelopmentOrderModelDispatch.RunS02Async(owner, current.Ledger, formatterRequest,
                formatterApproval, formatterAssignment, PairAcceptanceProfile.Catalog(), inputs, apiKey, ct);
            await RecordModelAsync(formatterResult, formatter: true);
            var formatterModel = current.Ledger;
            var formatterCandidate = formatterModel.LatestCandidate
                ?? throw new InvalidOperationException("Acceptance formatter candidate missing.");
            _ = NewStage("formatter-stage", new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["PairCalculator.cs"] = owner.RequireOrderCandidate(inputs.CalculatorAcceptance.Candidate),
                ["PairFormatter.cs"] = owner.RequireOrderCandidate(formatterCandidate)
            });
            string formatterSource = NewStage("formatter-verification",
                DevelopmentOrderFormatterVerifier.Prepare(owner, accepted, formatterModel, inputs));
            await AuthorityAsync();
            var formatterGuest = DevelopmentOrderFormatterVerifier.ProposeGuest(owner, accepted, formatterModel,
                inputs, 420, DateTime.UtcNow.Ticks);
            await AdmitAsync(formatterGuest, new AdmitPair(formatterGuest),
                () => DevelopmentOrderFormatterVerifier.StoreGuestSnapshot(owner, accepted, formatterModel,
                    inputs, formatterGuest, formatterSource));
            await AuthorityAsync();
            var formatterOutcome = await DevelopmentSandboxRunner.RunFormatterReservedAsync(baselineRepository,
                formatterSource, results, owner, accepted, formatterModel, inputs, current.Ledger, formatterGuest, Phase, ct);
            await RecordGuestAsync(formatterOutcome, observed => new RecordFormatterGuest(observed));
            RequireAction(OrderWorkflowAction.AcceptFormatter);
            await AuthorityAsync();
            current = await AskAsync(new AcceptFormatter());
            RequireAction(OrderWorkflowAction.VerifyIntegration);
            OrderCheck.Require(current.Stage == 13 && current.Ledger.AcceptedS01 == accepted.AcceptedS01
                && current.Ledger.AcceptedS02 is not null && current.Ledger.ReservedSeconds == 1020,
                "ACCEPTANCE_S02_ACK");

            var history = RequirePairHistory(current);
            var observations = await PairObservationAsync(history);
            string finalSource = NewStage("integration-verification",
                DevelopmentOrderIntegrationVerifier.Prepare(owner, history, observations));
            await AuthorityAsync();
            var finalRequest = DevelopmentOrderIntegrationVerifier.ProposeGuest(owner, history, observations,
                420, DateTime.UtcNow.Ticks);
            await AdmitAsync(finalRequest, new AdmitPair(finalRequest),
                () => DevelopmentOrderIntegrationVerifier.StoreGuestSnapshot(owner, history, observations, finalRequest, finalSource));
            await AuthorityAsync();
            var finalOutcome = await DevelopmentSandboxRunner.RunIntegrationReservedAsync(baselineRepository,
                finalSource, results, owner, history, observations, current.Ledger, finalRequest, Phase, ct);
            await RecordGuestAsync(finalOutcome, observed => new RecordIntegrationGuest(observed));
            RequireAction(OrderWorkflowAction.AcceptOrder);
            await AuthorityAsync();
            current = await AskAsync(new AcceptOrder());
            RequireAction(OrderWorkflowAction.InspectCompletedOrder);
            var terminal = current.Ledger.AcceptedOrder ?? throw new InvalidOperationException("Acceptance terminal missing.");
            var finalCompleted = RequireLedger(current.FinalCompletion);
            var finalObserved = await DevelopmentSandboxRunner.RequireProtectedGuestAsync(owner, RequireGuest(finalCompleted), ct);
            _ = DevelopmentOrderIntegrationVerifier.RequireCompletedGuest(owner, history,
                await PairObservationAsync(history), finalCompleted, finalObserved);
            _ = await BaselineAsync();
            OrderCheck.Require(current.Stage == 16 && current.Ledger == finalCompleted.AcceptOrder(claim, terminal)
                && current.Ledger.Sequence == 5 && current.Ledger.Models == 2 && current.Ledger.Guests == 3
                && current.Ledger.ReservedSeconds == 1440 && current.Ledger.StartedUtcTicks == accepted.StartedUtcTicks
                && current.Ledger.AcceptedS01 == accepted.AcceptedS01 && current.Ledger.Pending is null,
                "ACCEPTANCE_TERMINAL_ACK");
            host.Guard.RequireObserved();
            owner.ValidateOrderJournalFiles();
            Console.WriteLine(FormattableString.Invariant(
                $"ACCEPTANCE_TERMINAL: job={claim.JobId}; start={current.Ledger.StartedUtcTicks}; sequence=5; models=2; guests=3; charged=1440; calculator={accepted.AcceptedS01!.Candidate.ArtifactHash}; formatter={current.Ledger.AcceptedS02!.Candidate.ArtifactHash}; acceptance={terminal.PairAcceptanceHash}"));
            return 0;
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
