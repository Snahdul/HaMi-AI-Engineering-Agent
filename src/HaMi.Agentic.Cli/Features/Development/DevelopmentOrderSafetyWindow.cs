namespace HaMi.Agentic.Cli.Features.Development;

// One local safety-only monotonic window per reserved run. Starts on the
// first safety action (process termination or normal cleanup), never on
// each phase. Does not extend or authorize the business operation.
internal sealed class DevelopmentOrderSafetyWindow(TimeSpan allowance, TimeProvider? timeProvider = null) : IDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long started;
    private CancellationTokenSource? timer;

    internal void Start()
    {
        OrderCheck.Require(allowance > TimeSpan.Zero, "ORDER_SAFETY_LIMIT");
        if (timer is null)
        {
            started = clock.GetTimestamp();
            timer = new CancellationTokenSource(allowance, clock);
        }
    }

    internal TimeSpan Limit(TimeSpan phaseLimit)
    {
        OrderCheck.Require(phaseLimit > TimeSpan.Zero, "ORDER_SAFETY_LIMIT");
        Start();
        CancellationTokenSource current = timer!;
        TimeSpan elapsed = clock.GetElapsedTime(started);
        OrderCheck.Require(elapsed >= TimeSpan.Zero, "ORDER_EXECUTION_CLOCK");
        if (elapsed >= allowance)
        {
            current.Cancel();
            throw new TimeoutException("ORDER_SAFETY_RESERVE_EXHAUSTED");
        }

        current.Token.ThrowIfCancellationRequested();
        TimeSpan remaining = allowance - elapsed;
        return remaining < phaseLimit ? remaining : phaseLimit;
    }

    internal CancellationToken Token
    {
        get
        {
            _ = Limit(TimeSpan.MaxValue);
            return timer!.Token;
        }
    }

    internal void Check() => _ = Limit(TimeSpan.MaxValue);

    public void Dispose() => timer?.Dispose();
}
