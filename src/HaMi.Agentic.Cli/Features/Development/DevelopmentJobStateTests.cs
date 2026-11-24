using System.Text;
using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

internal static class DevelopmentJobStateTests
{
    internal static void SelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "hami-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string hash = new('A', 64);
            var identity = new DevelopmentJobIdentity(new string('a', 32),
                GitHubIssueGateway.Repository, 62, new string('b', 40),
                hash, hash, hash, hash, Path.Combine(root, "results"),
                "offline-scripted", 2, DevelopmentJobState.PublicationLimit,
                "production-retry-budget", "main", true, false, false);
            var initial = DevelopmentJobState.Create(identity);
            var store = new DevelopmentJobStateStore(root);
            Reject(() => initial.ReservePublication());
            Reject(() => DevelopmentJobState.Create(identity with { MaxAttempts = 3 }));
            Reject(() => DevelopmentJobState.Create(identity with { PublishIssue = true }));
            Reject(() => DevelopmentJobState.Create(identity with { PublishDraft = false }));
            Reject(() => DevelopmentJobState.Create(identity with { JobId = "../other" }));

            byte[] initialBytes = initial.Serialize();
            if (DevelopmentJobState.Parse(initialBytes) != initial)
                throw new InvalidOperationException("Recovery state serialization changed.");
            string json = Encoding.UTF8.GetString(initialBytes);
            foreach (string invalid in new[]
            {
                json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal),
                json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1", StringComparison.Ordinal),
                json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"unexpected\": true", StringComparison.Ordinal),
                json.Replace("\"maxAttempts\": 2", "\"maxAttempts\": 3", StringComparison.Ordinal),
                json.Replace("\"stage\": \"accepted\"", "\"stage\": \"published\"", StringComparison.Ordinal),
                "{}"
            })
            {
                if (invalid == json)
                    throw new InvalidOperationException("Recovery negative fixture did not change.");
                Reject(() => DevelopmentJobState.Parse(Encoding.UTF8.GetBytes(invalid)));
            }

            using (var owner = store.Acquire(62))
            {
                owner.Claim(initial);
                // Separate store, same canonical repository/issue; the lock
                // must not depend on a checkout or caller-selected results path.
                Reject(() => new DevelopmentJobStateStore(root).Acquire(62).Dispose());
                using (var other = store.Acquire(63))
                    if (other.Current is not null)
                        throw new InvalidOperationException("Unrelated issue unexpectedly claimed.");
                owner.Commit(initial.Start());
                Reject(() => owner.Claim(initial));
                Reject(() => owner.Commit(initial));
                Reject(() => owner.Commit(owner.Current!.Verify(2, 2, hash) with
                {
                    Identity = identity with { ResultsRoot = Path.Combine(root, "other-results") }
                }));
            }

            using (var owner = store.Acquire(62))
            {
                if (owner.Current?.Stage != DevelopmentJobState.Executing)
                    throw new InvalidOperationException("Claim lost after lock release.");
                Reject(() => owner.Claim(DevelopmentJobState.Create(identity with
                {
                    JobId = new string('c', 32)
                })));
                owner.Commit(owner.Current!.Verify(2, 2, hash));
                owner.Commit(owner.Current!.ReservePublication());
            }

            using (var owner = store.Acquire(62))
            {
                var pendingState = owner.Current;
                if (pendingState?.Stage != DevelopmentJobState.PublicationPending
                    || pendingState.PublicationAttempts != 1)
                    throw new InvalidOperationException("Publication intent not retained.");
                owner.Commit(pendingState.ReservePublication());
                owner.Commit(owner.Current!.ReservePublication());
                Reject(() => owner.Current!.ReservePublication());
                Reject(() => owner.Commit(owner.Current!.ConfirmPublication(59) with
                {
                    EvidenceHash = new string('B', 64)
                }));
                owner.Commit(owner.Current!.ConfirmPublication(59));
                Reject(() => owner.Current!.ReservePublication());
            }

            using (var owner = store.Acquire(62))
            {
                var published = owner.Current;
                if (published?.PullRequestNumber != 59
                    || published.Stage != DevelopmentJobState.Published)
                    throw new InvalidOperationException("Published identity lost.");
                Reject(() => owner.Claim(initial));
            }

            // Reentry selects only the private registry's original identity.
            using (var reopened = store.AcquireJob(identity.JobId))
            {
                if (reopened.Current?.Stage != DevelopmentJobState.Published
                    || reopened.Current.Identity != identity)
                    throw new InvalidOperationException("Recovery lookup selected a different job.");
                Reject(() => store.AcquireJob(identity.JobId).Dispose());
            }
            Reject(() => store.AcquireJob(new string('f', 32)).Dispose());
            Reject(() => store.AcquireJob("../other").Dispose());
            using (var duplicate = store.Acquire(67))
                duplicate.Claim(DevelopmentJobState.Create(identity with { IssueNumber = 67 }));
            Reject(() => store.AcquireJob(identity.JobId).Dispose());

            foreach (string terminal in new[]
            {
                DevelopmentJobState.Failed, DevelopmentJobState.Exhausted, DevelopmentJobState.Cancelled
            })
            {
                var failed = initial.Start().FinishExecution(terminal, 2, 2);
                Reject(() => failed.ReservePublication());
                Reject(() => failed.Start());
            }

            var blocked = initial.Start().BlockUnknownExecution();
            Reject(() => blocked.Verify(2, 2, hash));
            Reject(() => blocked.Start());

            // Abandoned temporary data is not a committed checkpoint.
            string pending = Path.Combine(root, "issue-62.next");
            File.WriteAllText(pending, "uncommitted");
            Reject(() => store.Acquire(62).Dispose());
            if (File.ReadAllText(pending) != "uncommitted")
                throw new InvalidOperationException("Uncommitted state was modified.");
            File.Delete(pending); // test fixture cleanup only
            string statePath = Path.Combine(root, "issue-62.json");
            File.WriteAllText(statePath, "{");
            Reject(() => store.Acquire(62).Dispose());
            if (File.ReadAllText(statePath) != "{")
                throw new InvalidOperationException("Corrupt state was overwritten.");

            // Legacy paths share the same lock and durable blocker, but
            // never manufacture a V1 recovery authorization.
            using (var legacy = store.Acquire(65))
            {
                legacy.ClaimLegacy(identity.JobId);
                Reject(() => legacy.Claim(DevelopmentJobState.Create(identity with { IssueNumber = 65 })));
                Reject(() => store.Acquire(65).Dispose());
                legacy.CompleteLegacy();
            }
            using (var legacy = store.Acquire(65))
            {
                legacy.ClaimLegacy(identity.JobId);
                // Simulate loss of the owner without confirmed completion.
            }
            Reject(() => store.Acquire(65).Dispose());
            if (!File.Exists(Path.Combine(root, "issue-65.legacy-active")))
                throw new InvalidOperationException("Interrupted legacy claim was discarded.");

            using (var durable = store.Acquire(66))
            {
                durable.Claim(DevelopmentJobState.Create(identity with { IssueNumber = 66 }));
                Reject(() => durable.ClaimLegacy(identity.JobId));
            }

            // A state file copied to a different issue is not a valid claim.
            File.WriteAllBytes(Path.Combine(root, "issue-64.json"), initialBytes);
            Reject(() => store.Acquire(64).Dispose());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        catch (IOException) { return; }
        catch (JsonException) { return; }
        throw new InvalidOperationException("Unsafe recovery state or ownership operation accepted.");
    }
}
