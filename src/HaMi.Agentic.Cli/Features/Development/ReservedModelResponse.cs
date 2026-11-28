using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace HaMi.Agentic.Cli.Features.Development;

// Local, closed response classification; never a journal completion or retry grant.
internal sealed record ModelResponseCompleted;
internal sealed record ModelResponseIncomplete(string Reason);
internal sealed record ModelResponseFailed;
internal sealed record ModelResponseCancelled;
internal sealed record ModelResponseNonTerminal(string Status);
internal sealed record ModelResponseUnknown(string Reason);
internal union ModelResponseOutcome(ModelResponseCompleted, ModelResponseIncomplete,
    ModelResponseFailed, ModelResponseCancelled, ModelResponseNonTerminal, ModelResponseUnknown);

internal static class ReservedModelResponse
{
    internal static void RequireCompleted(ChatResponse response, bool requireNativeResponse)
    {
        ModelResponseOutcome outcome = Classify(response, requireNativeResponse);
        (string Status, string? Error) decision = outcome switch
        {
            ModelResponseCompleted => ("completed", null),
            ModelResponseIncomplete incomplete => ("incomplete/" + incomplete.Reason, "ORDER_MODEL_RESPONSE_INCOMPLETE"),
            ModelResponseFailed => ("failed", "ORDER_MODEL_RESPONSE_FAILED"),
            ModelResponseCancelled => ("cancelled", "ORDER_MODEL_RESPONSE_CANCELLED"),
            ModelResponseNonTerminal nonTerminal => (nonTerminal.Status, "ORDER_MODEL_RESPONSE_NONTERMINAL"),
            ModelResponseUnknown unknown => ("unknown/" + unknown.Reason, "ORDER_MODEL_RESPONSE_UNKNOWN"),
            null => throw new InvalidOperationException("ORDER_MODEL_RESPONSE_UNKNOWN")
        };

        // Only fixed classifications and numeric usage. No provider payload,
        // refusal/error messages, prompts, reasoning, credentials or response ID.
        Console.WriteLine(FormattableString.Invariant(
            $"MODEL_RESPONSE: status={decision.Status}; inputTokens={response.Usage?.InputTokenCount}; outputTokens={response.Usage?.OutputTokenCount}; totalTokens={response.Usage?.TotalTokenCount}"));
        if (decision.Error is not null)
            throw new InvalidOperationException(decision.Error);
    }

#pragma warning disable OPENAI001
    private static ModelResponseOutcome Classify(ChatResponse response, bool requireNativeResponse)
    {
        if (response.RawRepresentation is not ResponseResult native)
        {
            // Explicit opt-out is used solely by existing deterministic IChatClient
            // fixtures. Production reserved dispatch defaults to native proof.
            if (requireNativeResponse || response.RawRepresentation is not null)
                return new ModelResponseUnknown("native-status-missing");
            if (response.FinishReason == ChatFinishReason.Stop)
                return new ModelResponseCompleted();
            if (response.FinishReason == ChatFinishReason.Length)
                return new ModelResponseIncomplete("token-limit");
            if (response.FinishReason == ChatFinishReason.ContentFilter)
                return new ModelResponseIncomplete("content-filter");
            return new ModelResponseUnknown("finish-reason");
        }

        // Compare SDK enum values, not JSON wire strings or ToString().
        // An unknown enum value is rejected at this external boundary.
        return native.Status switch
        {
            ResponseStatus.Completed => Completed(response, native),
            ResponseStatus.Incomplete => new ModelResponseIncomplete(IncompleteReason(response)),
            ResponseStatus.Failed => new ModelResponseFailed(),
            ResponseStatus.Cancelled => new ModelResponseCancelled(),
            ResponseStatus.Queued => new ModelResponseNonTerminal("queued"),
            ResponseStatus.InProgress => new ModelResponseNonTerminal("in_progress"),
            null => new ModelResponseUnknown("native-status-missing"),
            _ => new ModelResponseUnknown("native-status-unrecognized")
        };
    }

    private static ModelResponseOutcome Completed(ChatResponse response, ResponseResult native)
    {
        if (native.Error is not null || native.IncompleteStatusDetails is not null
            || native.BackgroundModeEnabled == true
            || (response.FinishReason is not null && response.FinishReason != ChatFinishReason.Stop))
            return new ModelResponseUnknown("contradictory-completion");

        // A bounded source response can contain reasoning items and exactly one
        // completed assistant message. Tools, refusals and other output fail closed.
        var messages = native.OutputItems.OfType<MessageResponseItem>().ToArray();
        if (messages.Length != 1 || native.OutputItems.Any(item => item is not (MessageResponseItem or ReasoningResponseItem))
            || messages[0].Role != MessageRole.Assistant || messages[0].Status != MessageStatus.Completed
            || messages[0].Content.Count == 0
            || messages[0].Content.Any(part => part.Kind != ResponseContentPartKind.OutputText))
            return new ModelResponseUnknown("unexpected-content");

        return new ModelResponseCompleted();
    }
#pragma warning restore OPENAI001

    private static string IncompleteReason(ChatResponse response) =>
        response.FinishReason == ChatFinishReason.Length ? "token-limit" :
        response.FinishReason == ChatFinishReason.ContentFilter ? "content-filter" : "unspecified";
}
