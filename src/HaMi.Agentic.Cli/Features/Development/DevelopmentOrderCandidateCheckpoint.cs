using System.Globalization;

namespace HaMi.Agentic.Cli.Features.Development;

// One host-extracted source candidate per reserved model operation.
// V1 remains S01-only; V2 binds S02 to the accepted predecessor.
// No raw provider response, arbitrary locator,
// verifier acceptance or permission to resume a pending external effect.
internal sealed record OrderCandidateCheckpoint(
    int Version, string ArchiveHash, string OperationId, string Step,
    string SourcePath, string BaselineHash, string InputHash,
    int ByteCount, string ArtifactHash, string Quality, long ObservedUtcTicks)
{
    internal const int MaxBytes = 64 * 1024;
    // Reviewed pair envelope: four model calls plus five guest runs.
    internal const int MaxSequence = 9;

    internal int ValidateBinding(DevelopmentOrderClaim claim)
    {
        string prefix = claim.JobId + ":";
        bool calculator = Version == 1 && Step == "S01" && SourcePath == DevelopmentAgentContract.PairFirst
            && BaselineHash == claim.Binding.CalculatorBaselineHash && InputHash == BaselineHash;
        bool formatter = Version == 2 && Step == "S02" && SourcePath == DevelopmentAgentContract.PairSecond
            && BaselineHash == claim.Binding.FormatterBaselineHash && OrderCheck.Hash(InputHash);
        OrderCheck.Require(ArchiveHash == claim.ArchiveHash && (calculator || formatter)
            && ByteCount is > 0 and <= MaxBytes && OrderCheck.Hash(ArtifactHash)
            && Quality is "Partial" or "Generated"
            && OperationId is not null && OperationId.StartsWith(prefix, StringComparison.Ordinal),
            "ORDER_CANDIDATE_BINDING");
        string operationId = OperationId ?? throw new InvalidOperationException("ORDER_CANDIDATE_BINDING");
        if (!int.TryParse(operationId.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int sequence)
            || sequence is < 1 or > MaxSequence || operationId != prefix + sequence.ToString(CultureInfo.InvariantCulture))
            throw new InvalidOperationException("ORDER_CANDIDATE_SEQUENCE");
        return sequence;
    }

    internal static string FormatterInputHash(DevelopmentOrderClaim claim, OrderS01Accepted accepted)
    {
        OrderCheck.Require(accepted.Version == 1 && accepted.ArchiveHash == claim.ArchiveHash
            && accepted.Candidate is { Version: 1, Step: "S01", Quality: "Generated" }
            && accepted.FormatterBaselineHash == claim.Binding.FormatterBaselineHash
            && accepted.ScopeHash == claim.Binding.ScopeHash, "ORDER_FORMATTER_INPUT_BINDING");
        _ = accepted.Candidate.ValidateBinding(claim);
        return DevelopmentJobState.Hash(OrderStorageJson.Encode(new
        {
            Version = 1,
            claim.ArchiveHash,
            FormatterBaselineHash = claim.Binding.FormatterBaselineHash,
            CalculatorAcceptance = accepted
        }, DevelopmentJobState.MaxBytes));
    }

    internal void RequireFormatterInput(DevelopmentOrderClaim claim, OrderS01Accepted accepted)
    {
        _ = ValidateBinding(claim);
        OrderCheck.Require(Version == 2 && InputHash == FormatterInputHash(claim, accepted),
            "ORDER_FORMATTER_INPUT_BINDING");
    }

    internal void ValidateBytes(byte[] bytes) =>
        OrderCheck.Require(bytes.Length == ByteCount && bytes.Length is > 0 and <= MaxBytes
            && DevelopmentJobState.Hash(bytes) == ArtifactHash, "ORDER_CANDIDATE_BYTES");
}
