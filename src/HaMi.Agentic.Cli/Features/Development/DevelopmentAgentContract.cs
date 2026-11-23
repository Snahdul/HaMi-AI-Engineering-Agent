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
    string[] AcceptanceCriteria)
{
    internal const string TrustedPath = "contracts/development/agent-console-retry.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static DevelopmentAgentContract Load(string repository, string? contractPath)
    {
        string selected = contractPath ?? TrustedPath;
        if (!string.Equals(selected, TrustedPath, StringComparison.Ordinal))
            throw new InvalidOperationException("Agent run requires the reviewed development contract.");
        string text = File.ReadAllText(Path.Combine(repository, selected.Replace('/', Path.DirectorySeparatorChar)));
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Development agent contract must be a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
            if (!seen.Add(property.Name))
                throw new InvalidOperationException("Duplicate agent contract fields are forbidden.");
        DevelopmentAgentContract contract = JsonSerializer.Deserialize<DevelopmentAgentContract>(text, Options)
            ?? throw new InvalidOperationException("Agent contract is empty.");
        contract.Validate();
        return contract;
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || TargetFramework != "net11.0"
            || ProjectFile != "Demo.csproj" || SourceFile != "Program.cs"
            || Mode != "run" || TaskId != "sandbox-console-retry")
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
        try { invalid.Validate(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unbounded actor attempts accepted.");
    }
}
