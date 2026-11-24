using System.Security.Cryptography;
using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

// Host-owned recovery metadata, not a user-importable report or authorization.
internal sealed record DevelopmentJobIdentity(
    string JobId, string Repository, int IssueNumber, string BaseCommit,
    string TaskHash, string ContractHash, string ScopeHash, string PolicyHash,
    string ResultsRoot, string GenerationMode, int MaxAttempts,
    int MaxPublicationAttempts);

internal sealed record DevelopmentJobState(
    int SchemaVersion, long Revision, DevelopmentJobIdentity Identity,
    string Stage, int ModelCalls, int Attempts, int PublicationAttempts,
    string? EvidenceHash, int? PullRequestNumber, string? Reason)
{
    internal const int Version = 1;
    internal const int PublicationLimit = 3;
    internal const int MaxBytes = 16 * 1024;
    internal const string Accepted = "accepted";
    internal const string Executing = "executing";
    internal const string CandidateVerified = "candidate-verified";
    internal const string PublicationPending = "publication-pending";
    internal const string Published = "published";
    internal const string Failed = "failed";
    internal const string Exhausted = "exhausted";
    internal const string Cancelled = "cancelled";
    internal const string RecoveryBlocked = "recovery-blocked";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] StateFields =
        ["schemaVersion", "revision", "identity", "stage", "modelCalls", "attempts",
            "publicationAttempts", "evidenceHash", "pullRequestNumber", "reason"];
    private static readonly string[] IdentityFields =
        ["jobId", "repository", "issueNumber", "baseCommit", "taskHash", "contractHash",
            "scopeHash", "policyHash", "resultsRoot", "generationMode", "maxAttempts",
            "maxPublicationAttempts"];

    internal static DevelopmentJobState Create(DevelopmentJobIdentity identity)
    {
        var state = new DevelopmentJobState(Version, 0, identity, Accepted,
            0, 0, 0, null, null, null);
        state.Validate();
        return state;
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static DevelopmentJobState Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > MaxBytes)
            throw new InvalidOperationException("Recovery state size rejected.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        RequireFields(document.RootElement, StateFields);
        RequireFields(document.RootElement.GetProperty("identity"), IdentityFields);
        var state = JsonSerializer.Deserialize<DevelopmentJobState>(bytes, JsonOptions)
            ?? throw new InvalidOperationException("Recovery state missing.");
        state.Validate();
        return state;
    }

    private static void RequireFields(JsonElement element, string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(fields.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Recovery state fields rejected.");
    }

    internal byte[] Serialize()
    {
        Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
        if (bytes.Length > MaxBytes)
            throw new InvalidOperationException("Recovery state size rejected.");
        return bytes;
    }

    private static bool UpperHash(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool LowerHex(string? value, int length) =>
        value is not null && value.Length == length
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal void Validate()
    {
        var id = Identity;
        if (SchemaVersion != Version || Revision is < 0 or > 16 || id is null
            || !LowerHex(id.JobId, 32) || id.Repository != GitHubIssueGateway.Repository
            || id.IssueNumber <= 0 || !LowerHex(id.BaseCommit, 40)
            || !UpperHash(id.TaskHash) || !UpperHash(id.ContractHash)
            || !UpperHash(id.ScopeHash) || !UpperHash(id.PolicyHash)
            || id.ResultsRoot is not { Length: > 0 and <= 4096 }
            || id.ResultsRoot.Any(char.IsControl) || !Path.IsPathFullyQualified(id.ResultsRoot)
            || Path.TrimEndingDirectorySeparator(Path.GetFullPath(id.ResultsRoot)) != id.ResultsRoot
            || id.GenerationMode is not ("offline-scripted" or "live")
            || id.MaxAttempts != 2 || id.MaxPublicationAttempts != PublicationLimit
            || ModelCalls < 0 || ModelCalls > id.MaxAttempts
            || Attempts < 0 || Attempts > ModelCalls
            || PublicationAttempts < 0 || PublicationAttempts > id.MaxPublicationAttempts)
            throw new InvalidOperationException("Recovery state identity or budget rejected.");

        bool verified = EvidenceHash is not null;
        if (verified && (!UpperHash(EvidenceHash) || ModelCalls < 1 || Attempts < 1))
            throw new InvalidOperationException("Recovery evidence identity rejected.");
        bool valid = Stage switch
        {
            Accepted => Revision == 0 && ModelCalls == 0 && Attempts == 0
                && !verified && PublicationAttempts == 0 && PullRequestNumber is null && Reason is null,
            Executing => Revision == 1 && ModelCalls == 0 && Attempts == 0
                && !verified && PublicationAttempts == 0 && PullRequestNumber is null && Reason is null,
            CandidateVerified => Revision == 2 && verified && PublicationAttempts == 0
                && PullRequestNumber is null && Reason is null,
            PublicationPending => Revision == 2 + PublicationAttempts && verified
                && PublicationAttempts > 0 && PullRequestNumber is null && Reason is null,
            Published => Revision == 3 + PublicationAttempts && verified
                && PublicationAttempts > 0 && PullRequestNumber is > 0 && Reason is null,
            Failed or Exhausted or Cancelled => Revision == 2 && !verified
                && PublicationAttempts == 0 && PullRequestNumber is null && Reason is null,
            RecoveryBlocked => Revision is 1 or 2 && !verified && PublicationAttempts == 0
                && PullRequestNumber is null && Reason == "EXECUTION_STATE_UNKNOWN",
            _ => false
        };
        if (!valid)
            throw new InvalidOperationException("Recovery state stage rejected.");
    }

    internal DevelopmentJobState Start() =>
        Advance(this with { Revision = Revision + 1, Stage = Executing });

    internal DevelopmentJobState Verify(int modelCalls, int attempts, string evidenceHash) =>
        Advance(this with { Revision = Revision + 1, Stage = CandidateVerified,
            ModelCalls = modelCalls, Attempts = attempts, EvidenceHash = evidenceHash });

    internal DevelopmentJobState ReservePublication() =>
        Advance(this with { Revision = Revision + 1, Stage = PublicationPending,
            PublicationAttempts = PublicationAttempts + 1 });

    internal DevelopmentJobState ConfirmPublication(int pullRequestNumber) =>
        Advance(this with { Revision = Revision + 1, Stage = Published,
            PullRequestNumber = pullRequestNumber });

    internal DevelopmentJobState FinishExecution(string stage, int modelCalls, int attempts) =>
        Advance(this with { Revision = Revision + 1, Stage = stage,
            ModelCalls = modelCalls, Attempts = attempts });

    internal DevelopmentJobState BlockUnknownExecution() =>
        Advance(this with { Revision = Revision + 1, Stage = RecoveryBlocked,
            Reason = "EXECUTION_STATE_UNKNOWN" });

    private DevelopmentJobState Advance(DevelopmentJobState next)
    {
        ValidateTransition(this, next);
        return next;
    }

    internal static void ValidateTransition(DevelopmentJobState previous, DevelopmentJobState next)
    {
        previous.Validate();
        next.Validate();
        if (next.Identity != previous.Identity || next.Revision != previous.Revision + 1)
            throw new InvalidOperationException("Recovery state identity or revision changed.");
        bool valid = (previous.Stage, next.Stage) switch
        {
            (Accepted, Executing or RecoveryBlocked) => true,
            (Executing, CandidateVerified or Failed or Exhausted or Cancelled or RecoveryBlocked) => true,
            (CandidateVerified or PublicationPending, PublicationPending) =>
                next.PublicationAttempts == previous.PublicationAttempts + 1 && SameEvidence(),
            (PublicationPending, Published) =>
                next.PublicationAttempts == previous.PublicationAttempts && SameEvidence(),
            _ => false
        };
        if (!valid)
            throw new InvalidOperationException("Recovery state transition rejected.");

        bool SameEvidence() => next.ModelCalls == previous.ModelCalls
            && next.Attempts == previous.Attempts && next.EvidenceHash == previous.EvidenceHash;
    }
}
