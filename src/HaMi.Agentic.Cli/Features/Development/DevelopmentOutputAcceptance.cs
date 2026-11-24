namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>
/// Deterministic semantic acceptance for the reviewed one-line console task.
/// Neither GitHub Issue text nor generated code can supply the expected value.
/// Operates on the locally retained guest execution log AFTER MicroVM cleanup.
/// </summary>
internal static class DevelopmentOutputAcceptance
{
    internal const string CorrectionFeedback =
        "The program built and exited with status zero, but the exact console output "
        + "was wrong. Print only HaMi isolated correction passed followed by one "
        + "normal line terminator. Do not print any extra text.";

    internal static string ForExpected(string expected) => expected switch
    {
        DevelopmentAgentContract.ReviewedStdout => CorrectionFeedback,
        DevelopmentAgentContract.ReviewedPairStdout =>
            "The trusted offline boundary harness rejected the candidate. "
            + "Fix the two reviewed C# files without modifying the trusted Program.cs. "
            + "Produce only HaMi pair acceptance passed on a successful execution.",
        _ => throw new InvalidOperationException("Unreviewed stdout expectation.")
    };

    internal static bool Matches(string? actual, string expected)
    {
        if (actual is null || actual.Length > 16 * 1024
            || expected is not (DevelopmentAgentContract.ReviewedStdout
                or DevelopmentAgentContract.ReviewedPairStdout))
            return false;

        // Normalize Windows vs Linux line endings, but allow at most ONE
        // final line terminator. Extra whitespace, extra lines and stderr
        // (which the runner appends with a marker) are semantic failures.
        string normalized = actual.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (normalized.EndsWith('\n'))
            normalized = normalized[..^1];
        return string.Equals(normalized, expected, StringComparison.Ordinal);
    }

    internal static void SelfTest()
    {
        string expected = DevelopmentAgentContract.ReviewedStdout;
        foreach (string valid in new[] { expected, expected + "\n", expected + "\r\n" })
            if (!Matches(valid, expected))
                throw new InvalidOperationException("Reviewed stdout was rejected.");

        foreach (string? invalid in new string?[]
        {
            null, "", "wrong", expected.ToUpperInvariant(),
            expected + " ", expected + "\n\n", expected + "\r\n\r\n",
            expected + "\nother", "prefix\n" + expected,
            expected + "\nSTDERR:\nwarning",
            expected + "\n" + new string('x', 17 * 1024)
        })
            if (Matches(invalid, expected))
                throw new InvalidOperationException("Incorrect stdout was accepted.");

        if (Matches(expected, "untrusted issue-supplied expected output"))
            throw new InvalidOperationException("Unreviewed expected output was accepted.");
        string pair = DevelopmentAgentContract.ReviewedPairStdout;
        if (!Matches(pair + "\n", pair) || Matches(pair + "\nextra", pair)
            || string.IsNullOrWhiteSpace(ForExpected(pair))
            || !string.Equals(ForExpected(expected), CorrectionFeedback, StringComparison.Ordinal))
            throw new InvalidOperationException("Pair output acceptance gate failed.");
    }
}
