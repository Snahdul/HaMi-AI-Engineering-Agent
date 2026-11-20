// Trusted regression harness for a real Coding feature source file.
// This file and its .csproj are never writable by the model.
using HaMi.Agentic.Cli.Features.Coding;

int tested = 0;
int failed = 0;

void Check(int attempt, int maxCalls)
{
    ++tested;
    bool expected = attempt >= 1 && maxCalls >= 1 && attempt < maxCalls;
    bool actual = RetryBudget.HasAnotherAttempt(attempt, maxCalls);
    if (actual == expected) return;
    ++failed;
    if (failed <= 10)
        Console.Error.WriteLine($"FAIL attempt={attempt} maxCalls={maxCalls}: expected {expected}, got {actual}");
}

// The complete small boundary grid, including negative/zero attempts.
for (int maxCalls = -2; maxCalls <= 7; ++maxCalls)
    for (int attempt = -3; attempt <= 10; ++attempt)
        Check(attempt, maxCalls);

// Randomized coverage, deterministic for reproducibility.
var random = new Random(1337);
for (int i = 0; i < 128; i++)
    Check(random.Next(-30, 31), random.Next(-10, 31));

Console.WriteLine($"RetryBudget: {tested} tests, {failed} failures.");
return failed == 0 ? 0 : 1;
