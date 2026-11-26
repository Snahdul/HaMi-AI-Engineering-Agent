using System.Security.Cryptography;

namespace HaMi.Agentic.Cli.Features.Development;

// Canonical input identity: normalized relative paths, exact byte lengths
// and SHA-256, sorted ordinally. Never include absolute host paths.
// Caller must exclusively control the source/staging directories.
internal static class DevelopmentOrderSnapshot
{
    private const int MaxFileBytes = 16 * 1024 * 1024;
    private const int MaxManifestBytes = 1024 * 1024;

    private sealed record SnapshotFile(string Path, long Bytes, string Sha256);
    private sealed record SnapshotManifest(int Version, SnapshotFile[] Files);

    internal static string Capture(string source, string project)
    {
        string[] paths = DevelopmentSandboxRunner.InspectSnapshot(source, project);
        var entries = new List<SnapshotFile>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (string relative in paths)
        {
            string normalized = relative.Replace(Path.DirectorySeparatorChar, '/');
            OrderCheck.Require(!normalized.Contains('\\') && normalized.Length <= 1024
                && !normalized.Any(char.IsControl) && unique.Add(normalized), "ORDER_SNAPSHOT_PATH");
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
        return DevelopmentJobState.Hash(OrderStorageJson.Encode(manifest, MaxManifestBytes));
    }

    internal static void Require(string source, string project, string? expectedHash)
    {
        OrderCheck.Require(OrderCheck.Hash(expectedHash)
            && Capture(source, project) == expectedHash, "ORDER_SNAPSHOT_MISMATCH");
    }

    internal static void SelfTest(string source)
    {
        const string project = "Demo.csproj";
        string original = Capture(source, project);
        Require(source, project, original);
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
