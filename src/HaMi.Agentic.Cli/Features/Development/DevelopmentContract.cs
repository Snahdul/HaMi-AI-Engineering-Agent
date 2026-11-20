using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>
/// A human-reviewed, versioned scope contract. This is not a permission to
/// execute generated code or to let a model choose additional write paths.
/// </summary>
internal sealed record DevelopmentContract(
    int SchemaVersion,
    string TaskId,
    string Description,
    string TargetFramework,
    string[] AllowedWritePaths,
    string[] AcceptanceCriteria)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static DevelopmentContract Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Development contract must be a JSON object.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
            if (!keys.Add(property.Name))
                throw new InvalidOperationException("Development contract contains duplicate keys.");

        DevelopmentContract? contract = JsonSerializer.Deserialize<DevelopmentContract>(json, Options);
        if (contract is null)
            throw new InvalidOperationException("Development contract is empty.");

        contract.Validate();
        return contract;
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || TargetFramework != "net11.0")
            throw new InvalidOperationException("Unsupported contract version or target framework.");
        if (string.IsNullOrEmpty(TaskId)
            || TaskId.Length > 72
            || !Regex.IsMatch(TaskId, @"^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Contract taskId is invalid.");
        if (string.IsNullOrWhiteSpace(Description) || Description.Length > 1000)
            throw new InvalidOperationException("Contract description is missing or too long.");
        if (AllowedWritePaths is null || AllowedWritePaths.Length is < 1 or > 8
            || AllowedWritePaths.Any(p => !IsAllowedSourcePath(p))
            || AllowedWritePaths.Distinct(StringComparer.Ordinal).Count() != AllowedWritePaths.Length)
            throw new InvalidOperationException("Contract must allowlist 1-8 distinct production C# paths.");
        if (AcceptanceCriteria is null || AcceptanceCriteria.Length is < 1 or > 12
            || AcceptanceCriteria.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 500))
            throw new InvalidOperationException("Contract requires explicit acceptance criteria.");
    }

    public static bool IsContractPath(string? path) =>
        IsSafeRelativePath(path)
        && path!.StartsWith("contracts/development/", StringComparison.Ordinal)
        && path.EndsWith(".json", StringComparison.Ordinal);

    public static bool IsAllowedSourcePath(string? path) =>
        IsSafeRelativePath(path)
        && path!.StartsWith("src/HaMi.Agentic.Cli/", StringComparison.Ordinal)
        && path.EndsWith(".cs", StringComparison.Ordinal)
        && !path.Split('/').Any(s => s is "obj" or "bin");

    private static bool IsSafeRelativePath(string? path) =>
        path is { Length: > 0 and <= 240 }
        && path.All(c => c is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '-' or '_' or '.' or '/')
        && path.Split('/').All(s => s.Length > 0 && s is not "." and not "..");
}
