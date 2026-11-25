using System.Text.Json;

namespace HaMi.Agentic.Cli.Features.Development;

// Typed, provider-neutral, synthetic correlation records. Trusted catalog and
// verifier observations are separate inputs, not assertions from a model.
internal sealed record OrderAgentIdentity(
    string Id, string Version, string ProfileHash, string Provider, string Model, string ConfigurationHash);

internal sealed record OrderAgentProfile(OrderAgentIdentity Identity, bool Enabled, string[] Competencies);

internal sealed record OrderAssignment(
    OrderBinding Binding, Guid JobId, string StepId, int Revision, OrderAgentIdentity Agent,
    string Rationale, DateTimeOffset DecidedAt);

internal sealed record OrderAttempt(
    OrderBinding Binding, Guid JobId, string StepId, int AssignmentRevision, Guid AttemptId,
    int AttemptNumber, OrderAgentIdentity Agent);

internal sealed record OrderArtifact(
    string Id, string MediaType, int ByteCount, string Sha256, Guid? JobId, string? StepId, Guid? AttemptId);

internal sealed record OrderArtifactObservation(OrderArtifact Artifact, byte[] Bytes);

internal sealed record OrderStepResult(
    OrderAttempt Attempt, OrderArtifact[] Inputs, OrderArtifact[] Outputs, string Status, string Findings);

internal sealed record OrderVerifier(string Id, string Version, string Digest, string ActorId);

internal sealed record OrderCriterion(string Id, bool Passed);

internal sealed record OrderStepEvidence(
    Guid EvidenceId, OrderAttempt Attempt, OrderVerifier Verifier, OrderArtifact[] Inputs,
    OrderArtifact[] Outputs, string[] ChangedPaths, OrderArtifact[] PreservedOutputs,
    OrderCriterion[] Criteria, bool CleanupSucceeded, bool Accepted, DateTimeOffset ObservedAt);

internal sealed record OrderFinalEvidence(
    Guid EvidenceId, OrderBinding Binding, Guid JobId, OrderVerifier Verifier, OrderArtifact[] Inputs,
    string[] ChangedPaths, bool BaselineUnchanged, OrderCriterion[] Criteria,
    bool CleanupSucceeded, bool Accepted, DateTimeOffset ObservedAt);

internal sealed record OrderKnowledge(
    string Id, OrderAttempt Attempt, Guid EvidenceId, string Claim, string Rationale, string Relevance);

internal sealed record OrderKnowledgeReview(OrderKnowledge Contribution, OrderVerifier Reviewer, string Status);

internal static class DevelopmentOrderHandover
{
    internal static void ValidateAssignment(DevelopmentOrderApproval approval, Guid jobId, OrderAssignment assignment,
        IReadOnlyList<OrderAgentProfile> catalog)
    {
        JsonElement step = Step(approval, assignment.StepId);
        OrderCheck.Require(jobId != Guid.Empty && assignment.JobId == jobId && assignment.Binding == approval.Binding
            && assignment.Revision > 0 && assignment.DecidedAt >= approval.ObservedAt
            && OrderCheck.Text(assignment.Rationale, 1500), "ORDER_ASSIGNMENT_BINDING");
        OrderAgentIdentity agent = assignment.Agent;
        OrderCheck.Require(OrderCheck.Text(agent.Id, 100) && OrderCheck.Text(agent.Version, 100)
            && OrderCheck.Text(agent.Provider, 100) && OrderCheck.Text(agent.Model, 100)
            && OrderCheck.Hash(agent.ProfileHash) && OrderCheck.Hash(agent.ConfigurationHash), "ORDER_AGENT_IDENTITY");
        OrderAgentProfile[] eligible = catalog.Where(p => p.Identity.Id == agent.Id).ToArray();
        OrderCheck.Require(eligible.Length == 1 && eligible[0].Enabled && eligible[0].Identity == agent
            && step.GetProperty("competencies").EnumerateArray().All(c => eligible[0].Competencies.Contains(c.GetString()!,
                StringComparer.Ordinal)), "ORDER_AGENT_CAPABILITY");
    }

    // Validate the entire successful two-step reference in order. There is no
    // reusable runtime acceptance token, no filesystem/URL resolution and no execution.
    // A failed candidate cannot become a predecessor input.
    internal static void ValidateReference(DevelopmentOrderApproval approval, Guid jobId,
        OrderAssignment[] assignments, OrderStepResult[] results, OrderStepEvidence[] evidence,
        OrderFinalEvidence final, IReadOnlyList<OrderAgentProfile> agents, IReadOnlyList<OrderVerifier> verifiers,
        OrderArtifactObservation[] store, OrderKnowledge[] contributions, OrderKnowledgeReview[] knowledgeReviews)
    {
        OrderCheck.Require(assignments.Length == 2 && results.Length == 2 && evidence.Length == 2
            && store.Length == 4 && contributions.Length <= 24 && knowledgeReviews.Length <= contributions.Length,
            "ORDER_HANDOVER_SHAPE");

        var artifacts = new Dictionary<string, OrderArtifact>(StringComparer.Ordinal);
        foreach (OrderArtifactObservation observation in store)
        {
            OrderArtifact artifact = observation.Artifact;
            OrderCheck.Require(OrderCheck.Text(artifact.Id, 100) && artifact.MediaType == "text/x-csharp"
                && artifact.ByteCount is > 0 and <= 256 * 1024
                && observation.Bytes.Length == artifact.ByteCount
                && artifact.Sha256 == DevelopmentJobState.Hash(observation.Bytes)
                && artifacts.TryAdd(artifact.Id, artifact), "ORDER_ARTIFACT_BYTES");
        }

        OrderArtifact Baseline(string id, string expectedHash)
        {
            OrderCheck.Require(artifacts.TryGetValue(id, out OrderArtifact? value) && value.JobId is null
                && value.StepId is null && value.AttemptId is null && value.Sha256 == expectedHash,
                "ORDER_BASELINE_ARTIFACT");
            return artifacts[id];
        }

        OrderArtifact calculator = Baseline("calculator-baseline", approval.Binding.CalculatorBaselineHash);
        OrderArtifact formatter = Baseline("formatter-baseline", approval.Binding.FormatterBaselineHash);
        string[] assignedAgents = assignments.Select(a => a.Agent.Id).ToArray();
        var attemptIds = new HashSet<Guid>();
        var evidenceIds = new HashSet<Guid>();
        var accepted = new List<OrderArtifact>();
        DateTimeOffset last = approval.ObservedAt;
        for (int i = 0; i < 2; i++)
        {
            OrderAssignment assignment = assignments[i];
            string stepId = i == 0 ? "S01" : "S02";
            OrderCheck.Require(assignment.StepId == stepId, "ORDER_STEP_SEQUENCE");
            ValidateAssignment(approval, jobId, assignment, agents);
            JsonElement step = Step(approval, stepId);
            OrderStepResult result = results[i];
            OrderAttempt attempt = result.Attempt;
            OrderCheck.Require(attempt.Binding == approval.Binding && attempt.JobId == jobId
                && attempt.StepId == stepId && attempt.AssignmentRevision == assignment.Revision
                && attempt.Agent == assignment.Agent && attempt.AttemptId != Guid.Empty && attemptIds.Add(attempt.AttemptId)
                && assignment.Revision == 1 && attempt.AttemptNumber == 1
                && result.Status == "candidate" && OrderCheck.Text(result.Findings, 1500), "ORDER_ATTEMPT_BINDING");

            OrderArtifact[] inputs = i == 0 ? [calculator] : [formatter, accepted[0]];
            string outputId = step.GetProperty("outputs")[0].GetString()!;
            OrderCheck.Require(result.Inputs.SequenceEqual(inputs) && result.Outputs.Length == 1
                && artifacts.ContainsKey(outputId), "ORDER_RESULT_ARTIFACTS");
            OrderArtifact output = artifacts[outputId];
            OrderCheck.Require(result.Outputs[0] == output && output.JobId == jobId
                && output.StepId == stepId && output.AttemptId == attempt.AttemptId, "ORDER_OUTPUT_PRODUCER");

            OrderStepEvidence proof = evidence[i];
            OrderCheck.Require(proof.Attempt == attempt && proof.EvidenceId != Guid.Empty
                && evidenceIds.Add(proof.EvidenceId) && proof.Inputs.SequenceEqual(inputs)
                && proof.Outputs.SequenceEqual(result.Outputs)
                && proof.ObservedAt >= last && proof.ObservedAt >= assignment.DecidedAt
                && proof.CleanupSucceeded && proof.Accepted, "ORDER_STEP_EVIDENCE");
            string[] allowedPaths = step.GetProperty("allowedWritePaths").EnumerateArray()
                .Select(p => p.GetString()!).ToArray();
            OrderCheck.Require(proof.ChangedPaths.SequenceEqual(allowedPaths)
                && proof.PreservedOutputs.SequenceEqual(accepted), "ORDER_STEP_SCOPE_EVIDENCE");
            ValidateVerifier(step, proof.Verifier, verifiers, assignedAgents);
            ValidateCriteria(step, proof.Criteria);
            accepted.Add(output);
            last = proof.ObservedAt;
        }

        JsonElement finalPlan = approval.Order.Document.GetProperty("finalVerification");
        OrderCheck.Require(final.Binding == approval.Binding && final.JobId == jobId
            && final.EvidenceId != Guid.Empty && evidenceIds.Add(final.EvidenceId)
            && final.Inputs.SequenceEqual(accepted) && final.ObservedAt >= last
            && final.CleanupSucceeded && final.Accepted, "ORDER_FINAL_EVIDENCE");
        OrderCheck.Require(final.BaselineUnchanged && final.ChangedPaths.SequenceEqual(
            approval.Order.Document.GetProperty("steps").EnumerateArray()
                .SelectMany(s => s.GetProperty("allowedWritePaths").EnumerateArray().Select(p => p.GetString()!))),
            "ORDER_FINAL_SCOPE_EVIDENCE");
        ValidateVerifier(finalPlan, final.Verifier, verifiers, assignedAgents);
        ValidateCriteria(finalPlan, final.Criteria);

        var findingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (OrderKnowledge contribution in contributions)
        {
            OrderCheck.Require(OrderCheck.Text(contribution.Id, 100) && findingIds.Add(contribution.Id)
                && OrderCheck.Text(contribution.Claim, 1500) && OrderCheck.Text(contribution.Rationale, 1500)
                && OrderCheck.Text(contribution.Relevance, 1000)
                && evidence.Any(e => e.Attempt == contribution.Attempt && e.EvidenceId == contribution.EvidenceId),
                "ORDER_KNOWLEDGE_BINDING");
        }

        // Findings are proposed unless a separate trusted review binds the exact
        // contribution. A model claim or even passing code tests is not factual verification.
        var reviewedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (OrderKnowledgeReview review in knowledgeReviews)
        {
            OrderCheck.Require(contributions.Contains(review.Contribution) && reviewedIds.Add(review.Contribution.Id)
                && review.Status is "verified" or "rejected"
                && evidence.Any(e => e.EvidenceId == review.Contribution.EvidenceId && e.Verifier == review.Reviewer),
                "ORDER_KNOWLEDGE_REVIEW");
        }
    }

    private static JsonElement Step(DevelopmentOrderApproval approval, string id)
    {
        JsonElement[] steps = approval.Order.Document.GetProperty("steps").EnumerateArray()
            .Where(s => s.GetProperty("id").GetString() == id).ToArray();
        OrderCheck.Require(steps.Length == 1, "ORDER_ASSIGNMENT_STEP");
        return steps[0];
    }

    private static void ValidateVerifier(JsonElement plan, OrderVerifier verifier,
        IReadOnlyList<OrderVerifier> catalog, string[] agents)
    {
        string id = plan.GetProperty("verifier").GetString()!;
        OrderVerifier[] reviewed = catalog.Where(v => v.Id == id).ToArray();
        OrderCheck.Require(reviewed.Length == 1 && reviewed[0] == verifier
            && OrderCheck.Text(verifier.Version, 100) && OrderCheck.Hash(verifier.Digest)
            && OrderCheck.Text(verifier.ActorId, 100) && !agents.Contains(verifier.ActorId, StringComparer.Ordinal),
            "ORDER_VERIFIER_INDEPENDENCE");
    }

    private static void ValidateCriteria(JsonElement plan, OrderCriterion[] criteria)
    {
        string[] expected = plan.GetProperty("criteria").EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToArray();
        OrderCheck.Require(criteria.Select(c => c.Id).SequenceEqual(expected) && criteria.All(c => c.Passed),
            "ORDER_CRITERION_EVIDENCE");
    }
}
