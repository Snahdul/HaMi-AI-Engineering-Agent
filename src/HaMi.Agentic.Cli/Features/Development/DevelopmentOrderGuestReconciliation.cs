using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaMi.Agentic.Cli.Features.Development;

// A known successful guest operation is not an independently accepted step.
// Nonzero/ambiguous exits, timeout, missing report or unknown cleanup remain
// blocked in this first reconciliation slice. No effect is dispatched here.
internal sealed record OrderGuestOutcome(
    int Version, string ArchiveHash, OrderOperationReservation Reservation,
    string RunId, string ReportHash, long ObservedUtcTicks)
{
    internal void Validate(string archiveHash, OrderOperationReservation reservation)
    {
        OrderCheck.Require(Version == 1 && ArchiveHash == archiveHash && OrderCheck.Hash(archiveHash)
            && Reservation == reservation && RunId == DevelopmentOrderGuestReconciliation.RunId(reservation)
            && OrderCheck.Hash(ReportHash) && ObservedUtcTicks >= reservation.ObservedUtcTicks
            && ObservedUtcTicks <= DateTime.MaxValue.Ticks, "ORDER_GUEST_OUTCOME");
    }
}

internal static class DevelopmentOrderGuestReconciliation
{
    internal const int MaxReportBytes = 32 * 1024;
    private static readonly JsonSerializerOptions ReportOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    // Existing runner uses a 32-character lowercase run ID and the first
    // 12 characters as sandbox name. Derive it BEFORE dispatch from the
    // immutable reserved operation, never from an untrusted report/path.
    // The reserved runner entry consumes this ID, checks name absence
    // and never retries a previously consumed local dispatch slot.
    internal static string RunId(OrderOperationReservation reservation)
    {
        OrderCheck.Require(reservation.Version == 4 && reservation.Kind == "guest"
            && OrderCheck.Hash(reservation.SnapshotHash)
            && reservation.Step is "S01" or "S02" or "FINAL" && reservation.Assignment is null
            && reservation.ReservedSeconds > 0 && reservation.ObservedUtcTicks > 0,
            "ORDER_GUEST_RESERVATION");
        string operationId = reservation.OperationId ?? throw new InvalidOperationException("ORDER_GUEST_ID");
        OrderCheck.Require(operationId.Length is >= 34 and <= 36 && operationId[32] == ':'
            && operationId[..32].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            && int.TryParse(operationId.AsSpan(33), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int sequence)
            && sequence is >= 1 and <= OrderCandidateCheckpoint.MaxSequence
            && operationId[33..] == sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "ORDER_GUEST_ID");
        return DevelopmentJobState.Hash(Encoding.UTF8.GetBytes("hami-order-guest-v1:" + operationId))
            .ToLowerInvariant()[..32];
    }

    // Trusted host supplies exact runner report bytes and a fresh read-only
    // inspect observation for the expected run ID. This function neither
    // invokes sbx nor authenticates the caller, and stores no raw diagnostics.
    // Null means reconciliation required, not a retryable failure.
    internal static OrderGuestOutcome? Observe(string archiveHash, OrderOperationReservation reservation,
        byte[]? reportBytes, int inspectExitCode, string inspectOutput, string inspectError, long now)
    {
        string runId = RunId(reservation);
        OrderCheck.Require(OrderCheck.Hash(archiveHash) && now >= reservation.ObservedUtcTicks
            && now <= DateTime.MaxValue.Ticks, "ORDER_GUEST_OBSERVATION");
        if (reportBytes is null || !IsSuccessfulReport(reservation, reportBytes)
            || !DevelopmentSandboxRunner.IsConfirmedAbsent(runId, inspectExitCode, inspectOutput, inspectError))
            return null;

        return new(1, archiveHash, reservation, runId, DevelopmentJobState.Hash(reportBytes), now);
    }

    internal static bool IsSuccessfulReport(OrderOperationReservation reservation, byte[] reportBytes)
    {
        string runId = RunId(reservation);
        if (reportBytes.Length is 0 or > MaxReportBytes)
            return false;
        DevelopmentSandboxRunner.SandboxRunReport? report;
        try
        {
            report = JsonSerializer.Deserialize<DevelopmentSandboxRunner.SandboxRunReport>(reportBytes, ReportOptions);
        }
        catch (JsonException)
        {
            return false;
        }
        // Exact canonical runner format rejects duplicate/unknown/missing
        // fields, alternate encodings, and trailing or truncated data.
        if (report is null || !reportBytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(report, ReportOptions))
            || report.SchemaVersion != 4 || report.RunId != runId || report.Project != "Demo.csproj"
            || report.SnapshotHash != reservation.SnapshotHash
            || report.Mode != "run" || report.TimedOut || !report.SandboxRemovalSucceeded || report.Error is not null
            || report.RestoreExitCode != 0 || report.BuildExitCode != 0 || report.ExecutionExitCode != 0
            || report.CreateMilliseconds < 0 || report.CopyMilliseconds < 0 || report.RestoreMilliseconds < 0
            || report.BuildMilliseconds < 0 || report.ExecutionMilliseconds < 0 || report.CleanupMilliseconds < 0
            || report.TotalMilliseconds < 0)
            return false;

        return true;
    }

    // Replay can fold an acknowledged outcome descriptor; the host must
    // compare against independently obtained evidence before append or reuse.
    internal static void RequireObserved(OrderGuestOutcome recorded, OrderGuestOutcome? independentlyObserved)
    {
        OrderCheck.Require(independentlyObserved is not null
            && recorded with { ObservedUtcTicks = independentlyObserved.ObservedUtcTicks } == independentlyObserved
            && independentlyObserved.ObservedUtcTicks >= recorded.ObservedUtcTicks, "ORDER_GUEST_RECONCILIATION");
    }
}
