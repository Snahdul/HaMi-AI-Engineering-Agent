namespace HaMi.Agentic.Cli.Features.Coding;

/// <summary>
/// Compile-time allowlist of narrowly authorized coding assignments. Selecting a
/// task never grants general file access or permission to execute arbitrary code.
/// </summary>
internal sealed record CodingTaskDefinition(
    string Id,
    string TargetPath,
    string FixtureDirectory,
    string ProjectFile,
    string PromptAsset,
    string PromptName,
    string PolicyAsset,
    IReadOnlySet<string> AllowedIdentifiers)
{
    public static CodingTaskDefinition Clamp { get; } = new(
        "clamp-kata",
        "samples/ClampKata/Clamp.cs",
        "samples/ClampKata",
        "ClampKata.csproj",
        "prompts/coding/ClampExpression.prompty",
        "clamp-expression",
        "policies/coding/clamp.policy.json",
        new HashSet<string>(["value", "min", "max", "true", "false"], StringComparer.Ordinal));

    public static CodingTaskDefinition RetryBudget { get; } = new(
        "retry-budget",
        "src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs",
        "samples/RetryBudget",
        "RetryBudget.csproj",
        "prompts/coding/RetryBudget.prompty",
        "retry-budget-expression",
        "policies/coding/retry-budget.policy.json",
        new HashSet<string>(["attempt", "maxCalls", "true", "false"], StringComparer.Ordinal));

    public static bool TryResolve(string? requested, out CodingTaskDefinition task)
    {
        switch (requested ?? Clamp.Id)
        {
            case "clamp-kata":
                task = Clamp;
                return true;
            case "retry-budget":
                task = RetryBudget;
                return true;
            default:
                task = null!;
                return false;
        }
    }

    /// <summary>
    /// Replace only the expression of the approved expression-bodied method.
    /// Preserve XML comments, whitespace and existing line endings.
    /// </summary>
    public string ReplaceExpression(string originalSource, string expression)
    {
        string signature = Id switch
        {
            "clamp-kata" => "public static int Clamp(int value, int min, int max) =>",
            "retry-budget" => "internal static bool HasAnotherAttempt(int attempt, int maxCalls) =>",
            _ => throw new InvalidOperationException("Unsupported coding task.")
        };

        // Never patch a missing/ambiguous trusted scaffold.
        int signatureOffset = originalSource.IndexOf(signature, StringComparison.Ordinal);
        if (signatureOffset < 0
            || originalSource.LastIndexOf(signature, StringComparison.Ordinal) != signatureOffset)
            throw new InvalidOperationException("Expected exactly one approved method signature.");

        int valueStart = signatureOffset + signature.Length;
        int terminator = originalSource.IndexOf(';', valueStart);
        if (terminator < 0)
            throw new InvalidOperationException("Approved method has no expression terminator.");

        // Preserve whitespace around the expression, including CRLF.
        int beginning = valueStart;
        while (beginning < terminator && char.IsWhiteSpace(originalSource[beginning]))
            beginning++;
        int ending = terminator;
        while (ending > beginning && char.IsWhiteSpace(originalSource[ending - 1]))
            ending--;

        if (beginning == ending)
            throw new InvalidOperationException("Approved method expression is missing.");

        return originalSource[..beginning] + expression.Trim() + originalSource[ending..];
    }

    public static void SelfTest()
    {
        if (!TryResolve(null, out var defaultTask) || defaultTask.Id != Clamp.Id
            || !TryResolve("retry-budget", out var retry) || retry != RetryBudget
            || TryResolve("../../../src/Program.cs", out _)
            || TryResolve("RETRY-BUDGET", out _)
            || RetryBudget.TargetPath == Clamp.TargetPath
            || RetryBudget.AllowedIdentifiers.Contains("value")
            || Clamp.AllowedIdentifiers.Contains("maxCalls"))
            throw new InvalidOperationException("Coding task allowlist accepted an invalid task.");

        // The no-op must be byte-identical; changing the expression must not
        // remove XML documentation or normalize Windows CRLF line endings.
        string retryBaseline =
            "namespace HaMi.Agentic.Cli.Features.Coding;\r\n"
            + "\r\n/// <summary>Keep this documentation.</summary>\r\n"
            + "internal static class RetryBudget\r\n{\r\n"
            + "    internal static bool HasAnotherAttempt(int attempt, int maxCalls) =>\r\n"
            + "        attempt >= 1 && maxCalls >= 1 && attempt < maxCalls;\r\n"
            + "}\r\n";
        const string originalExpression = "attempt >= 1 && maxCalls >= 1 && attempt < maxCalls";
        const string alternative = "attempt > 0 && maxCalls > 0 && attempt < maxCalls";
        string changed = RetryBudget.ReplaceExpression(retryBaseline, alternative);
        string expected = retryBaseline.Replace(originalExpression, alternative,
            StringComparison.Ordinal);
        if (changed != expected
            || RetryBudget.ReplaceExpression(retryBaseline, originalExpression) != retryBaseline
            || !changed.Contains("/// <summary>Keep this documentation.</summary>\r\n",
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Coding source patch did not preserve comments, CRLF or no-op expression.");

        const string clampBaseline =
            "// Keep original source comments.\npublic static class ClampKata\n{\n"
            + "    public static int Clamp(int value, int min, int max) => throw new NotImplementedException();\n}\n";
        string clampChanged = Clamp.ReplaceExpression(clampBaseline, "value");
        if (clampChanged != clampBaseline.Replace("throw new NotImplementedException()",
                "value", StringComparison.Ordinal))
            throw new InvalidOperationException("Clamp patch changed more than its expression.");

        foreach (string invalid in new[]
        {
            retryBaseline.Replace("HasAnotherAttempt", "UnknownMethod", StringComparison.Ordinal),
            retryBaseline + retryBaseline
        })
        {
            try
            {
                _ = RetryBudget.ReplaceExpression(invalid, "true");
                throw new InvalidOperationException("Ambiguous coding scaffold was accepted.");
            }
            catch (InvalidOperationException ex) when (
                ex.Message == "Expected exactly one approved method signature.")
            {
                // Expected fail-closed result.
            }
        }
    }
}
