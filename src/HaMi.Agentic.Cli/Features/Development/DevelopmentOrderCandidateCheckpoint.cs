using System.Globalization;

namespace HaMi.Agentic.Cli.Features.Development;

// First bounded checkpoint slice: one host-extracted S01 source candidate
// per reserved model operation. No raw provider response, arbitrary locator,
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
        OrderCheck.Require(Version == 1 && ArchiveHash == claim.ArchiveHash
            && Step == "S01" && SourcePath == DevelopmentAgentContract.PairFirst
            && BaselineHash == claim.Binding.CalculatorBaselineHash
            && InputHash == claim.Binding.CalculatorBaselineHash
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

    internal void ValidateBytes(byte[] bytes) =>
        OrderCheck.Require(bytes.Length == ByteCount && bytes.Length is > 0 and <= MaxBytes
            && DevelopmentJobState.Hash(bytes) == ArtifactHash, "ORDER_CANDIDATE_BYTES");
}
