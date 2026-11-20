namespace HaMi.Agentic.Cli.Features.Coding;

// Result types stay local to the Coding vertical slice. Each union expresses
// a closed set of domain outcomes; it is not a generic application-wide Result<T>.

internal sealed record SourceValidationPassed(string Expression);
internal sealed record SourceValidationRejected(IReadOnlyList<string> Diagnostics);
internal union SourceValidationResult(SourceValidationPassed, SourceValidationRejected);

internal sealed record CompilationSucceeded;
internal sealed record CompilationFailed(string Diagnostics);
internal union CompilationResult(CompilationSucceeded, CompilationFailed);

internal sealed record TestExecutionSucceeded(string Output);
internal sealed record TestExecutionFailed(string Diagnostics);
internal union TestExecutionResult(TestExecutionSucceeded, TestExecutionFailed);

internal sealed record ExpressionRejectedAttempt(int Number, IReadOnlyList<string> Diagnostics);
internal sealed record CompilationFailedAttempt(int Number, string Diagnostics);
internal sealed record TestsFailedAttempt(int Number, string Diagnostics);
internal sealed record CodingSucceededAttempt(int Number);
internal union CodingAttemptResult(
    ExpressionRejectedAttempt,
    CompilationFailedAttempt,
    TestsFailedAttempt,
    CodingSucceededAttempt);

internal sealed record CodingRunSucceeded(
    int ModelCalls, IReadOnlyList<CodingAttemptResult> Attempts);
internal sealed record CodingRunExhausted(
    int ModelCalls, IReadOnlyList<CodingAttemptResult> Attempts);
internal union CodingRunResult(CodingRunSucceeded, CodingRunExhausted);

/// <summary>
/// Stable compatibility DTO for existing report.json consumers. Keep its
/// PascalCase attempt fields even though the internal attempt states are unions.
/// </summary>
internal sealed record CodingAttemptReport(
    int Attempt, bool ValidationPassed, bool BuildPassed, bool TestsPassed, string Status);

/// <summary>
/// Domain-to-report projection: serializable diagnostics are intentionally not
/// copied into the historical report, preserving its established wire format.
/// </summary>
internal sealed record CodingRunReport(
    string Status, int ModelCalls, IReadOnlyList<CodingAttemptReport> Attempts)
{
    public bool Success => Status == "generated";

    public static CodingRunReport From(CodingRunResult result) => result switch
    {
        CodingRunSucceeded completed => new("generated", completed.ModelCalls,
            completed.Attempts.Select(AttemptToReport).ToArray()),
        CodingRunExhausted rejected => new("validation_failed", rejected.ModelCalls,
            rejected.Attempts.Select(AttemptToReport).ToArray()),
        null => throw new InvalidOperationException("Coding run has no outcome.")
    };

    private static CodingAttemptReport AttemptToReport(CodingAttemptResult result) =>
        result switch
        {
            ExpressionRejectedAttempt rejected => new(rejected.Number, false, false, false,
                "expression_rejected"),
            CompilationFailedAttempt failed => new(failed.Number, true, false, false,
                "build_failed"),
            TestsFailedAttempt failed => new(failed.Number, true, true, false,
                "tests_failed"),
            CodingSucceededAttempt completed => new(completed.Number, true, true, true,
                "passed"),
            null => throw new InvalidOperationException("Coding attempt has no outcome.")
        };

    public static void SelfTest()
    {
        CodingAttemptResult[] allCases =
        [
            new ExpressionRejectedAttempt(1, ["Forbidden identifier"]),
            new CompilationFailedAttempt(2, "CS1002"),
            new TestsFailedAttempt(3, "Expected 3, received 4"),
            new CodingSucceededAttempt(4)
        ];

        CodingRunReport completed = From(new CodingRunSucceeded(4, allCases));
        CodingRunReport rejected = From(new CodingRunExhausted(3, allCases[..3]));

        if (!completed.Success || completed.Status != "generated"
            || completed.ModelCalls != 4 || completed.Attempts.Count != 4
            || completed.Attempts[0] is not { Status: "expression_rejected",
                ValidationPassed: false, BuildPassed: false, TestsPassed: false }
            || completed.Attempts[1] is not { Status: "build_failed",
                ValidationPassed: true, BuildPassed: false, TestsPassed: false }
            || completed.Attempts[2] is not { Status: "tests_failed",
                ValidationPassed: true, BuildPassed: true, TestsPassed: false }
            || completed.Attempts[3] is not { Status: "passed",
                ValidationPassed: true, BuildPassed: true, TestsPassed: true }
            || rejected.Success || rejected.Status != "validation_failed"
            || rejected.ModelCalls != 3 || rejected.Attempts.Count != 3)
            throw new InvalidOperationException("Coding unions did not preserve report states.");

        // Check exactly the legacy attempt field names, independent of union storage.
        string json = System.Text.Json.JsonSerializer.Serialize(completed.Attempts[3]);
        if (!json.Contains("\"ValidationPassed\":true", StringComparison.Ordinal)
            || !json.Contains("\"BuildPassed\":true", StringComparison.Ordinal)
            || !json.Contains("\"TestsPassed\":true", StringComparison.Ordinal)
            || !json.Contains("\"Status\":\"passed\"", StringComparison.Ordinal))
            throw new InvalidOperationException("Coding attempt JSON wire format changed.");

        Console.WriteLine("C# 15 coding union self-test passed: exhaustive cases, "
            + "valid state combinations and legacy report mapping.");
    }
}
