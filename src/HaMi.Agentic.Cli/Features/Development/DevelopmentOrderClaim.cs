using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaMi.Agentic.Cli.Features.Development;

// Version 2 is an immutable ownership/identity claim, NOT a checkpoint.
// Only the future journal may own the mutable multi-step workflow state.
internal sealed record DevelopmentOrderClaim(
    int SchemaVersion, string JobId, OrderBinding Binding, string ArchiveHash)
{
    internal const int Version = 2;
    internal string PersistenceId => "hami-order-v1-" + JobId;

    internal static DevelopmentOrderClaim Create(string jobId, OrderBinding binding, string archiveHash)
    {
        var claim = new DevelopmentOrderClaim(Version, jobId, binding, archiveHash);
        claim.Validate(binding.IssueNumber);
        return claim;
    }

    internal byte[] Serialize()
    {
        Validate(Binding.IssueNumber);
        return OrderStorageJson.Encode(this, DevelopmentJobState.MaxBytes);
    }

    internal static DevelopmentOrderClaim Parse(byte[] bytes, int issue)
    {
        var claim = OrderStorageJson.Decode<DevelopmentOrderClaim>(bytes, DevelopmentJobState.MaxBytes);
        claim.Validate(issue);
        return claim;
    }

    private void Validate(int issue)
    {
        OrderCheck.Require(SchemaVersion == Version && JobId is { Length: 32 }
            && JobId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            && OrderCheck.Hash(ArchiveHash), "ORDER_CLAIM_IDENTITY");
        if (Binding is null)
            throw new InvalidOperationException("ORDER_CLAIM_BINDING");
        OrderCheck.Require(Binding.Repository == GitHubIssueGateway.Repository && issue > 0
            && Binding.IssueNumber == issue && Binding.IssueId > 0
            && Binding.Revision is >= 1 and <= 1000 && Binding.OwnerId > 0 && Binding.ApprovalCommentId > 0
            && OrderCheck.Text(Binding.OwnerLogin, 100) && OrderCheck.Commit(Binding.BaseCommit)
            && OrderCheck.Hash(Binding.OrderHash) && OrderCheck.Hash(Binding.ContractHash)
            && OrderCheck.Hash(Binding.ScopeHash) && OrderCheck.Hash(Binding.PolicyHash)
            && OrderCheck.Hash(Binding.CalculatorBaselineHash) && OrderCheck.Hash(Binding.FormatterBaselineHash),
            "ORDER_CLAIM_BINDING");
    }
}

// Exact approved LF-normalized plan and selected approval observation.
// An archive is historical evidence, not a fresh issue observation or
// permission to execute. Live approval must be obtained separately.
internal sealed record DevelopmentOrderArchive(
    int SchemaVersion, OrderBinding Binding, string Title, DateTimeOffset ObservedAt,
    OrderCommentObservation Comment, string Plan)
{
    internal const int MaxBytes = 256 * 1024;

    internal static DevelopmentOrderArchive From(DevelopmentOrderApproval approval) =>
        new(1, approval.Binding, approval.Title, approval.ObservedAt, approval.Comment, approval.Order.Plan);

    internal byte[] Serialize() => OrderStorageJson.Encode(this, MaxBytes);

    internal static DevelopmentOrderArchive Parse(byte[] bytes, DevelopmentOrderClaim claim)
    {
        OrderCheck.Require(DevelopmentJobState.Hash(bytes) == claim.ArchiveHash, "ORDER_ARCHIVE_HASH");
        var archive = OrderStorageJson.Decode<DevelopmentOrderArchive>(bytes, MaxBytes);
        OrderCheck.Require(archive.SchemaVersion == 1 && archive.Binding == claim.Binding
            && archive.ObservedAt != default,
            "ORDER_ARCHIVE_BINDING");
        OrderBinding binding = claim.Binding;
        // Revalidate archived plan bytes and the selected historical marker,
        // not the completeness/currentness of the remote comment history.
        DevelopmentOrder order = DevelopmentOrder.ParseIssueBody(Encoding.UTF8.GetBytes(
            "\x60\x60\x60hami-order-json\n" + archive.Plan + "\x60\x60\x60\n"));
        var document = order.Document;
        OrderCommentObservation comment = archive.Comment
            ?? throw new InvalidOperationException("ORDER_ARCHIVE_OBSERVATION");
        string expected = FormattableString.Invariant(
            $"HaMi-Order-Approval: v1 {binding.Revision} {binding.OrderHash} {binding.BaseCommit}");
        OrderCheck.Require(order.Plan == archive.Plan && order.Sha256 == binding.OrderHash
            && document.GetProperty("revision").GetInt32() == binding.Revision
            && document.GetProperty("repository").GetString() == binding.Repository
            && document.GetProperty("issueNumber").GetInt32() == binding.IssueNumber
            && document.GetProperty("title").GetString() == archive.Title
            && document.GetProperty("baseCommit").GetString() == binding.BaseCommit
            && comment.Id == binding.ApprovalCommentId && comment.AuthorId == binding.OwnerId
            && comment.AuthorLogin == binding.OwnerLogin && comment.Body == expected
            && comment.CreatedAt != default && comment.CreatedAt == comment.UpdatedAt
            && comment.CreatedAt <= archive.ObservedAt, "ORDER_ARCHIVE_OBSERVATION");
        return archive;
    }
}

// Private, host-produced storage format. Canonical re-encoding rejects
// duplicate, missing, reordered and unknown fields as well as alternate
// encodings. No schema or caller-controlled type resolution.
internal static class OrderStorageJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static byte[] Encode<T>(T value, int maxBytes)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        OrderCheck.Require(bytes.Length > 0 && bytes.Length <= maxBytes, "ORDER_STORAGE_SIZE");
        return bytes;
    }

    internal static T Decode<T>(byte[] bytes, int maxBytes) where T : class
    {
        OrderCheck.Require(bytes.Length > 0 && bytes.Length <= maxBytes, "ORDER_STORAGE_SIZE");
        T value = JsonSerializer.Deserialize<T>(bytes, Options)
            ?? throw new InvalidOperationException("ORDER_STORAGE_MISSING");
        OrderCheck.Require(bytes.AsSpan().SequenceEqual(Encode(value, maxBytes)), "ORDER_STORAGE_FORMAT");
        return value;
    }
}
