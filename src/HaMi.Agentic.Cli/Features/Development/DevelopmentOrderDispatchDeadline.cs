namespace HaMi.Agentic.Cli.Features.Development;

// Local cooperative deadline derived from a freshly acknowledged reservation.
// The journal remains authoritative; this object neither admits nor settles work.
// The host must not construct it for recovered pending effects.
internal sealed class DevelopmentOrderDispatchDeadline : IDisposable
{
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly TimeSpan allowance;
    private readonly CancellationTokenSource timer;
    private readonly CancellationTokenSource linked;

    private DevelopmentOrderDispatchDeadline(TimeProvider clock, long started,
        TimeSpan allowance, CancellationToken cancellationToken)
    {
        this.clock = clock;
        this.started = started;
        this.allowance = allowance;
        TimeSpan remaining = Remaining;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("ORDER_OPERATION_DISPATCH_EXPIRED");
        timer = new CancellationTokenSource(remaining, clock);
        linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancellationToken);
    }

    internal static DevelopmentOrderDispatchDeadline Start(DevelopmentOrderOperationLedger acknowledged,
        OrderOperationReservation reservation, CancellationToken cancellationToken,
        TimeProvider? clock = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        clock ??= TimeProvider.System;
        OrderCheck.Require(acknowledged.Pending == reservation, "ORDER_OPERATION_DISPATCH_ID");
        var limits = acknowledged.CurrentLimits ?? throw new InvalidOperationException("Order budget missing.");
        // Capture the monotonic origin before sampling UTC so time spent
        // evaluating admission cannot extend the permitted local window.
        long started = clock.GetTimestamp();
        TimeSpan allowance = acknowledged.DispatchAllowance(limits, reservation.OperationId,
            clock.GetUtcNow().UtcDateTime.Ticks);
        return new(clock, started, allowance, cancellationToken);
    }

    internal CancellationToken Token => linked.Token;

    internal TimeSpan Remaining
    {
        get
        {
            TimeSpan elapsed = clock.GetElapsedTime(started);
            OrderCheck.Require(elapsed >= TimeSpan.Zero, "ORDER_EXECUTION_CLOCK");
            return elapsed >= allowance ? TimeSpan.Zero : allowance - elapsed;
        }
    }

    // Call before every regular phase. A fresh phase cap can only shorten
    // the original operation window, never restart it.
    internal TimeSpan Limit(TimeSpan phaseLimit)
    {
        OrderCheck.Require(phaseLimit > TimeSpan.Zero, "ORDER_PHASE_LIMIT");
        TimeSpan remaining = Remaining;
        if (remaining <= TimeSpan.Zero)
        {
            timer.Cancel();
            throw new TimeoutException("ORDER_OPERATION_DISPATCH_EXPIRED");
        }
        Token.ThrowIfCancellationRequested();
        return remaining < phaseLimit ? remaining : phaseLimit;
    }

    public void Dispose()
    {
        linked.Dispose();
        timer.Dispose();
    }
}
