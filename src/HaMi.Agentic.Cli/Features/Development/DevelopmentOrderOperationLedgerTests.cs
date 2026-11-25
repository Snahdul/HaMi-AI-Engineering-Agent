namespace HaMi.Agentic.Cli.Features.Development;

internal static class DevelopmentOrderOperationLedgerTests
{
    internal static void SelfTest(DevelopmentOrderApproval approval)
    {
        var limits = OrderOperationLimits.From(approval.Order);
        string job = new('a', 32);
        string hash = new('A', 64);
        var initial = new DevelopmentOrderOperationLedger();
        var reserve = initial.Propose(job, "S01", "model", 30);
        string evt = DevelopmentOrderOperationLedger.EncodeReservation(reserve);
        var reserved = initial.Reserve(job, limits, DevelopmentOrderOperationLedger.DecodeReservation(evt));
        if (reserved.Sequence != 1 || reserved.Models != 1 || reserved.Guests != 0
            || reserved.ReservedSeconds != 30 || reserved.Pending != reserve)
            throw new InvalidOperationException("Reservation was not charged.");
        Reject(() => reserved.Reserve(job, limits, reserved.Propose(job, "S02", "guest", 1)));
        Reject(() => initial.Reserve(job, limits, reserve with { Version = 2 }));
        Reject(() => initial.Reserve(job, limits, reserve with { OperationId = new string('b', 32) + ":1" }));
        Reject(() => initial.Reserve(job, limits, reserve with { ReservedSeconds = 0 }));
        Reject(() => initial.Reserve(job, limits, reserve with { ReservedSeconds = limits.Seconds + 1 }));
        Reject(() => initial.Reserve(job, limits, reserve with { Kind = "unknown" }));
        Reject(() => initial.Reserve(job, limits, reserve with { Step = "FINAL" }));
        Reject(() => reserved.Complete(new(1, job + ":2", hash)));
        Reject(() => reserved.Complete(new(1, reserve.OperationId, "not-a-hash")));
        Reject(() => DevelopmentOrderOperationLedger.DecodeReservation(evt + " "));
        var completion = new OrderOperationCompletion(1, reserve.OperationId, hash);
        string complete = DevelopmentOrderOperationLedger.EncodeCompletion(completion);
        var settled = reserved.Complete(DevelopmentOrderOperationLedger.DecodeCompletion(complete));
        if (settled.Pending is not null || settled.Models != 1 || settled.ReservedSeconds != 30)
            throw new InvalidOperationException("Completion refunded a reservation.");
        Reject(() => settled.Complete(completion));
        Reject(() => settled.Reserve(job, limits, reserve));

        DevelopmentOrderOperationLedger Spend(DevelopmentOrderOperationLedger state, string step, string kind)
        {
            var request = state.Propose(job, step, kind, 1);
            return state.Reserve(job, limits, request).Complete(new(1, request.OperationId, hash));
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
            initial.Propose(job, "S01", "guest", 1)));
        // Re-fold the same events without effects.
        var replay = initial.Reserve(job, limits, DevelopmentOrderOperationLedger.DecodeReservation(evt))
            .Complete(DevelopmentOrderOperationLedger.DecodeCompletion(complete));
        if (replay != settled)
            throw new InvalidOperationException("Operation budget replay differs.");
        Console.WriteLine("PASS: versioned operation reservation, stable identity, monotone budgets and unknown-effect block.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Invalid operation budget transition accepted.");
    }
}
