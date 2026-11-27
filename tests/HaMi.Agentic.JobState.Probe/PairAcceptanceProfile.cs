using System.Text;
using HaMi.Agentic.Cli.Features.Development;

// Only the isolated Issue #73 acceptance host may use this profile.
// It is not a production policy, a grant of authority or a dispatch path.
internal sealed class PairAcceptanceProfile
{
    internal const int IssueNumber = 73;
    internal const long IssueId = 6646199459L;
    internal const string Model = "gpt-5.4-mini";
    private const long OwnerId = 55017501;
    private const string OwnerLogin = "Snahdul";
    private const string BaselineCommit = "44eb7f51a94eb69587426515333346d34b21997e";
    private const string OrderHash = "E159755CF0AE4ED2840A145AE36CC9502F21A15826FE8C1B0A86551FC40B83B7";
    private const string PolicyHash = "1B6CEB5B2636D8E75D9CC374659BB45398E72EEF6C3E3FF677426E7D86806815";
    private const string ContractHash = "5D9C0D4C46FB32F599FE7099108AC32D5383CEBEBC3E7A6F8D5848EB2A08CBFE";
    private const string ScopeHash = "AF9D146D246A9B27067891302BF0424943F3CED2F9B3C509B9A2EE942893D065";
    private const string CalculatorHash = "01534C05F2AF110A2D566EDC7732882C913C602A9016449AD17BAF75BC4284A6";
    private const string FormatterHash = "A55F72DABF44FF7077D2A71937050F13A2C3105D66C677BBDCF65858593765FD";
    private static readonly OrderOperationLimits Limits = new(2, 3, 1800, 1, 1, 1, 1, 1);
    private static readonly OrderRequiredOperation[] Operations =
    [
        new("S01", "model", 90),
        new("S01", "guest", 420),
        new("S02", "model", 90),
        new("S02", "guest", 420),
        new("FINAL", "guest", 420)
    ];

    private PairAcceptanceProfile(DevelopmentOrderBudgetPolicy policy) => Policy = policy;

    internal DevelopmentOrderBudgetPolicy Policy { get; }

    internal static PairAcceptanceProfile Load()
    {
        using Stream stream = typeof(PairAcceptanceProfile).Assembly.GetManifestResourceStream("HaMi.Probe.PairAcceptance73Policy")
            ?? throw new InvalidOperationException("ACCEPTANCE_POLICY_MISSING");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var utf8 = new UTF8Encoding(false, true);
        // The reviewed policy uses LF. Canonicalize checkout CRLF only;
        // every other byte change must fail the fixed digest.
        byte[] bytes = utf8.GetBytes(utf8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal));
        var policy = DevelopmentOrderBudgetPolicy.Parse(bytes);
        OrderCheck.Require(policy.Sha256 == PolicyHash && policy.Defaults == Limits
            && policy.Maximums == Limits && policy.FinalVerificationSeconds == 420
            && policy.SafetyReserveSeconds == 150 && Operations.Sum(operation => operation.Seconds) == 1440,
            "ACCEPTANCE_POLICY_BINDING");
        return new(policy);
    }

    // The future host calls this before initial claim and before every new
    // effect or reentry. A historical archive alone never grants authority.
    internal async Task<DevelopmentOrderApproval> ObserveApprovalAsync(string repository,
        GitHubIssueGateway gateway, DevelopmentOrderClaim? existing, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var baseline = await DevelopmentWorktreeGuard.ObserveOrderBaselineAsync(repository);
        var observed = await gateway.ObserveOrderAsync(IssueNumber, ct);
        var profile = new OrderReviewedProfile(GitHubIssueGateway.Repository, OwnerId, OwnerLogin,
            BaselineCommit, ContractHash, ScopeHash, Policy.Sha256, CalculatorHash, FormatterHash);
        var approval = DevelopmentOrderApproval.Observe(observed, profile, Policy);
        RequireBinding(approval.Binding);
        baseline.RequireBinding(approval.Binding);
        OrderCheck.Require(approval.EffectiveLimits == Limits
            && (existing is null || existing.Binding == approval.Binding), "ACCEPTANCE_CURRENT_AUTHORITY");
        ct.ThrowIfCancellationRequested();
        return approval;
    }

    internal void RequireBinding(OrderBinding binding)
    {
        OrderCheck.Require(binding.Repository == GitHubIssueGateway.Repository
            && binding.IssueId == IssueId && binding.IssueNumber == IssueNumber && binding.Revision == 1
            && binding.OrderHash == OrderHash && binding.BaseCommit == BaselineCommit
            && binding.ApprovalCommentId > 0 && binding.OwnerId == OwnerId && binding.OwnerLogin == OwnerLogin
            && binding.ContractHash == ContractHash && binding.ScopeHash == ScopeHash
            && binding.PolicyHash == Policy.Sha256 && binding.CalculatorBaselineHash == CalculatorHash
            && binding.FormatterBaselineHash == FormatterHash, "ACCEPTANCE_ORDER_BINDING");
    }

    private static OrderAgentIdentity AgentFor(string step)
    {
        OrderCheck.Require(step is "S01" or "S02", "ACCEPTANCE_ASSIGNMENT_STEP");
        string profileHash = DevelopmentJobState.Hash(OrderStorageJson.Encode(new
        {
            Version = 1,
            IssueId,
            OrderHash,
            PolicyHash,
            Step = step,
            Competency = "csharp",
            Provider = "openai",
            Model
        }, DevelopmentJobState.MaxBytes));
        return new("pair-acceptance-" + step, "1", profileHash, "openai", Model,
            DevelopmentAiGenerator.ReservedConfigurationHash(step, Model));
    }

    internal static OrderAgentProfile[] Catalog() =>
        [new(AgentFor("S01"), true, ["csharp"]), new(AgentFor("S02"), true, ["csharp"])];

    private OrderAssignment AssignmentFor(DevelopmentOrderClaim claim, string step, long observedUtcTicks)
    {
        RequireBinding(claim.Binding);
        OrderCheck.Require(observedUtcTicks > 0 && observedUtcTicks <= DateTime.MaxValue.Ticks,
            "ACCEPTANCE_ASSIGNMENT_TIME");
        return new(claim.Binding, Guid.ParseExact(claim.JobId, "N"), step, 1, AgentFor(step),
            "Reviewed isolated pair acceptance C# assignment.", new DateTimeOffset(observedUtcTicks, TimeSpan.Zero));
    }

    // The approval observed before reservation is the one used to validate
    // this assignment at dispatch. A subsequent current-authority check
    // must independently confirm the identical binding before any effect.
    internal OrderAssignment Assign(DevelopmentOrderClaim claim, DevelopmentOrderApproval approval,
        string step, long observedUtcTicks)
    {
        OrderCheck.Require(approval.Binding == claim.Binding && approval.EffectiveLimits == Limits,
            "ACCEPTANCE_ASSIGNMENT_AUTHORITY");
        var assignment = AssignmentFor(claim, step, observedUtcTicks);
        DevelopmentOrderHandover.ValidateAssignment(approval, Guid.ParseExact(claim.JobId, "N"), assignment, Catalog());
        return assignment;
    }

    // Replay reconstructs the exact original assignment from the immutable
    // claim and reservation timestamp, not today's approval observation.
    internal OrderAssignment RequireAssignment(DevelopmentOrderClaim claim, OrderOperationReservation request)
    {
        OrderCheck.Require(request.Kind == "model", "ACCEPTANCE_ASSIGNMENT_KIND");
        var assignment = AssignmentFor(claim, request.Step, request.ObservedUtcTicks);
        (request.Assignment ?? throw new InvalidOperationException("ACCEPTANCE_MODEL_ASSIGNMENT"))
            .RequireAssignment(assignment);
        return assignment;
    }

    // The same reviewed schedule must be used by both the admission caller
    // and the journal actor, including replay of admission decisions.
    internal OrderRequiredOperation[] RequiredAfter(DevelopmentOrderClaim claim,
        DevelopmentOrderOperationLedger ledger, OrderOperationReservation request)
    {
        RequireBinding(claim.Binding);
        OrderCheck.Require(ledger.PolicyHash == Policy.Sha256 && ledger.ArchiveHash == claim.ArchiveHash
            && ledger.CurrentLimits == Limits && ledger.FinalReserveSeconds == Policy.FinalVerificationSeconds
            && ledger.BudgetRevision == 0 && ledger.ExtensionHashes.Length == 0
            && ledger.Sequence >= 0 && ledger.Sequence < Operations.Length, "ACCEPTANCE_LEDGER_BINDING");
        var expected = Operations[ledger.Sequence];
        OrderCheck.Require(request.Step == expected.Step && request.Kind == expected.Kind
            && request.ReservedSeconds == expected.Seconds
            && ledger.ReservedSeconds == Operations.Take(ledger.Sequence).Sum(operation => operation.Seconds),
            "ACCEPTANCE_OPERATION_PLAN");
        if (request.Kind == "model")
            _ = RequireAssignment(claim, request);
        return Operations[(ledger.Sequence + 1)..];
    }

    internal OrderAdmissionDecision Decide(DevelopmentOrderClaim claim,
        DevelopmentOrderOperationLedger ledger, OrderOperationReservation request) =>
        DevelopmentOrderAdmission.Decide(claim, ledger, request, RequiredAfter(claim, ledger, request));

    internal static void SelfTest()
    {
        var profile = Load();
        // Pure binding and budget tests only; this deliberately creates no
        // approval, claim file, journal, HTTP request or sandbox.
        var binding = new OrderBinding(GitHubIssueGateway.Repository, IssueId, IssueNumber, 1, OrderHash,
            BaselineCommit, 1, OwnerId, OwnerLogin, ContractHash, ScopeHash, PolicyHash, CalculatorHash, FormatterHash);
        var claim = DevelopmentOrderClaim.Create(new string('a', 32), binding, new string('A', 64));
        long now = DateTimeOffset.UtcNow.UtcTicks;
        var ledger = new DevelopmentOrderOperationLedger().Start(new OrderExecutionStart(1, claim.ArchiveHash, now),
            claim.ArchiveHash, Limits, profile.Policy);
        var assigned = profile.AssignmentFor(claim, "S01", now);
        var agent = assigned.Agent;
        var assignment = OrderOperationAgentBinding.FromAssignment(assigned);
        var request = ledger.Propose(claim.JobId, "S01", "model", 90, now, assignment);
        var reloaded = Load().RequireAssignment(claim, request);
        var formatter = profile.AssignmentFor(claim, "S02", now);
        if (reloaded != assigned || assigned.DecidedAt.UtcTicks != now
            || assigned.Agent.ConfigurationHash == formatter.Agent.ConfigurationHash
            || assigned.Agent.ProfileHash == formatter.Agent.ProfileHash
            || Catalog().Count(item => item.Identity == assigned.Agent) != 1
            || Catalog().Count(item => item.Identity == formatter.Agent) != 1)
            throw new InvalidOperationException("Acceptance assignment cannot be reconstructed exactly.");
        var decision = profile.Decide(claim, ledger, request);
        var remaining = profile.RequiredAfter(claim, ledger, request);
        if (decision.Ledger.Pending != request || decision.Ledger.ReservedSeconds != 90
            || remaining.Length != 4 || remaining.Sum(operation => operation.Seconds) != 1350
            || remaining[^1] != new OrderRequiredOperation("FINAL", "guest", 420))
            throw new InvalidOperationException("Acceptance reservation lost mandatory work.");
        long lateTicks = now + TimeSpan.FromSeconds(361).Ticks;
        var late = profile.Decide(claim, ledger, request with
        {
            ObservedUtcTicks = lateTicks,
            Assignment = OrderOperationAgentBinding.FromAssignment(profile.AssignmentFor(claim, "S01", lateTicks))
        });
        if (late.Ledger.SoftStop?.Reason != "time" || late.Ledger.Pending is not null || late.Ledger.ReservedSeconds != 0)
            throw new InvalidOperationException("Acceptance admission did not preserve the mandatory rest budget.");

        void Reject(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Invalid acceptance profile input was accepted.");
        }

        Reject(() => profile.RequireBinding(binding with { IssueId = 1 }));
        Reject(() => profile.RequireBinding(binding with { OrderHash = new string('0', 64) }));
        Reject(() => profile.RequireBinding(binding with { BaseCommit = new string('0', 40) }));
        Reject(() => profile.RequireBinding(binding with { PolicyHash = new string('0', 64) }));
        Reject(() => profile.Decide(claim, ledger, request with { ReservedSeconds = 91 }));
        Reject(() => profile.Decide(claim, ledger, request with { Step = "S02" }));
        Reject(() => profile.Decide(claim, ledger with { BudgetRevision = 1 }, request));
        Reject(() => profile.Decide(claim, ledger with { ReservedSeconds = 1 }, request));
        Reject(() => profile.Decide(claim, ledger, request with
        {
            Assignment = assignment with { Agent = agent with { Model = "other-model" } }
        }));
        Reject(() => profile.RequireAssignment(claim, request with { ObservedUtcTicks = now + 1 }));
        Reject(() => profile.RequireAssignment(claim, request with
        {
            Assignment = assignment with { Agent = agent with { ConfigurationHash = new string('0', 64) } }
        }));
        Reject(() => profile.RequireAssignment(claim, request with
        {
            Assignment = assignment with { AssignmentHash = new string('0', 64) }
        }));
        Console.WriteLine("PASS: Issue 73 acceptance profile pins order, baseline and budget; exact five-operation plan preserves FINAL reserve and rejects altered bindings, extensions and model substitutions.");
        Console.WriteLine("PASS: Issue 73 model assignments reconstruct exact reserved identity without a client; timestamp, configuration and assignment hash substitutions rejected.");
    }
}
