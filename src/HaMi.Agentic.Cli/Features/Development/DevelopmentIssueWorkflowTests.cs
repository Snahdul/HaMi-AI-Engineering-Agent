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
        var workflow = new DevelopmentIssueWorkflow(gateway, issue, jobId, allowFollowUps: true);
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
            || gateway.CommentBody.Contains("C:\\secret", StringComparison.Ordinal))
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
            "offline-selftest-issue-002", allowFollowUps: false);
        await noFollowUps.PublishFinishedAsync(failed with { JobId = "offline-selftest-issue-002" },
            CancellationToken.None);
        if (gateway.CreatedFollowUps != 1)
            throw new InvalidOperationException("Follow-up created without authorization.");
    }

    private static JsonElement FakeIssue(int number, string body, string author, bool approved) =>
        JsonSerializer.SerializeToElement(new
        {
            number, title = "Freigegebener Demo-Auftrag", body,
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

    private sealed class MemoryIssueGateway : IDevelopmentIssueGateway
    {
        private readonly Dictionary<string, int> followUps = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> comments = new(StringComparer.Ordinal);
        public int CreatedComments { get; private set; }
        public int UpdatedComments { get; private set; }
        public int CreatedFollowUps { get; private set; }
        public int ReviewCalls { get; private set; }
        public string CommentBody { get; private set; } = "";

        public Task<JsonDocument> GetIssueAsync(int number, CancellationToken ct) =>
            throw new InvalidOperationException("Offline selftest must not fetch GitHub.");
        public Task<int?> FindJobCommentAsync(int number, string marker, CancellationToken ct) =>
            Task.FromResult(comments.TryGetValue(marker, out int id) ? (int?)id : null);
        public Task<int> CreateCommentAsync(int number, string body, CancellationToken ct)
        {
            string marker = body.Split('\n')[0];
            CreatedComments++;
            comments[marker] = CreatedComments;
            CommentBody = body;
            return Task.FromResult(CreatedComments);
        }
        public Task UpdateCommentAsync(int id, string body, CancellationToken ct)
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
