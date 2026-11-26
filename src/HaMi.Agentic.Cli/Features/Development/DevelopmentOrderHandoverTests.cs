using System.Text;
using System.Text.Json.Nodes;

namespace HaMi.Agentic.Cli.Features.Development;

internal static class DevelopmentOrderHandoverTests
{
    internal static void SelfTest(string plan, DevelopmentOrderBudgetPolicy policy)
    {
        byte[] body = Encoding.UTF8.GetBytes("### Approved plan\n\n" + "\x60\x60\x60hami-order-json\n" + plan + "\x60\x60\x60\n");
        DevelopmentOrder order = DevelopmentOrder.ParseIssueBody(body);
        DateTimeOffset time = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        string hash = DevelopmentJobState.Hash(Encoding.UTF8.GetBytes("synthetic reviewed profile"));
        string baseCommit = order.Document.GetProperty("baseCommit").GetString()!;
        string repository = order.Document.GetProperty("repository").GetString()!;
        string title = order.Document.GetProperty("title").GetString()!;
        int number = order.Document.GetProperty("issueNumber").GetInt32();
        // Expected baseline digests are independent of the supplied artifact store.
        string baselineOne = DevelopmentJobState.Hash(Encoding.UTF8.GetBytes("synthetic baseline one"));
        string baselineTwo = DevelopmentJobState.Hash(Encoding.UTF8.GetBytes("synthetic baseline two"));
        var profile = new OrderReviewedProfile(repository, 42, "fixture-owner", baseCommit, hash, hash, policy.Sha256,
            baselineOne, baselineTwo);
        var comment = new OrderCommentObservation(1, 42, "fixture-owner",
            $"HaMi-Order-Approval: v1 1 {order.Sha256} {baseCommit}", time, time);
        var issue = new OrderIssueObservation(repository, 901, number, title, 42, "fixture-owner",
            true, true, body, time.AddMinutes(1), true, [comment]);

        DevelopmentOrderApproval Approve(OrderIssueObservation observation) => DevelopmentOrderApproval.Observe(observation, profile, policy);
        DevelopmentOrderApproval approved = Approve(issue);
        DevelopmentOrderClaimTests.SelfTest(approved);
        DevelopmentOrderOperationLedgerTests.SelfTest(approved);

        void Reject(Action action, string reason)
        {
            try
            {
                action();
            }
            catch (InvalidOperationException ex) when (ex.Message == reason)
            {
                return;
            }

            throw new InvalidOperationException("Expected rejection: " + reason);
        }

        // A human approves the unchanged plan without budget fields. The
        // host resolves defaults; no second approval marker is required.
        JsonObject without = JsonNode.Parse(plan)!.AsObject();
        without.Remove("limits");
        foreach (JsonNode? step in without["steps"]!.AsArray())
        {
            step!.AsObject().Remove("maxModelCalls");
            step.AsObject().Remove("maxSandboxRuns");
        }
        without["finalVerification"]!.AsObject().Remove("maxSandboxRuns");
        byte[] defaultBody = Encoding.UTF8.GetBytes("\x60\x60\x60hami-order-json\n" + without.ToJsonString() + "\n\x60\x60\x60\n");
        DevelopmentOrder defaultOrder = DevelopmentOrder.ParseIssueBody(defaultBody);
        var defaultComment = comment with { Body = $"HaMi-Order-Approval: v1 1 {defaultOrder.Sha256} {baseCommit}" };
        DevelopmentOrderApproval defaultApproval = Approve(issue with { Body = defaultBody, Comments = [defaultComment] });
        if (defaultApproval.EffectiveLimits != policy.Defaults || defaultApproval.Order.Plan != defaultOrder.Plan)
            throw new InvalidOperationException("Implicit budget changed approved plan.");
        DevelopmentOrderClaimTests.SelfTest(defaultApproval);

        // Use a deliberately tighter test policy to distinguish defaults
        // from explicit requests. The current real fixture is not a
        // production budget recommendation.
        JsonObject tighter = JsonNode.Parse(policy.Source)!.AsObject();
        tighter["defaults"]!["elapsedSeconds"] = 60;
        tighter["maximums"]!["elapsedSeconds"] = 120;
        var changed = DevelopmentOrderBudgetPolicy.Parse(Encoding.UTF8.GetBytes(tighter.ToJsonString()));
        Reject(() => DevelopmentOrderApproval.Observe(issue, profile, changed), "ORDER_BUDGET_POLICY_BINDING");
        Reject(() => changed.Resolve(order), "ORDER_BUDGET_MAXIMUM");
        var changedApproval = DevelopmentOrderApproval.Observe(
            issue with { Body = defaultBody, Comments = [defaultComment] },
            profile with { PolicyHash = changed.Sha256 }, changed);
        if (changedApproval.EffectiveLimits.Seconds != 60 || defaultApproval.EffectiveLimits.Seconds != policy.Defaults.Seconds)
            throw new InvalidOperationException("Policy change retroactively changed accepted budget.");
        Reject(() => DevelopmentOrderBudgetPolicy.Parse(Encoding.UTF8.GetBytes("{}")), "ORDER_BUDGET_POLICY_FIELDS");
        tighter["defaults"]!["elapsedSeconds"] = 121;
        Reject(() => DevelopmentOrderBudgetPolicy.Parse(Encoding.UTF8.GetBytes(tighter.ToJsonString())), "ORDER_BUDGET_MAXIMUM");
        JsonObject safety = JsonNode.Parse(policy.Source)!.AsObject();
        safety.Remove("safetyReserveSeconds");
        Reject(() => DevelopmentOrderBudgetPolicy.Parse(Encoding.UTF8.GetBytes(safety.ToJsonString())), "ORDER_BUDGET_POLICY_FIELDS");
        foreach (int invalid in new[] { -1, 0, 151 })
        {
            safety["safetyReserveSeconds"] = invalid;
            Reject(() => DevelopmentOrderBudgetPolicy.Parse(Encoding.UTF8.GetBytes(safety.ToJsonString())), "ORDER_BUDGET_SAFETY_TIME");
        }
        safety["safetyReserveSeconds"] = 40;
        var shorterSafety = DevelopmentOrderBudgetPolicy.Parse(Encoding.UTF8.GetBytes(safety.ToJsonString()));
        if (shorterSafety.SafetyReserveSeconds != 40 || shorterSafety.Sha256 == policy.Sha256
            || shorterSafety.Defaults != policy.Defaults || shorterSafety.FinalVerificationSeconds != policy.FinalVerificationSeconds)
            throw new InvalidOperationException("Safety reserve changed business budget.");
        Reject(() => DevelopmentOrderApproval.Observe(issue, profile, shorterSafety), "ORDER_BUDGET_POLICY_BINDING");
        safety["version"] = 1;
        Reject(() => DevelopmentOrderBudgetPolicy.Parse(Encoding.UTF8.GetBytes(safety.ToJsonString())), "ORDER_BUDGET_POLICY_VERSION");
        Console.WriteLine("PASS: omitted budget resolved from reviewed policy; exact policy binding and immutable effective limits.");

        Reject(() => Approve(issue with { IsOpen = false }), "ORDER_ISSUE_AUTHORITY");
        Reject(() => Approve(issue with { IsActive = false }), "ORDER_ISSUE_AUTHORITY");
        Reject(() => Approve(issue with { AuthorId = 43 }), "ORDER_ISSUE_AUTHORITY");
        Reject(() => Approve(issue with { Title = "Changed" }), "ORDER_OBSERVATION_BINDING");
        Reject(() => Approve(issue with { Number = number + 1 }), "ORDER_OBSERVATION_BINDING");
        Reject(() => DevelopmentOrderApproval.Observe(issue, profile with { BaseCommit = new string('a', 40) }, policy),
            "ORDER_OBSERVATION_BINDING");
        Reject(() => Approve(issue with { CompleteHistory = false }), "ORDER_APPROVAL_HISTORY");
        Reject(() => Approve(issue with { Comments = [] }), "ORDER_APPROVAL_AMBIGUOUS");
        Reject(() => Approve(issue with { Comments = [comment with { AuthorId = 43 }] }), "ORDER_COMMENT_AUTHORITY");
        Reject(() => Approve(issue with { Comments = [comment with { AuthorLogin = "other" }] }), "ORDER_COMMENT_AUTHORITY");
        Reject(() => Approve(issue with { Comments = [comment with { AuthorId = 43, AuthorLogin = "other" }] }),
            "ORDER_APPROVAL_AMBIGUOUS");
        Reject(() => Approve(issue with { Comments = [comment with { UpdatedAt = time.AddSeconds(1) }] }), "ORDER_APPROVAL_MARKER");
        Reject(() => Approve(issue with { Comments = [comment with { Body = comment.Body + "\n" }] }), "ORDER_APPROVAL_MARKER");
        Reject(() => Approve(issue with { Comments = [comment, comment with { Id = 2 }] }), "ORDER_APPROVAL_AMBIGUOUS");
        var revoke = comment with
        {
            Id = 2,
            Body = $"HaMi-Order-Revoke: v1 1 {order.Sha256}",
            CreatedAt = time.AddSeconds(1),
            UpdatedAt = time.AddSeconds(1)
        };
        Reject(() => Approve(issue with { Comments = [revoke, comment] }), "ORDER_APPROVAL_STALE_OR_REVOKED");
        Reject(() => Approve(issue with
        {
            Body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body)
                .Replace(plan, " " + plan, StringComparison.Ordinal))
        }), "ORDER_APPROVAL_STALE_OR_REVOKED");
        var reapproval = comment with { Id = 3, CreatedAt = time.AddSeconds(2), UpdatedAt = time.AddSeconds(2) };
        if (Approve(issue with { Comments = [reapproval, revoke, comment] }).Binding.ApprovalCommentId != 3)
            throw new InvalidOperationException("Latest explicit approval not bound.");

        Guid job = new("10000000-0000-0000-0000-000000000001");
        var agent = new OrderAgentIdentity("fixture-agent", "1", hash, "fixture-provider", "fixture-model", hash);
        var catalog = new OrderAgentProfile(agent, true, ["csharp"]);
        var secondAgent = agent with { Id = "fixture-agent-two" };
        var secondProfile = new OrderAgentProfile(secondAgent, true, ["csharp"]);
        var a1 = new OrderAssignment(approved.Binding, job, "S01", 1, agent, "Reviewed capability match", time.AddMinutes(2));
        var a2 = a1 with { StepId = "S02", Agent = secondAgent };
        OrderAssignment[] assignments = [a1, a2];
        DevelopmentOrderHandover.ValidateAssignment(approved, job, a1, [catalog]);
        Reject(() => DevelopmentOrderHandover.ValidateAssignment(approved, job, a1, [catalog with { Enabled = false }]),
            "ORDER_AGENT_CAPABILITY");
        Reject(() => DevelopmentOrderHandover.ValidateAssignment(approved, job, a1, [catalog with { Competencies = ["shell"] }]),
            "ORDER_AGENT_CAPABILITY");
        Reject(() => DevelopmentOrderHandover.ValidateAssignment(approved, job,
            a1 with { Agent = agent with { Provider = "unapproved" } }, [catalog]), "ORDER_AGENT_CAPABILITY");
        Reject(() => DevelopmentOrderHandover.ValidateAssignment(approved, job,
            a1 with { Binding = approved.Binding with { Revision = 2 } }, [catalog]), "ORDER_ASSIGNMENT_BINDING");

        var t1 = new OrderAttempt(approved.Binding, job, "S01", 1,
            new Guid("20000000-0000-0000-0000-000000000001"), 1, agent);
        var t2 = t1 with { StepId = "S02", Agent = secondAgent, AttemptId = new Guid("20000000-0000-0000-0000-000000000002") };

        OrderArtifactObservation Artifact(string id, string content, OrderAttempt? attempt = null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            return new OrderArtifactObservation(new OrderArtifact(id, "text/x-csharp", bytes.Length,
                DevelopmentJobState.Hash(bytes), attempt?.JobId, attempt?.StepId, attempt?.AttemptId), bytes);
        }

        OrderArtifactObservation b1 = Artifact("calculator-baseline", "synthetic baseline one");
        OrderArtifactObservation b2 = Artifact("formatter-baseline", "synthetic baseline two");
        OrderArtifactObservation o1 = Artifact("calculator-source", "synthetic accepted one", t1);
        OrderArtifactObservation o2 = Artifact("formatter-source", "synthetic accepted two", t2);
        OrderArtifactObservation[] store = [b1, b2, o1, o2];
        var r1 = new OrderStepResult(t1, [b1.Artifact], [o1.Artifact], "candidate", "Proposed calculation");
        var r2 = new OrderStepResult(t2, [b2.Artifact, o1.Artifact], [o2.Artifact], "candidate", "Proposed format");
        OrderStepResult[] results = [r1, r2];

        var v1 = new OrderVerifier("pair-calculator", "1", hash, "fixture-host");
        var v2 = v1 with { Id = "pair-formatter" };
        var vf = v1 with { Id = "pair-integration" };
        OrderVerifier[] verifiers = [v1, v2, vf];
        var e1 = new OrderStepEvidence(new Guid("30000000-0000-0000-0000-000000000001"), t1, v1,
            r1.Inputs, r1.Outputs, [DevelopmentAgentContract.PairFirst], [],
            [new OrderCriterion("C01", true)], true, true, time.AddMinutes(3));
        var e2 = new OrderStepEvidence(new Guid("30000000-0000-0000-0000-000000000002"), t2, v2,
            r2.Inputs, r2.Outputs, [DevelopmentAgentContract.PairSecond], [o1.Artifact],
            [new OrderCriterion("C02", true)], true, true, time.AddMinutes(4));
        OrderStepEvidence[] evidence = [e1, e2];
        var final = new OrderFinalEvidence(new Guid("30000000-0000-0000-0000-000000000003"), approved.Binding,
            job, vf, [o1.Artifact, o2.Artifact], [DevelopmentAgentContract.PairFirst, DevelopmentAgentContract.PairSecond],
            true, [new OrderCriterion("C03", true)], true, true, time.AddMinutes(5));
        var finding = new OrderKnowledge("finding-1", t1, e1.EvidenceId, "A proposed finding", "Recorded rationale", "S02 context");
        var review = new OrderKnowledgeReview(finding, v1, "verified");

        void Verify(OrderAssignment[]? aa = null, OrderStepResult[]? rr = null, OrderStepEvidence[]? ee = null,
            OrderFinalEvidence? ff = null, OrderArtifactObservation[]? ss = null,
            OrderVerifier[]? vv = null, OrderKnowledgeReview[]? kk = null) =>
            DevelopmentOrderHandover.ValidateReference(approved, job, aa ?? assignments, rr ?? results,
                ee ?? evidence, ff ?? final, [catalog, secondProfile], vv ?? verifiers, ss ?? store, [finding], kk ?? [review]);

        Verify();
        Verify(kk: []); // Proposed findings need no assertion of truth.
        Reject(() => Verify(aa: [a2, a1]), "ORDER_STEP_SEQUENCE");
        Reject(() => Verify(rr: [r1, r2 with { Attempt = t2 with { AssignmentRevision = 2 } }]), "ORDER_ATTEMPT_BINDING");
        Reject(() => Verify(rr: [r1, r2 with { Attempt = t2 with { AttemptId = t1.AttemptId } }]), "ORDER_ATTEMPT_BINDING");
        Reject(() => Verify(rr: [r1, r2 with { Inputs = [b2.Artifact] }]), "ORDER_RESULT_ARTIFACTS");
        Reject(() => Verify(ee: [e1 with { Accepted = false }, e2]), "ORDER_STEP_EVIDENCE");
        Reject(() => Verify(ee: [e1, e2 with { Attempt = t1 }]), "ORDER_STEP_EVIDENCE");
        Reject(() => Verify(ee: [e1, e2 with { Criteria = [new OrderCriterion("C02", false)] }]), "ORDER_CRITERION_EVIDENCE");
        OrderVerifier self = v1 with { ActorId = agent.Id };
        Reject(() => Verify(ee: [e1 with { Verifier = self }, e2], vv: [self, v2, vf]), "ORDER_VERIFIER_INDEPENDENCE");
        Reject(() => Verify(ss: [b1, b2, o1 with { Bytes = [1, 2, 3] }, o2]), "ORDER_ARTIFACT_BYTES");
        Reject(() => Verify(ss: [b1, b2, o1]), "ORDER_HANDOVER_SHAPE");
        Reject(() => Verify(ff: final with { Inputs = [o2.Artifact, o1.Artifact] }), "ORDER_FINAL_EVIDENCE");
        Reject(() => Verify(ff: final with { CleanupSucceeded = false }), "ORDER_FINAL_EVIDENCE");
        Reject(() => Verify(kk: [review with { Contribution = finding with { Claim = "Different claim" } }]), "ORDER_KNOWLEDGE_REVIEW");
        // Substituting both bytes and descriptor must still fail against the reviewed base.
        OrderArtifactObservation substituted = Artifact("calculator-baseline", "different but self-consistent baseline");
        Reject(() => Verify(ss: [substituted, b2, o1, o2], rr: [r1 with { Inputs = [substituted.Artifact] }, r2]),
            "ORDER_BASELINE_ARTIFACT");
        OrderVerifier otherParticipant = v1 with { ActorId = secondAgent.Id };
        Reject(() => Verify(ee: [e1 with { Verifier = otherParticipant }, e2], vv: [otherParticipant, v2, vf]),
            "ORDER_VERIFIER_INDEPENDENCE");
        Reject(() => Verify(ee: [e1, e2 with { ChangedPaths = [DevelopmentAgentContract.PairFirst, DevelopmentAgentContract.PairSecond] }]),
            "ORDER_STEP_SCOPE_EVIDENCE");
        Reject(() => Verify(ee: [e1, e2 with { PreservedOutputs = [] }]), "ORDER_STEP_SCOPE_EVIDENCE");
        Reject(() => Verify(ff: final with { BaselineUnchanged = false }), "ORDER_FINAL_SCOPE_EVIDENCE");
        Reject(() => Verify(ff: final with { ChangedPaths = [DevelopmentAgentContract.PairSecond] }), "ORDER_FINAL_SCOPE_EVIDENCE");
        Reject(() => Verify(rr: [r1, r2 with { Attempt = t2 with { AttemptNumber = 2 } }]), "ORDER_ATTEMPT_BINDING");
        Console.WriteLine("Development order approval, assignment, synthetic artifact/evidence and knowledge correlation self-test passed.");
    }
}
