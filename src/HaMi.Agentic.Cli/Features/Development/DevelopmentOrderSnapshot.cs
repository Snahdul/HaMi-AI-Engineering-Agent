using System.Security.Cryptography;

namespace HaMi.Agentic.Cli.Features.Development;

// Canonical input identity: normalized relative paths, exact byte lengths
// and SHA-256, sorted ordinally. Never include absolute host paths.
// Caller must exclusively control the source/staging directories.
internal static class DevelopmentOrderSnapshot
{
    private const int MaxFileBytes = 16 * 1024 * 1024;
    internal const int MaxManifestBytes = 1024 * 1024;

    private sealed record SnapshotFile(string Path, long Bytes, string Sha256);
    private sealed record SnapshotManifest(int Version, SnapshotFile[] Files);

    internal static string Capture(string source, string project) =>
        DevelopmentJobState.Hash(CaptureBytes(source, project));

    internal static byte[] CaptureBytes(string source, string project) =>
        OrderStorageJson.Encode(CaptureManifest(source, project), MaxManifestBytes);

    // Build the expected manifest from trusted in-memory inputs, not from
    // a caller's materialized directory. Use exactly the existing format.
    internal static byte[] CaptureBytes(IReadOnlyDictionary<string, byte[]> files, string project)
    {
        OrderCheck.Require(files.Count is > 0 and <= 512, "ORDER_SNAPSHOT_MANIFEST");
        long total = 0;
        var entries = new List<SnapshotFile>();
        foreach (var file in files)
        {
            OrderCheck.Require(file.Value.LongLength <= MaxFileBytes - total, "ORDER_SNAPSHOT_SIZE");
            total += file.Value.LongLength;
            entries.Add(new(file.Key, file.Value.LongLength, DevelopmentJobState.Hash(file.Value)));
        }

        var manifest = new SnapshotManifest(1, entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray());
        ValidateManifest(manifest, project);
        return OrderStorageJson.Encode(manifest, MaxManifestBytes);
    }

    internal static void ValidateBytes(byte[] bytes, string project, string? expectedHash)
    {
        OrderCheck.Require(OrderCheck.Hash(expectedHash) && bytes.Length is > 0 and <= MaxManifestBytes
            && DevelopmentJobState.Hash(bytes) == expectedHash, "ORDER_SNAPSHOT_MISMATCH");
        var manifest = OrderStorageJson.Decode<SnapshotManifest>(bytes, MaxManifestBytes);
        ValidateManifest(manifest, project);
    }

    private static void ValidateManifest(SnapshotManifest manifest, string project)
    {
        OrderCheck.Require(manifest.Version == 1 && manifest.Files is { Length: > 0 and <= 512 },
            "ORDER_SNAPSHOT_MANIFEST");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previous = null;
        long total = 0;
        foreach (var entry in manifest.Files)
        {
            if (entry is null || entry.Path is not { Length: > 0 and <= 1024 } path)
                throw new InvalidOperationException("ORDER_SNAPSHOT_PATH");
            OrderCheck.Require(!path.Contains('\\') && !path.Contains(':') && !path.Contains('\uFFFD')
                && !path.Any(char.IsControl) && path.Split('/').All(part => part is not ("" or "." or ".."))
                && paths.Add(path) && (previous is null || string.CompareOrdinal(previous, path) < 0),
                "ORDER_SNAPSHOT_PATH");
            OrderCheck.Require(entry.Bytes >= 0 && entry.Bytes <= MaxFileBytes - total
                && OrderCheck.Hash(entry.Sha256), "ORDER_SNAPSHOT_SIZE");
            total += entry.Bytes;
            previous = path;
        }
        foreach (string path in paths)
            for (int separator = path.IndexOf('/'); separator >= 0; separator = path.IndexOf('/', separator + 1))
                OrderCheck.Require(!paths.Contains(path[..separator]), "ORDER_SNAPSHOT_PATH");
        OrderCheck.Require(manifest.Files.Any(entry => entry.Path == project), "ORDER_SNAPSHOT_PROJECT");
    }

    private static string ManifestHash(SnapshotManifest manifest) =>
        DevelopmentJobState.Hash(OrderStorageJson.Encode(manifest, MaxManifestBytes));

    private static SnapshotManifest CaptureManifest(string source, string project)
    {
        string[] paths = DevelopmentSandboxRunner.InspectSnapshot(source, project);
        var entries = new List<SnapshotFile>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (string relative in paths)
        {
            string normalized = relative.Replace(Path.DirectorySeparatorChar, '/');
            OrderCheck.Require(!normalized.Contains('\\') && normalized.Length <= 1024
                && !normalized.Contains('\uFFFD') && !normalized.Any(char.IsControl)
                && unique.Add(normalized), "ORDER_SNAPSHOT_PATH");
            string path = Path.Combine(source, relative);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = stream.Length;
            OrderCheck.Require(length >= 0 && length <= MaxFileBytes - total, "ORDER_SNAPSHOT_SIZE");
            // Bound the actual bytes read even if the source changes.
            byte[] bytes = new byte[checked((int)length)];
            stream.ReadExactly(bytes);
            OrderCheck.Require(stream.ReadByte() == -1, "ORDER_SNAPSHOT_SIZE");
            total += length;
            entries.Add(new(normalized, length, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        var manifest = new SnapshotManifest(1, entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray());
        ValidateManifest(manifest, project);
        return manifest;
    }

    internal static void Require(string source, string project, string? expectedHash)
    {
        OrderCheck.Require(OrderCheck.Hash(expectedHash)
            && Capture(source, project) == expectedHash, "ORDER_SNAPSHOT_MISMATCH");
    }

    // Exact GNU sha256sum --zero records. The content digest binds the
    // file length as well. NUL separators make whitespace in paths literal.
    internal static string[] RequireGuestRecords(string source, string project, string? expectedHash)
    {
        SnapshotManifest manifest = CaptureManifest(source, project);
        OrderCheck.Require(OrderCheck.Hash(expectedHash) && ManifestHash(manifest) == expectedHash,
            "ORDER_SNAPSHOT_MISMATCH");
        return manifest.Files.Select(file => file.Sha256.ToLowerInvariant() + "  ./" + file.Path).ToArray();
    }

    internal const string GuestEndMarker = "HAMI_SNAPSHOT_END";

    internal static void RequireGuestRecords(string[] expected, int exitCode, string output, string error)
    {
        // No tolerant parsing, duplicate collapse, diagnostic logging or
        // trusting a guest-supplied aggregate hash. Truncation fails closed.
        string[] actual = output.Split('\0');
        OrderCheck.Require(exitCode == 0 && error.Length == 0
            && actual.Length == expected.Length + 2 && actual[^1].Length == 0
            && actual[^2] == GuestEndMarker
            && actual.Take(expected.Length).OrderBy(record => record, StringComparer.Ordinal)
                .SequenceEqual(expected.OrderBy(record => record, StringComparer.Ordinal), StringComparer.Ordinal),
            "ORDER_GUEST_SNAPSHOT_MISMATCH");
    }

    internal static void SelfTest(string source)
    {
        const string project = "Demo.csproj";
        byte[] manifestBytes = CaptureBytes(source, project);
        string original = DevelopmentJobState.Hash(manifestBytes);
        ValidateBytes(manifestBytes, project, original);
        Require(source, project, original);
        void RejectManifest(byte[] candidate, string digest)
        {
            try { ValidateBytes(candidate, project, digest); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Invalid manifest bytes were accepted.");
        }

        RejectManifest([.. manifestBytes, 0x20], original);
        byte[] alternateEncoding = [.. manifestBytes, 0x20];
        RejectManifest(alternateEncoding, DevelopmentJobState.Hash(alternateEncoding));
        var manifest = CaptureManifest(source, project);
        byte[] reversed = OrderStorageJson.Encode(manifest with { Files = manifest.Files.Reverse().ToArray() }, MaxManifestBytes);
        RejectManifest(reversed, DevelopmentJobState.Hash(reversed));
        byte[] duplicate = OrderStorageJson.Encode(manifest with { Files = [.. manifest.Files, manifest.Files[0]] }, MaxManifestBytes);
        RejectManifest(duplicate, DevelopmentJobState.Hash(duplicate));
        string file = Path.Combine(source, "Program.cs");
        byte[] bytes = File.ReadAllBytes(file);
        string extra = Path.Combine(source, "Added.cs");
        void RejectChanged()
        {
            try { Require(source, project, original); }
            catch (InvalidOperationException ex) when (ex.Message == "ORDER_SNAPSHOT_MISMATCH") { return; }
            throw new InvalidOperationException("Changed snapshot was accepted.");
        }

        try
        {
            byte[] modified = (byte[])bytes.Clone();
            modified[0] ^= 1;
            File.WriteAllBytes(file, modified);
            RejectChanged();
            File.WriteAllBytes(file, bytes);
            File.WriteAllBytes(extra, bytes);
            RejectChanged();
            File.Delete(extra);
            File.Move(file, extra);
            RejectChanged();
            File.Move(extra, file);
            File.Delete(file);
            RejectChanged();
        }
        finally
        {
            File.WriteAllBytes(file, bytes);
            File.Delete(extra);
        }
        Require(source, project, original);
        Console.WriteLine("PASS: deterministic snapshot identity rejects modified, added, renamed and removed files.");
    }
}
