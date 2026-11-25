# Architecture decision 0005: Project-owned development orders and provenance

**Status:** Contract direction approved by project owner, 25 November 2026; implementation and acceptance in progress, [Issue #67](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/67). The direction and contract draft were approved; implementation, dependency review and acceptance remain open.
**Related:** [ADR-0001](0001-commandline-akka-isolated-coding.md), [ADR-0003](0003-security-architecture-and-credential-isolation.md), [ADR-0004](0004-durable-developer-job-recovery.md), roadmap #48, #19. Reviewed baseline: `9fa33db907c0a65e3766ed95f3e1e75dd40e25f3`.

## Context and decision

The project owns the knowledge, not the agent. Human-led engineering fixes the complete business decomposition, rationale, acceptance criteria and boundaries in the Issue before execution. The AI selects and coordinates approved specialists; the deterministic host enforces dependencies, scope, budgets, evidence and acceptance. A completed step must be reusable independently of the agent, provider, process or conversation. The approved plan is not a runtime capability. Existing M5a ownership, V1 publication recovery and credential separation remain unchanged.

Use an explicitly versioned, structured order in the Issue and an approval bound to its exact content and reviewed Git baseline. Preserve the approved bytes and approval observation, not just an Issue URL or a hash. Agent assignments are separate, versioned execution decisions. Changes to approved content require a new revision and human approval; routine correction inside the existing scope does not. Do not silently reinterpret an edited issue or a newly approved revision as the same running job.

Use a task-oriented Request/Result domain. IChatClient is an integration detail. Explicit versioned inputs, accepted artifacts and redacted findings replace a necessary provider conversation. Model-produced claims are not independent evidence. Capture useful rationale and decisions, not hidden reasoning, raw transcripts or secrets.

Adopt Scheduler-Agent-Supervisor, routing, sequential execution, independent evaluator/optimizer, Claim Check, idempotent effects and an outbox-style publication intent. Use W3C PROV-DM Entity/Activity/Agent and usage/generation/derivation/association as a vocabulary, not a requirement for RDF. JSON Schema Draft 2020-12 defines the structural order format. Host validation enforces semantic and authority constraints; schema validation alone cannot grant permission.

Keep the existing Developer slice and Akka technical coordinator. MEAI remains the provider boundary. Evaluate a standalone Microsoft Agent Framework specialist adapter in M5d against direct IChatClient, not a second workflow runtime. Evaluate existing state/evidence vs Akka.Persistence with a durable journal in M5c, not a second authoritative state owner. A journal is not necessarily Event Sourcing. No re-execution of external effects merely because events are replayed.

## Alternatives

- Issue prose/Markdown tables alone: readable but ambiguous for machine execution and exact version binding. Use a structured block as the authoritative specification; prose/journal outside it is not execution input.
- Native sub-issues/dependencies: useful for project planning and navigation, but individually mutable and not a single approved execution snapshot. Do not mirror two authoritative dependency graphs. For the first two-step order keep stable IDs in one issue; later sub-issue references may be projected from the plan.
- Git-only plan requiring a separate merge for each job: versionable but introduces an extra routine human gate. Use explicit issue approval and archive its exact bytes before effects; public Git snapshots are optional under approved publication policy, not an implicit extra merge requirement.
- Provider session or local reports alone: cannot reconstruct the approved business context independently. Retain project-owned artifacts and explicit context.
- New universal agent platform or parallel workflow engine: not needed for the reviewed Developer scope.

## Scope and consequences

[Contract and evidence design](../developer-order-contract.md), [draft order schema](../../contracts/development/development-order.schema.json), [synthetic reference order](../../samples/DevelopmentOrder/pair-order.json) specify the proposed M5b boundary. M5b read-only parsing/validation and deterministic offline tests; no new runtime dispatch, GitHub write capability, live model, guest execution or publication. M5c implements actual safe checkpoints, artifact retention and idempotent projection; M5d specialist selection and productive integration. Total host loss requires a separately approved backup/restore strategy. A checksum verifies bytes, not authenticity, availability or human authorship. The owner approved replacing the unsuccessful Corvus integration with a bounded in-house Draft 2020-12 subset based on System.Text.Json. No new dependency, no general schema engine, no weakening of quality gates. See the contract design for supported semantics, alternatives and pending acceptance.

## References

- Existing methodology: https://github.com/Snahdul/HaMi-AI-Engineering/blob/1b83080eeddc33cf9a990754d97674c364512a5b/docs/methodology/workflow.md
- Issue #67 architecture analysis and framework comparison: https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/67#issuecomment-7160113285 ; https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/67#issuecomment-7160638083
- Scheduler-Agent-Supervisor: https://learn.microsoft.com/en-us/azure/architecture/patterns/scheduler-agent-supervisor
- W3C PROV-DM: https://www.w3.org/TR/prov-dm/
- JSON Schema: https://json-schema.org/draft/2020-12
