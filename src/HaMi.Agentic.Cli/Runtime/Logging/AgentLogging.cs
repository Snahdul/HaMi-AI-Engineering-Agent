using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;
using HaMi.Agentic.Cli.Features.Coding;

namespace HaMi.Agentic.Cli.Runtime.Logging;

/// <summary>Versioned operational logging settings, separate from the execution policy.</summary>
internal sealed class AgentLoggingSettings
{
    public const string RelativePath = "config/logging.json";
    private static readonly Regex SafeName =
        new("^[A-Za-z0-9][A-Za-z0-9_-]{0,48}$", RegexOptions.CultureInvariant);

    public int Version { get; set; }
    public bool LogSuccessfulSteps { get; set; }
    public bool LogDecisionSummaries { get; set; } = true;
    public string MinimumLevel { get; set; } = "Information";
    public string RelativeDirectory { get; set; } = "hami-agent-logs";
    public long FileSizeLimitBytes { get; set; }
    public int RetainedFileCountLimit { get; set; }

    public LogEventLevel SerilogMinimumLevel => MinimumLevel switch
    {
        "Debug" => LogEventLevel.Debug,
        "Information" => LogEventLevel.Information,
        "Warning" => LogEventLevel.Warning,
        "Error" => LogEventLevel.Error,
        _ => throw new InvalidOperationException("Unrecognized logging level.")
    };

    public static AgentLoggingSettings Load(string repository)
    {
        string path = Path.Combine(Path.GetFullPath(repository),
            RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new InvalidOperationException("Required logging configuration is missing.");
        return Parse(File.ReadAllText(path));
    }

    public static AgentLoggingSettings Parse(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();
        AgentLoggingSettings settings = configuration.Get<AgentLoggingSettings>(
            options => options.ErrorOnUnknownConfiguration = true)
            ?? throw new InvalidOperationException("Logging settings must be a JSON object.");
        settings.Validate();
        return settings;
    }

    private void Validate()
    {
        if (Version != 1)
            throw new InvalidOperationException("Unsupported logging configuration version.");
        if (RelativeDirectory is null || !SafeName.IsMatch(RelativeDirectory))
            throw new InvalidOperationException("Logging directory must be one safe relative name.");
        if (MinimumLevel is not ("Debug" or "Information" or "Warning" or "Error"))
            throw new InvalidOperationException("Unsupported minimum log level.");
        if (FileSizeLimitBytes is < 65_536 or > 10_485_760
            || RetainedFileCountLimit is < 1 or > 30)
            throw new InvalidOperationException("Logging rotation or retention exceeds safe limits.");
    }
}

/// <summary>
/// Creates a Microsoft ILoggerFactory with a Serilog rolling compact-JSON file
/// provider. No ambient/global Log.Logger, no dynamic sink type activation.
/// </summary>
internal sealed class AgentLoggingSession : IDisposable
{
    private readonly ILoggerFactory _factory;
    public AgentLoggingSettings Settings { get; }
    public string LogDirectory { get; }

    private AgentLoggingSession(AgentLoggingSettings settings, string root)
    {
        Settings = settings;
        LogDirectory = Path.Combine(Path.GetFullPath(root), settings.RelativeDirectory);
        Directory.CreateDirectory(LogDirectory);

        string file = Path.Combine(LogDirectory, "agent-.clef");
        Serilog.ILogger sink = new Serilog.LoggerConfiguration()
            .MinimumLevel.Is(settings.SerilogMinimumLevel)
            .Enrich.FromLogContext()
            .WriteTo.File(new CompactJsonFormatter(), file,
                rollingInterval: Serilog.RollingInterval.Day,
                fileSizeLimitBytes: settings.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: settings.RetainedFileCountLimit)
            .CreateLogger();

        _factory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new SerilogLoggerProvider(sink, dispose: true));
        });
    }

    public static AgentLoggingSession Create(string repository) =>
        new(AgentLoggingSettings.Load(repository), RuntimeSettings.GetWorktreeRoot(repository));

    internal static AgentLoggingSession CreateForTest(
        AgentLoggingSettings settings, string temporaryRoot) =>
        new(settings, temporaryRoot);

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string category) =>
        _factory.CreateLogger(category);

    public void Dispose() => _factory.Dispose();
}

/// <summary>
/// Offline behavior tests for the two independent switches, structured event
/// fields, sanitization, path restrictions and fail-closed configuration.
/// </summary>
internal static class AgentLoggingSelfTest
{
    public static void Run(string repository)
    {
        AgentLoggingSettings defaults = AgentLoggingSettings.Load(repository);
        if (defaults.LogSuccessfulSteps || !defaults.LogDecisionSummaries
            || defaults.MinimumLevel != "Information")
            throw new InvalidOperationException("Logging default flags differ from the agreed policy.");

        string original = File.ReadAllText(Path.Combine(repository,
            AgentLoggingSettings.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        string temp = Path.Combine(Path.GetTempPath(),
            "hami-agent-logging-selftest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            string baseline = Emit(defaults, Path.Combine(temp, "default"));
            Ensure(baseline.Contains("ExpressionRejected", StringComparison.Ordinal)
                && baseline.Contains("CodingRunFailed", StringComparison.Ordinal)
                && baseline.Contains("DecisionRecorded", StringComparison.Ordinal)
                && !baseline.Contains("AttemptSucceeded", StringComparison.Ordinal)
                && !baseline.Contains("RunSucceeded", StringComparison.Ordinal),
                "Default must retain errors and decisions, but suppress successful steps.");

            AgentLoggingSettings successes = AgentLoggingSettings.Parse(
                original.Replace("\"logSuccessfulSteps\": false",
                    "\"logSuccessfulSteps\": true", StringComparison.Ordinal));
            string successText = Emit(successes, Path.Combine(temp, "successes"));
            Ensure(successText.Contains("AttemptSucceeded", StringComparison.Ordinal)
                && successText.Contains("DecisionRecorded", StringComparison.Ordinal),
                "Success logging did not enable independently of decision logging.");

            AgentLoggingSettings noDecisions = AgentLoggingSettings.Parse(
                original.Replace("\"logDecisionSummaries\": true",
                    "\"logDecisionSummaries\": false", StringComparison.Ordinal));
            string noDecisionText = Emit(noDecisions, Path.Combine(temp, "no-decisions"));
            Ensure(noDecisionText.Contains("ExpressionRejected", StringComparison.Ordinal)
                && !noDecisionText.Contains("DecisionRecorded", StringComparison.Ordinal),
                "Disabling decisions must not suppress failures or warnings.");

            AgentLoggingSettings onlySuccesses = AgentLoggingSettings.Parse(
                original.Replace("\"logSuccessfulSteps\": false",
                    "\"logSuccessfulSteps\": true", StringComparison.Ordinal)
                .Replace("\"logDecisionSummaries\": true",
                    "\"logDecisionSummaries\": false", StringComparison.Ordinal));
            string successOnly = Emit(onlySuccesses, Path.Combine(temp, "success-only"));
            Ensure(successOnly.Contains("AttemptSucceeded", StringComparison.Ordinal)
                && !successOnly.Contains("DecisionRecorded", StringComparison.Ordinal)
                && successOnly.Contains("ExpressionRejected", StringComparison.Ordinal),
                "Success=true and decisions=false must work independently.");

            TestRotation(original, Path.Combine(temp, "rotation"));

            foreach (string bad in new[]
            {
                original.Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal),
                original.Replace("\"minimumLevel\": \"Information\"",
                    "\"minimumLevel\": \"All\"", StringComparison.Ordinal),
                original.Replace("\"relativeDirectory\": \"hami-agent-logs\"",
                    "\"relativeDirectory\": \"../escape\"", StringComparison.Ordinal),
                original.Replace("\"relativeDirectory\": \"hami-agent-logs\"",
                    "\"relativeDirectory\": \"/tmp/escape\"", StringComparison.Ordinal),
                original.Replace("\"relativeDirectory\": \"hami-agent-logs\"",
                    "\"relativeDirectory\": \"C:\\\\Temp\"", StringComparison.Ordinal),
                original.Replace("\"fileSizeLimitBytes\": 1048576",
                    "\"fileSizeLimitBytes\": 999999999", StringComparison.Ordinal),
                original.Replace("\"retainedFileCountLimit\": 7",
                    "\"retainedFileCountLimit\": 999", StringComparison.Ordinal),
                original.Replace("\"version\": 1",
                    "\"version\": 1,\n  \"unknownField\": 1", StringComparison.Ordinal)
            })
            {
                try { _ = AgentLoggingSettings.Parse(bad); }
                catch (InvalidOperationException) { continue; }
                catch (FormatException) { continue; }
                throw new InvalidOperationException("Invalid logging configuration was accepted.");
            }

            Console.WriteLine("Structured logging self-test passed: "
                + "defaults success=false/decisions=true, all four toggle combinations, "
                + "correlated failure events, configured rollover/retention and safe paths.");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static void TestRotation(string configuration, string temp)
    {
        AgentLoggingSettings smallFiles = AgentLoggingSettings.Parse(
            configuration.Replace("\"fileSizeLimitBytes\": 1048576",
                    "\"fileSizeLimitBytes\": 65536", StringComparison.Ordinal)
                .Replace("\"retainedFileCountLimit\": 7",
                    "\"retainedFileCountLimit\": 2", StringComparison.Ordinal));

        using (var session = AgentLoggingSession.CreateForTest(smallFiles, temp))
        {
            var logger = session.CreateLogger("HaMi.Agentic.Cli.Features.Coding");
            using var scope = logger.BeginScope(new Dictionary<string, object>
            {
                ["RunId"] = "selftest-rotation-001"
            });

            // Many short structured events (not one oversized diagnostic) must
            // trigger size rollover; the retention policy keeps at most 2 files.
            for (int attempt = 1; attempt <= 2400; attempt++)
                CodingLogEvents.ExpressionRejected(logger, attempt, "invalid_expression");
        }

        string[] files = Directory.GetFiles(Path.Combine(temp,
            smallFiles.RelativeDirectory), "agent-*.clef");
        Ensure(files.Length == 2 && files.All(f => new FileInfo(f).Length
                <= smallFiles.FileSizeLimitBytes + 4096),
            "Serilog size rollover and retention did not enforce the configured limits.");
    }

    private static string Emit(AgentLoggingSettings settings, string temp)
    {
        Directory.CreateDirectory(temp);
        using (var session = AgentLoggingSession.CreateForTest(settings, temp))
        {
            var logger = session.CreateLogger("HaMi.Agentic.Cli.Features.Coding");
            using var runScope = logger.BeginScope(new Dictionary<string, object>
            {
                ["RunId"] = "selftest-logging-001"
            });

            CodingLogEvents.ExpressionRejected(logger, 1, "invalid_expression");
            // Exercise a terminal failure *after* a prior event while the same
            // correlation scope is still active, as in the Coding command's catch.
            try
            {
                throw new OperationCanceledException("Untrusted message must not be logged");
            }
            catch (OperationCanceledException ex)
            {
                CodingLogEvents.RunFailed(logger, ex.GetType().Name);
            }
            if (settings.LogDecisionSummaries)
                CodingLogEvents.DecisionRecorded(logger, "CLAMP_BOUNDARY",
                    "Trusted fixed fixture", "Generate bounded Clamp expression",
                    "Policy forbids model tools and other write paths");
            if (settings.LogSuccessfulSteps)
            {
                CodingLogEvents.AttemptSucceeded(logger, 2);
                CodingLogEvents.RunSucceeded(logger, 2);
            }
        }

        string[] files = Directory.GetFiles(Path.Combine(temp, settings.RelativeDirectory),
            "agent-*.clef");
        Ensure(files.Length > 0, "No log file was emitted.");
        string all = string.Join("\n", files.SelectMany(File.ReadAllLines));
        foreach (string line in all.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using JsonDocument json = JsonDocument.Parse(line);
            Ensure(json.RootElement.TryGetProperty("@mt", out _)
                && json.RootElement.TryGetProperty("RunId", out var id)
                && id.GetString() == "selftest-logging-001",
                "Log event is not correlated compact JSON.");
        }
        Ensure(!all.Contains("OPENAI_API_KEY", StringComparison.Ordinal)
            && !all.Contains("promptSha256", StringComparison.Ordinal)
            && !all.Contains("secret", StringComparison.OrdinalIgnoreCase),
            "Logging unexpectedly contained a secret or raw prompt field.");
        return all;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
