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
        HttpMethod method, string path, object? payload, CancellationToken ct, int? maxResponseBytes = null)
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

        Uri expectedUri = request.RequestUri!;
        using HttpResponseMessage response = await client.SendAsync(request,
            maxResponseBytes is null ? HttpCompletionOption.ResponseContentRead : HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                "GitHub Issues API rejected the request: HTTP " + (int)response.StatusCode
                + ". No response body or credential is logged.");
        if (maxResponseBytes is int maximum)
        {
            // Order observations never accept a redirected or unbounded response.
            OrderCheck.Require(response.RequestMessage?.RequestUri == expectedUri
                && !(response.Content.Headers.ContentLength > maximum), "ORDER_ISSUE_RESPONSE");
            using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), ct)) != 0)
            {
                OrderCheck.Require(bytes.Length + read <= maximum, "ORDER_ISSUE_RESPONSE_SIZE");
                bytes.Write(buffer, 0, read);
            }
            return JsonDocument.Parse(bytes.ToArray());
        }
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json);
    }

    public Task<JsonDocument> GetIssueAsync(int issueNumber, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "issues/" + issueNumber, null, ct);

    // Read-only transport, not approval or a dispatch capability. The caller
    // must independently bind the reviewed profile and validate current authority.
    internal async Task<OrderIssueObservation> ObserveOrderAsync(int issueNumber, CancellationToken ct)
    {
        OrderCheck.Require(issueNumber > 0 && !string.IsNullOrWhiteSpace(token), "ORDER_ISSUE_CREDENTIAL");
        const int pageSize = 25;
        const int maximumComments = 256;
        const int maximumResponseBytes = 4 * 1024 * 1024;
        const string repositoryUrl = "https://api.github.com/repos/" + Repository;
        string path = "issues/" + issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string issueUrl = repositoryUrl + "/" + path;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));

        Task<JsonDocument> ReadAsync(string relative) =>
            SendAsync(HttpMethod.Get, relative, null, deadline.Token, maximumResponseBytes);

        (long Id, int Number, string Title, long AuthorId, string AuthorLogin, bool IsOpen,
            bool IsActive, string Body, DateTimeOffset UpdatedAt, int Count) Header(JsonElement issue)
        {
            OrderCheck.Require(issue.ValueKind == JsonValueKind.Object
                && !issue.TryGetProperty("pull_request", out _)
                && issue.GetProperty("repository_url").GetString() == repositoryUrl
                && issue.GetProperty("url").GetString() == issueUrl
                && issue.GetProperty("number").GetInt32() == issueNumber, "ORDER_ISSUE_IDENTITY");
            string body = issue.GetProperty("body").GetString() ?? "";
            int count = issue.GetProperty("comments").GetInt32();
            OrderCheck.Require(new UTF8Encoding(false, true).GetByteCount(body) <= 64 * 1024
                && count is >= 0 and <= maximumComments, "ORDER_ISSUE_HISTORY_SIZE");
            var user = issue.GetProperty("user");
            string?[] labels = issue.GetProperty("labels").EnumerateArray()
                .Select(label => label.GetProperty("name").GetString()).ToArray();
            bool active = labels.Count(label => label == ApprovedDevelopmentIssue.ActiveLabel) == 1
                && labels.Count(label => label?.StartsWith("workflow:", StringComparison.Ordinal) == true) == 1;
            return (issue.GetProperty("id").GetInt64(), issueNumber,
                issue.GetProperty("title").GetString() ?? "", user.GetProperty("id").GetInt64(),
                user.GetProperty("login").GetString() ?? "", issue.GetProperty("state").GetString() == "open",
                active, body, issue.GetProperty("updated_at").GetDateTimeOffset(), count);
        }

        using JsonDocument before = await ReadAsync(path);
        var first = Header(before.RootElement);
        var comments = new List<OrderCommentObservation>();
        var ids = new HashSet<long>();
        bool complete = false;
        for (int page = 1; page <= maximumComments / pageSize + 1; page++)
        {
            using JsonDocument result = await ReadAsync(FormattableString.Invariant(
                $"{path}/comments?per_page={pageSize}&page={page}"));
            JsonElement array = result.RootElement;
            OrderCheck.Require(array.ValueKind == JsonValueKind.Array
                && array.GetArrayLength() <= pageSize, "ORDER_ISSUE_COMMENT_PAGE");
            foreach (JsonElement entry in array.EnumerateArray())
            {
                long id = entry.GetProperty("id").GetInt64();
                string body = entry.GetProperty("body").GetString() ?? "";
                OrderCheck.Require(entry.GetProperty("issue_url").GetString() == issueUrl
                    && id > 0 && ids.Add(id) && comments.Count < maximumComments
                    && body.Length <= 64 * 1024, "ORDER_ISSUE_COMMENT_IDENTITY");
                var user = entry.GetProperty("user");
                comments.Add(new(id, user.GetProperty("id").GetInt64(), user.GetProperty("login").GetString() ?? "",
                    body, entry.GetProperty("created_at").GetDateTimeOffset(), entry.GetProperty("updated_at").GetDateTimeOffset()));
            }
            if (array.GetArrayLength() < pageSize)
            {
                complete = true;
                break;
            }
        }

        using JsonDocument after = await ReadAsync(path);
        DateTimeOffset observed = DateTimeOffset.UtcNow;
        OrderCheck.Require(complete && comments.Count == first.Count && first == Header(after.RootElement)
            && first.UpdatedAt <= observed, "ORDER_ISSUE_OBSERVATION_CHANGED");
        return new(Repository, first.Id, first.Number, first.Title, first.AuthorId, first.AuthorLogin,
            first.IsOpen, first.IsActive, new UTF8Encoding(false, true).GetBytes(first.Body), observed,
            true, comments.ToArray());
    }

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
