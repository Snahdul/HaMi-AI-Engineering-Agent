using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaMi.Agentic.Cli.Features.Development;

// Only a trusted in-process coordinator result may create the original
// evidence. A caller-supplied report is not authorization. The durable
// private state will bind the manifest hash before publication.
internal static class DevelopmentJobEvidence
{
    internal const string OriginalReportName = "agent-report.original.json";
    internal const string ManifestName = "agent-evidence.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    internal sealed record Artifact(string Path, int Bytes, string Sha256);
    internal sealed record Manifest(int SchemaVersion, string JobId, Artifact[] Artifacts, string[] SandboxRunIds);

    internal static string JobRoot(DevelopmentJobIdentity identity) =>
        Path.Combine(identity.ResultsRoot, "agent-job-" + identity.JobId);

    internal static async Task<string> SealAsync(
        string repo, DevelopmentJobIdentity identity, DevelopmentJobReport trustedReport)
    {
        _ = DevelopmentJobState.Create(identity);
        // Reuse the existing independently reviewed publication gate on
        // the actual candidate, before sealing any recovery authorization.
        _ = await DevelopmentPublicationPolicy.ValidateCandidateAsync(repo, identity.ResultsRoot,
            trustedReport, identity.IssueNumber, identity.BaseCommit, identity.PublishDraft);
        string root = JobRoot(identity);
        byte[] reportBytes = JsonSerializer.SerializeToUtf8Bytes(trustedReport, JsonOptions);
        var manifest = BuildManifest(identity, trustedReport, reportBytes);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        WriteNew(Path.Combine(root, OriginalReportName), reportBytes);
        WriteNew(Path.Combine(root, ManifestName), bytes);
        if (!bytes.SequenceEqual(ReadBoundedFile(Path.Combine(root, ManifestName), 64 * 1024))
            || !reportBytes.SequenceEqual(ReadBoundedFile(Path.Combine(root, OriginalReportName), 64 * 1024)))
            throw new InvalidOperationException("Recovery evidence readback failed.");
        return DevelopmentJobState.Hash(bytes);
    }

    // The private, locked checkpoint is the trust anchor. Never accept a
    // report path or manifest digest from command-line arguments.
    internal static (DevelopmentJobReport Report, Manifest Evidence) ReadVerified(
        DevelopmentJobStateStore.Lease owner)
    {
        var state = owner.RequireCurrent();
        state.Validate();
        if (state.Stage is not (DevelopmentJobState.CandidateVerified
            or DevelopmentJobState.PublicationPending or DevelopmentJobState.Published))
            throw new InvalidOperationException("Recovery stage is not eligible.");
        string root = JobRoot(state.Identity);
        byte[] bytes = ReadBoundedFile(Path.Combine(root, ManifestName), 64 * 1024);
        if (DevelopmentJobState.Hash(bytes) != state.EvidenceHash)
            throw new InvalidOperationException("Recovery manifest differs from private checkpoint.");
        var expected = JsonSerializer.Deserialize<Manifest>(bytes, JsonOptions)
            ?? throw new InvalidOperationException("Recovery manifest missing.");
        if (expected.SchemaVersion != 1 || expected.JobId != state.Identity.JobId
            || expected.Artifacts is null || expected.Artifacts.Length is < 1 or > 16
            || expected.SandboxRunIds is null || expected.SandboxRunIds.Length is < 1 or > 2)
            throw new InvalidOperationException("Recovery manifest identity or bounds rejected.");
        var original = expected.Artifacts.SingleOrDefault(a => a.Path == OriginalReportName)
            ?? throw new InvalidOperationException("Recovery original report missing from manifest.");
        byte[] reportBytes = ReadBoundedFile(Path.Combine(root, OriginalReportName), 64 * 1024);
        if (original.Bytes != reportBytes.Length || original.Sha256 != DevelopmentJobState.Hash(reportBytes))
            throw new InvalidOperationException("Recovery original report changed.");
        var report = JsonSerializer.Deserialize<DevelopmentJobReport>(reportBytes, JsonOptions)
            ?? throw new InvalidOperationException("Recovery original report missing.");
        if (report.ModelCalls != state.ModelCalls || report.Attempts.Length != state.Attempts)
            throw new InvalidOperationException("Recovery attempt consumption changed.");

        // Rebuild from the fixed host-derived paths. Exact serialized bytes
        // also reject duplicated, reordered or unknown manifest fields.
        Manifest actual = BuildManifest(state.Identity, report, reportBytes);
        if (!bytes.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(actual, JsonOptions)))
            throw new InvalidOperationException("Recovery evidence no longer matches sealed manifest.");
        return (report, actual);
    }

    internal static Manifest BuildManifest(
        DevelopmentJobIdentity identity, DevelopmentJobReport report, byte[] originalReport)
    {
        _ = DevelopmentJobState.Create(identity);
        _ = DevelopmentPublicationPolicy.RequireSuccessfulAttempt(report, identity.IssueNumber, identity.BaseCommit);
        if (report.JobId != identity.JobId || report.DraftPullRequestNumber is not null
            || report.ModelCalls < report.Attempts.Length || report.ModelCalls > identity.MaxAttempts
            || originalReport.Length is < 1 or > 64 * 1024
            || !originalReport.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions)))
            throw new InvalidOperationException("Recovery report identity or budget rejected.");

        string root = JobRoot(identity);
        var artifacts = new List<Artifact>
        {
            new(OriginalReportName, originalReport.Length, DevelopmentJobState.Hash(originalReport))
        };
        var runs = new List<string>();
        for (int index = 0; index < report.Attempts.Length; index++)
        {
            DevelopmentJobAttempt attempt = report.Attempts[index];
            if (attempt.Number != index + 1)
                throw new InvalidOperationException("Recovery attempt order rejected.");
            if (attempt.Status == "source-rejected")
            {
                if (attempt.ReportPath is not null || attempt.CandidateWorktree is not null
                    || attempt.DiffPath is not null || attempt.ProductionEvidence is not null
                    || attempt.RestoreExitCode != -1 || attempt.BuildExitCode != -1 || attempt.RunExitCode != -1)
                    throw new InvalidOperationException("Rejected source has unexpected execution evidence.");
                continue;
            }

            if (attempt.Status is not ("passed" or "build-failed" or "run-failed" or "acceptance-failed"))
                throw new InvalidOperationException("Recovery attempt status rejected.");
            string number = attempt.Number.ToString("D2", CultureInfo.InvariantCulture);
            string worktree = Path.Combine(root, "worktrees", number);
            RequirePath(attempt.CandidateWorktree, worktree);
            string evaluations = Path.Combine(root, "evaluations", number);
            string production = OnlyDirectory(evaluations, "production-check-");
            string productionReportPath = Path.Combine(production, "production-report.json");
            var productionReport = JsonSerializer.Deserialize<DevelopmentProductionVerifier.ProductionReport>(
                Capture(productionReportPath, 32 * 1024), JsonOptions)
                ?? throw new InvalidOperationException("Recovery production report missing.");
            string diff = Path.Combine(production, "candidate.patch");
            RequirePath(attempt.DiffPath, diff);
            RequirePath(productionReport.DiffPath, diff);
            string reports = Path.Combine(production, "sandbox", "reports");
            string sandbox = OnlyDirectory(reports, "hami-code-");
            string sandboxReportPath = Path.Combine(sandbox, "report.json");
            RequirePath(attempt.ReportPath, sandboxReportPath);
            RequirePath(productionReport.SandboxReport, sandboxReportPath);
            var sandboxReport = JsonSerializer.Deserialize<DevelopmentSandboxRunner.SandboxRunReport>(
                Capture(sandboxReportPath, 32 * 1024), JsonOptions)
                ?? throw new InvalidOperationException("Recovery sandbox report missing.");
            bool passed = attempt.Status == "passed";
            if (productionReport.BaseCommit != identity.BaseCommit
                || productionReport.SourcePath != DevelopmentProductionVerifier.SourcePath
                || productionReport.Status != (passed ? "passed" : "failed")
                || !productionReport.CheckoutUnchanged || !productionReport.CleanupSucceeded
                || productionReport.OutputAccepted != passed
                || productionReport.RestoreExitCode != attempt.RestoreExitCode
                || productionReport.BuildExitCode != attempt.BuildExitCode
                || productionReport.RunExitCode != attempt.RunExitCode
                || sandboxReport.SchemaVersion != 1 || sandboxReport.Mode != "run"
                || sandboxReport.Project != "samples/RetryBudget/RetryBudget.csproj"
                || !DevelopmentPublicationPolicy.IsSha(sandboxReport.RunId, 32)
                || Path.GetFileName(sandbox) != "hami-code-" + sandboxReport.RunId
                || runs.Contains(sandboxReport.RunId, StringComparer.Ordinal)
                || sandboxReport.TimedOut || !sandboxReport.SandboxRemovalSucceeded
                || sandboxReport.RestoreExitCode != attempt.RestoreExitCode
                || sandboxReport.BuildExitCode != attempt.BuildExitCode
                || sandboxReport.ExecutionExitCode != attempt.RunExitCode)
                throw new InvalidOperationException("Recovery production or sandbox evidence disagrees.");
            runs.Add(sandboxReport.RunId);
            string source = Path.Combine(worktree,
                DevelopmentProductionVerifier.SourcePath.Replace('/', Path.DirectorySeparatorChar));
            byte[] sourceBytes = Capture(source, 64 * 1024);
            if (DevelopmentJobState.Hash(sourceBytes) != productionReport.SourceSha256
                || DevelopmentJobState.Hash(Capture(diff, 64 * 1024)) != productionReport.DiffSha256)
                throw new InvalidOperationException("Recovery source or patch bytes changed.");
            string snapshot = Path.Combine(production, "snapshot",
                DevelopmentProductionVerifier.SourcePath.Replace('/', Path.DirectorySeparatorChar));
            if (!sourceBytes.SequenceEqual(Capture(snapshot, 64 * 1024)))
                throw new InvalidOperationException("Recovery snapshot source changed.");
            if (passed)
            {
                if (productionReport != attempt.ProductionEvidence || sandboxReport.Error is not null)
                    throw new InvalidOperationException("Recovery successful evidence changed.");
                byte[] stdout = Capture(Path.Combine(sandbox, "execution.log"), 64 * 1024);
                if (!DevelopmentOutputAcceptance.Matches(Encoding.UTF8.GetString(stdout),
                    DevelopmentOutputAcceptance.ReviewedProductionStdout))
                    throw new InvalidOperationException("Recovery trusted stdout rejected.");
            }
        }

        return new Manifest(1, identity.JobId,
            [.. artifacts.OrderBy(a => a.Path, StringComparer.Ordinal)], [.. runs]);

        byte[] Capture(string path, int maxBytes)
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)
                || artifacts.Any(a => a.Path == relative))
                throw new InvalidOperationException("Recovery evidence path escaped or repeated.");
            byte[] bytes = ReadBoundedFile(path, maxBytes);
            artifacts.Add(new Artifact(relative, bytes.Length, DevelopmentJobState.Hash(bytes)));
            return bytes;
        }
    }

    private static string OnlyDirectory(string parent, string prefix)
    {
        RejectLinks(parent, directory: true);
        string[] directories = Directory.GetDirectories(parent);
        if (directories.Length != 1
            || !Path.GetFileName(directories[0]).StartsWith(prefix, StringComparison.Ordinal)
            || !DevelopmentPublicationPolicy.IsSha(Path.GetFileName(directories[0])[prefix.Length..], 32))
            throw new InvalidOperationException("Recovery evidence directory identity rejected.");
        RejectLinks(directories[0], directory: true);
        return directories[0];
    }

    private static void RequirePath(string? actual, string expected)
    {
        if (actual is null || !Path.IsPathFullyQualified(actual)
            || !string.Equals(actual, expected, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery evidence path identity rejected.");
    }

    private static void RejectLinks(string path, bool directory)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path)
            throw new InvalidOperationException("Recovery artifact path not canonical.");
        FileSystemInfo entry = directory ? new DirectoryInfo(path) : new FileInfo(path);
        if (!entry.Exists || entry.LinkTarget is not null
            || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Recovery artifact missing or linked.");
        for (DirectoryInfo? parent = directory ? new DirectoryInfo(path).Parent : new FileInfo(path).Directory;
            parent is not null; parent = parent.Parent)
            if (!parent.Exists || parent.LinkTarget is not null
                || (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Recovery artifact ancestor rejected.");
    }

    internal static byte[] ReadBoundedFile(string path, int maxBytes)
    {
        RejectLinks(path, directory: false);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 1 || stream.Length > maxBytes)
            throw new InvalidOperationException("Recovery artifact size rejected.");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidOperationException("Recovery artifact changed during read.");
        return bytes;
    }

    // Mutable projection is deliberately separate from the sealed report.
    // A crash may leave a stale projection, but never loses publication state.
    internal static void WriteProjection(DevelopmentJobIdentity identity, DevelopmentJobReport report)
    {
        if (report.JobId != identity.JobId || report.IssueNumber != identity.IssueNumber)
            throw new InvalidOperationException("Recovery report projection identity rejected.");
        string root = JobRoot(identity);
        string path = Path.Combine(root, "agent-report.json");
        string next = path + ".next";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        if (bytes.Length is < 1 or > 64 * 1024)
            throw new InvalidOperationException("Recovery report projection size rejected.");
        if (new FileInfo(path).LinkTarget is not null || Directory.Exists(path))
            throw new InvalidOperationException("Recovery report projection destination rejected.");
        if (File.Exists(path))
            RejectLinks(path, directory: false);
        WriteNew(next, bytes);
        File.Move(next, path, overwrite: true);
        if (!bytes.SequenceEqual(ReadBoundedFile(path, 64 * 1024)))
            throw new InvalidOperationException("Recovery report projection readback failed.");
    }

    // Only the already confirmed PR number is supplied by the private
    // state. Preserve an abandoned projection file without interpreting
    // its contents as a job checkpoint; then regenerate from sealed bytes.
    internal static void RepairPublishedProjection(DevelopmentJobIdentity identity,
        DevelopmentJobReport original, int pull)
    {
        if (pull <= 0 || original.DraftPullRequestNumber is not null
            || original.JobId != identity.JobId || original.IssueNumber != identity.IssueNumber)
            throw new InvalidOperationException("Published report projection identity rejected.");
        string path = Path.Combine(JobRoot(identity), "agent-report.json");
        string next = path + ".next";
        if (new FileInfo(next).LinkTarget is not null || Directory.Exists(next))
            throw new InvalidOperationException("Interrupted projection path rejected.");
        if (File.Exists(next))
        {
            RejectLinks(next, directory: false);
            string preserved = path + ".interrupted";
            if (File.Exists(preserved) || Directory.Exists(preserved)
                || new FileInfo(preserved).LinkTarget is not null)
                throw new InvalidOperationException("Interrupted projection preservation path occupied.");
            File.Move(next, preserved, overwrite: false);
        }
        WriteProjection(identity, original with { DraftPullRequestNumber = pull });
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        RejectLinks(Path.GetDirectoryName(path)!, directory: true);
        if (new FileInfo(path).LinkTarget is not null)
            throw new InvalidOperationException("Recovery evidence destination is linked.");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
