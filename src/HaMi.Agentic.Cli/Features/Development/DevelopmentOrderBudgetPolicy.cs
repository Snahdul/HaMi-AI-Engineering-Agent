using System.Text;
using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

// Reviewed pair-profile budget policy, not an instruction from the model
// or Issue. The caller supplies independently trusted policy bytes/digest.
internal sealed class DevelopmentOrderBudgetPolicy
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private DevelopmentOrderBudgetPolicy(string source, string hash,
        OrderOperationLimits defaults, OrderOperationLimits maximums, int finalVerificationSeconds)
    {
        FinalVerificationSeconds = finalVerificationSeconds;
        Source = source;
        Sha256 = hash;
        Defaults = defaults;
        Maximums = maximums;
    }

    internal string Source { get; }
    internal string Sha256 { get; }
    internal OrderOperationLimits Defaults { get; }
    internal OrderOperationLimits Maximums { get; }
    internal int FinalVerificationSeconds { get; }

    internal static DevelopmentOrderBudgetPolicy Parse(byte[] bytes)
    {
        OrderCheck.Require(bytes.Length is > 0 and <= 4096
            && !bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), "ORDER_BUDGET_POLICY_SIZE");
        string source = Utf8.GetString(bytes);
        using var json = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = 4 });
        var root = json.RootElement;
        RequireFields(root, ["version", "defaults", "maximums", "finalVerificationSeconds"]);
        OrderCheck.Require(Read(root, "version") == 1, "ORDER_BUDGET_POLICY_VERSION");
        OrderOperationLimits defaults = ReadLimits(root.GetProperty("defaults"));
        OrderOperationLimits maximums = ReadLimits(root.GetProperty("maximums"));
        // Existing reviewed pair contract is still the outer safety envelope.
        // This slice cannot expand it by loading a different budget policy.
        Validate(maximums, new(4, 5, 1800, 2, 2, 2, 2, 1));
        Validate(defaults, maximums);
        int finalSeconds = Read(root, "finalVerificationSeconds");
        OrderCheck.Require(finalSeconds > 0 && finalSeconds <= defaults.Seconds, "ORDER_BUDGET_FINAL_TIME");
        return new(source, DevelopmentJobState.Hash(bytes), defaults, maximums, finalSeconds);
    }

    internal OrderOperationLimits Resolve(DevelopmentOrder order)
    {
        var root = order.Document;
        var steps = root.GetProperty("steps");
        bool explicitTotal = root.TryGetProperty("limits", out var total);
        var result = new OrderOperationLimits(
            explicitTotal ? Read(total, "modelCalls") : Defaults.Models,
            explicitTotal ? Read(total, "sandboxRuns") : Defaults.Guests,
            explicitTotal ? Read(total, "elapsedSeconds") : Defaults.Seconds,
            Optional(steps[0], "maxModelCalls", Defaults.S01Models),
            Optional(steps[0], "maxSandboxRuns", Defaults.S01Guests),
            Optional(steps[1], "maxModelCalls", Defaults.S02Models),
            Optional(steps[1], "maxSandboxRuns", Defaults.S02Guests),
            Optional(root.GetProperty("finalVerification"), "maxSandboxRuns", Defaults.FinalGuests));
        ValidateEffective(result);
        return result;
    }

    internal void ValidateEffective(OrderOperationLimits value)
    {
        Validate(value, Maximums);
        OrderCheck.Require(value.Seconds >= FinalVerificationSeconds, "ORDER_BUDGET_FINAL_TIME");
    }

    private static int Optional(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out _) ? Read(element, name) : fallback;

    private static int Read(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int number))
            throw new InvalidOperationException("ORDER_BUDGET_POLICY_INTEGER");
        return number;
    }

    private static void RequireFields(JsonElement element, string[] expected)
    {
        OrderCheck.Require(element.ValueKind == JsonValueKind.Object, "ORDER_BUDGET_POLICY_FIELDS");
        string[] actual = element.EnumerateObject().Select(p => p.Name).ToArray();
        OrderCheck.Require(actual.Length == expected.Length
            && actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected), "ORDER_BUDGET_POLICY_FIELDS");
    }

    private static OrderOperationLimits ReadLimits(JsonElement element)
    {
        RequireFields(element, ["modelCalls", "sandboxRuns", "elapsedSeconds", "s01ModelCalls",
            "s01SandboxRuns", "s02ModelCalls", "s02SandboxRuns", "finalSandboxRuns"]);
        return new(Read(element, "modelCalls"), Read(element, "sandboxRuns"), Read(element, "elapsedSeconds"),
            Read(element, "s01ModelCalls"), Read(element, "s01SandboxRuns"),
            Read(element, "s02ModelCalls"), Read(element, "s02SandboxRuns"), Read(element, "finalSandboxRuns"));
    }

    private static void Validate(OrderOperationLimits value, OrderOperationLimits maximum)
    {
        OrderCheck.Require(value.Models > 0 && value.Models <= maximum.Models
            && value.Guests > 0 && value.Guests <= maximum.Guests
            && value.Seconds > 0 && value.Seconds <= maximum.Seconds
            && value.S01Models > 0 && value.S01Models <= maximum.S01Models
            && value.S01Guests > 0 && value.S01Guests <= maximum.S01Guests
            && value.S02Models > 0 && value.S02Models <= maximum.S02Models
            && value.S02Guests > 0 && value.S02Guests <= maximum.S02Guests
            && value.FinalGuests == 1 && value.FinalGuests <= maximum.FinalGuests,
            "ORDER_BUDGET_MAXIMUM");
        OrderCheck.Require(value.S01Models + value.S02Models <= value.Models
            && value.S01Guests + value.S02Guests + value.FinalGuests <= value.Guests,
            "ORDER_BUDGET_TOTAL");
    }
}
