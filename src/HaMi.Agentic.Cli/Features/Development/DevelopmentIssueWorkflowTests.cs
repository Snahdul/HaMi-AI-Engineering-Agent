using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>Pure offline tests: never instantiate an actual HTTP client.</summary>
internal static class DevelopmentIssueWorkflowTests
{
    internal static async Task SelfTestAsync()
    {
        DevelopmentAgentContract contract = new(1, "sandbox-console-retry",
            "Write an offline console app.", "net11.0", "Demo.csproj",
            "Program.cs", "run", 2, ["Program exits zero."]);
        contract.Validate();

        const string goal = "Erzeuge ein einzelnes, sicheres Konsolenprogramm mit überprüfbarem Ergebnis.";
        string body = "HaMi-Agent-Task: sandbox-console-retry\n"
            + "### Ziel und Nutzen\n" + goal + "\n"
            + "### Ausgangslage und Kontext\nNur ein Demo-Fixture.\n"
            + "### Akzeptanzkriterien\n- [ ] Kompiliert erfolgreich in der MicroVM.\n"
            + "- [ ] Ausgabe und Exitcode stimmen überein.\n"
            + "### Prüfungen, Ergebnisse und Belege\nNoch offen.\n";
        JsonElement trusted = FakeIssue(77, body, "Snahdul", true);
        ApprovedDevelopmentIssue issue = ApprovedDevelopmentIssue.Parse(trusted, contract);
        DevelopmentAgentContract bound = issue.Bind(contract);
        if (bound.Description.Contains(goal, StringComparison.Ordinal) != true
            || bound.ProjectFile != contract.ProjectFile || bound.SourceFile != contract.SourceFile
            || bound.MaxAttempts != contract.MaxAttempts || bound.AcceptanceCriteria.Length != 3)
            throw new InvalidOperationException("GitHub Issue did not provide bounded task context.");
        ExpectRejected(() => ApprovedDevelopmentIssue.Parse(
            FakeIssue(77, body, "attacker", true), contract));
        ExpectRejected(() => ApprovedDevelopmentIssue.Parse(
            FakeIssue(77, body, "Snahdul", false), contract));
        ExpectRejected(() => ApprovedDevelopmentIssue.Parse(
            FakeIssue(77, body.Replace("HaMi-Agent-Task: sandbox-console-retry",
                "HaMi-Agent-Task: arbitrary-shell", StringComparison.Ordinal), "Snahdul", true), contract));
        ExpectRejected(() => ApprovedDevelopmentIssue.Parse(
            FakeIssue(77, body.Replace("### Akzeptanzkriterien",
                "### Keine Akzeptanzkriterien", StringComparison.Ordinal), "Snahdul", true), contract));

        var gateway = new MemoryIssueGateway();
        const string jobId = "offline-selftest-issue-001";
        var workflow = new DevelopmentIssueWorkflow(gateway, issue, jobId, allowFollowUps: true, liveGeneration: false);
        await workflow.PublishStartedAsync(CancellationToken.None);
        await workflow.PublishStartedAsync(CancellationToken.None);
        if (gateway.CreatedComments != 1 || gateway.UpdatedComments != 1)
            throw new InvalidOperationException("Duplicate start created an extra issue comment.");
        var report = new DevelopmentJobReport(jobId, "passed", 2,
        [
            new DevelopmentJobAttempt(1, "build-failed", 0, 1, -1, false, true, "C:\\secret\\build.log"),
            new DevelopmentJobAttempt(2, "passed", 0, 0, 0, false, true, "C:\\secret\\run.log")
        ], null, issue.Number);
        await workflow.PublishFinishedAsync(report, CancellationToken.None);
        if (gateway.CreatedComments != 1 || gateway.ReviewCalls != 1
            || !gateway.CommentBody.Contains("Build=1", StringComparison.Ordinal)
            || !gateway.CommentBody.Contains("passed", StringComparison.Ordinal)
            || gateway.CommentBody.Contains("C:\\secret", StringComparison.Ordinal)
            || !gateway.CommentBody.Contains("RETRY_APPROVED", StringComparison.Ordinal)
            || !gateway.CommentBody.Contains("offline-scripted", StringComparison.Ordinal))
            throw new InvalidOperationException("Result journal was duplicated, incorrect or leaked paths.");
        ExpectRejected(() => workflow.PublishFinishedAsync(report with { IssueNumber = 123 },
            CancellationToken.None).GetAwaiter().GetResult());

        var failed = report with { Status = "exhausted" };
        await workflow.PublishFinishedAsync(failed, CancellationToken.None);
        await workflow.PublishFinishedAsync(failed, CancellationToken.None);
        if (gateway.CreatedFollowUps != 1 || gateway.ReviewCalls != 1
            || !gateway.CommentBody.Contains("Folge-Issue: #88", StringComparison.Ordinal))
            throw new InvalidOperationException("Blocking follow-up creation was duplicated or misclassified.");

        var noFollowUps = new DevelopmentIssueWorkflow(gateway, issue,
            "offline-selftest-issue-002", allowFollowUps: false, liveGeneration: false);
        await noFollowUps.PublishFinishedAsync(failed with { JobId = "offline-selftest-issue-002" },
            CancellationToken.None);
        if (gateway.CreatedFollowUps != 1)
            throw new InvalidOperationException("Follow-up created without authorization.");
        await VerifyHttpAdapterOfflineAsync();
    }

    private static async Task VerifyHttpAdapterOfflineAsync()
    {
        // Exercise HTTP method/path, Bearer usage, 64-bit comment identifiers
        // and label reconciliation through a fully in-memory transport.
        using var http = new HttpClient(new FakeGitHubHandler());
        using var adapter = new GitHubIssueGateway(http, "FAKE_TEST_TOKEN");
        using JsonDocument source = await adapter.GetIssueAsync(77, CancellationToken.None);
        if (source.RootElement.GetProperty("number").GetInt32() != 77)
            throw new InvalidOperationException("GitHub issue endpoint returned wrong task.");
        if (await adapter.FindJobCommentAsync(77, "<!-- none -->", CancellationToken.None) is not null)
            throw new InvalidOperationException("Nonexistent issue journal was not recognized.");
        long id = await adapter.CreateCommentAsync(77, "<!-- hami-agent-job:test -->",
            CancellationToken.None);
        if (id != 7127452838L)
            throw new InvalidOperationException("GitHub's 64-bit comment ID was truncated.");
        await adapter.UpdateCommentAsync(id, "Safe result summary", CancellationToken.None);
        if (await adapter.FindFollowUpAsync("<!-- none -->", CancellationToken.None) is not null)
            throw new InvalidOperationException("Nonexistent follow-up was not recognized.");
        if (await adapter.CreateFollowUpAsync("[Task] Follow-up", "Safe details",
            CancellationToken.None) != 88)
            throw new InvalidOperationException("Follow-up issue API returned wrong number.");
        await adapter.MarkReviewAsync(77, CancellationToken.None);
        await adapter.MarkReviewAsync(77, CancellationToken.None);
        using var denied = new GitHubIssueGateway(http, "");
        ExpectRejected(denied.RequireWriteAccess);
    }

    private static JsonElement FakeIssue(int number, string body, string author, bool approved) =>
        JsonSerializer.SerializeToElement(new
        {
            number,
            title = "Freigegebener Demo-Auftrag",
            body,
            state = "open",
            user = new { login = author },
            labels = approved ? new[] { new { name = "workflow:in-progress" } }
                : new[] { new { name = "unapproved" } }
        });

    private static void ExpectRejected(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unauthorized GitHub Issue was accepted.");
    }

    private sealed class FakeGitHubHandler : HttpMessageHandler
    {
        private bool inProgress = true;
        private bool review;
        private const string Base = "/repos/Snahdul/HaMi-AI-Engineering-Agent/";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.Host != "api.github.com"
                || request.Headers.Authorization?.Parameter != "FAKE_TEST_TOKEN")
                throw new InvalidOperationException("GitHub endpoint or auth header incorrect.");
            string path = request.RequestUri.AbsolutePath;
            string method = request.Method.Method;
            string json;
            if (method == "GET" && path == Base + "issues/77")
                json = "{\"number\":77,\"labels\":["
                    + (inProgress ? "{\"name\":\"workflow:in-progress\"}" : "")
                    + (inProgress && review ? "," : "")
                    + (review ? "{\"name\":\"workflow:review\"}" : "")
                    + "]}";
            else if (method == "GET" && path == Base + "issues/77/comments")
                json = "[]";
            else if (method == "GET" && path == Base + "issues")
                json = "[]";
            else if (method == "POST" && path == Base + "issues/77/comments")
                json = "{\"id\":7127452838}";
            else if (method == "PATCH" && path == Base + "issues/comments/7127452838")
                json = "{\"id\":7127452838}";
            else if (method == "POST" && path == Base + "issues")
                json = "{\"number\":88}";
            else if (method == "POST" && path == Base + "issues/77/labels")
            {
                review = true;
                json = "[]";
            }
            else if (method == "DELETE" && path == Base + "issues/77/labels/workflow%3Ain-progress")
            {
                if (!review) throw new InvalidOperationException("Review label was not added first.");
                inProgress = false;
                json = "[]";
            }
            else
                throw new InvalidOperationException("Unexpected GitHub transport call: " + method + " " + path);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
        }
    }

    private sealed class MemoryIssueGateway : IDevelopmentIssueGateway
    {
        private readonly Dictionary<string, int> followUps = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> comments = new(StringComparer.Ordinal);
        public int CreatedComments { get; private set; }
        public int UpdatedComments { get; private set; }
        public int CreatedFollowUps { get; private set; }
        public int ReviewCalls { get; private set; }
        public string CommentBody { get; private set; } = "";

        public Task<JsonDocument> GetIssueAsync(int number, CancellationToken ct) =>
            throw new InvalidOperationException("Offline selftest must not fetch GitHub.");
        public Task<long?> FindJobCommentAsync(int number, string marker, CancellationToken ct) =>
            Task.FromResult(comments.TryGetValue(marker, out long id) ? (long?)id : null);
        public Task<long> CreateCommentAsync(int number, string body, CancellationToken ct)
        {
            string marker = body.Split('\n')[0];
            CreatedComments++;
            comments[marker] = CreatedComments;
            CommentBody = body;
            return Task.FromResult((long)CreatedComments);
        }
        public Task UpdateCommentAsync(long id, string body, CancellationToken ct)
        {
            UpdatedComments++;
            CommentBody = body;
            return Task.CompletedTask;
        }
        public Task<int?> FindFollowUpAsync(string marker, CancellationToken ct) =>
            Task.FromResult(followUps.TryGetValue(marker, out int id) ? (int?)id : null);
        public Task<int> CreateFollowUpAsync(string title, string body, CancellationToken ct)
        {
            CreatedFollowUps++;
            followUps[body.Split('\n')[0]] = 88;
            return Task.FromResult(88);
        }
        public Task MarkReviewAsync(int number, CancellationToken ct)
        {
            ReviewCalls++;
            return Task.CompletedTask;
        }
    }
}
