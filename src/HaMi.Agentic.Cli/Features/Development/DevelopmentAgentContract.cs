using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>
/// Versioned, human-reviewed task for the first actor-driven C# vertical slice.
/// This intentionally permits exactly one generated file and no host commands.
/// </summary>
internal sealed record DevelopmentAgentContract(
    int SchemaVersion,
    string TaskId,
    string Description,
    string TargetFramework,
    string ProjectFile,
    string SourceFile,
    string Mode,
    int MaxAttempts,
    string[] AcceptanceCriteria,
    string ExpectedStdout = "HaMi isolated correction passed",
    string? FixtureDirectory = null,
    string? ScopeContract = null)
{
    internal const string ReviewedStdout = "HaMi isolated correction passed";
    internal const string ReviewedPairStdout = "HaMi pair acceptance passed";
    internal const string TrustedPath = "contracts/development/agent-console-retry.json";
    internal const string PairPath = "contracts/development/agent-pair.json";
    internal const string ProductionPath = "contracts/development/agent-production-retry-budget.json";
    internal const string ProductionScopePath = "contracts/development/agent-production-retry-budget-scope.json";
    internal const string PairScopePath = "contracts/development/sandbox-pair.json";
    internal const string PairFixture = "samples/SandboxCodingPair";
    internal const string PairFirst = "src/HaMi.Agentic.Cli/Features/Development/PairCalculator.cs";
    internal const string PairSecond = "src/HaMi.Agentic.Cli/Features/Development/PairFormatter.cs";

    // This is a resolved, independently validated DevelopmentContract,
    // never a field accepted from JSON or supplied by a model/GitHub Issue.
    [JsonIgnore]
    internal DevelopmentContract? ReviewedScope { get; init; }

    [JsonIgnore]
    internal bool IsPair => TaskId == "sandbox-pair";

    [JsonIgnore]
    internal bool IsProduction => TaskId == "production-retry-budget";

    [JsonIgnore]
    internal string[] GeneratedPaths => ReviewedScope?.AllowedWritePaths ?? [SourceFile];

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static DevelopmentAgentContract Load(string repository, string? contractPath)
    {
        string selected = contractPath ?? TrustedPath;
        if (selected is not (TrustedPath or PairPath or ProductionPath))
            throw new InvalidOperationException("Agent run requires a reviewed development contract.");
        string text = File.ReadAllText(Path.Combine(repository, selected.Replace('/', Path.DirectorySeparatorChar)));
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Development agent contract must be a JSON object.");
        if (!document.RootElement.TryGetProperty("expectedStdout", out JsonElement expected)
            || expected.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException(
                "Reviewed agent contract must explicitly declare expectedStdout.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
            if (!seen.Add(property.Name))
                throw new InvalidOperationException("Duplicate agent contract fields are forbidden.");
        DevelopmentAgentContract contract = JsonSerializer.Deserialize<DevelopmentAgentContract>(text, Options)
            ?? throw new InvalidOperationException("Agent contract is empty.");
        if (selected is PairPath or ProductionPath)
        {
            string reviewedPath = selected == PairPath ? PairScopePath : ProductionScopePath;
            if (contract.ScopeContract != reviewedPath)
                throw new InvalidOperationException("Unreviewed development scope contract.");
            string scopeText = File.ReadAllText(Path.Combine(repository,
                reviewedPath.Replace('/', Path.DirectorySeparatorChar)));
            contract = contract with { ReviewedScope = DevelopmentContract.Parse(scopeText) };
        }
        contract.Validate();
        if ((selected == PairPath) != contract.IsPair
            || (selected == ProductionPath) != contract.IsProduction)
            throw new InvalidOperationException("Task and agent contract path do not match.");
        return contract;
    }

    public void Validate()
    {
        bool single = TaskId == "sandbox-console-retry"
            && SourceFile == "Program.cs" && FixtureDirectory is null
            && ScopeContract is null && ReviewedScope is null
            && ExpectedStdout == ReviewedStdout;
        bool pair = TaskId == "sandbox-pair"
            && SourceFile == PairFirst && FixtureDirectory == PairFixture
            && ScopeContract == PairScopePath
            && ReviewedScope is { TaskId: "sandbox-pair", TargetFramework: "net11.0" }
            && ReviewedScope.AllowedWritePaths.SequenceEqual(
                [PairFirst, PairSecond], StringComparer.Ordinal)
            && ExpectedStdout == ReviewedPairStdout
            && MaxAttempts == 2;
        bool production = TaskId == "production-retry-budget"
            && ProjectFile == "samples/RetryBudget/RetryBudget.csproj"
            && SourceFile == DevelopmentProductionVerifier.SourcePath
            && ScopeContract == ProductionScopePath
            && FixtureDirectory is null
            && ReviewedScope is { TaskId: "production-retry-budget", TargetFramework: "net11.0" }
            && ReviewedScope.AllowedWritePaths.SequenceEqual(
                [DevelopmentProductionVerifier.SourcePath], StringComparer.Ordinal)
            && ExpectedStdout == DevelopmentOutputAcceptance.ReviewedProductionStdout
            && MaxAttempts == 2;
        if (SchemaVersion != 1 || TargetFramework != "net11.0"
            || Mode != "run"
            || (!production && ProjectFile != "Demo.csproj")
            || !(single || pair || production))
            throw new InvalidOperationException("Agent contract scope or version is not approved.");
        if (MaxAttempts is < 1 or > 3)
            throw new InvalidOperationException("Agent retry budget must be between 1 and 3.");
        if (string.IsNullOrWhiteSpace(Description) || Description.Length > 1500
            || AcceptanceCriteria is not { Length: >= 1 and <= 8 }
            || AcceptanceCriteria.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 300))
            throw new InvalidOperationException("Agent contract description or acceptance criteria invalid.");
    }

    internal static void SelfTest()
    {
        var test = new DevelopmentAgentContract(1, "sandbox-console-retry",
            "Write an offline console program.", "net11.0",
            "Demo.csproj", "Program.cs", "run", 2, ["Program exits zero."]);
        test.Validate();
        var invalid = test with { MaxAttempts = 100 };
        ExpectInvalid(invalid, "Unbounded actor attempts accepted.");
        ExpectInvalid(test with { ExpectedStdout = "ignored" },
            "Unreviewed stdout expectation accepted.");
        ExpectInvalid(test with { ExpectedStdout = "" },
            "Empty stdout expectation accepted.");
        var scope = DevelopmentContract.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            taskId = "sandbox-pair",
            description = "two files",
            targetFramework = "net11.0",
            allowedWritePaths = new[] { PairFirst, PairSecond },
            acceptanceCriteria = new[] { "Validated by trusted tests." }
        }));
        var pair = test with
        {
            TaskId = "sandbox-pair",
            SourceFile = PairFirst,
            ExpectedStdout = ReviewedPairStdout,
            FixtureDirectory = PairFixture,
            ScopeContract = PairScopePath,
            ReviewedScope = scope
        };
        pair.Validate();
        ExpectInvalid(pair with { ReviewedScope = null }, "Missing reviewed scope accepted.");
        ExpectInvalid(pair with { ExpectedStdout = "wrong" }, "Changed pair stdout accepted.");
        ExpectInvalid(pair with { FixtureDirectory = "../unsafe" }, "Changed fixture accepted.");
        ExpectInvalid(pair with { MaxAttempts = 3 }, "Unapproved retry expansion accepted.");
        var productionScope = DevelopmentContract.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            taskId = "production-retry-budget",
            description = "One approved production file.",
            targetFramework = "net11.0",
            allowedWritePaths = new[] { DevelopmentProductionVerifier.SourcePath },
            acceptanceCriteria = new[] { "Trusted host tests verify the candidate." }
        }));
        var production = test with
        {
            TaskId = "production-retry-budget",
            ProjectFile = "samples/RetryBudget/RetryBudget.csproj",
            SourceFile = DevelopmentProductionVerifier.SourcePath,
            ExpectedStdout = DevelopmentOutputAcceptance.ReviewedProductionStdout,
            ScopeContract = ProductionScopePath,
            ReviewedScope = productionScope
        };
        production.Validate();
        ExpectInvalid(production with { ReviewedScope = null },
            "Production scope was not required.");
        ExpectInvalid(production with { MaxAttempts = 3 },
            "Production retry budget could be widened.");
        ExpectInvalid(production with { SourceFile = PairFirst },
            "Production source path could be changed.");
        ExpectInvalid(production with { ScopeContract = PairScopePath },
            "Production scope could be changed.");
    }

    private static void ExpectInvalid(DevelopmentAgentContract contract, string message)
    {
        try { contract.Validate(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }
}
