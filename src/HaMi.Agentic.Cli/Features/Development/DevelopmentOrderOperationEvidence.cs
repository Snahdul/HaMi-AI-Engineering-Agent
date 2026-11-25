using System.Globalization;

namespace HaMi.Agentic.Cli.Features.Development;

// Bounded synthetic model-operation receipt. This records an observation by
// the trusted host fixture, not a model's assertion or a real verifier result.
// The caller must establish the outcome independently before storing it.
internal sealed record OrderOperationEvidence(
    int Version, string ArchiveHash, OrderOperationReservation Reservation,
    string Observer, string Outcome, string BaselineHash, string InputHash,
    string? OutputHash, long ObservedUtcTicks)
{
    internal const int MaxBytes = 4096;
    internal const string FixtureObserver = "hami-offline-host-observer-v1";

    internal int ValidateBinding(DevelopmentOrderClaim claim)
    {
        var operation = Reservation ?? throw new InvalidOperationException("ORDER_EVIDENCE_RESERVATION");
        string prefix = claim.JobId + ":";
        string baseline = operation.Step == "S01" ? claim.Binding.CalculatorBaselineHash : claim.Binding.FormatterBaselineHash;
        (operation.Assignment ?? throw new InvalidOperationException("ORDER_EVIDENCE_ASSIGNMENT")).Validate();
        OrderCheck.Require(Version == 2 && ArchiveHash == claim.ArchiveHash
            && Observer == FixtureObserver && operation.Version == 3 && operation.Kind == "model"
            && operation.Step is "S01" or "S02"
            && operation.ReservedSeconds > 0 && operation.ObservedUtcTicks > 0
            && ObservedUtcTicks >= operation.ObservedUtcTicks && ObservedUtcTicks <= DateTime.MaxValue.Ticks
            && BaselineHash == baseline && OrderCheck.Hash(InputHash)
            && (operation.Step != "S01" || InputHash == baseline)
            && ((Outcome == "succeeded" && OrderCheck.Hash(OutputHash))
                || (Outcome == "failed" && OutputHash is null))
            && operation.OperationId is not null && operation.OperationId.StartsWith(prefix, StringComparison.Ordinal),
            "ORDER_EVIDENCE_BINDING");
        string id = operation.OperationId ?? throw new InvalidOperationException("ORDER_EVIDENCE_BINDING");
        if (!int.TryParse(id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int sequence)
            || sequence is < 1 or > OrderCandidateCheckpoint.MaxSequence
            || id != prefix + sequence.ToString(CultureInfo.InvariantCulture))
            throw new InvalidOperationException("ORDER_EVIDENCE_SEQUENCE");
        return sequence;
    }

    internal byte[] Serialize() => OrderStorageJson.Encode(this, MaxBytes);

    internal OrderOperationCompletion Completion() =>
        new(2, Reservation.OperationId, DevelopmentJobState.Hash(Serialize()), Outcome, ObservedUtcTicks);

    internal void ValidateCompletion(DevelopmentOrderClaim claim, OrderOperationReservation reservation,
        OrderOperationCompletion completion)
    {
        _ = ValidateBinding(claim);
        OrderCheck.Require(Reservation == reservation && Completion() == completion, "ORDER_EVIDENCE_COMPLETION");
    }
}
