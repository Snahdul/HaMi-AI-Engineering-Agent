using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace HaMi.Agentic.Cli.Features.Coding;

/// <summary>
/// Loads the bounded system/user Prompty asset. This intentionally supports only
/// static system/user messages and one required {{feedback}} placeholder, not the
/// full Prompty/Jinja2 runtime. Unsupported features fail closed.
/// </summary>
internal sealed class CodingPromptTemplate
{
    public const string RelativePath = "prompts/coding/ClampExpression.prompty";

    private static readonly Regex RoleMarker =
        new(@"(?m)^(system|user|assistant):[ \t]*$", RegexOptions.CultureInvariant);

    private CodingPromptTemplate(string system, string userTemplate)
    {
        System = system;
        UserTemplate = userTemplate;
    }

    private string System { get; }
    private string UserTemplate { get; }

    public static CodingPromptTemplate Load(string repository)
    {
        string path = Path.Combine(repository,
            RelativePath.Replace('/', Path.DirectorySeparatorChar));
        return Parse(File.ReadAllText(path));
    }

    public CodingPrompt Render(string? feedback) =>
        new(System, UserTemplate.Replace("{{feedback}}",
            string.IsNullOrEmpty(feedback) ? "No previous failed attempt." : feedback,
            StringComparison.Ordinal));

    private static CodingPromptTemplate Parse(string content)
    {
        if (content.Length is < 50 or > 16_384)
            throw new InvalidOperationException("Prompt asset is empty or exceeds 16 KiB.");

        string text = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            throw new InvalidOperationException("Prompt asset needs YAML frontmatter.");

        int end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException("Prompt asset has no closing YAML delimiter.");

        var document = new YamlStream();
        document.Load(new StringReader(text[4..end]));
        if (document.Documents.Count != 1
            || document.Documents[0].RootNode is not YamlMappingNode yaml)
            throw new InvalidOperationException("Prompt frontmatter must contain a YAML map.");

        // Runtime safety is configured in C#: no model connections, tools, paths
        // or execution settings may be silently introduced from a prompt asset.
        foreach (YamlNode key in yaml.Children.Keys)
            if (key is not YamlScalarNode { Value: "name" or "description" or "inputs" or "template" })
                throw new InvalidOperationException("Unsupported Prompty frontmatter property.");

        if (GetScalar(yaml, "name") != "clamp-expression")
            throw new InvalidOperationException("Unexpected prompt asset name.");

        YamlMappingNode template = GetMap(yaml, "template");
        if (GetScalar(GetMap(template, "format"), "kind") != "jinja2"
            || GetScalar(GetMap(template, "parser"), "kind") != "prompty")
            throw new InvalidOperationException("Unsupported template format.");

        string body = text[(end + "\n---\n".Length)..].Trim();
        MatchCollection roles = RoleMarker.Matches(body);
        if (roles.Count != 2 || roles[0].Value.Trim() != "system:"
            || roles[1].Value.Trim() != "user:"
            || roles[0].Index != 0)
            throw new InvalidOperationException("Only exactly one system and one user role are supported.");

        string system = body[roles[0].Length..roles[1].Index].Trim();
        string user = body[(roles[1].Index + roles[1].Length)..].Trim();
        if (string.IsNullOrEmpty(system) || string.IsNullOrEmpty(user)
            || system.Contains("{{", StringComparison.Ordinal)
            || !user.Contains("{{feedback}}", StringComparison.Ordinal))
            throw new InvalidOperationException("Missing text or required feedback placeholder.");

        string withoutFeedback = user.Replace("{{feedback}}", "", StringComparison.Ordinal);
        if (withoutFeedback.Contains("{{", StringComparison.Ordinal)
            || withoutFeedback.Contains("}}", StringComparison.Ordinal)
            || withoutFeedback.Contains("{%", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsupported template expression.");

        return new CodingPromptTemplate(system, user);
    }

    private static YamlMappingNode GetMap(YamlMappingNode map, string name) =>
        map.Children.TryGetValue(new YamlScalarNode(name), out YamlNode? value)
            && value is YamlMappingNode result
                ? result : throw new InvalidOperationException($"Missing YAML map: {name}");

    private static string? GetScalar(YamlMappingNode map, string name) =>
        map.Children.TryGetValue(new YamlScalarNode(name), out YamlNode? value)
            && value is YamlScalarNode scalar
                ? scalar.Value : null;

    public static void SelfTest(string repository)
    {
        CodingPromptTemplate asset = Load(repository);
        CodingPrompt first = asset.Render(null);
        CodingPrompt second = asset.Render("Fixed tests failed: expected 10 but got 11");

        if (string.IsNullOrWhiteSpace(first.System)
            || !first.User.Contains("No previous failed attempt.", StringComparison.Ordinal)
            || !second.User.Contains("Fixed tests failed:", StringComparison.Ordinal)
            || first.System.Contains("{{", StringComparison.Ordinal)
            || second.User.Contains("{{feedback}}", StringComparison.Ordinal))
            throw new InvalidOperationException("Prompty asset was not loaded and rendered correctly.");

        string original = File.ReadAllText(Path.Combine(repository,
            RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        foreach (string modified in new[]
        {
            original.Replace("name: clamp-expression", "name: other-task", StringComparison.Ordinal),
            original.Replace("{{feedback}}", "{{unknown}}", StringComparison.Ordinal),
            original.Replace("user:\n", "assistant:\n", StringComparison.Ordinal),
            original.Replace("template:\n", "tools: []\ntemplate:\n", StringComparison.Ordinal)
        })
        {
            try
            {
                _ = Parse(modified);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            throw new InvalidOperationException("Invalid Prompty asset was accepted.");
        }

        Console.WriteLine("Prompty asset self-test passed: external system/user prompt, "
            + "feedback rendering and strict asset validation.");
    }
}

internal sealed record CodingPrompt(string System, string User);
