namespace HaMi.Agentic.Cli.Features.Development;

internal static class DevelopmentOrderOperationLedgerTests
{
    internal static void SelfTest(DevelopmentOrderApproval approval)
    {
        var limits = OrderOperationLimits.From(approval.Order);
        string job = new('a', 32);
        string hash = new('A', 64);
        long now = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc).Ticks;
        var start = new OrderExecutionStart(1, hash, now);
        var initial = new DevelopmentOrderOperationLedger().Start(
            DevelopmentOrderOperationLedger.DecodeStart(DevelopmentOrderOperationLedger.EncodeStart(start)), hash, limits);
        var reserve = initial.Propose(job, "S01", "model", 30, now);
        Reject(() => new DevelopmentOrderOperationLedger().Reserve(job, limits, reserve));
        Reject(() => initial.Start(start, hash, limits));
        Reject(() => initial.Reserve(job, limits, reserve with { ObservedUtcTicks = now - 1 }));
        string evt = DevelopmentOrderOperationLedger.EncodeReservation(reserve);
        var reserved = initial.Reserve(job, limits, DevelopmentOrderOperationLedger.DecodeReservation(evt));
        if (reserved.Sequence != 1 || reserved.Models != 1 || reserved.Guests != 0
            || reserved.ReservedSeconds != 30 || reserved.Pending != reserve)
            throw new InvalidOperationException("Reservation was not charged.");
        Reject(() => reserved.Reserve(job, limits, reserved.Propose(job, "S02", "guest", 1, now)));
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
        var retry = failed.Propose(job, "S01", "model", 30, afterTen);
        if (failed.LastOutcome != "failed" || failed.LastEvidenceHash != hash || retry.OperationId == reserve.OperationId)
            throw new InvalidOperationException("Failure/retry identity differs.");
        var retried = failed.Reserve(job, limits, retry);
        if (retried.Models != 2 || retried.ReservedSeconds != 60)
            throw new InvalidOperationException("Retry refunded prior budget.");
        long expired = now + (long)limits.Seconds * TimeSpan.TicksPerSecond;
        Reject(() => settled.Reserve(job, limits, settled.Propose(job, "S02", "model", 1, expired)));
        Reject(() => failed.Reserve(job, limits, failed.Propose(job, "S02", "model", 1, now)));
        if (failed.Remaining(limits, afterTen) != TimeSpan.FromSeconds(limits.Seconds - 10))
            throw new InvalidOperationException("Recovery window reset.");
        var late = reserved.Complete(completion with { ObservedUtcTicks = expired });
        if (late.Remaining(limits, expired) != TimeSpan.Zero)
            throw new InvalidOperationException("Late completion extended deadline.");
        Reject(() => late.Reserve(job, limits, late.Propose(job, "S02", "model", 1, expired)));

        DevelopmentOrderOperationLedger Spend(DevelopmentOrderOperationLedger state, string step, string kind)
        {
            var request = state.Propose(job, step, kind, 1, now);
            return state.Reserve(job, limits, request).Complete(new(2, request.OperationId, hash, "succeeded", now));
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
        var time = initial.Reserve(job, limits, reserve with { ReservedSeconds = limits.Seconds })
            .Complete(completion);
        Reject(() => Spend(time, "S01", "model"));
        Reject(() => initial.Reserve(job, limits with { Models = 0 }, reserve));
        Reject(() => initial.Reserve(job, limits with { Guests = limits.FinalGuests },
            initial.Propose(job, "S01", "guest", 1, now)));
        // Re-fold the same events without effects.
        var replay = initial.Reserve(job, limits, DevelopmentOrderOperationLedger.DecodeReservation(evt))
            .Complete(DevelopmentOrderOperationLedger.DecodeCompletion(complete));
        if (replay != settled)
            throw new InvalidOperationException("Operation budget replay differs.");
        Console.WriteLine("PASS: versioned operation reservation, stable identity, monotone budgets and unknown-effect block.");
        Console.WriteLine("PASS: persistent execution window, known failure retry, expiry and clock rollback rejection.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Invalid operation budget transition accepted.");
    }
}
