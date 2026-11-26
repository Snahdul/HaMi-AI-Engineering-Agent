using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

namespace HaMi.Agentic.Cli.Features.Coding;

/// <summary>
/// Opt-in model transport for the existing bounded Clamp task.
/// The provider is injected via IChatClient, allowing deterministic offline tests.
/// </summary>
internal sealed class CodingAiClient(IChatClient client, CodingExecutionPolicy policy)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            plan = new { type = "string" },
            expression = new { type = "string" }
        },
        required = new[] { "plan", "expression" }
    });

    public async Task<CodingSuggestion> GenerateAsync(
        CodingPrompt prompt, string model, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt.System);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt.User);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        ChatOptions options = CreateOptions(model, prompt.System);
        using var apiDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        apiDeadline.CancelAfter(TimeSpan.FromSeconds(policy.ApiTimeoutSeconds));
        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt.User)], options, apiDeadline.Token);

        if (response.FinishReason == ChatFinishReason.Length
            || string.IsNullOrWhiteSpace(response.Text))
            throw new InvalidOperationException("AI response is incomplete or empty.");

        try
        {
            CodingSuggestion? suggestion =
                JsonSerializer.Deserialize<CodingSuggestion>(response.Text, SerializerOptions);
            if (string.IsNullOrWhiteSpace(suggestion?.Plan)
                || string.IsNullOrWhiteSpace(suggestion.Expression))
                throw new InvalidOperationException("Structured AI response has missing fields.");

            return suggestion;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Structured AI response is not valid JSON.");
        }
    }

    // The OpenAI Responses SDK currently marks CreateResponseOptions experimental (OPENAI001).
    // Keep the opt-in compatibility boundary explicit and limited to this method.
#pragma warning disable OPENAI001
    private ChatOptions CreateOptions(string model, string instructions) => new()
    {
        ModelId = model,
        Instructions = instructions,
        MaxOutputTokens = policy.MaxOutputTokens,
        ResponseFormat = ChatResponseFormat.ForJsonSchema(Schema, "hami_clamp_coding"),
        ToolMode = ChatToolMode.None,
        Tools = [],
        AllowBackgroundResponses = false,
        // The OpenAI Responses adapter uses this provider-specific options object.
        // Preserve the old transport's explicit store=false contract.
        RawRepresentationFactory = _ => new CreateResponseOptions
        {
            StoredOutputEnabled = policy.StoreModelResponses
        }
    };

#pragma warning restore OPENAI001

    public static IChatClient CreateOpenAi(string apiKey, OpenAIClientOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        OpenAIClient provider = options is null ? new(apiKey)
            : new(new System.ClientModel.ApiKeyCredential(apiKey), options);
        // OPENAI001 marks this Responses->IChatClient adapter experimental.
        // It is kept behind an explicit CLI opt-in pending a real compatibility test.
#pragma warning disable OPENAI001
        return provider.GetResponsesClient().AsIChatClient();
#pragma warning restore OPENAI001
    }

    public static async Task SelfTestAsync(string repository)
    {
        CodingPromptTemplate.SelfTest(repository);
        CodingExecutionPolicy policy = CodingExecutionPolicy.Load(repository);
        CodingPrompt task = CodingPromptTemplate.Load(repository)
            .Render(null, policy.MaxFeedbackCharacters);
        const string validJson =
            """{"plan":"Compare the value with the bounds.","expression":"value < min ? min : value > max ? max : value"}""";

        using var fake = new ScriptedChatClient(validJson);
        var gateway = new CodingAiClient(fake, policy);
        CodingSuggestion result = await gateway.GenerateAsync(task, "test-model");

#pragma warning disable OPENAI001
        if (result.Expression != "value < min ? min : value > max ? max : value"
            || fake.Calls != 1
            || fake.Options?.ModelId != "test-model"
            || fake.Options.Instructions != task.System
            || fake.ReceivedUserPrompt != task.User
            || !fake.ReceivedCancellableToken
            || fake.Options.MaxOutputTokens != policy.MaxOutputTokens
            || fake.Options.ResponseFormat is not ChatResponseFormatJson
            || fake.Options.ToolMode is not NoneChatToolMode
            || fake.Options.Tools?.Count != 0
            || fake.Options.AllowBackgroundResponses != false
            || fake.Options.RawRepresentationFactory?.Invoke(fake) is not CreateResponseOptions
            { StoredOutputEnabled: false })
            throw new InvalidOperationException(
                "AI adapter did not preserve strict JSON format, no-tools, token budget or store=false.");
#pragma warning restore OPENAI001

        using var cancelledSource = new CancellationTokenSource();
        cancelledSource.Cancel();
        try
        {
            await gateway.GenerateAsync(task, "test-model", cancelledSource.Token);
            throw new InvalidOperationException("AI adapter ignored caller cancellation.");
        }
        catch (OperationCanceledException) when (cancelledSource.IsCancellationRequested)
        {
            // Expected: caller cancellation propagates through the policy deadline.
        }

        using var bad = new ScriptedChatClient("not JSON");
        try
        {
            await new CodingAiClient(bad, policy).GenerateAsync(task, "test-model");
            throw new InvalidOperationException("AI adapter accepted invalid JSON.");
        }
        catch (InvalidOperationException ex) when (ex.Message == "Structured AI response is not valid JSON.")
        {
            // Expected: malformed provider output must be rejected before writing code.
        }

        Console.WriteLine("Microsoft.Extensions.AI self-test passed: injected IChatClient, "
            + "JSON schema, no model tools, store=false, cancellation and invalid response rejection.");
    }

    internal sealed record CodingSuggestion(string Plan, string Expression);

    private sealed class ScriptedChatClient(string responseText) : IChatClient
    {
        public int Calls { get; private set; }
        public ChatOptions? Options { get; private set; }
        public string? ReceivedUserPrompt { get; private set; }
        public bool ReceivedCancellableToken { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ChatMessage user = messages.Single();
            if (user.Role != ChatRole.User)
                throw new InvalidOperationException("Expected a single user prompt.");
            ReceivedUserPrompt = user.Text;
            ReceivedCancellableToken = cancellationToken.CanBeCanceled;
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Options = options;
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, responseText)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }
}
