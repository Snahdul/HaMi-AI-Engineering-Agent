using HaMi.Agentic.Cli.Runtime.Logging;
using Microsoft.Extensions.Logging;

namespace HaMi.Agentic.Cli.Features.Development;

// Host-only reentry. There is deliberately no generator, ActorSystem,
// build runner, guest creation or issue-journal dependency in this use case.
internal static class DevelopmentRecoveryCommand
{
    internal static async Task<int> RunAsync(string repo, string jobId, string approvedBase)
    {
        string phase = "recovery-ownership";
        AgentLoggingSession? logging = null;
        ILogger? logger = null;
        try
        {
            logging = AgentLoggingSession.Create(repo, includeConsole: true);
            logger = logging.CreateLogger("HaMi.Agentic.Cli.Features.Development");
            await DevelopmentIssueWorkflow.VerifyOriginAsync(repo);
            var store = DevelopmentJobStateRoot.Open(repo, null);
            using var owner = store.AcquireJob(jobId);
            var state = owner.RequireCurrent();
            var identity = state.Identity;
            if (identity.BaseCommit != approvedBase)
                throw new InvalidOperationException("Recovery authorization base differs.");
            // Recheck the persisted results location against the fixed root.
            // No caller-supplied results directory is accepted.
            _ = DevelopmentJobStateRoot.Open(repo, identity.ResultsRoot);
            DevelopmentIsolatedRunner.ValidateStoragePaths(repo,
                Path.Combine(repo, "samples", "RetryBudget"), identity.ResultsRoot);

            if (state.Stage is DevelopmentJobState.Accepted or DevelopmentJobState.Executing)
            {
                owner.Commit(state.BlockUnknownExecution());
                throw new InvalidOperationException("Interrupted execution requires operator reconciliation.");
            }

            phase = "recovery-evidence";
            var (report, evidence) = DevelopmentJobEvidence.ReadVerified(owner);
            if (state.Stage == DevelopmentJobState.Published)
            {
                // Terminal report repair is local only; not even a GitHub
                // credential is resolved. It cannot change remote resources.
                DevelopmentJobEvidence.RepairPublishedProjection(identity, report, state.PullRequestNumber!.Value);
                Console.WriteLine("Previously published draft: #" + state.PullRequestNumber.Value);
                return 0;
            }
            if (state.PublicationAttempts >= identity.MaxPublicationAttempts)
                throw new InvalidOperationException("Recovery publication budget exhausted.");

            phase = "recovery-authorization";
            var contract = DevelopmentAgentContract.Load(repo, DevelopmentAgentContract.ProductionPath);
            using var issues = new GitHubIssueGateway();
            using var issueJson = await issues.GetIssueAsync(identity.IssueNumber, CancellationToken.None);
            var issue = ApprovedDevelopmentIssue.Parse(issueJson.RootElement, contract);
            contract = issue.Bind(contract);
            var current = await DevelopmentJobIdentityFactory.CreateAsync(
                repo, identity.ResultsRoot, identity.JobId, identity.IssueNumber, issue,
                contract, approvedBase, identity.GenerationMode == "live");
            if (current != identity)
                throw new InvalidOperationException("Recovery authorization or reviewed identity changed.");

            phase = "recovery-cleanup";
            foreach (string runId in evidence.SandboxRunIds)
                await DevelopmentSandboxRunner.RequireRemovedAsync(runId);

            phase = "recovery-candidate";
            _ = await DevelopmentPublicationPolicy.ValidateCandidateAsync(repo,
                identity.ResultsRoot, report, identity.IssueNumber, approvedBase, true);
            // Re-read after external inspection; no mutable report or
            // artifact may replace the original while we hold the claim.
            (report, _) = DevelopmentJobEvidence.ReadVerified(owner);
            phase = "recovery-publication";
            owner.Commit(owner.RequireCurrent().ReservePublication());
            using var publisher = new DevelopmentPublicationGateway();
            int pull = await publisher.PublishAsync(repo, identity.ResultsRoot, report,
                identity.IssueNumber, approvedBase, true, CancellationToken.None);
            owner.Commit(owner.RequireCurrent().ConfirmPublication(pull));
            phase = "recovery-report";
            DevelopmentJobEvidence.RepairPublishedProjection(identity, report, pull);
            Console.WriteLine("Draft pull request: #" + pull);
            return 0;
        }
        catch (Exception ex)
        {
            // No issue text, raw sbx output, path or remote body in diagnostics.
            if (logger is not null)
                DevelopmentLogEvents.JobFailed(logger, jobId, phase, "RECOVERY_REJECTED",
                    ex.GetType().Name, "Persistente Claim und Originalnachweise pruefen; keinen neuen Job blind starten.");
            else
                Console.Error.WriteLine("Recovery rejected before logging: " + ex.GetType().Name);
            return 1;
        }
        finally
        {
            logging?.Dispose();
        }
    }
}
