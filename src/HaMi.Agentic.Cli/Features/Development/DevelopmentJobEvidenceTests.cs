using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

internal static class DevelopmentJobEvidenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    internal static void SelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "hami-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string hash = new('A', 64);
            var identity = new DevelopmentJobIdentity(new string('a', 32),
                GitHubIssueGateway.Repository, 62, new string('b', 40),
                hash, hash, hash, hash, root, "offline-scripted", 2,
                DevelopmentJobState.PublicationLimit, "production-retry-budget", "main", true, false, false);
            string jobRoot = DevelopmentJobEvidence.JobRoot(identity);
            string sourcePath = DevelopmentProductionVerifier.SourcePath;
            var first = Fixture(1, passed: false);
            var second = Fixture(2, passed: true);
            var job = new DevelopmentJobReport(identity.JobId, "passed", 2,
                [first, second], null, identity.IssueNumber);
            byte[] original = JsonSerializer.SerializeToUtf8Bytes(job, JsonOptions);
            var manifest = DevelopmentJobEvidence.BuildManifest(identity, job, original);
            if (manifest.SchemaVersion != 1 || manifest.JobId != identity.JobId
                || manifest.Artifacts.Length != 12 || manifest.SandboxRunIds.Length != 2
                || manifest.Artifacts.Select(a => a.Path).Distinct(StringComparer.Ordinal).Count() != manifest.Artifacts.Length)
                throw new InvalidOperationException("Recovery evidence manifest incomplete.");

            // Re-serialize each deliberately invalid report so rejection
            // is caused by the changed identity/evidence, not stale JSON bytes.
            RejectReport(job with { JobId = new string('e', 32) });
            RejectReport(job with { Attempts = [first with { CleanupSucceeded = false }, second] });
            RejectReport(job with { Attempts = [first, second with { CandidateWorktree = root }] });
            Reject(() => DevelopmentJobEvidence.BuildManifest(identity, job, "{}"u8.ToArray()));

            void RejectReport(DevelopmentJobReport invalid) =>
                Reject(() => DevelopmentJobEvidence.BuildManifest(identity, invalid,
                    JsonSerializer.SerializeToUtf8Bytes(invalid, JsonOptions)));

            string log = Path.Combine(Path.GetDirectoryName(second.ReportPath!)!, "execution.log");
            string originalOutput = File.ReadAllText(log);
            File.WriteAllText(log, "Rejected guest output.");
            Reject(() => DevelopmentJobEvidence.BuildManifest(identity, job, original));
            File.WriteAllText(log, originalOutput);

            string source = Path.Combine(second.CandidateWorktree!, sourcePath.Replace('/', Path.DirectorySeparatorChar));
            string originalSource = File.ReadAllText(source);
            File.WriteAllText(source, "Different source bytes.");
            Reject(() => DevelopmentJobEvidence.BuildManifest(identity, job, original));
            File.WriteAllText(source, originalSource);

            string priorReport = first.ReportPath!;
            byte[] priorBytes = File.ReadAllBytes(priorReport);
            var prior = JsonSerializer.Deserialize<DevelopmentSandboxRunner.SandboxRunReport>(priorBytes, JsonOptions)!;
            WriteJson(priorReport, prior with { SandboxRemovalSucceeded = false });
            Reject(() => DevelopmentJobEvidence.BuildManifest(identity, job, original));
            File.WriteAllBytes(priorReport, priorBytes);

            var restored = DevelopmentJobEvidence.BuildManifest(identity, job, original);
            if (!JsonSerializer.SerializeToUtf8Bytes(restored, JsonOptions)
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions)))
                throw new InvalidOperationException("Recovery evidence digest not reproducible.");

            string projection = Path.Combine(jobRoot, "agent-report.json");
            string originalPath = Path.Combine(jobRoot, DevelopmentJobEvidence.OriginalReportName);
            File.WriteAllBytes(originalPath, original);
            string privateRoot = Path.Combine(root, "state-fixture");
            Directory.CreateDirectory(privateRoot);
            var store = new DevelopmentJobStateStore(privateRoot);
            byte[] sealedBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            string manifestPath = Path.Combine(jobRoot, DevelopmentJobEvidence.ManifestName);
            File.WriteAllBytes(manifestPath, sealedBytes);
            using (var owner = store.Acquire(identity.IssueNumber))
            {
                owner.Claim(DevelopmentJobState.Create(identity));
                owner.Commit(owner.Current!.Start());
                Reject(() => DevelopmentJobEvidence.ReadVerified(owner));
                owner.Commit(owner.Current!.Verify(2, 2, DevelopmentJobState.Hash(sealedBytes)));
            }
            using (var owner = store.AcquireJob(identity.JobId))
            {
                var opened = DevelopmentJobEvidence.ReadVerified(owner);
                if (opened.Report.JobId != identity.JobId
                    || opened.Evidence.SandboxRunIds.Length != 2)
                    throw new InvalidOperationException("Verified reentry evidence differs.");
                File.AppendAllText(manifestPath, " ");
                Reject(() => DevelopmentJobEvidence.ReadVerified(owner));
                File.WriteAllBytes(manifestPath, sealedBytes);
                File.AppendAllText(originalPath, " ");
                Reject(() => DevelopmentJobEvidence.ReadVerified(owner));
                File.WriteAllBytes(originalPath, original);
                File.WriteAllText(log, originalOutput + " ");
                Reject(() => DevelopmentJobEvidence.ReadVerified(owner));
                File.WriteAllText(log, originalOutput);
                // Even a semantically equivalent output modification must
                // fail the manifest hash comparison, not only the stdout gate.
                File.WriteAllText(log, originalOutput + "\n");
                Reject(() => DevelopmentJobEvidence.ReadVerified(owner));
                File.WriteAllText(log, originalOutput);
                owner.Commit(owner.Current!.ReservePublication());
                _ = DevelopmentJobEvidence.ReadVerified(owner);
                owner.Commit(owner.Current!.ConfirmPublication(59));
                _ = DevelopmentJobEvidence.ReadVerified(owner);
            }
            DevelopmentJobEvidence.WriteProjection(identity, job);
            var published = job with { DraftPullRequestNumber = 59 };
            DevelopmentJobEvidence.WriteProjection(identity, published);
            if (!File.ReadAllBytes(originalPath).SequenceEqual(original)
                || JsonSerializer.Deserialize<DevelopmentJobReport>(File.ReadAllBytes(projection), JsonOptions)?.DraftPullRequestNumber != 59)
                throw new InvalidOperationException("Report projection changed original evidence.");
            string next = projection + ".next";
            File.WriteAllText(next, "Interrupted report projection");
            byte[] priorProjection = File.ReadAllBytes(projection);
            try
            {
                DevelopmentJobEvidence.WriteProjection(identity, job);
                throw new InvalidOperationException("Interrupted projection was overwritten.");
            }
            catch (IOException)
            {
                if (!File.ReadAllBytes(projection).SequenceEqual(priorProjection)
                    || File.ReadAllText(next) != "Interrupted report projection")
                    throw new InvalidOperationException("Interrupted report projection was not preserved.");
            }

            DevelopmentJobAttempt Fixture(int attempt, bool passed)
            {
                string number = attempt.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
                string candidate = Path.Combine(jobRoot, "worktrees", number);
                string production = Path.Combine(jobRoot, "evaluations", number,
                    "production-check-" + new string(passed ? 'c' : 'd', 32));
                string runId = new string(passed ? 'f' : 'e', 32);
                string sandbox = Path.Combine(production, "sandbox", "reports", "hami-code-" + runId);
                string source = Path.Combine(candidate, sourcePath.Replace('/', Path.DirectorySeparatorChar));
                string snapshot = Path.Combine(production, "snapshot", sourcePath.Replace('/', Path.DirectorySeparatorChar));
                string diff = Path.Combine(production, "candidate.patch");
                string reportPath = Path.Combine(sandbox, "report.json");
                Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
                Directory.CreateDirectory(sandbox);
                File.WriteAllText(source, "Fixture candidate " + number);
                File.Copy(source, snapshot);
                File.WriteAllText(diff, "Fixture diff " + number);
                int execution = passed ? 0 : 1;
                var productionReport = new DevelopmentProductionVerifier.ProductionReport(
                    passed ? "passed" : "failed", identity.BaseCommit, sourcePath,
                    DevelopmentJobState.Hash(File.ReadAllBytes(source)), diff, reportPath,
                    0, 0, execution, true, passed, true, DevelopmentJobState.Hash(File.ReadAllBytes(diff)));
                WriteJson(Path.Combine(production, "production-report.json"), productionReport);
                WriteJson(reportPath, new DevelopmentSandboxRunner.SandboxRunReport(
                    1, runId, "samples/RetryBudget/RetryBudget.csproj", "run", 0, 0, execution,
                    false, 1, 1, 1, 1, 1, 1, 6, true, null));
                if (passed)
                    File.WriteAllText(Path.Combine(sandbox, "execution.log"), DevelopmentOutputAcceptance.ReviewedProductionStdout);
                return new DevelopmentJobAttempt(attempt, passed ? "passed" : "run-failed",
                    0, 0, execution, false, true, reportPath, passed, candidate, diff,
                    passed ? productionReport : null);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteJson<T>(string path, T value) =>
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe recovery evidence accepted.");
    }
}
