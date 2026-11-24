using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>
/// Only the host may choose a source file or patch location. The model may
/// provide precisely one bounded C# expression in a fixed JSON envelope.
/// </summary>
internal static class DevelopmentProductionExpression
{
    internal static string Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 2048)
            throw new InvalidOperationException("Production model response is absent or oversized.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Expected a production expression object.");
            JsonProperty[] properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != "expression"
                || properties[0].Value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Model may specify only one expression.");
            string expression = properties[0].Value.GetString() ?? "";
            DevelopmentProductionVerifier.ValidateExpression(expression);
            return expression.Trim();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Production response is not valid structured JSON.");
        }
    }

    internal static string Serialize(string expression) =>
        JsonSerializer.Serialize(new { expression });

    internal static void SelfTest()
    {
        string good = Serialize("attempt > 0 && maxCalls > 0 && attempt < maxCalls");
        if (Parse(good) != "attempt > 0 && maxCalls > 0 && attempt < maxCalls")
            throw new InvalidOperationException("Valid production expression rejected.");

        foreach (string bad in new[]
        {
            "", "not JSON", "[]", "{}", "{\"expression\":null}",
            "{\"expression\":\"true\",\"expression\":\"false\"}",
            "{\"expression\":\"true\",\"path\":\"../unsafe.cs\"}",
            Serialize("Environment.Exit(0) == 0"),
            Serialize("attempt < maxCalls; Console.WriteLine(1)"),
            Serialize("attempt < maxCalls\n"),
            Serialize("unexpectedVariable"),
            Serialize(new string('a', 400)),
            new string('x', 3000)
        })
        {
            try { _ = Parse(bad); }
            catch (InvalidOperationException) { continue; }
            throw new InvalidOperationException("Unsafe production expression was accepted.");
        }
    }
}
