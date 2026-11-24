using HaMi.Agentic.Cli.Runtime;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>
/// Host-only, least-privilege adapter for the trusted HaMi Agent repository.
/// Never instantiated inside a sandbox or passed to a model.
/// </summary>
internal interface IDevelopmentIssueGateway
{
    Task<JsonDocument> GetIssueAsync(int issueNumber, CancellationToken ct);
    Task<long?> FindJobCommentAsync(int issueNumber, string marker, CancellationToken ct);
    Task<long> CreateCommentAsync(int issueNumber, string body, CancellationToken ct);
    Task UpdateCommentAsync(long commentId, string body, CancellationToken ct);
    Task<int?> FindFollowUpAsync(string marker, CancellationToken ct);
    Task<int> CreateFollowUpAsync(string title, string body, CancellationToken ct);
    Task MarkReviewAsync(int issueNumber, CancellationToken ct);
}

internal sealed class GitHubIssueGateway : IDevelopmentIssueGateway, IDisposable
{
    internal const string Repository = "Snahdul/HaMi-AI-Engineering-Agent";
    private const string TokenVariable = "HAMI_AGENT_GITHUB_TOKEN";
    private readonly HttpClient client;
    private readonly string? token;
    private readonly bool ownsClient;

    internal GitHubIssueGateway(HttpClient? client = null, string? token = null)
    {
        ownsClient = client is null;
        this.client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // An explicitly passed value (including empty for negative tests)
        // wins. On Windows, only the dedicated Credential Manager entry is
        // accepted, never the operator's broader gh auth or process token.
        // Non-Windows hosts may opt into a separate temporary environment
        // token; no credential store is available there.
        this.token = token ?? (OperatingSystem.IsWindows()
            ? WindowsIssueCredentialStore.Read()
            : Environment.GetEnvironmentVariable(TokenVariable));
    }

    internal void RequireWriteAccess()
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "GitHub write requested: store a dedicated Issues token using config --store-github-issues-token on HaMi11.");
    }

    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method,
            "https://api.github.com/repos/" + Repository + "/" + path);
        request.Headers.UserAgent.ParseAdd("HaMi-AI-Engineering-Agent/0.1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload),
                Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                "GitHub Issues API rejected the request: HTTP " + (int)response.StatusCode
                + ". No response body or credential is logged.");
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json);
    }

    public Task<JsonDocument> GetIssueAsync(int issueNumber, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "issues/" + issueNumber, null, ct);

    public async Task<long?> FindJobCommentAsync(int issueNumber, string marker, CancellationToken ct)
    {
        for (int page = 1; page <= 10; page++)
        {
            using JsonDocument result = await SendAsync(HttpMethod.Get,
                "issues/" + issueNumber + "/comments?per_page=100&page=" + page, null, ct);
            JsonElement array = result.RootElement;
            if (array.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("GitHub comments API returned no array.");
            foreach (JsonElement entry in array.EnumerateArray())
                if (entry.TryGetProperty("body", out JsonElement body)
                    && body.ValueKind == JsonValueKind.String
                    && (body.GetString() ?? "").Contains(marker, StringComparison.Ordinal))
                    return entry.GetProperty("id").GetInt64();
            if (array.GetArrayLength() < 100) return null;
        }
        throw new InvalidOperationException("GitHub comment pagination limit reached; no duplicate write attempted.");
    }

    public async Task<long> CreateCommentAsync(int issueNumber, string body, CancellationToken ct)
    {
        RequireWriteAccess();
        using JsonDocument result = await SendAsync(HttpMethod.Post,
            "issues/" + issueNumber + "/comments", new { body }, ct);
        return result.RootElement.GetProperty("id").GetInt64();
    }

    public async Task UpdateCommentAsync(long commentId, string body, CancellationToken ct)
    {
        RequireWriteAccess();
        using JsonDocument _ = await SendAsync(new HttpMethod("PATCH"),
            "issues/comments/" + commentId, new { body }, ct);
    }

    public async Task<int?> FindFollowUpAsync(string marker, CancellationToken ct)
    {
        // Scan all reachable pages rather than depend on eventually indexed search.
        // Fail closed if the bounded scan cannot establish absence.
        for (int page = 1; page <= 10; page++)
        {
            using JsonDocument result = await SendAsync(HttpMethod.Get,
                "issues?state=all&per_page=100&page=" + page, null, ct);
            JsonElement array = result.RootElement;
            if (array.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("GitHub issue listing returned no array.");
            foreach (JsonElement entry in array.EnumerateArray())
                if (!entry.TryGetProperty("pull_request", out _)
                    && entry.TryGetProperty("body", out JsonElement body)
                    && body.ValueKind == JsonValueKind.String
                    && (body.GetString() ?? "").Contains(marker, StringComparison.Ordinal))
                    return entry.GetProperty("number").GetInt32();
            if (array.GetArrayLength() < 100) return null;
        }
        throw new InvalidOperationException("GitHub issue pagination limit reached; follow-up creation refused.");
    }

    public async Task<int> CreateFollowUpAsync(string title, string body, CancellationToken ct)
    {
        RequireWriteAccess();
        using JsonDocument result = await SendAsync(HttpMethod.Post, "issues",
            new { title, body }, ct);
        return result.RootElement.GetProperty("number").GetInt32();
    }

    public async Task MarkReviewAsync(int issueNumber, CancellationToken ct)
    {
        RequireWriteAccess();
        // Idempotent: retrying after a completed transition must not fail on
        // a missing old label. Project #2 remains owned by existing Actions.
        using JsonDocument issue = await GetIssueAsync(issueNumber, ct);
        JsonElement labels = issue.RootElement.GetProperty("labels");
        bool inProgress = labels.EnumerateArray().Any(e =>
            e.GetProperty("name").GetString() == "workflow:in-progress");
        bool review = labels.EnumerateArray().Any(e =>
            e.GetProperty("name").GetString() == "workflow:review");
        if (!inProgress && !review)
            throw new InvalidOperationException(
                "Review transition rejected: issue has no active agent status label.");
        // Add the destination label BEFORE removing the source label.
        // If either request fails midway, the next run can reconcile
        // safely and never leave the Issue without an automation label.
        if (!review)
        {
            using JsonDocument added = await SendAsync(HttpMethod.Post,
                "issues/" + issueNumber + "/labels",
                new { labels = new[] { "workflow:review" } }, ct);
        }
        if (inProgress)
        {
            using JsonDocument removed = await SendAsync(HttpMethod.Delete,
                "issues/" + issueNumber + "/labels/workflow%3Ain-progress", null, ct);
        }
    }

    public void Dispose()
    {
        if (ownsClient) client.Dispose();
    }
}
