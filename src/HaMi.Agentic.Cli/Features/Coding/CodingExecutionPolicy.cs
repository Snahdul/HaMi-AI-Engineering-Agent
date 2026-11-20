using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace HaMi.Agentic.Cli.Features.Coding;

/// <summary>
/// Strongly typed, versioned execution policy for this bounded coding slice.
/// Only the repository-owned JSON file can supply permissions; model prompts,
/// environment variables and API responses cannot override them.
/// </summary>
internal sealed class CodingExecutionPolicy
{
    public const string RelativePath = "policies/coding/clamp.policy.json";

    // Both supported coding tasks are approved in CodingTaskDefinition,
    // never granted implicitly by JSON, prompt text or model output.
    private const string SupportedWritePath = "samples/ClampKata/Clamp.cs";

    public int Version { get; set; }
    public string[] AllowedWritePaths { get; set; } = [];
    public string[] AllowedModelTools { get; set; } = [];
    public bool AllowShellCommands { get; set; }
    public bool AllowGitHubWrites { get; set; }
    public bool StoreModelResponses { get; set; }
    public int MaxModelCalls { get; set; }
    public int MaxOutputTokens { get; set; }
    public int ProcessTimeoutSeconds { get; set; }
    public int ApiTimeoutSeconds { get; set; }
    public int MaxFeedbackCharacters { get; set; }

    public string Sha256 { get; private set; } = "";
    public string AssetPath { get; private set; } = RelativePath;

    public string OnlyWritablePath => AllowedWritePaths.Single();

    public static CodingExecutionPolicy Load(
        string repository, CodingTaskDefinition? task = null)
    {
        task ??= CodingTaskDefinition.Clamp;
        string policyPath = task.PolicyAsset;
        string fullRepository = Path.GetFullPath(repository);
        string path = Path.Combine(fullRepository,
            policyPath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path))
            throw new InvalidOperationException($"Required coding policy missing: {policyPath}");

        // Deliberately only one trusted provider. Do NOT add environment, command-line,
        // prompt or model response configuration providers here: no permission escalation.
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(fullRepository)
            .AddJsonFile(policyPath, optional: false, reloadOnChange: false)
            .Build();

        CodingExecutionPolicy policy = configuration.Get<CodingExecutionPolicy>(
            options => options.ErrorOnUnknownConfiguration = true)
            ?? throw new InvalidOperationException("Coding policy must be a JSON object.");

        policy.Validate(task);
        policy.AssetPath = policyPath;
        policy.Sha256 = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(path))))
            .ToLowerInvariant();

        return policy;
    }

    private void Validate(CodingTaskDefinition task)
    {
        if (Version != 1)
            throw new InvalidOperationException("Unsupported coding policy version.");

        if (AllowedWritePaths is not { Length: 1 }
            || !string.Equals(AllowedWritePaths[0], task.TargetPath, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Coding policy path must match exactly the compile-time selected task.");

        if (AllowedModelTools is not { Length: 0 }
            || AllowShellCommands || AllowGitHubWrites || StoreModelResponses)
            throw new InvalidOperationException(
                "Unsupported capability: model tools, shell/GitHub writes and stored model responses are forbidden.");

        // The policy can lower budgets, not silently exceed the explicitly reviewed MVP ceiling.
        if (MaxModelCalls is < 1 or > 3
            || MaxOutputTokens is < 128 or > 1000
            || ProcessTimeoutSeconds is < 10 or > 110
            || ApiTimeoutSeconds is < 10 or > 240
            || MaxFeedbackCharacters is < 128 or > 4096)
            throw new InvalidOperationException("Coding policy exceeds supported resource limits.");
    }

    public static void SelfTest(string repository)
    {
        CodingExecutionPolicy actual = Load(repository);
        if (actual.MaxModelCalls != 3
            || actual.MaxOutputTokens != 1000
            || actual.OnlyWritablePath != SupportedWritePath
            || actual.MaxFeedbackCharacters != 1600
            || actual.Sha256.Length != 64)
            throw new InvalidOperationException("Default coding policy does not match the test fixture.");

        CodingExecutionPolicy retry = Load(repository, CodingTaskDefinition.RetryBudget);
        if (retry.OnlyWritablePath != CodingTaskDefinition.RetryBudget.TargetPath
            || retry.AssetPath != CodingTaskDefinition.RetryBudget.PolicyAsset
            || retry.Sha256.Length != 64)
            throw new InvalidOperationException("Retry task policy is not the expected narrow allowlist.");

        string temp = Path.Combine(Path.GetTempPath(), "hami-coding-policy-test",
            Guid.NewGuid().ToString("N"));
        string testPolicyDir = Path.Combine(temp, "policies", "coding");
        Directory.CreateDirectory(testPolicyDir);

        try
        {
            string sourcePath = Path.Combine(repository,
                RelativePath.Replace('/', Path.DirectorySeparatorChar));
            string baseline = File.ReadAllText(sourcePath);
            string target = Path.Combine(testPolicyDir, "clamp.policy.json");

            File.WriteAllText(target, baseline);
            if (Load(temp).Sha256 != actual.Sha256)
                throw new InvalidOperationException("Policy SHA-256 is not deterministic.");

            // A policy owner may tighten resource budgets without editing C#.
            string tighter = baseline
                .Replace("\"maxModelCalls\": 3", "\"maxModelCalls\": 2", StringComparison.Ordinal)
                .Replace("\"maxOutputTokens\": 1000", "\"maxOutputTokens\": 500", StringComparison.Ordinal)
                .Replace("\"processTimeoutSeconds\": 110", "\"processTimeoutSeconds\": 30", StringComparison.Ordinal)
                .Replace("\"maxFeedbackCharacters\": 1600", "\"maxFeedbackCharacters\": 512", StringComparison.Ordinal);
            File.WriteAllText(target, tighter);
            CodingExecutionPolicy reduced = Load(temp);
            if (reduced.MaxModelCalls != 2 || reduced.MaxOutputTokens != 500
                || reduced.ProcessTimeoutSeconds != 30
                || reduced.MaxFeedbackCharacters != 512)
                throw new InvalidOperationException("Tighter declarative policy was not applied.");

            // Fail closed when a human typo or accidental widening is introduced.
            foreach (string malformed in new[]
            {
                baseline.Replace("\"maxModelCalls\": 3", "\"maxModelCalls\": 99", StringComparison.Ordinal),
                baseline.Replace("\"allowedModelTools\": []",
                    "\"allowedModelTools\": [\"shell\"]", StringComparison.Ordinal),
                baseline.Replace("\"allowShellCommands\": false",
                    "\"allowShellCommands\": true", StringComparison.Ordinal),
                baseline.Replace("\"allowGitHubWrites\": false",
                    "\"allowGitHubWrites\": true", StringComparison.Ordinal),
                baseline.Replace("\"storeModelResponses\": false",
                    "\"storeModelResponses\": true", StringComparison.Ordinal),
                baseline.Replace(SupportedWritePath, "../README.md", StringComparison.Ordinal),
                baseline.Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal),
                baseline.Replace("\"apiTimeoutSeconds\": 240",
                    "\"apiTimeoutSeconds\": 241", StringComparison.Ordinal),
                baseline.Replace("\"maxFeedbackCharacters\": 1600",
                    "\"maxFeedbackCharacters\": 4097", StringComparison.Ordinal),
                baseline.Replace("\"maxFeedbackCharacters\": 1600",
                    "\"maxFeedbackCharacters\": 0", StringComparison.Ordinal),
                baseline.Replace("\"version\": 1",
                    "\"version\": 1,\n  \"unexpectedSetting\": true", StringComparison.Ordinal)
            })
            {
                File.WriteAllText(target, malformed);
                try
                {
                    _ = Load(temp);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                catch (FormatException)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    "Invalid or overly permissive coding policy was accepted.");
            }

            Console.WriteLine("Execution policy self-test passed: strict JSON binding, "
                + "capability allowlist, resource ceilings and SHA-256 audit.");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
