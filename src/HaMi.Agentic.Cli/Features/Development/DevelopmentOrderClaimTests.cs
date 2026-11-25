using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

internal static class DevelopmentOrderClaimTests
{
    internal static void SelfTest(DevelopmentOrderApproval approval)
    {
        string root = Path.Combine(Path.GetTempPath(), "hami-order-claim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int issue = approval.Binding.IssueNumber;
            string jobId = Guid.NewGuid().ToString("N");
            string oldJob = Guid.NewGuid().ToString("N");
            string hash = new('A', 64);
            int oldIssue = issue == 62 ? 63 : 62;
            var v1 = DevelopmentJobState.Create(new DevelopmentJobIdentity(oldJob, GitHubIssueGateway.Repository,
                oldIssue, new string('b', 40), hash, hash, hash, hash, Path.Combine(root, "results"),
                "offline-scripted", 2, DevelopmentJobState.PublicationLimit,
                "production-retry-budget", "main", true, false, false));

            var store = new DevelopmentJobStateStore(root);
            using (var old = store.Acquire(oldIssue))
                old.Claim(v1);
            Reject(() => store.AcquireOrder(oldIssue).Dispose());

            DevelopmentOrderClaim claim;
            using (var owner = store.AcquireOrder(issue))
            {
                Reject(() => owner.Claim(v1));
                Reject(() => owner.ClaimLegacy(oldJob));
                owner.ClaimOrder(jobId, approval);
                claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
                if (claim.JobId != jobId || claim.PersistenceId != "hami-order-v1-" + jobId
                    || claim.Binding != approval.Binding || owner.Current is not null)
                    throw new InvalidOperationException("Order claim identity mismatch.");
                Reject(() => owner.ClaimOrder(Guid.NewGuid().ToString("N"), approval));
                Reject(() => store.Acquire(issue).Dispose());
                Reject(() => store.AcquireOrder(issue).Dispose());
            }

            using (var old = store.AcquireJob(oldJob))
                if (old.Current != v1)
                    throw new InvalidOperationException("V1 lookup changed by order claim.");
            Reject(() => store.AcquireJob(jobId).Dispose());
            Reject(() => store.Acquire(issue).Dispose());

            using (var resumed = store.AcquireOrder(issue))
            {
                DevelopmentOrderArchive archive = resumed.RequireArchivedOrder();
                if (resumed.CurrentOrder != claim || archive.Plan != approval.Order.Plan
                    || archive.Comment != approval.Comment || archive.Title != approval.Title
                    || archive.ObservedAt != approval.ObservedAt || archive.Binding != approval.Binding
                    || archive.BudgetPolicy != approval.BudgetPolicy.Source
                    || archive.EffectiveLimits != approval.EffectiveLimits)
                    throw new InvalidOperationException("Order archive reentry mismatch.");
            }

            var originalArchive = DevelopmentOrderArchive.From(approval);
            byte[] wrongBudget = (originalArchive with
            {
                EffectiveLimits = originalArchive.EffectiveLimits with { Seconds = originalArchive.EffectiveLimits.Seconds - 1 }
            }).Serialize();
            Reject(() => DevelopmentOrderArchive.Parse(wrongBudget,
                claim with { ArchiveHash = DevelopmentJobState.Hash(wrongBudget) }));

            string prefix = Path.Combine(root, FormattableString.Invariant($"issue-{issue}"));
            string archivePath = prefix + ".order-approval";
            byte[] original = File.ReadAllBytes(archivePath);
            File.WriteAllBytes(archivePath, [.. original, 0x20]);
            Reject(() => store.AcquireOrder(issue).Dispose());
            Reject(() => store.Acquire(issue).Dispose());
            File.Delete(archivePath);
            Reject(() => store.AcquireOrder(issue).Dispose());
            Reject(() => store.Acquire(issue).Dispose());
            File.WriteAllBytes(archivePath, original);

            string statePath = prefix + ".json";
            byte[] state = File.ReadAllBytes(statePath);
            File.WriteAllBytes(statePath, [.. state, 0x20]);
            Reject(() => store.AcquireOrder(issue).Dispose());
            File.WriteAllBytes(statePath, state);
            File.Delete(statePath);
            Reject(() => store.AcquireOrder(issue).Dispose());
            Reject(() => store.Acquire(issue).Dispose());
            File.WriteAllBytes(statePath, state);
            File.WriteAllText(prefix + ".next", "interrupted");
            Reject(() => store.AcquireOrder(issue).Dispose());
            Reject(() => store.Acquire(issue).Dispose());
            Console.WriteLine("PASS: immutable order claim, exact approval archive, V1 collision and incomplete/tampered storage rejection.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            return;
        }

        throw new InvalidOperationException("Invalid order storage operation was accepted.");
    }
}
