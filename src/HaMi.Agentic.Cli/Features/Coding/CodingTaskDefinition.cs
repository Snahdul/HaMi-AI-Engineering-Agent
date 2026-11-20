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

    public string RenderSource(string expression) => Id switch
    {
        "clamp-kata" =>
            "// Fixed C# source scaffold. Only the expression after => is model-generated.\n"
            + "public static class ClampKata\n{\n"
            + "    public static int Clamp(int value, int min, int max) => "
            + expression.Trim() + ";\n"
            + "}\n",
        "retry-budget" =>
            "namespace HaMi.Agentic.Cli.Features.Coding;\n\n"
            + "// Fixed trusted scaffold: the model can change only the expression.\n"
            + "internal static class RetryBudget\n{\n"
            + "    internal static bool HasAnotherAttempt(int attempt, int maxCalls) => "
            + expression.Trim() + ";\n"
            + "}\n",
        _ => throw new InvalidOperationException("Unsupported coding task.")
    };

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

        string source = RetryBudget.RenderSource(
            "attempt >= 1 && maxCalls >= 1 && attempt < maxCalls");
        if (!source.Contains("internal static bool HasAnotherAttempt(", StringComparison.Ordinal)
            || !source.Contains("attempt < maxCalls;", StringComparison.Ordinal))
            throw new InvalidOperationException("Retry task did not render the fixed scaffold.");
    }
}
