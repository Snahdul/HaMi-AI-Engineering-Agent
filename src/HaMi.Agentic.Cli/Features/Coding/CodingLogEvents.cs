using Microsoft.Extensions.Logging;

namespace HaMi.Agentic.Cli.Features.Coding;

/// <summary>
/// Compile-time-generated, strongly typed logging events for the Coding slice.
/// Every message is deliberately short. Never pass raw model answers, prompts,
/// compiler output, API tokens, repository contents or exception messages.
/// </summary>
internal static partial class CodingLogEvents
{
    [LoggerMessage(EventId = 2400, Level = LogLevel.Information,
        Message = "DecisionRecorded {DecisionCode}: assumption={Assumption}; action={Action}; rationale={Rationale}")]
    internal static partial void DecisionRecorded(ILogger logger,
        string decisionCode, string assumption, string action, string rationale);

    [LoggerMessage(EventId = 2410, Level = LogLevel.Warning,
        Message = "ExpressionRejected attempt={Attempt} reason={FailureCode}")]
    internal static partial void ExpressionRejected(ILogger logger, int attempt, string failureCode);

    [LoggerMessage(EventId = 2411, Level = LogLevel.Warning,
        Message = "CompilationFailed attempt={Attempt} reason={FailureCode}")]
    internal static partial void CompilationFailed(ILogger logger, int attempt, string failureCode);

    [LoggerMessage(EventId = 2412, Level = LogLevel.Warning,
        Message = "TestExecutionFailed attempt={Attempt} reason={FailureCode}")]
    internal static partial void TestsFailed(ILogger logger, int attempt, string failureCode);

    [LoggerMessage(EventId = 2413, Level = LogLevel.Error,
        Message = "CodingRunExhausted modelCalls={ModelCalls} reason=attempt_budget")]
    internal static partial void RunExhausted(ILogger logger, int modelCalls);

    [LoggerMessage(EventId = 2414, Level = LogLevel.Error,
        Message = "CodingRunFailed errorType={ErrorType}")]
    internal static partial void RunFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 2420, Level = LogLevel.Information,
        Message = "AttemptSucceeded attempt={Attempt}")]
    internal static partial void AttemptSucceeded(ILogger logger, int attempt);

    [LoggerMessage(EventId = 2421, Level = LogLevel.Information,
        Message = "RunSucceeded modelCalls={ModelCalls}")]
    internal static partial void RunSucceeded(ILogger logger, int modelCalls);
}
