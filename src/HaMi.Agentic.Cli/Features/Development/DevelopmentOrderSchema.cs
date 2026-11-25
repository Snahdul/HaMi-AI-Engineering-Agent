using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>
/// The reviewed M5b Draft 2020-12 subset, not a general JSON Schema engine.
/// Schemas are trusted embedded assets; instances have already passed the order input limits.
/// </summary>
internal sealed class DevelopmentOrderSchema
{
    private const string Dialect = "https://json-schema.org/draft/2020-12/schema";
    private static readonly HashSet<string> Patterns = new(StringComparer.Ordinal)
    {
        "^[0-9a-f]{40}$",
        "^[a-z][a-z0-9-]{0,63}$",
        "^S[0-9]{2}$",
        @"^[A-Za-z0-9._/-]+\.cs$",
        "^C[0-9]{2}$"
    };

    private readonly JsonElement schema;

    private DevelopmentOrderSchema(JsonElement schema) => this.schema = schema;

    internal static DevelopmentOrderSchema Parse(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        Check(document.RootElement, true);
        return new DevelopmentOrderSchema(document.RootElement.Clone());
    }

    internal bool Validate(JsonElement value) => Matches(schema, value);

    private static void Check(JsonElement node, bool root = false)
    {
        CheckObject(node);
        if (root)
            Require(node.TryGetProperty("$schema", out JsonElement dialect)
                && dialect.ValueKind == JsonValueKind.String && dialect.GetString() == Dialect);

        foreach (JsonProperty keyword in node.EnumerateObject())
        {
            JsonElement argument = keyword.Value;
            switch (keyword.Name)
            {
                case "$schema":
                    Require(root && argument.ValueKind == JsonValueKind.String && argument.GetString() == Dialect);
                    break;
                case "title":
                    Require(argument.ValueKind == JsonValueKind.String);
                    break;
                case "type":
                    Require(argument.ValueKind == JsonValueKind.String
                        && argument.GetString() is "object" or "array" or "string" or "integer" or "null");
                    break;
                case "properties":
                    CheckObject(argument);
                    foreach (JsonProperty property in argument.EnumerateObject())
                        Check(property.Value);
                    break;
                case "required":
                    Require(argument.ValueKind == JsonValueKind.Array);
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (JsonElement name in argument.EnumerateArray())
                        Require(name.ValueKind == JsonValueKind.String && names.Add(name.GetString()!));
                    break;
                case "additionalProperties":
                case "uniqueItems":
                    Require(argument.ValueKind is JsonValueKind.True or JsonValueKind.False);
                    break;
                case "const":
                    CheckLiteral(argument);
                    break;
                case "enum":
                    Require(argument.ValueKind == JsonValueKind.Array && argument.GetArrayLength() > 0);
                    JsonElement[] choices = argument.EnumerateArray().ToArray();
                    foreach (JsonElement choice in choices)
                        CheckLiteral(choice);
                    Require(Distinct(choices));
                    break;
                case "minimum":
                case "maximum":
                    Require(argument.ValueKind == JsonValueKind.Number && argument.TryGetInt32(out _));
                    break;
                case "minLength":
                case "maxLength":
                case "minItems":
                case "maxItems":
                    Require(argument.ValueKind == JsonValueKind.Number && argument.TryGetInt32(out int bound) && bound >= 0);
                    break;
                case "pattern":
                    Require(argument.ValueKind == JsonValueKind.String && Patterns.Contains(argument.GetString()!));
                    break;
                case "items":
                    Check(argument);
                    break;
                case "anyOf":
                    Require(argument.ValueKind == JsonValueKind.Array && argument.GetArrayLength() > 0);
                    foreach (JsonElement alternative in argument.EnumerateArray())
                        Check(alternative);
                    break;
                default:
                    Require(false);
                    break;
            }
        }

        foreach ((string lower, string upper) in new[] { ("minimum", "maximum"), ("minLength", "maxLength"), ("minItems", "maxItems") })
            if (node.TryGetProperty(lower, out JsonElement min) && node.TryGetProperty(upper, out JsonElement max))
                Require(min.GetInt32() <= max.GetInt32());
    }

    private static void CheckObject(JsonElement node)
    {
        Require(node.ValueKind == JsonValueKind.Object);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in node.EnumerateObject())
            Require(names.Add(property.Name));
    }

    private static void CheckLiteral(JsonElement value) =>
        Require(value.ValueKind is JsonValueKind.String or JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False
            || (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _)));

    private static bool Matches(JsonElement node, JsonElement value)
    {
        if (node.TryGetProperty("type", out JsonElement type) && !HasType(type.GetString()!, value))
            return false;
        if (node.TryGetProperty("const", out JsonElement constant) && !JsonElement.DeepEquals(constant, value))
            return false;
        if (node.TryGetProperty("enum", out JsonElement choices)
            && !choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(choice, value)))
            return false;
        if (node.TryGetProperty("anyOf", out JsonElement alternatives)
            && !alternatives.EnumerateArray().Any(alternative => Matches(alternative, value)))
            return false;

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                return MatchesObject(node, value);
            case JsonValueKind.Array:
                return MatchesArray(node, value);
            case JsonValueKind.String:
                string text = value.GetString()!;
                int length = 0;
                foreach (Rune rune in text.EnumerateRunes())
                    length++;
                return InRange(node, "minLength", "maxLength", length)
                    && (!node.TryGetProperty("pattern", out JsonElement pattern)
                        || Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                            TimeSpan.FromMilliseconds(100)));
            case JsonValueKind.Number:
                return value.TryGetInt32(out int number) && InRange(node, "minimum", "maximum", number);
            default:
                return true;
        }
    }

    private static bool HasType(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false
    };

    private static bool MatchesObject(JsonElement node, JsonElement value)
    {
        if (node.TryGetProperty("required", out JsonElement required))
            foreach (JsonElement name in required.EnumerateArray())
                if (!value.TryGetProperty(name.GetString()!, out _))
                    return false;

        bool hasProperties = node.TryGetProperty("properties", out JsonElement properties);
        bool allowAdditional = !node.TryGetProperty("additionalProperties", out JsonElement additional)
            || additional.GetBoolean();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
                return false;
            if (hasProperties && properties.TryGetProperty(property.Name, out JsonElement child))
            {
                if (!Matches(child, property.Value))
                    return false;
            }
            else if (!allowAdditional)
                return false;
        }

        return true;
    }

    private static bool MatchesArray(JsonElement node, JsonElement value)
    {
        if (!InRange(node, "minItems", "maxItems", value.GetArrayLength()))
            return false;
        if (node.TryGetProperty("items", out JsonElement items)
            && !value.EnumerateArray().All(item => Matches(items, item)))
            return false;
        return !node.TryGetProperty("uniqueItems", out JsonElement unique)
            || !unique.GetBoolean() || Distinct(value.EnumerateArray().ToArray());
    }

    private static bool Distinct(JsonElement[] values)
    {
        for (int i = 0; i < values.Length; i++)
            for (int j = 0; j < i; j++)
                if (JsonElement.DeepEquals(values[i], values[j]))
                    return false;
        return true;
    }

    private static bool InRange(JsonElement node, string lower, string upper, int value) =>
        (!node.TryGetProperty(lower, out JsonElement min) || value >= min.GetInt32())
        && (!node.TryGetProperty(upper, out JsonElement max) || value <= max.GetInt32());

    private static void Require(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("ORDER_SCHEMA_UNSUPPORTED");
    }
}
