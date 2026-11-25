using System.Text;
using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>
/// Read-only M5b order observation. A validated plan is not approval,
/// a runtime capability or authorization to execute a step.
/// </summary>
internal sealed class DevelopmentOrder
{
    private const string Fence = "```hami-order-json";
    private const string SchemaResource = "HaMi.DevelopmentOrder.Schema";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Lazy<DevelopmentOrderSchema> Schema = new(LoadSchema);

    private DevelopmentOrder(string plan, JsonElement document)
    {
        Plan = plan;
        Document = document;
        Sha256 = DevelopmentJobState.Hash(Utf8.GetBytes(plan));
    }

    internal string Plan { get; }
    internal JsonElement Document { get; }
    internal string Sha256 { get; }

    internal static DevelopmentOrder ParseIssueBody(ReadOnlySpan<byte> body)
    {
        Require(body.Length is > 0 and <= 64 * 1024, "ORDER_BODY_SIZE");
        Require(!body.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), "ORDER_BODY_BOM");
        string text;
        try
        {
            text = Utf8.GetString(body).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException("ORDER_BODY_ENCODING");
        }

        Require(!text.Any(c => char.IsControl(c) && c is not ('\n' or '\t')), "ORDER_BODY_CONTROL");
        string[] lines = text.Split('\n');
        int start = Array.IndexOf(lines, Fence);
        Require(start >= 0 && Array.LastIndexOf(lines, Fence) == start, "ORDER_FENCE_COUNT");
        int end = Array.IndexOf(lines, "```", start + 1);
        Require(end > start + 1, "ORDER_FENCE_END");
        string plan = string.Join('\n', lines[(start + 1)..end]) + "\n";
        Require(Utf8.GetByteCount(plan) <= 32 * 1024, "ORDER_PLAN_SIZE");

        using JsonDocument json = JsonDocument.Parse(plan, new JsonDocumentOptions { MaxDepth = 16 });
        ValidateJson(json.RootElement);
        Require(Schema.Value.Validate(json.RootElement), "ORDER_SCHEMA");
        ValidateProfile(json.RootElement);
        return new DevelopmentOrder(plan, json.RootElement.Clone());
    }

    private static DevelopmentOrderSchema LoadSchema()
    {
        // The schema is compiled into the reviewed host assembly, never
        // selected by an issue or fetched from a caller-controlled URL.
        using Stream stream = typeof(DevelopmentOrder).Assembly.GetManifestResourceStream(SchemaResource)
            ?? throw new InvalidOperationException("ORDER_SCHEMA_MISSING");
        using var reader = new StreamReader(stream, Utf8);
        string schema = reader.ReadToEnd();
        return DevelopmentOrderSchema.Parse(schema);
    }

    private static void ValidateJson(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Require(names.Add(property.Name), "ORDER_DUPLICATE_FIELD");
                    ValidateText(property.Name);
                    ValidateJson(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                    ValidateJson(item);
                break;
            case JsonValueKind.String:
                ValidateText(element.GetString()!);
                break;
            case JsonValueKind.Number:
                Require(element.TryGetInt32(out _), "ORDER_INTEGER");
                break;
        }
    }

    private static void ValidateText(string value)
    {
        Require(!string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl), "ORDER_TEXT");
        // Reject malformed UTF-16 after JSON unescaping.
        _ = Utf8.GetByteCount(value);
    }

    private static void ValidateProfile(JsonElement root)
    {
        // Structural validation alone does not establish capability or scope.
        // This first profile recognizes only the reviewed two-file fixture.
        JsonElement[] steps = root.GetProperty("steps").EnumerateArray().ToArray();
        JsonElement first = steps[0];
        JsonElement second = steps[1];
        RequireStep(first, "S01", [], DevelopmentAgentContract.PairFirst,
            "calculator-source", "pair-calculator");
        RequireStep(second, "S02", ["S01"], DevelopmentAgentContract.PairSecond,
            "formatter-source", "pair-formatter");

        RequireInputs(first, [("baseline", "calculator-baseline", null)]);
        RequireInputs(second, [("baseline", "formatter-baseline", null),
            ("step", "calculator-source", "S01")]);

        JsonElement final = root.GetProperty("finalVerification");
        Require(Text(final, "verifier") == "pair-integration", "ORDER_FINAL_VERIFIER");
        RequireDistinct(root.GetProperty("decisions").EnumerateArray().Select(x => Text(x, "id")),
            "ORDER_DECISION_IDS");
        RequireDistinct(steps.Append(final).SelectMany(s => s.GetProperty("criteria").EnumerateArray()
            .Select(x => Text(x, "id"))), "ORDER_CRITERION_IDS");

        // Preserve the existing explicit-budget checks. Missing fields are
        // resolved against the separately trusted policy at approval.
        if (root.TryGetProperty("limits", out JsonElement limits))
        {
            if (steps.All(s => s.TryGetProperty("maxModelCalls", out _)))
                Require(steps.Sum(s => Number(s, "maxModelCalls")) <= Number(limits, "modelCalls"), "ORDER_BUDGET");
            if (steps.All(s => s.TryGetProperty("maxSandboxRuns", out _))
                && final.TryGetProperty("maxSandboxRuns", out _))
                Require(steps.Sum(s => Number(s, "maxSandboxRuns")) + Number(final, "maxSandboxRuns")
                    <= Number(limits, "sandboxRuns"), "ORDER_BUDGET");
        }
    }

    private static void RequireStep(JsonElement step, string id, string[] dependencies,
        string path, string output, string verifier)
    {
        Require(Text(step, "id") == id, "ORDER_STEP_ID");
        Require(Strings(step, "dependsOn").SequenceEqual(dependencies), "ORDER_DEPENDENCY");
        Require(Strings(step, "competencies").SequenceEqual(["csharp"]), "ORDER_COMPETENCY");
        Require(DevelopmentContract.IsAllowedSourcePath(path)
            && Strings(step, "allowedWritePaths").SequenceEqual([path]), "ORDER_SCOPE");
        Require(Strings(step, "outputs").SequenceEqual([output]), "ORDER_OUTPUT");
        Require(Text(step, "verifier") == verifier, "ORDER_VERIFIER");
    }

    private static void RequireInputs(JsonElement step, (string Source, string Id, string? StepId)[] expected)
    {
        var actual = step.GetProperty("inputs").EnumerateArray().Select(x =>
            (Text(x, "source"), Text(x, "id"), x.GetProperty("stepId").GetString()));
        Require(actual.SequenceEqual(expected), "ORDER_INPUT");
    }

    private static void RequireDistinct(IEnumerable<string> values, string reason)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Require(values.All(seen.Add), reason);
    }

    private static string[] Strings(JsonElement element, string name) =>
        element.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToArray();

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()!;
    private static int Number(JsonElement element, string name) => element.GetProperty(name).GetInt32();

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException(reason);
    }
}
