using System.Globalization;
using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

// The caller must provide a host-controlled private local root. Do not expose
// this constructor as a CLI path override. Production root/ACL policy is a
// separate integration gate; this primitive is also used by offline fixtures.
internal sealed class DevelopmentJobStateStore(string trustedRoot,
    Action<string, bool>? validatePrivateEntry = null,
    Action<string>? validateNativeRollback = null)
{
    private readonly string root = ValidateRoot(trustedRoot);

    internal Lease Acquire(int issue) => AcquireCore(issue, orderClaim: false);

    internal Lease AcquireOrder(int issue) => AcquireCore(issue, orderClaim: true);

    // One account-scoped coordinator for cooperating reserved runners, not
    // a daemon-wide security boundary. Do not select a lock by job or path.
    internal FileStream AcquireSandboxBackend() =>
        AcquireSandboxBackendCore(root, validatePrivateEntry);

    private static FileStream AcquireSandboxBackendCore(string root, Action<string, bool>? validatePrivateEntry)
    {
        if (validatePrivateEntry is null)
            throw new InvalidOperationException("Private sandbox backend lease required.");
        _ = ValidateRoot(root);
        validatePrivateEntry(root, true);
        string path = Path.Combine(root, "sandbox-backend.lock");
        bool exists = RegularFileExists(path);
        if (exists)
            validatePrivateEntry(path, false);

        FileStream handle;
        if (!exists && OperatingSystem.IsWindows())
            handle = DevelopmentJobStateRoot.CreateWindowsPrivateFile(path, FileOptions.None);
        else
        {
            var options = new FileStreamOptions
            {
                Mode = exists ? FileMode.Open : FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None
            };
            if (!exists && !OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            handle = new FileStream(path, options);
        }

        try
        {
            _ = RegularFileExists(path);
            if (!OperatingSystem.IsWindows())
                validatePrivateEntry(path, false);
            if (handle.Length != 0)
                throw new InvalidOperationException("Sandbox backend lock contents rejected.");
            // Keep the lock file permanently. Dispose releases the handle;
            // it is never evidence of successful guest cleanup.
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private Lease AcquireCore(int issue, bool orderClaim)
    {
        if (issue <= 0)
            throw new InvalidOperationException("Recovery issue identity rejected.");
        _ = ValidateRoot(root);
        validatePrivateEntry?.Invoke(root, true);
        // One canonical repository is supported by this vertical slice.
        // The key deliberately excludes checkout, base, task and results path.
        string prefix = Path.Combine(root, "issue-" + issue.ToString(CultureInfo.InvariantCulture));
        return new Lease(prefix, issue, validatePrivateEntry, orderClaim, validateNativeRollback);
    }

    // Locate the original issue through the private registry only. The
    // scan is not authorization: acquire the canonical issue lock and
    // re-read the selected state under that lock before using it.
    internal Lease AcquireJob(string jobId)
    {
        if (!DevelopmentPublicationPolicy.IsSha(jobId, 32)
            || jobId != jobId.ToLowerInvariant())
            throw new InvalidOperationException("Recovery job ID rejected.");
        _ = ValidateRoot(root);
        validatePrivateEntry?.Invoke(root, true);
        int? selected = null;
        int count = 0;
        foreach (string path in Directory.EnumerateFiles(root, "issue-*.json"))
        {
            if (++count > 1024)
                throw new InvalidOperationException("Recovery registry scan limit exceeded.");
            string name = Path.GetFileNameWithoutExtension(path);
            if (!int.TryParse(name.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out int issue)
                || issue <= 0 || name != "issue-" + issue.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Recovery registry entry name rejected.");
            if (!RegularFileExists(path))
                throw new InvalidOperationException("Recovery registry entry disappeared.");
            validatePrivateEntry?.Invoke(path, false);
            byte[] bytes = ReadClaimBytes(path);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.GetProperty("schemaVersion").GetInt32() == DevelopmentOrderClaim.Version)
            {
                DevelopmentOrderClaim order = DevelopmentOrderClaim.Parse(bytes, issue);
                if (order.JobId == jobId)
                    throw new InvalidOperationException("Mehrschrittauftrag requires order recovery, not V1 recovery.");
                continue;
            }
            var state = ParseState(bytes, issue);
            if (state.Identity.JobId != jobId)
                continue;
            if (selected is not null)
                throw new InvalidOperationException("Ambiguous recovery job identity.");
            selected = issue;
        }
        if (selected is null)
            throw new InvalidOperationException("Recovery job not found.");
        var owner = Acquire(selected.Value);
        if (owner.Current?.Identity.JobId == jobId)
            return owner;
        owner.Dispose();
        throw new InvalidOperationException("Recovery job identity changed during acquisition.");
    }

    private static DevelopmentJobState ReadState(string path, int issue) => ParseState(ReadClaimBytes(path), issue);

    private static byte[] ReadClaimBytes(string path)
    {
        // Atomic replacement can occur while scanning other issues. The
        // open handle observes a complete old or new checkpoint, not a mix.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        if (stream.Length is < 1 or > DevelopmentJobState.MaxBytes)
            throw new InvalidOperationException("Recovery state size rejected.");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidOperationException("Recovery state changed during read.");
        return bytes;
    }

    private static DevelopmentJobState ParseState(byte[] bytes, int issue)
    {
        var state = DevelopmentJobState.Parse(bytes);
        if (state.Identity.IssueNumber != issue)
            throw new InvalidOperationException("Recovery state belongs to a different issue.");
        return state;
    }

    private static string ValidateRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Recovery root must be absolute.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full != path || !Directory.Exists(full))
            throw new InvalidOperationException("Recovery root must be an existing canonical directory.");
        for (DirectoryInfo? directory = new(full); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || directory.LinkTarget is not null)
                throw new InvalidOperationException("Recovery root traverses a link.");
        }
        return full;
    }

    private static bool RegularFileExists(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
            throw new InvalidOperationException("Recovery file is a link.");
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException("Recovery file is not a regular file.");
        return true;
    }

    // The open file handle provides exclusive sharing across cooperating
    // processes. Keep the lock file permanently; never unlink a live lock.
    // The persisted state remains the claim even after the OS releases the handle.
    internal sealed class Lease : IDisposable
    {
        private readonly FileStream handle;
        private readonly string statePath;
        private readonly string nextPath;
        private readonly string legacyPath;
        private readonly string approvalPath;
        private readonly string journalPath;
        private bool freshOrder;
        private readonly bool orderClaim;
        private byte[]? legacyClaim;
        private readonly int issue;
        private readonly Action<string, bool>? validatePrivateEntry;
        private readonly Action<string>? validateNativeRollback;
        private bool disposed;

        internal Lease(string prefix, int issue, Action<string, bool>? validatePrivateEntry, bool orderClaim,
            Action<string>? validateNativeRollback)
        {
            this.validateNativeRollback = validateNativeRollback;
            this.orderClaim = orderClaim;
            this.issue = issue;
            this.validatePrivateEntry = validatePrivateEntry;
            statePath = prefix + ".json";
            nextPath = prefix + ".next";
            legacyPath = prefix + ".legacy-active";
            approvalPath = prefix + ".order-approval";
            journalPath = prefix + ".order.db";
            string lockPath = prefix + ".lock";
            bool lockExists = RegularFileExists(lockPath);
            if (lockExists)
                validatePrivateEntry?.Invoke(lockPath, false);
            // A racing creator must not be silently accepted as a newly
            // provisioned private file. Open an existing validated lock, or
            // exclusively create a new one. Never overwrite an existing lock.
            handle = OpenPrivateFile(lockPath, lockExists ? FileMode.Open : FileMode.CreateNew, FileOptions.None);
            try
            {
                _ = RegularFileExists(lockPath);
                // Never infer an uncommitted transition from an abandoned temp file.
                // Preserve it for explicit reconciliation; no automatic deletion.
                if (RegularFileExists(nextPath))
                    throw new InvalidOperationException("Uncommitted recovery state requires reconciliation.");
                if (RegularFileExists(legacyPath))
                    throw new InvalidOperationException("Previous non-recoverable issue execution requires reconciliation.");
                if (orderClaim)
                {
                    CurrentOrder = ReadOrder();
                    if (CurrentOrder is not null)
                        _ = RequireArchivedOrder();
                    else if (RegularFileExists(approvalPath) || JournalFilesExist())
                        throw new InvalidOperationException("Order storage exists without its claim.");
                }
                else
                {
                    Current = Read();
                    if (RegularFileExists(approvalPath) || JournalFilesExist())
                        throw new InvalidOperationException("Order storage requires order recovery.");
                }
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        internal DevelopmentJobState? Current { get; private set; }
        internal DevelopmentOrderClaim? CurrentOrder { get; private set; }

        private DevelopmentOrderClaim? ReadOrder()
        {
            if (!RegularFileExists(statePath))
                return null;
            validatePrivateEntry?.Invoke(statePath, false);
            return DevelopmentOrderClaim.Parse(ReadClaimBytes(statePath), issue);
        }

        internal DevelopmentOrderArchive RequireArchivedOrder()
        {
            CheckOpen();
            if (!orderClaim || CurrentOrder is null || ReadOrder() != CurrentOrder
                || !RegularFileExists(approvalPath))
                throw new InvalidOperationException("Order claim or archive missing or changed.");
            validatePrivateEntry?.Invoke(approvalPath, false);
            byte[] bytes = DevelopmentJobEvidence.ReadBoundedFile(approvalPath, DevelopmentOrderArchive.MaxBytes);
            return DevelopmentOrderArchive.Parse(bytes, CurrentOrder);
        }

        internal void ClaimOrder(string jobId, DevelopmentOrderApproval approval)
        {
            CheckOpen();
            if (!orderClaim || CurrentOrder is not null || ReadOrder() is not null
                || RegularFileExists(legacyPath) || RegularFileExists(approvalPath)
                || RegularFileExists(nextPath) || JournalFilesExist() || approval.Binding.IssueNumber != issue)
                throw new InvalidOperationException("Existing order claim cannot be replaced.");

            var archive = DevelopmentOrderArchive.From(approval);
            byte[] bytes = archive.Serialize();
            var claim = DevelopmentOrderClaim.Create(jobId, approval.Binding, DevelopmentJobState.Hash(bytes));
            byte[] claimBytes = claim.Serialize();
            // Claim first: a crash before the archive is durable must block
            // this issue, not silently create a new job. No effects here.
            WriteNew(nextPath, claimBytes);
            File.Move(nextPath, statePath, overwrite: false);
            CurrentOrder = ReadOrder();
            if (CurrentOrder != claim)
                throw new InvalidOperationException("Order claim readback failed.");
            WriteNew(approvalPath, bytes);
            _ = RequireArchivedOrder();
            freshOrder = true;
        }

        private bool JournalFilesExist() =>
            new[] { journalPath, journalPath + "-journal", journalPath + "-wal", journalPath + "-shm",
                ArtifactPath("S01"), ArtifactPath("S02") }.Any(RegularFileExists)
            || Enumerable.Range(1, OrderCandidateCheckpoint.MaxSequence)
                .Any(sequence => RegularFileExists(CandidatePath(sequence)) || RegularFileExists(OperationEvidencePath(sequence))
                    || RegularFileExists(GuestReportPath(sequence)) || RegularFileExists(SnapshotManifestPath(sequence))
                    || RegularFileExists(GuestDispatchPath(sequence)) || RegularFileExists(ModelDispatchPath(sequence)));

        // Fixed, host-owned path; only the same lease that made the claim
        // can initialize an empty file. Never recreate a missing journal.
        internal string CreateOrderJournal()
        {
            RequireOrderStorage();
            if (!freshOrder || JournalFilesExist())
                throw new InvalidOperationException("Order journal initialization rejected.");
            freshOrder = false;
            WriteNew(journalPath, []);
            ValidateOrderJournalFiles();
            return journalPath;
        }

        internal string RequireOrderJournal()
        {
            ValidateOrderJournalFiles();
            using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[16];
            if (stream.Length < 100)
                throw new InvalidOperationException("Order journal is empty or incomplete.");
            stream.ReadExactly(header);
            if (!header.SequenceEqual("SQLite format 3\0"u8))
                throw new InvalidOperationException("Order journal header rejected.");
            return journalPath;
        }

        internal void ValidateOrderJournalFiles()
        {
            RequireOrderStorage();
            if (!RegularFileExists(journalPath))
                throw new InvalidOperationException("Order journal missing.");
            validatePrivateEntry!(journalPath, false);
            // This increment uses rollback DELETE journaling only. A hot
            // rollback journal is preserved for SQLite to recover; WAL/SHM
            // are outside the reviewed mode and must not be silently removed.
            if (RegularFileExists(journalPath + "-wal") || RegularFileExists(journalPath + "-shm"))
                throw new InvalidOperationException("Unexpected order journal mode.");
            string rollback = journalPath + "-journal";
            if (RegularFileExists(rollback))
            {
                if (validateNativeRollback is not null)
                    validateNativeRollback(rollback);
                else
                    validatePrivateEntry!(rollback, false);
            }
        }

        private string ArtifactPath(string step) => step switch
        {
            "S01" => journalPath + ".s01",
            "S02" => journalPath + ".s02",
            _ => throw new InvalidOperationException("Unknown order artifact slot.")
        };

        internal string StoreOrderArtifact(string step, byte[] bytes)
        {
            RequireOrderStorage();
            OrderCheck.Require(bytes.Length is > 0 and <= 64 * 1024, "ORDER_ARTIFACT_SIZE");
            string path = ArtifactPath(step);
            if (RegularFileExists(path))
                throw new InvalidOperationException("Order artifact already exists.");
            WriteNew(path, bytes);
            string hash = DevelopmentJobState.Hash(bytes);
            _ = RequireOrderArtifact(step, hash);
            return hash;
        }

        internal byte[] RequireOrderArtifact(string step, string hash)
        {
            RequireOrderStorage();
            string path = ArtifactPath(step);
            if (!OrderCheck.Hash(hash) || !RegularFileExists(path))
                throw new InvalidOperationException("Order artifact missing or invalid digest.");
            validatePrivateEntry!(path, false);
            byte[] bytes = DevelopmentJobEvidence.ReadBoundedFile(path, 64 * 1024);
            OrderCheck.Require(DevelopmentJobState.Hash(bytes) == hash, "ORDER_ARTIFACT_HASH");
            return bytes;
        }

        private string CandidatePath(int sequence)
        {
            OrderCheck.Require(sequence is >= 1 and <= OrderCandidateCheckpoint.MaxSequence, "ORDER_CANDIDATE_SEQUENCE");
            return journalPath + ".candidate-" + sequence.ToString(CultureInfo.InvariantCulture);
        }

        // Host supplies only an extracted, policy-approved source candidate.
        // The journal descriptor is committed after exclusive durable write
        // and exact readback. A file without its event is not a checkpoint.
        internal void StoreOrderCandidate(OrderCandidateCheckpoint candidate, byte[] bytes)
        {
            RequireOrderStorage();
            DevelopmentOrderClaim claim = CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
            string path = CandidatePath(candidate.ValidateBinding(claim));
            candidate.ValidateBytes(bytes);
            if (RegularFileExists(path))
                throw new InvalidOperationException("Order candidate already exists.");
            WriteNew(path, bytes);
            _ = RequireOrderCandidate(candidate);
        }

        internal byte[] RequireOrderCandidate(OrderCandidateCheckpoint candidate)
        {
            RequireOrderStorage();
            DevelopmentOrderClaim claim = CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
            string path = CandidatePath(candidate.ValidateBinding(claim));
            if (!RegularFileExists(path))
                throw new InvalidOperationException("Order candidate missing.");
            validatePrivateEntry!(path, false);
            byte[] bytes = DevelopmentJobEvidence.ReadBoundedFile(path, OrderCandidateCheckpoint.MaxBytes);
            candidate.ValidateBytes(bytes);
            return bytes;
        }

        private string OperationEvidencePath(int sequence)
        {
            OrderCheck.Require(sequence is >= 1 and <= OrderCandidateCheckpoint.MaxSequence, "ORDER_EVIDENCE_SEQUENCE");
            return journalPath + ".evidence-" + sequence.ToString(CultureInfo.InvariantCulture);
        }

        // Immutable exact host-observation bytes; never a second mutable
        // workflow authority. Persist completion only after readback.
        internal OrderOperationCompletion StoreOrderOperationEvidence(OrderOperationEvidence evidence)
        {
            RequireOrderStorage();
            DevelopmentOrderClaim claim = CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
            string path = OperationEvidencePath(evidence.ValidateBinding(claim));
            if (RegularFileExists(path))
                throw new InvalidOperationException("Order operation evidence already exists.");
            byte[] bytes = evidence.Serialize();
            var completion = evidence.Completion();
            WriteNew(path, bytes);
            _ = RequireOrderOperationEvidence(evidence.Reservation, completion);
            return completion;
        }

        internal OrderOperationEvidence RequireOrderOperationEvidence(OrderOperationReservation reservation,
            OrderOperationCompletion completion)
        {
            RequireOrderStorage();
            DevelopmentOrderClaim claim = CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
            string prefix = claim.JobId + ":";
            OrderCheck.Require(reservation.OperationId is not null
                && reservation.OperationId.StartsWith(prefix, StringComparison.Ordinal), "ORDER_EVIDENCE_SEQUENCE");
            string id = reservation.OperationId ?? throw new InvalidOperationException("ORDER_EVIDENCE_SEQUENCE");
            if (!int.TryParse(id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int sequence)
                || sequence is < 1 or > OrderCandidateCheckpoint.MaxSequence
                || id != prefix + sequence.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("ORDER_EVIDENCE_SEQUENCE");
            string path = OperationEvidencePath(sequence);
            if (!RegularFileExists(path))
                throw new InvalidOperationException("Order operation evidence missing.");
            validatePrivateEntry!(path, false);
            byte[] bytes = DevelopmentJobEvidence.ReadBoundedFile(path, OrderOperationEvidence.MaxBytes);
            OrderCheck.Require(DevelopmentJobState.Hash(bytes) == completion.EvidenceHash, "ORDER_EVIDENCE_HASH");
            var evidence = OrderStorageJson.Decode<OrderOperationEvidence>(bytes, OrderOperationEvidence.MaxBytes);
            evidence.ValidateCompletion(claim, reservation, completion);
            return evidence;
        }

        private string ModelDispatchPath(int sequence) =>
            journalPath + ".model-dispatch-" + sequence.ToString(CultureInfo.InvariantCulture);

        private sealed record ModelDispatchReceipt(int Version, string ArchiveHash,
            OrderOperationReservation Reservation, string InputHash);

        private (string Path, byte[] Bytes) BoundModelDispatch(OrderOperationReservation reservation)
        {
            RequireOrderStorage();
            DevelopmentOrderClaim claim = CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
            OrderCheck.Require(reservation.Version == 4 && reservation.Kind == "model" && reservation.Step == "S01"
                && reservation.SnapshotHash is null && reservation.ReservedSeconds > 0 && reservation.ObservedUtcTicks > 0,
                "ORDER_MODEL_DISPATCH_RESERVATION");
            (reservation.Assignment ?? throw new InvalidOperationException("ORDER_OPERATION_ASSIGNMENT")).Validate();
            string prefix = claim.JobId + ":";
            string id = reservation.OperationId;
            if (!id.StartsWith(prefix, StringComparison.Ordinal)
                || !int.TryParse(id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int sequence)
                || sequence is < 1 or > OrderCandidateCheckpoint.MaxSequence
                || id != prefix + sequence.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("ORDER_MODEL_DISPATCH_SEQUENCE");
            byte[] bytes = OrderStorageJson.Encode(new ModelDispatchReceipt(1, claim.ArchiveHash, reservation,
                claim.Binding.CalculatorBaselineHash), DevelopmentOrderOperationLedger.EventMaxBytes);
            return (ModelDispatchPath(sequence), bytes);
        }

        // This is a private once-only intent, not an outcome or fresh ack.
        // Consume before calling the external provider; never on replay.
        internal void ConsumeOrderModelDispatch(DevelopmentOrderOperationLedger acknowledged,
            OrderOperationReservation reservation)
        {
            var (path, bytes) = BoundModelDispatch(reservation);
            DevelopmentOrderClaim claim = CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
            OrderCheck.Require(acknowledged.Pending == reservation && acknowledged.ArchiveHash == claim.ArchiveHash,
                "ORDER_MODEL_DISPATCH_PENDING");
            if (RegularFileExists(path))
                throw new InvalidOperationException("Order model dispatch already consumed.");
            var limits = acknowledged.CurrentLimits ?? throw new InvalidOperationException("Order budget missing.");
            _ = acknowledged.DispatchAllowance(limits, reservation.OperationId, DateTime.UtcNow.Ticks);
            WriteNew(path, bytes);
            RequireOrderModelDispatch(reservation);
        }

        internal void RequireOrderModelDispatch(OrderOperationReservation reservation)
        {
            var (path, expected) = BoundModelDispatch(reservation);
            if (!RegularFileExists(path))
                throw new InvalidOperationException("Order model dispatch receipt missing.");
            validatePrivateEntry!(path, false);
            byte[] bytes = DevelopmentJobEvidence.ReadBoundedFile(path, DevelopmentOrderOperationLedger.EventMaxBytes);
            OrderCheck.Require(bytes.AsSpan().SequenceEqual(expected), "ORDER_MODEL_DISPATCH_BYTES");
        }

        private string GuestReportPath(int sequence)
        {
            OrderCheck.Require(sequence is >= 1 and <= OrderCandidateCheckpoint.MaxSequence, "ORDER_GUEST_REPORT_SEQUENCE");
            return journalPath + ".guest-report-" + sequence.ToString(CultureInfo.InvariantCulture);
        }

        private string SnapshotManifestPath(int sequence) =>
            journalPath + ".snapshot-" + sequence.ToString(CultureInfo.InvariantCulture);

        private int BoundGuestSequence(OrderOperationReservation reservation)
        {
            RequireOrderStorage();
            DevelopmentOrderClaim claim = CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
            _ = DevelopmentOrderGuestReconciliation.RunId(reservation);
            string prefix = claim.JobId + ":";
            string id = reservation.OperationId;
            if (!id.StartsWith(prefix, StringComparison.Ordinal)
                || !int.TryParse(id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int sequence)
                || sequence is < 1 or > OrderCandidateCheckpoint.MaxSequence
                || id != prefix + sequence.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("ORDER_GUEST_REPORT_SEQUENCE");
            return sequence;
        }

        // Store before the fresh reservation is acknowledged. An orphan file
        // is not permission to dispatch or to replace a reserved identity.
        internal void StoreOrderSnapshotManifest(OrderOperationReservation reservation, byte[] bytes)
        {
            string path = SnapshotManifestPath(BoundGuestSequence(reservation));
            DevelopmentOrderSnapshot.ValidateBytes(bytes, "Demo.csproj", reservation.SnapshotHash);
            if (RegularFileExists(path))
                throw new InvalidOperationException("Order snapshot manifest already exists.");
            WriteNew(path, bytes);
            _ = RequireOrderSnapshotManifest(reservation);
        }

        internal byte[] RequireOrderSnapshotManifest(OrderOperationReservation reservation)
        {
            string path = SnapshotManifestPath(BoundGuestSequence(reservation));
            if (!RegularFileExists(path))
                throw new InvalidOperationException("Order snapshot manifest missing.");
            validatePrivateEntry!(path, false);
            byte[] bytes = DevelopmentJobEvidence.ReadBoundedFile(path, DevelopmentOrderSnapshot.MaxManifestBytes);
            DevelopmentOrderSnapshot.ValidateBytes(bytes, "Demo.csproj", reservation.SnapshotHash);
            return bytes;
        }

        private sealed record GuestDispatchReceipt(int Version, string ArchiveHash,
            OrderOperationReservation Reservation, string RunId);

        private string GuestDispatchPath(int sequence) =>
            journalPath + ".guest-dispatch-" + sequence.ToString(CultureInfo.InvariantCulture);

        private byte[] GuestDispatchBytes(OrderOperationReservation reservation)
        {
            DevelopmentOrderClaim claim = CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
            return OrderStorageJson.Encode(new GuestDispatchReceipt(1, claim.ArchiveHash, reservation,
                DevelopmentOrderGuestReconciliation.RunId(reservation)), DevelopmentOrderOperationLedger.EventMaxBytes);
        }

        // A one-shot private intent receipt, not a workflow transition or
        // proof that sbx create succeeded. Only the trusted host may consume
        // it after a fresh journal ack. A recovered ledger is not a fresh ack.
        internal void ConsumeOrderGuestDispatch(DevelopmentOrderOperationLedger acknowledged,
            OrderOperationReservation reservation)
        {
            string path = GuestDispatchPath(BoundGuestSequence(reservation));
            DevelopmentOrderClaim claim = CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
            OrderCheck.Require(acknowledged.Pending == reservation && acknowledged.ArchiveHash == claim.ArchiveHash,
                "ORDER_GUEST_DISPATCH_PENDING");
            _ = RequireOrderSnapshotManifest(reservation);
            if (RegularFileExists(path))
                throw new InvalidOperationException("Order guest dispatch already consumed.");
            var limits = acknowledged.CurrentLimits ?? throw new InvalidOperationException("Order budget missing.");
            _ = acknowledged.DispatchAllowance(limits, reservation.OperationId, DateTime.UtcNow.Ticks);
            WriteNew(path, GuestDispatchBytes(reservation));
            RequireOrderGuestDispatch(reservation);
        }

        internal void RequireOrderGuestDispatch(OrderOperationReservation reservation)
        {
            string path = GuestDispatchPath(BoundGuestSequence(reservation));
            if (!RegularFileExists(path))
                throw new InvalidOperationException("Order guest dispatch receipt missing.");
            validatePrivateEntry!(path, false);
            byte[] bytes = DevelopmentJobEvidence.ReadBoundedFile(path, DevelopmentOrderOperationLedger.EventMaxBytes);
            OrderCheck.Require(bytes.AsSpan().SequenceEqual(GuestDispatchBytes(reservation)), "ORDER_GUEST_DISPATCH_BYTES");
        }

        private string BoundGuestReportPath(OrderGuestOutcome outcome, OrderOperationReservation reservation)
        {
            int sequence = BoundGuestSequence(reservation);
            DevelopmentOrderClaim claim = CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
            outcome.Validate(claim.ArchiveHash, reservation);
            _ = RequireOrderSnapshotManifest(reservation);
            RequireOrderGuestDispatch(reservation);
            return GuestReportPath(sequence);
        }

        private static void ValidateGuestReportBytes(OrderGuestOutcome outcome, byte[] bytes)
        {
            OrderCheck.Require(DevelopmentOrderGuestReconciliation.IsSuccessfulReport(outcome.Reservation, bytes)
                && DevelopmentJobState.Hash(bytes) == outcome.ReportHash, "ORDER_GUEST_REPORT_BYTES");
        }

        // Only successful, independently observed reports are retained by
        // this slice. Exact original bytes; no normalization or overwrite.
        // This storage API is not an independent absence observation.
        internal void StoreOrderGuestReport(OrderGuestOutcome outcome, OrderOperationReservation reservation, byte[] bytes)
        {
            string path = BoundGuestReportPath(outcome, reservation);
            ValidateGuestReportBytes(outcome, bytes);
            if (RegularFileExists(path))
                throw new InvalidOperationException("Order guest report already exists.");
            WriteNew(path, bytes);
            _ = RequireOrderGuestReport(outcome, reservation);
        }

        internal byte[] RequireOrderGuestReport(OrderGuestOutcome outcome, OrderOperationReservation reservation)
        {
            string path = BoundGuestReportPath(outcome, reservation);
            if (!RegularFileExists(path))
                throw new InvalidOperationException("Order guest report missing.");
            validatePrivateEntry!(path, false);
            byte[] bytes = DevelopmentJobEvidence.ReadBoundedFile(path, DevelopmentOrderGuestReconciliation.MaxReportBytes);
            ValidateGuestReportBytes(outcome, bytes);
            return bytes;
        }

        internal FileStream AcquireOrderSandboxBackend()
        {
            RequireOrderStorage();
            return AcquireSandboxBackendCore(Path.GetDirectoryName(journalPath)!, validatePrivateEntry);
        }

        private void RequireOrderStorage()
        {
            _ = RequireArchivedOrder();
            if (validatePrivateEntry is null)
                throw new InvalidOperationException("Private order journal policy required.");
            validatePrivateEntry(Path.GetDirectoryName(journalPath)!, true);
        }

        private void RequireV1()
        {
            CheckOpen();
            if (orderClaim)
                throw new InvalidOperationException("V1 operation rejected on an order lease.");
        }

        internal DevelopmentJobState RequireCurrent()
        {
            RequireV1();
            if (Current is null || Read() != Current)
                throw new InvalidOperationException("Recovery claim missing or changed.");
            return Current;
        }

        private DevelopmentJobState? Read()
        {
            if (!RegularFileExists(statePath))
                return null;
            validatePrivateEntry?.Invoke(statePath, false);
            return ReadState(statePath, issue);
        }

        internal void Claim(DevelopmentJobState initial)
        {
            RequireV1();
            initial.Validate();
            if (legacyClaim is not null || RegularFileExists(legacyPath)
                || Current is not null || initial.Stage != DevelopmentJobState.Accepted
                || initial.Identity.IssueNumber != issue || Read() is not null)
                throw new InvalidOperationException("An existing recovery claim cannot be replaced.");
            Write(initial, replace: false);
        }

        // Existing issue-journal/non-publication paths do not acquire recovery
        // authority. They still use the very same issue lease, and leave a
        // durable blocker on interruption rather than bypassing ownership.
        internal void ClaimLegacy(string jobId)
        {
            RequireV1();
            if (!DevelopmentPublicationPolicy.IsSha(jobId, 32)
                || legacyClaim is not null || Current is not null || Read() is not null
                || RegularFileExists(legacyPath))
                throw new InvalidOperationException("Existing issue claim cannot be replaced.");
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes("v1:" + jobId);
            using (var stream = OpenPrivateFile(legacyPath, FileMode.CreateNew, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            validatePrivateEntry?.Invoke(legacyPath, false);
            legacyClaim = bytes;
        }

        internal void CompleteLegacy()
        {
            RequireV1();
            if (legacyClaim is null || !RegularFileExists(legacyPath))
                throw new InvalidOperationException("Non-recoverable issue claim missing.");
            validatePrivateEntry?.Invoke(legacyPath, false);
            if (!legacyClaim.SequenceEqual(DevelopmentJobEvidence.ReadBoundedFile(legacyPath, 64)))
                throw new InvalidOperationException("Non-recoverable issue claim changed.");
            File.Delete(legacyPath);
            legacyClaim = null;
        }

        internal void Commit(DevelopmentJobState next)
        {
            RequireV1();
            if (Current is null || Read() != Current)
                throw new InvalidOperationException("Recovery claim missing or changed.");
            DevelopmentJobState.ValidateTransition(Current, next);
            Write(next, replace: true);
        }

        private void Write(DevelopmentJobState next, bool replace)
        {
            byte[] bytes = next.Serialize();
            if (RegularFileExists(nextPath))
                throw new InvalidOperationException("Uncommitted recovery state requires reconciliation.");
            // Same-directory temporary file, exclusively created. Flush the
            // contents before rename. This is a process-crash checkpoint,
            // not a claim of guaranteed durability through power loss.
            WriteNew(nextPath, bytes);
            File.Move(nextPath, statePath, overwrite: replace);
            Current = Read();
            if (Current != next)
                throw new InvalidOperationException("Recovery checkpoint readback failed.");
        }

        private void WriteNew(string path, byte[] bytes)
        {
            using (var stream = OpenPrivateFile(path, FileMode.CreateNew, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            validatePrivateEntry?.Invoke(path, false);
        }

        private FileStream OpenPrivateFile(string path, FileMode mode, FileOptions options)
        {
            if (OperatingSystem.IsWindows() && mode == FileMode.CreateNew && validatePrivateEntry is not null)
                return DevelopmentJobStateRoot.CreateWindowsPrivateFile(path, options);
            var settings = new FileStreamOptions
            {
                Mode = mode,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = options
            };
            if (!OperatingSystem.IsWindows() && mode == FileMode.CreateNew)
                settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            return new FileStream(path, settings);
        }

        private void CheckOpen()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            handle.Dispose();
        }
    }
}
