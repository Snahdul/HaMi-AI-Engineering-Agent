namespace HaMi.Agentic.Cli.Features.Development;

internal static class DevelopmentOrderOperationLedgerTests
{
    internal static void SelfTest(DevelopmentOrderApproval approval)
    {
        var limits = approval.EffectiveLimits;
        var policy = approval.BudgetPolicy;
        string job = new('a', 32);
        string hash = new('A', 64);
        long now = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc).Ticks;
        var agent = new OrderAgentIdentity("fixture-agent", "1", hash, "offline", "synthetic", hash);
        OrderAssignment Assignment(string step) => new(approval.Binding, Guid.ParseExact(job, "N"), step, 1,
            agent, "Synthetic reviewed assignment.", approval.ObservedAt);
        OrderOperationReservation Propose(DevelopmentOrderOperationLedger state, string step, string kind, int seconds, long observed) =>
            state.Propose(job, step, kind, seconds, observed,
                kind == "model" ? OrderOperationAgentBinding.FromAssignment(Assignment(step)) : null,
                kind == "guest" ? hash : null);

        var start = new OrderExecutionStart(1, hash, now);
        var initial = new DevelopmentOrderOperationLedger().Start(
            DevelopmentOrderOperationLedger.DecodeStart(DevelopmentOrderOperationLedger.EncodeStart(start)), hash, limits, policy);
        var reserve = Propose(initial, "S01", "model", 30, now);
        Reject(() => new DevelopmentOrderOperationLedger().Reserve(job, limits, reserve));
        Reject(() => initial.Start(start, hash, limits, policy));
        Reject(() => initial.Reserve(job, limits, reserve with { ObservedUtcTicks = now - 1 }));
        string evt = DevelopmentOrderOperationLedger.EncodeReservation(reserve);
        var reserved = initial.Reserve(job, limits, DevelopmentOrderOperationLedger.DecodeReservation(evt));
        if (reserved.Sequence != 1 || reserved.Models != 1 || reserved.Guests != 0
            || reserved.ReservedSeconds != 30 || reserved.Pending != reserve)
            throw new InvalidOperationException("Reservation was not charged.");
        Reject(() => reserved.Reserve(job, limits, Propose(reserved, "S02", "guest", 1, now)));
        Reject(() => initial.Reserve(job, limits, reserve with { Version = 1 }));
        Reject(() => initial.Reserve(job, limits, reserve with { OperationId = new string('b', 32) + ":1" }));
        Reject(() => initial.Reserve(job, limits, reserve with { ReservedSeconds = 0 }));
        Reject(() => initial.Reserve(job, limits, reserve with { ReservedSeconds = limits.Seconds + 1 }));
        Reject(() => initial.Reserve(job, limits, reserve with { Kind = "unknown" }));
        Reject(() => initial.Reserve(job, limits, reserve with { Step = "FINAL" }));
        Reject(() => reserved.Complete(new(2, job + ":2", hash, "succeeded", now)));
        Reject(() => reserved.Complete(new(2, reserve.OperationId, "not-a-hash", "succeeded", now)));
        Reject(() => DevelopmentOrderOperationLedger.DecodeReservation(evt + " "));
        var completion = new OrderOperationCompletion(2, reserve.OperationId, hash, "succeeded", now);
        string complete = DevelopmentOrderOperationLedger.EncodeCompletion(completion);
        var settled = reserved.Complete(DevelopmentOrderOperationLedger.DecodeCompletion(complete));
        if (settled.Pending is not null || settled.Models != 1 || settled.ReservedSeconds != 30)
            throw new InvalidOperationException("Completion refunded a reservation.");
        Reject(() => settled.Complete(completion));
        Reject(() => settled.Reserve(job, limits, reserve));
        Reject(() => reserved.Complete(completion with { Outcome = "unknown" }));
        Reject(() => reserved.Complete(completion with { ObservedUtcTicks = now - 1 }));
        long afterTen = now + 10 * TimeSpan.TicksPerSecond;
        if (reserved.DispatchAllowance(limits, reserve.OperationId, afterTen) != TimeSpan.FromSeconds(20))
            throw new InvalidOperationException("Acknowledgement time not charged.");
        Reject(() => reserved.DispatchAllowance(limits, job + ":2", now));
        Reject(() => reserved.DispatchAllowance(limits, reserve.OperationId, now + 30 * TimeSpan.TicksPerSecond));
        var failed = reserved.Complete(completion with { Outcome = "failed", ObservedUtcTicks = afterTen });
        var retry = Propose(failed, "S01", "model", 30, afterTen);
        if (failed.LastOutcome != "failed" || failed.LastEvidenceHash != hash || retry.OperationId == reserve.OperationId)
            throw new InvalidOperationException("Failure/retry identity differs.");
        var retried = failed.Reserve(job, limits, retry);
        if (retried.Models != 2 || retried.ReservedSeconds != 60)
            throw new InvalidOperationException("Retry refunded prior budget.");
        long expired = now + (long)limits.Seconds * TimeSpan.TicksPerSecond;
        Reject(() => settled.Reserve(job, limits, Propose(settled, "S02", "model", 1, expired)));
        Reject(() => failed.Reserve(job, limits, Propose(failed, "S02", "model", 1, now)));
        if (failed.Remaining(limits, afterTen) != TimeSpan.FromSeconds(limits.Seconds - 10))
            throw new InvalidOperationException("Recovery window reset.");
        var late = reserved.Complete(completion with { ObservedUtcTicks = expired });
        if (late.Remaining(limits, expired) != TimeSpan.Zero)
            throw new InvalidOperationException("Late completion extended deadline.");
        Reject(() => late.Reserve(job, limits, Propose(late, "S02", "model", 1, expired)));

        DevelopmentOrderOperationLedger Spend(DevelopmentOrderOperationLedger state, string step, string kind)
        {
            var request = Propose(state, step, kind, step == "FINAL" ? policy.FinalVerificationSeconds : 1, now);
            var charged = state.Reserve(job, limits, request);
            return kind == "guest"
                ? charged.CompleteGuest(new(1, hash, request, DevelopmentOrderGuestReconciliation.RunId(request), hash, now))
                : charged.Complete(new(2, request.OperationId, hash, "succeeded", now));
        }

        var modelLimit = settled;
        while (modelLimit.S01Models < limits.S01Models)
            modelLimit = Spend(modelLimit, "S01", "model");
        Reject(() => Spend(modelLimit, "S01", "model"));
        while (modelLimit.S02Models < limits.S02Models)
            modelLimit = Spend(modelLimit, "S02", "model");
        Reject(() => Spend(modelLimit, "S02", "model"));

        var guestLimit = initial;
        while (guestLimit.S01Guests < limits.S01Guests)
            guestLimit = Spend(guestLimit, "S01", "guest");
        while (guestLimit.S02Guests < limits.S02Guests)
            guestLimit = Spend(guestLimit, "S02", "guest");
        Reject(() => Spend(guestLimit, "S02", "guest"));
        while (guestLimit.FinalGuests < limits.FinalGuests)
            guestLimit = Spend(guestLimit, "FINAL", "guest");
        Reject(() => Spend(guestLimit, "FINAL", "guest"));
        if (guestLimit.Guests != limits.Guests)
            throw new InvalidOperationException("Final allowance changed.");
        var time = initial.Reserve(job, limits, reserve with { ReservedSeconds = limits.Seconds - policy.FinalVerificationSeconds })
            .Complete(completion);
        Reject(() => Spend(time, "S01", "model"));
        Reject(() => initial.Reserve(job, limits with { Models = 0 }, reserve));
        Reject(() => initial.Reserve(job, limits with { Guests = limits.FinalGuests },
            Propose(initial, "S01", "guest", 1, now)));
        // Re-fold the same events without effects.
        var replay = initial.Reserve(job, limits, DevelopmentOrderOperationLedger.DecodeReservation(evt))
            .Complete(DevelopmentOrderOperationLedger.DecodeCompletion(complete));
        if (replay != settled)
            throw new InvalidOperationException("Operation budget replay differs.");
        Console.WriteLine("PASS: versioned operation reservation, stable identity, monotone budgets and unknown-effect block.");
        Console.WriteLine("PASS: persistent execution window, known failure retry, expiry and clock rollback rejection.");

        var small = limits with { Seconds = 120 };
        var smallStart = new DevelopmentOrderOperationLedger().Start(start, hash, small, policy);
        var first = smallStart.Reserve(job, small, reserve).Complete(completion);
        var blockedRequest = Propose(first, "S02", "model", 100, now);
        var stop = new OrderBudgetExhausted(1, blockedRequest, "time");
        if (first.BudgetDenial(job, small, blockedRequest) != "time")
            throw new InvalidOperationException("Closing time reserve not protected.");
        Reject(() => first.Reserve(job, small, blockedRequest));
        Reject(() => first.StopForBudget(job, small, stop with { Reason = "count" }));
        var stopped = first.StopForBudget(job, small,
            DevelopmentOrderOperationLedger.DecodeBudgetStop(DevelopmentOrderOperationLedger.EncodeBudgetStop(stop)));
        Reject(() => stopped.Reserve(job, small, Propose(stopped, "S02", "model", 1, now)));
        Reject(() => stopped.Reserve(job, limits, blockedRequest));
        Reject(() => smallStart.Reserve(job, small, reserve with { Step = "FINAL", Kind = "guest", ReservedSeconds = 1, Assignment = null, SnapshotHash = hash }));
        var extension = new OrderBudgetExtensionApproved(1, job, hash, 0, limits, new string('B', 64), afterTen);
        Reject(() => first.Extend(job, policy, extension));
        Reject(() => reserved.Extend(job, policy, extension));
        Reject(() => stopped.Extend(job, policy, extension with { JobId = new string('b', 32) }));
        Reject(() => stopped.Extend(job, policy, extension with { ArchiveHash = new string('C', 64) }));
        Reject(() => stopped.Extend(job, policy, extension with { PreviousRevision = 1 }));
        Reject(() => stopped.Extend(job, policy, extension with { Limits = limits with { Seconds = limits.Seconds + 1 } }));
        Reject(() => stopped.Extend(job, policy, extension with { Limits = small }));
        var stillStopped = stopped.Extend(job, policy, extension with { Limits = small with { Seconds = 130 } });
        if (stillStopped.BudgetStop is null || stillStopped.BudgetRevision != 1)
            throw new InvalidOperationException("Insufficient extension resumed work.");
        Reject(() => stillStopped.Extend(job, policy, extension with { PreviousRevision = 1 }));
        var resumed = stopped.Extend(job, policy,
            DevelopmentOrderOperationLedger.DecodeBudgetExtension(DevelopmentOrderOperationLedger.EncodeBudgetExtension(extension)));
        if (resumed.BudgetStop is not null || resumed.BudgetRevision != 1 || resumed.CurrentLimits != limits
            || resumed.StartedUtcTicks != now || resumed.Sequence != 1 || resumed.Models != 1
            || resumed.ReservedSeconds != 30 || resumed.LastEvidenceHash != hash)
            throw new InvalidOperationException("Budget extension reset prior work.");
        Reject(() => resumed.Extend(job, policy, extension));
        var next = Propose(resumed, "S02", "model", 100, afterTen);
        var resumedReservation = resumed.Reserve(job, limits, next);
        if (resumedReservation.ReservedSeconds != 130 || resumedReservation.Sequence != 2)
            throw new InvalidOperationException("Continuation did not preserve charge.");
        var tooLate = stopped.Extend(job, policy, extension with { ObservedUtcTicks = expired });
        if (tooLate.BudgetStop is null)
            throw new InvalidOperationException("Expired extension created new time window.");
        Console.WriteLine("PASS: hard budget stop, final time reserve and cumulative approved-extension reducer.");

        var claim = DevelopmentOrderClaim.Create(job, approval.Binding, hash);
        var candidate = new OrderCandidateCheckpoint(1, hash, reserve.OperationId, "S01",
            DevelopmentAgentContract.PairFirst, claim.Binding.CalculatorBaselineHash,
            claim.Binding.CalculatorBaselineHash, 1, hash, "Partial", now);
        var candidateEvent = DevelopmentOrderOperationLedger.EncodeCandidate(candidate);
        var checkpointed = reserved.Checkpoint(claim, DevelopmentOrderOperationLedger.DecodeCandidate(candidateEvent));
        if (checkpointed.LatestCandidate != candidate || checkpointed.Pending != reserve
            || checkpointed.Models != 1 || checkpointed.ReservedSeconds != 30
            || checkpointed.LastOutcome is not null)
            throw new InvalidOperationException("Candidate checkpoint settled or refunded operation.");
        Reject(() => checkpointed.Checkpoint(claim, candidate));
        Reject(() => initial.Checkpoint(claim, candidate));
        Reject(() => reserved.Checkpoint(claim, candidate with { Quality = "Accepted" }));
        Reject(() => reserved.Checkpoint(claim, candidate with { Quality = "Validated" }));
        Reject(() => reserved.Checkpoint(claim, candidate with { SourcePath = "../Other.cs" }));
        Reject(() => reserved.Checkpoint(claim, candidate with { InputHash = new string('B', 64) }));
        Reject(() => reserved.Checkpoint(claim, candidate with { OperationId = job + ":2" }));
        Reject(() => reserved.Checkpoint(claim, candidate with { OperationId = job + ":01" }));
        Reject(() => reserved.Checkpoint(claim, candidate with { ObservedUtcTicks = now - 1 }));
        Reject(() => checkpointed.Reserve(job, limits, Propose(checkpointed, "S01", "model", 1, now)));
        Reject(() => DevelopmentOrderOperationLedger.DecodeCandidate(candidateEvent + " "));
        if (checkpointed.Complete(completion with { Outcome = "failed" }).LatestCandidate != candidate)
            throw new InvalidOperationException("Known failure discarded candidate.");
        Console.WriteLine("PASS: source candidate checkpoint is immutable, unaccepted and does not settle pending operation.");

        // Checkpoint provenance is independent of the claimed quality.
        Reject(() => reserved.Checkpoint(claim, candidate with { ArchiveHash = new string('B', 64) }));
        var foreignClaim = DevelopmentOrderClaim.Create(new string('b', 32), approval.Binding, hash);
        Reject(() => reserved.Checkpoint(foreignClaim, candidate));
        Reject(() => reserved.Checkpoint(claim, candidate with { BaselineHash = new string('B', 64) }));
        Reject(() => reserved.Checkpoint(claim, candidate with { ByteCount = 0 }));
        Reject(() => reserved.Checkpoint(claim, candidate with { ByteCount = OrderCandidateCheckpoint.MaxBytes + 1 }));

        byte[] source = [0x61, 0x62, 0x63];
        var actualCandidate = candidate with
        {
            ByteCount = source.Length,
            ArtifactHash = DevelopmentJobState.Hash(source),
            Quality = "Generated"
        };
        actualCandidate.ValidateBytes(source);
        Reject(() => actualCandidate.ValidateBytes([0x61, 0x62, 0x64]));
        Reject(() => actualCandidate.ValidateBytes([0x61, 0x62]));
        var generated = reserved.Checkpoint(claim, actualCandidate);
        if (generated.Pending != reserve || generated.LastOutcome is not null)
            throw new InvalidOperationException("Generated candidate became a completed operation.");

        // Known failure permits a new charged attempt, but its old checkpoint
        // cannot be relabeled as evidence for that new operation.
        var failedCandidate = generated.Complete(completion with { Outcome = "failed", ObservedUtcTicks = afterTen });
        var retryCandidate = failedCandidate.Reserve(job, limits, retry);
        Reject(() => retryCandidate.Checkpoint(claim, actualCandidate with { ObservedUtcTicks = afterTen }));
        var secondCandidate = actualCandidate with { OperationId = retry.OperationId, ObservedUtcTicks = afterTen };
        var secondCheckpoint = retryCandidate.Checkpoint(claim, secondCandidate);
        if (secondCheckpoint.Pending != retry || secondCheckpoint.LatestCandidate != secondCandidate
            || secondCheckpoint.Models != 2 || secondCheckpoint.ReservedSeconds != 60)
            throw new InvalidOperationException("Retry checkpoint changed operation identity or charge.");
        Console.WriteLine("PASS: checkpoint provenance, byte integrity and charged retry identity.");

        var observation = new OrderOperationEvidence(2, hash, reserve, OrderOperationEvidence.FixtureObserver,
            "succeeded", claim.Binding.CalculatorBaselineHash, claim.Binding.CalculatorBaselineHash,
            actualCandidate.ArtifactHash, afterTen);
        var observedCompletion = observation.Completion();
        observation.ValidateCompletion(claim, reserve, observedCompletion);
        Reject(() => observation.ValidateCompletion(foreignClaim, reserve, observedCompletion));
        Reject(() => observation.ValidateCompletion(claim, retry, observedCompletion));
        Reject(() => observation.ValidateCompletion(claim, reserve with { ReservedSeconds = 31 }, observedCompletion));
        Reject(() => observation.ValidateCompletion(claim, reserve, observedCompletion with { EvidenceHash = actualCandidate.ArtifactHash }));
        Reject(() => observation.ValidateCompletion(claim, reserve, observedCompletion with { Outcome = "failed" }));
        Reject(() => observation.ValidateCompletion(claim, reserve, observedCompletion with { ObservedUtcTicks = afterTen + 1 }));
        Reject(() => (observation with { Observer = "model-assertion" }).ValidateBinding(claim));
        Reject(() => (observation with { InputHash = new string('C', 64) }).ValidateBinding(claim));
        Reject(() => (observation with { Outcome = "unknown" }).ValidateBinding(claim));
        Reject(() => (observation with { Outcome = "failed" }).ValidateBinding(claim));
        var knownFailure = observation with { Outcome = "failed", OutputHash = null };
        knownFailure.ValidateCompletion(claim, reserve, knownFailure.Completion());
        if (reserved.Complete(knownFailure.Completion()).ReservedSeconds != 30)
            throw new InvalidOperationException("Known failure evidence refunded charge.");
        Console.WriteLine("PASS: exact operation evidence binds reservation, outcome, observer and completion digest.");

        var assignment = Assignment("S01");
        var agentBinding = reserve.Assignment ?? throw new InvalidOperationException("Model assignment missing.");
        agentBinding.RequireAssignment(assignment);
        Reject(() => initial.Reserve(job, limits, reserve with { Assignment = null }));
        Reject(() => initial.Reserve(job, limits, reserve with { Version = 2 }));
        Reject(() => initial.Reserve(job, limits, reserve with { Assignment = agentBinding with { Revision = 0 } }));
        Reject(() => initial.Reserve(job, limits, reserve with { Assignment = agentBinding with { AssignmentHash = "invalid" } }));
        Reject(() => agentBinding.RequireAssignment(assignment with { Revision = 2 }));
        Reject(() => agentBinding.RequireAssignment(Assignment("S02")));
        Reject(() => agentBinding.RequireAssignment(assignment with { JobId = Guid.ParseExact(new string('b', 32), "N") }));
        Reject(() => agentBinding.RequireAssignment(assignment with { Agent = agent with { Version = "2" } }));
        Reject(() => agentBinding.RequireAssignment(assignment with { Agent = agent with { ConfigurationHash = new string('C', 64) } }));
        // A self-consistent replacement receipt with a different identity or
        // revision must still fail against the original journal reservation.
        foreach (var changedAssignment in new[]
        {
            assignment with { Revision = 2 },
            assignment with { Agent = agent with { Id = "foreign-agent" } },
            assignment with { Agent = agent with { Version = "2" } },
            assignment with { Agent = agent with { Model = "other-model" } },
            assignment with { Agent = agent with { Provider = "other-provider" } },
            assignment with { Agent = agent with { ProfileHash = new string('B', 64) } },
            assignment with { Agent = agent with { ConfigurationHash = new string('C', 64) } },
            Assignment("S02")
        })
        {
            var substituted = observation with
            {
                Reservation = reserve with { Assignment = OrderOperationAgentBinding.FromAssignment(changedAssignment) }
            };
            Reject(() => substituted.ValidateCompletion(claim, reserve, substituted.Completion()));
        }
        Reject(() => observation.ValidateCompletion(claim, retry, observation.Completion()));
        Reject(() => DevelopmentOrderOperationLedger.DecodeReservation(evt.Replace("reserve-v4:", "reserve-v3:", StringComparison.Ordinal)));
        Console.WriteLine("PASS: journaled model assignment binds agent version, revision, configuration and attempt; substituted receipts rejected.");

        var guestRequest = Propose(initial, "S01", "guest", 30, now);
        Reject(() => initial.Reserve(job, limits, guestRequest with { SnapshotHash = null }));
        Reject(() => initial.Reserve(job, limits, guestRequest with { SnapshotHash = "invalid" }));
        Reject(() => initial.Reserve(job, limits, reserve with { SnapshotHash = hash }));
        var guestPending = initial.Reserve(job, limits, guestRequest);
        string runId = DevelopmentOrderGuestReconciliation.RunId(guestRequest);
        if (runId.Length != 32
            || runId != DevelopmentOrderGuestReconciliation.RunId(DevelopmentOrderOperationLedger.DecodeReservation(
                DevelopmentOrderOperationLedger.EncodeReservation(guestRequest)))
            || runId == DevelopmentOrderGuestReconciliation.RunId(Propose(guestPending, "S01", "guest", 30, now)))
            throw new InvalidOperationException("Guest identity was not stable and operation-specific.");
        Reject(() => guestPending.Complete(new(2, guestRequest.OperationId, hash, "succeeded", now)));
        Reject(() => guestPending.Reserve(job, limits, Propose(guestPending, "S01", "guest", 30, now)));
        Reject(() => DevelopmentOrderGuestReconciliation.RunId(reserve));
        string absent = "error: sandbox 'hami-code-" + runId[..12] + "' not found";
        var report = new DevelopmentSandboxRunner.SandboxRunReport(3, runId, "Demo.csproj", "run",
            0, 0, 0, false, 1, 1, 1, 1, 1, 1, 6, true, null, hash);
        var legacyReport = report with { SchemaVersion = 1, SnapshotHash = null };
        if (System.Text.Json.JsonSerializer.Serialize(legacyReport, new System.Text.Json.JsonSerializerOptions(
            System.Text.Json.JsonSerializerDefaults.Web)).Contains("snapshotHash", StringComparison.Ordinal))
            throw new InvalidOperationException("Legacy report shape changed.");
        byte[] Report(DevelopmentSandboxRunner.SandboxRunReport value) =>
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true });
        OrderGuestOutcome? Observe(DevelopmentSandboxRunner.SandboxRunReport value) =>
            DevelopmentOrderGuestReconciliation.Observe(hash, guestRequest, Report(value), 1, "", absent, afterTen);
        if (DevelopmentOrderGuestReconciliation.Observe(hash, guestRequest, null, 1, "", absent, afterTen) is not null
            || DevelopmentOrderGuestReconciliation.Observe(hash, guestRequest, Report(report), 0, absent, "", afterTen) is not null
            || DevelopmentOrderGuestReconciliation.Observe(hash, guestRequest, Report(report), 1, "", "backend unavailable", afterTen) is not null
            || DevelopmentOrderGuestReconciliation.Observe(hash, guestRequest, [.. Report(report), 0x20], 1, "", absent, afterTen) is not null)
            throw new InvalidOperationException("Missing/ambiguous guest evidence was accepted.");
        foreach (var invalidReport in new[]
        {
            legacyReport,
            report with { SchemaVersion = 2 },
            report with { SnapshotHash = null },
            report with { SnapshotHash = new string('B', 64) },
            report with { RunId = new string('f', 32) },
            report with { Project = "Other.csproj" },
            report with { Mode = "build" },
            report with { TimedOut = true },
            report with { SandboxRemovalSucceeded = false },
            report with { ExecutionExitCode = 1 },
            report with { BuildExitCode = 1, ExecutionExitCode = -1 },
            report with { RestoreExitCode = 1, BuildExitCode = -1, ExecutionExitCode = -1 },
            report with { Error = "uncertain host failure" }
        })
            if (Observe(invalidReport) is not null)
                throw new InvalidOperationException("Unconfirmed guest outcome accepted.");

        var guestOutcome = Observe(report) ?? throw new InvalidOperationException("Known guest outcome missing.");
        var recoveredOutcome = DevelopmentOrderOperationLedger.DecodeGuestOutcome(
            DevelopmentOrderOperationLedger.EncodeGuestOutcome(guestOutcome));
        DevelopmentOrderGuestReconciliation.RequireObserved(recoveredOutcome, Observe(report));
        Reject(() => DevelopmentOrderGuestReconciliation.RequireObserved(recoveredOutcome, null));
        Reject(() => DevelopmentOrderGuestReconciliation.RequireObserved(recoveredOutcome, Observe(report with { TotalMilliseconds = 7 })));
        Reject(() => guestPending.CompleteGuest(guestOutcome with { RunId = new string('f', 32) }));
        Reject(() => guestPending.CompleteGuest(guestOutcome with { ArchiveHash = new string('C', 64) }));
        Reject(() => reserved.CompleteGuest(guestOutcome));
        var guestSettled = guestPending.CompleteGuest(recoveredOutcome);
        if (guestSettled.Pending is not null || guestSettled.Guests != 1 || guestSettled.ReservedSeconds != 30
            || guestSettled.LatestGuestOutcome != guestOutcome || guestSettled.LastOutcome != "succeeded")
            throw new InvalidOperationException("Reconciled guest refunded budget or lost identity.");
        Reject(() => guestSettled.CompleteGuest(recoveredOutcome));
        Console.WriteLine("PASS: pre-bound guest run identity, exact report and confirmed absence required; unknown guest remains blocked.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Invalid operation budget transition accepted.");
    }
}
