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

            bool published = state.Stage == DevelopmentJobState.Published;
            int pull = await ResumeOwnedAsync(repo, owner, approvedBase,
                ReadApprovedIssueAsync, DevelopmentSandboxRunner.RequireRemovedAsync,
                () => new DevelopmentPublicationGateway(), value => phase = value);
            Console.WriteLine((published ? "Previously published draft: #" : "Draft pull request: #") + pull);
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

    private static async Task<System.Text.Json.JsonDocument> ReadApprovedIssueAsync(int issue)
    {
        using var issues = new GitHubIssueGateway();
        return await issues.GetIssueAsync(issue, CancellationToken.None);
    }

    // The production command and offline crash regression exercise this
    // same host use case. Only the external I/O boundaries are substituted.
    // The real private checkpoint, identity, evidence, Git gate and publisher
    // still run. No caller-supplied CLI report or dependency override exists.
    internal static async Task<int> ResumeOwnedAsync(string repo,
        DevelopmentJobStateStore.Lease owner, string approvedBase,
        Func<int, Task<System.Text.Json.JsonDocument>> readIssue,
        Func<string, Task> requireRemoved, Func<DevelopmentPublicationGateway> createPublisher,
        Action<string>? setPhase = null)
    {
        var state = owner.RequireCurrent();
        var identity = state.Identity;
        if (identity.BaseCommit != approvedBase)
            throw new InvalidOperationException("Recovery authorization base differs.");
        if (state.Stage is DevelopmentJobState.Accepted or DevelopmentJobState.Executing)
        {
            owner.Commit(state.BlockUnknownExecution());
            throw new InvalidOperationException("Interrupted execution requires operator reconciliation.");
        }

        setPhase?.Invoke("recovery-evidence");
        var (report, evidence) = DevelopmentJobEvidence.ReadVerified(owner);
        if (state.Stage == DevelopmentJobState.Published)
        {
            // Terminal report repair is local only, with no credential or
            // external dependency resolved, even when the issue is closed.
            int published = state.PullRequestNumber!.Value;
            DevelopmentJobEvidence.RepairPublishedProjection(identity, report, published);
            return published;
        }
        if (state.PublicationAttempts >= identity.MaxPublicationAttempts)
            throw new InvalidOperationException("Recovery publication budget exhausted.");

        setPhase?.Invoke("recovery-authorization");
        var contract = DevelopmentAgentContract.Load(repo, DevelopmentAgentContract.ProductionPath);
        using var issueJson = await readIssue(identity.IssueNumber);
        var issue = ApprovedDevelopmentIssue.Parse(issueJson.RootElement, contract);
        contract = issue.Bind(contract);
        var current = await DevelopmentJobIdentityFactory.CreateAsync(
            repo, identity.ResultsRoot, identity.JobId, identity.IssueNumber, issue,
            contract, approvedBase, identity.GenerationMode == "live");
        if (current != identity)
            throw new InvalidOperationException("Recovery authorization or reviewed identity changed.");

        setPhase?.Invoke("recovery-cleanup");
        foreach (string runId in evidence.SandboxRunIds)
            await requireRemoved(runId);

        setPhase?.Invoke("recovery-candidate");
        _ = await DevelopmentPublicationPolicy.ValidateCandidateAsync(repo,
            identity.ResultsRoot, report, identity.IssueNumber, approvedBase, true);
        // Re-read after external inspection; no mutable report or
        // artifact may replace the original while we hold the claim.
        (report, _) = DevelopmentJobEvidence.ReadVerified(owner);
        setPhase?.Invoke("recovery-publication");
        owner.Commit(owner.RequireCurrent().ReservePublication());
        using var publisher = createPublisher();
        int pull = await publisher.PublishAsync(repo, identity.ResultsRoot, report,
            identity.IssueNumber, approvedBase, true, CancellationToken.None);
        owner.Commit(owner.RequireCurrent().ConfirmPublication(pull));
        setPhase?.Invoke("recovery-report");
        DevelopmentJobEvidence.RepairPublishedProjection(identity, report, pull);
        return pull;
    }

}
