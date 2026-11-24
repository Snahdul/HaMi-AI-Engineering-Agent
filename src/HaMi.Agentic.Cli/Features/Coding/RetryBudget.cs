namespace HaMi.Agentic.Cli.Features.Coding;

/// <summary>
/// Domain rule: whether a failed, one-based model attempt can be followed by another.
/// The coding task may replace only the bounded expression below in a detached worktree.
/// The outer for-loop still independently enforces the policy maximum.
/// </summary>
internal static class RetryBudget
{
    internal static bool HasAnotherAttempt(int attempt, int maxCalls) =>
        attempt > 0 && maxCalls > 0 && attempt < maxCalls;
}
