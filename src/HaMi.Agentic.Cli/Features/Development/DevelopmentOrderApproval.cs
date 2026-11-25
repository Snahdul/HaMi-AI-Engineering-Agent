using System.Globalization;
using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

// Read-only observations supplied by the offline fixture. These records are not
// an authenticated GitHub client, a durable archive or runtime capabilities.
internal sealed record OrderIssueObservation(
    string Repository, long IssueId, int Number, string Title, long AuthorId, string AuthorLogin,
    bool IsOpen, bool IsActive, byte[] Body, DateTimeOffset ObservedAt,
    bool CompleteHistory, OrderCommentObservation[] Comments);

internal sealed record OrderCommentObservation(
    long Id, long AuthorId, string AuthorLogin, string Body, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

internal sealed record OrderReviewedProfile(
    string Repository, long OwnerId, string OwnerLogin, string BaseCommit,
    string ContractHash, string ScopeHash, string PolicyHash,
    string CalculatorBaselineHash, string FormatterBaselineHash);

internal sealed record OrderBinding(
    string Repository, long IssueId, int IssueNumber, int Revision, string OrderHash,
    string BaseCommit, long ApprovalCommentId, long OwnerId, string OwnerLogin,
    string ContractHash, string ScopeHash, string PolicyHash,
    string CalculatorBaselineHash, string FormatterBaselineHash);

internal sealed class DevelopmentOrderApproval
{
    private DevelopmentOrderApproval(DevelopmentOrder order, OrderBinding binding,
        string title, DateTimeOffset observedAt, OrderCommentObservation comment)
    {
        Order = order;
        Binding = binding;
        Title = title;
        ObservedAt = observedAt;
        Comment = comment;
    }

    internal DevelopmentOrder Order { get; }
    internal OrderBinding Binding { get; }
    internal string Title { get; }
    internal DateTimeOffset ObservedAt { get; }
    internal OrderCommentObservation Comment { get; }

    internal static DevelopmentOrderApproval Observe(OrderIssueObservation issue, OrderReviewedProfile profile)
    {
        OrderCheck.Require(issue.Repository == profile.Repository && issue.IssueId > 0
            && issue.Number > 0 && profile.OwnerId > 0 && OrderCheck.Text(profile.OwnerLogin, 100)
            && issue.AuthorId == profile.OwnerId && issue.AuthorLogin == profile.OwnerLogin
            && issue.IsOpen && issue.IsActive && issue.ObservedAt != default, "ORDER_ISSUE_AUTHORITY");
        OrderCheck.Require(OrderCheck.Commit(profile.BaseCommit) && OrderCheck.Hash(profile.ContractHash)
            && OrderCheck.Hash(profile.ScopeHash) && OrderCheck.Hash(profile.PolicyHash)
            && OrderCheck.Hash(profile.CalculatorBaselineHash) && OrderCheck.Hash(profile.FormatterBaselineHash),
            "ORDER_REVIEW_PROFILE");

        DevelopmentOrder order = DevelopmentOrder.ParseIssueBody(issue.Body);
        JsonElement plan = order.Document;
        int revision = plan.GetProperty("revision").GetInt32();
        OrderCheck.Require(plan.GetProperty("repository").GetString() == issue.Repository
            && plan.GetProperty("issueNumber").GetInt32() == issue.Number
            && plan.GetProperty("title").GetString() == issue.Title
            && plan.GetProperty("baseCommit").GetString() == profile.BaseCommit, "ORDER_OBSERVATION_BINDING");

        OrderCheck.Require(issue.CompleteHistory && issue.Comments.Length <= 256, "ORDER_APPROVAL_HISTORY");
        var ids = new HashSet<long>();
        var decisions = new List<OrderCommentObservation>();
        foreach (OrderCommentObservation comment in issue.Comments)
        {
            OrderCheck.Require(comment.Id > 0 && ids.Add(comment.Id) && comment.AuthorId > 0
                && OrderCheck.Text(comment.AuthorLogin, 100) && comment.Body.Length <= 64 * 1024
                && comment.CreatedAt != default && comment.CreatedAt <= comment.UpdatedAt
                && comment.UpdatedAt <= issue.ObservedAt, "ORDER_COMMENT_OBSERVATION");
            bool ownerId = comment.AuthorId == profile.OwnerId;
            bool ownerLogin = comment.AuthorLogin == profile.OwnerLogin;
            OrderCheck.Require(ownerId == ownerLogin, "ORDER_COMMENT_AUTHORITY");
            if (!ownerId)
                continue;
            if (!comment.Body.Contains("HaMi-Order-Approval", StringComparison.Ordinal)
                && !comment.Body.Contains("HaMi-Order-Revoke", StringComparison.Ordinal))
                continue;

            // Owner decision markers are whole, immutable, canonical comment bodies.
            // Malformed or edited owner decisions stop the observation, even if older.
            string[] parts = comment.Body.Split(' ');
            bool approval = parts[0] == "HaMi-Order-Approval:";
            bool revoke = parts[0] == "HaMi-Order-Revoke:";
            OrderCheck.Require(((approval && parts.Length == 5) || (revoke && parts.Length == 4))
                && parts[1] == "v1"
                && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                && number is >= 1 and <= 1000 && parts[2] == number.ToString(CultureInfo.InvariantCulture)
                && OrderCheck.Hash(parts[3]) && (!approval || OrderCheck.Commit(parts[4]))
                && comment.UpdatedAt == comment.CreatedAt, "ORDER_APPROVAL_MARKER");
            decisions.Add(comment);
        }

        OrderCheck.Require(decisions.Count > 0
            && decisions.Select(c => c.CreatedAt).Distinct().Count() == decisions.Count, "ORDER_APPROVAL_AMBIGUOUS");
        OrderCommentObservation latest = decisions.OrderBy(c => c.CreatedAt).Last();
        string expected = FormattableString.Invariant($"HaMi-Order-Approval: v1 {revision} {order.Sha256} {profile.BaseCommit}");
        OrderCheck.Require(latest.Body == expected, "ORDER_APPROVAL_STALE_OR_REVOKED");

        return new DevelopmentOrderApproval(order, new OrderBinding(issue.Repository, issue.IssueId, issue.Number,
            revision, order.Sha256, profile.BaseCommit, latest.Id, profile.OwnerId, profile.OwnerLogin,
            profile.ContractHash, profile.ScopeHash, profile.PolicyHash, profile.CalculatorBaselineHash,
            profile.FormatterBaselineHash), issue.Title, issue.ObservedAt, latest);
    }
}

internal static class OrderCheck
{
    internal static bool Hash(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal static bool Commit(string? value) =>
        value is { Length: 40 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool Text(string? value, int max) =>
        value is not null && value.Length <= max && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    internal static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException(reason);
    }
}
