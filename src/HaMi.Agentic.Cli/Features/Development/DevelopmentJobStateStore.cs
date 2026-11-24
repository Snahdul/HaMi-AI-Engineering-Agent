using System.Globalization;

namespace HaMi.Agentic.Cli.Features.Development;

// The caller must provide a host-controlled private local root. Do not expose
// this constructor as a CLI path override. Production root/ACL policy is a
// separate integration gate; this primitive is also used by offline fixtures.
internal sealed class DevelopmentJobStateStore(string trustedRoot,
    Action<string, bool>? validatePrivateEntry = null)
{
    private readonly string root = ValidateRoot(trustedRoot);

    internal Lease Acquire(int issue)
    {
        if (issue <= 0)
            throw new InvalidOperationException("Recovery issue identity rejected.");
        _ = ValidateRoot(root);
        validatePrivateEntry?.Invoke(root, true);
        // One canonical repository is supported by this vertical slice.
        // The key deliberately excludes checkout, base, task and results path.
        string prefix = Path.Combine(root, "issue-" + issue.ToString(CultureInfo.InvariantCulture));
        return new Lease(prefix, issue, validatePrivateEntry);
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
            var state = ReadState(path, issue);
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

    private static DevelopmentJobState ReadState(string path, int issue)
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
        private byte[]? legacyClaim;
        private readonly int issue;
        private readonly Action<string, bool>? validatePrivateEntry;
        private bool disposed;

        internal Lease(string prefix, int issue, Action<string, bool>? validatePrivateEntry)
        {
            this.issue = issue;
            this.validatePrivateEntry = validatePrivateEntry;
            statePath = prefix + ".json";
            nextPath = prefix + ".next";
            legacyPath = prefix + ".legacy-active";
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
                Current = Read();
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        internal DevelopmentJobState? Current { get; private set; }

        internal DevelopmentJobState RequireCurrent()
        {
            CheckOpen();
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
            CheckOpen();
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
            CheckOpen();
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
            CheckOpen();
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
            CheckOpen();
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
            using (var stream = OpenPrivateFile(nextPath, FileMode.CreateNew, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            validatePrivateEntry?.Invoke(nextPath, false);
            File.Move(nextPath, statePath, overwrite: replace);
            Current = Read();
            if (Current != next)
                throw new InvalidOperationException("Recovery checkpoint readback failed.");
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
