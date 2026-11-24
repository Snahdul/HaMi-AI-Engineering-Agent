using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>
/// Fail-closed JSON envelope for the two-file actor slice. File paths are
/// chosen by the reviewed DevelopmentContract, never by GitHub or the model.
/// </summary>
internal static class DevelopmentCandidateFiles
{
    internal sealed record CandidateFile(string Path, string Content);

    internal static IReadOnlyDictionary<string, string> Parse(
        DevelopmentAgentContract contract, string payload)
    {
        if (!contract.IsPair || contract.ReviewedScope is null
            || string.IsNullOrWhiteSpace(payload) || payload.Length > 16 * 1024)
            throw new InvalidOperationException("Unreviewed multi-file candidate.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Multi-file response must be a JSON object.");
            RequireExactProperties(root, "files");
            JsonElement files = root.GetProperty("files");
            string[] allowed = contract.GeneratedPaths;
            if (files.ValueKind != JsonValueKind.Array
                || files.GetArrayLength() != allowed.Length)
                throw new InvalidOperationException("The candidate must contain exactly the allowed files.");

            var output = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonElement file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Candidate entry must be an object.");
                RequireExactProperties(file, "path", "content");
                string path = file.GetProperty("path").GetString() ?? "";
                string source = file.GetProperty("content").GetString() ?? "";
                if (!allowed.Contains(path, StringComparer.Ordinal)
                    || string.IsNullOrWhiteSpace(source)
                    || source.Length > 8 * 1024
                    || source.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
                    || !output.TryAdd(path, source))
                    throw new InvalidOperationException("Candidate contains an unknown, repeated or invalid file.");
            }
            if (output.Count != allowed.Length
                || allowed.Any(path => !output.ContainsKey(path)))
                throw new InvalidOperationException("Candidate omitted an approved file.");
            return output;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Candidate does not contain valid structured JSON.");
        }
    }

    private static void RequireExactProperties(JsonElement element, params string[] expected)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!expected.Contains(property.Name, StringComparer.Ordinal)
                || !seen.Add(property.Name))
                throw new InvalidOperationException("Unknown or duplicate JSON property.");
        if (seen.Count != expected.Length)
            throw new InvalidOperationException("Missing required JSON property.");
        foreach (string name in expected)
            if (!seen.Contains(name))
                throw new InvalidOperationException("Missing required JSON property.");
    }

    internal static string Scripted(bool compileError)
    {
        string calculator = compileError
            ? "namespace HaMi.Agentic.Cli.Features.Development; internal static class PairCalculator "
                + "{ internal static int Clamp(int value, int min, int max) => UnknownIdentifier; }"
            : "namespace HaMi.Agentic.Cli.Features.Development; internal static class PairCalculator "
                + "{ internal static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max); }";
        const string formatter = "namespace HaMi.Agentic.Cli.Features.Development; "
            + "internal static class PairFormatter { internal static string Format(int clamped, int min, int max) => "
            + "clamped.ToString(System.Globalization.CultureInfo.InvariantCulture) + "
            + "\" [\" + min.ToString(System.Globalization.CultureInfo.InvariantCulture) + "
            + "\",\" + max.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"]\"; }";
        return JsonSerializer.Serialize(new
        {
            files = new[]
            {
                new { path = DevelopmentAgentContract.PairFirst, content = calculator },
                new { path = DevelopmentAgentContract.PairSecond, content = formatter }
            }
        });
    }

    internal static void SelfTest(DevelopmentAgentContract contract)
    {
        if (!contract.IsPair)
            throw new InvalidOperationException("Expected the reviewed pair contract.");

        string valid = Scripted(compileError: false);
        var parsed = Parse(contract, valid);
        if (parsed.Count != 2
            || !parsed.ContainsKey(DevelopmentAgentContract.PairFirst)
            || !parsed.ContainsKey(DevelopmentAgentContract.PairSecond))
            throw new InvalidOperationException("Two-file response was rejected.");

        string first = DevelopmentAgentContract.PairFirst;
        string second = DevelopmentAgentContract.PairSecond;
        string[] invalid =
        [
            "{}",
            "{\"files\":[]}",
            "{\"files\":[{\"path\":\"../escape.cs\",\"content\":\"code\"},"
                + "{\"path\":\"" + second + "\",\"content\":\"code\"}]}",
            "{\"files\":[{\"path\":\"" + first + "\",\"content\":\"code\"},"
                + "{\"path\":\"" + first + "\",\"content\":\"code\"}]}",
            "{\"files\":[{\"path\":\"" + first + "\",\"content\":\"code\"}]}",
            "{\"files\":[{\"path\":\"" + first + "\",\"content\":\"code\"},"
                + "{\"path\":\"" + second + "\",\"content\":\"\"}]}",
            valid[..^1] + ",\"extra\":true}",
            valid.Replace("\"content\":", "\"content\":\"override\",\"content\":", StringComparison.Ordinal),
            new string('x', 17 * 1024)
        ];
        foreach (string payload in invalid)
        {
            try { _ = Parse(contract, payload); }
            catch (InvalidOperationException) { continue; }
            throw new InvalidOperationException("Invalid multi-file candidate was accepted.");
        }
    }
}
