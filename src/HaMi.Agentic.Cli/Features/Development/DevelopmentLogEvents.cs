using Microsoft.Extensions.Logging;

namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>
/// Auditable, bounded lifecycle events. Never pass raw prompts, model output,
/// compiler text, exception messages or secrets into these structured fields.
/// </summary>
internal static partial class DevelopmentLogEvents
{
    [LoggerMessage(EventId = 3600, Level = LogLevel.Information,
        Message = "JobRequested job={JobId} repo={Repository} results={ResultsDirectory} generation={GenerationMode} model={Model} contract={ContractPath}")]
    internal static partial void JobRequested(ILogger logger, string jobId,
        string repository, string resultsDirectory, string generationMode, string model, string contractPath);

    [LoggerMessage(EventId = 3601, Level = LogLevel.Information,
        Message = "PreflightAccepted job={JobId} task={TaskId} project={Project} maxAttempts={MaxAttempts} guestIsolation=microvm")]
    internal static partial void PreflightAccepted(ILogger logger, string jobId,
        string taskId, string project, int maxAttempts);

    [LoggerMessage(EventId = 3602, Level = LogLevel.Information,
        Message = "GenerationStarted job={JobId} attempt={Attempt} correction={HasFeedback}")]
    internal static partial void GenerationStarted(ILogger logger, string jobId,
        int attempt, bool hasFeedback);

    [LoggerMessage(EventId = 3603, Level = LogLevel.Information,
        Message = "GenerationFinished job={JobId} attempt={Attempt} chars={CharacterCount} durationMs={ElapsedMs}")]
    internal static partial void GenerationFinished(ILogger logger, string jobId,
        int attempt, int characterCount, long elapsedMs);

    [LoggerMessage(EventId = 3604, Level = LogLevel.Information,
        Message = "SandboxStarted job={JobId} attempt={Attempt} isolation=fresh-microvm")]
    internal static partial void SandboxStarted(ILogger logger, string jobId, int attempt);

    [LoggerMessage(EventId = 3605, Level = LogLevel.Information,
        Message = "SandboxFinished job={JobId} attempt={Attempt} classification={Classification} restore={RestoreExit} build={BuildExit} run={RunExit} timeout={TimedOut} cleanup={CleanupSucceeded} durationMs={ElapsedMs}")]
    internal static partial void SandboxFinished(ILogger logger, string jobId, int attempt,
        string classification, int restoreExit, int buildExit, int runExit,
        bool timedOut, bool cleanupSucceeded, long elapsedMs);

    [LoggerMessage(EventId = 3606, Level = LogLevel.Information,
        Message = "DecisionRecorded job={JobId} attempt={Attempt} decision={DecisionCode} reason={ReasonCode}")]
    internal static partial void DecisionRecorded(ILogger logger, string jobId,
        int attempt, string decisionCode, string reasonCode);

    [LoggerMessage(EventId = 3607, Level = LogLevel.Warning,
        Message = "AttemptFailed job={JobId} attempt={Attempt} classification={Classification} diagnostic={DiagnosticCode}; correction may be possible")]
    internal static partial void AttemptFailed(ILogger logger, string jobId,
        int attempt, string classification, string diagnosticCode);

    [LoggerMessage(EventId = 3608, Level = LogLevel.Error,
        Message = "JobFailed job={JobId} stage={Stage} reason={ReasonCode} exceptionType={ExceptionType} action={Action}")]
    internal static partial void JobFailed(ILogger logger, string jobId, string stage,
        string reasonCode, string exceptionType, string action);

    [LoggerMessage(EventId = 3609, Level = LogLevel.Information,
        Message = "JobFinished job={JobId} status={Status} generatorCalls={GeneratorCalls} attempts={AttemptCount} report={ReportPath}")]
    internal static partial void JobFinished(ILogger logger, string jobId,
        string status, int generatorCalls, int attemptCount, string reportPath);

    [LoggerMessage(EventId = 3611, Level = LogLevel.Information,
        Message = "SandboxPhase job={JobId} attempt={Attempt}: {Description}; sandbox={SandboxName} phase={PhaseCode} elapsedMs={ElapsedMs} exitCode={ExitCode}")]
    internal static partial void SandboxPhase(ILogger logger, string jobId, int attempt,
        string description, string sandboxName, string phaseCode, long elapsedMs, int exitCode);

    [LoggerMessage(EventId = 3612, Level = LogLevel.Information,
        Message = "IssueBound job={JobId} issue={IssueNumber} source=approved-github-issue scope=reviewed-contract")]
    internal static partial void IssueBound(ILogger logger, string jobId, int issueNumber);

    [LoggerMessage(EventId = 3613, Level = LogLevel.Information,
        Message = "IssueJournalPublished job={JobId} issue={IssueNumber} phase={Phase}")]
    internal static partial void IssueJournalPublished(ILogger logger, string jobId, int issueNumber, string phase);

    [LoggerMessage(EventId = 3610, Level = LogLevel.Debug,
        Message = "ActorMessageIgnored job={JobId} attempt={Attempt} stage={Stage} reason=stale-or-duplicate")]
    internal static partial void MessageIgnored(ILogger logger, string jobId, int attempt, string stage);
}
