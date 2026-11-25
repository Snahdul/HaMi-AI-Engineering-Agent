# Development order and knowledge handover — M5b design

Status: proposed for review in #67 (S01–S03). No new execution capability. [ADR-0005](decisions/0005-project-owned-development-orders.md).

## 1. Order and approval

Reuse existing task Issue Forms for human-readable goals, context, references and acceptance. Add exactly one authoritative fenced block with opening line ```hami-order-json and closing line ``` on its own. The complete structured order includes the business plan, source references, decisions and alternatives, criteria, stable steps, dependencies, required competencies, artifacts, requested scope and budgets. [Schema](../contracts/development/development-order.schema.json) and [synthetic example](../samples/DevelopmentOrder/pair-order.json). The example Issue number 900001 is deliberately synthetic; it is not an actual approved Issue or CLI task.

Proposal: strict UTF-8, no BOM, at most 64 KiB for the Issue body and 32 KiB for the block; normalize CRLF to LF only, reject lone CR, extract the lines between the exact fences including the final LF. Reject missing/multiple/unterminated blocks, duplicate JSON property names at any depth, unknown fields/versions, invalid JSON, excessive nesting (16), invalid UTF-8, disallowed control characters. Preserve exact normalized block bytes; no reserialization, whitespace trimming or semantic normalization for SHA-256. A title inside the block must match the observed Issue title. Text outside the block is human discussion or a projection, not another source of execution instructions; all binding business constraints must be in the block.

Proposed approval record: a separate owner-authored Issue comment with exact single-line marker `HaMi-Order-Approval: v1 <revision> <SHA256> <baseCommit>` (uppercase 64-digit SHA-256, lowercase 40-digit Git commit). The host observes repository, issue identity, open state, owner, active label, exact body and matching approval comment identity/author/content. A later explicit approval or revocation invalidates the older authorization; read the bounded complete decision history, reject ambiguous/malformed or unavailable history, and check again before future execution/reentry. Proposed revocation marker: `HaMi-Order-Revoke: v1 <revision> <SHA256>`. Do not treat owner name printed in body or arbitrary copied comment JSON as authority. M5b evaluates supplied synthetic/read-only observations; it does not grant production authorization. M5c must pin the approved base and current remote/local authorization before effects. No new live Issue comment is needed for this synthetic reference.

Archive the exact approved normalized block, observed Issue metadata (including title), approval comment ID, observed body/author/timestamps, and baseline/contract/scope/policy digests before any future execution. Bind the archive hash into private owned state. Issue edits are allowed as discussion; edits to the block/title, revoked approval, altered reviewed baseline, closed issue or removed active label stop further execution. No silent rebinding, no claim transfer. A new revision requires new explicit approval and a separately reviewed transition/reconciliation policy. GitHub comments can be edited/deleted; the archive proves the trusted host's observed record, not a cryptographic signature or tamper-proof external audit.

## 2. Structure and semantic validation

Use Draft 2020-12 structural schema; select a maintained .NET validator before implementation and verify pinned version, license, transitive dependencies, duplicate-field handling, deterministic offline validation and no external schema fetching. Do not write a general schema engine. Existing strict JSON and reviewed contract mechanisms remain applicable. Use exact case-sensitive IDs and ordinal comparisons; path validation reuses DevelopmentContract.IsAllowedSourcePath and exact reviewed allowlists.

- The first profile is deliberately only the two-step SandboxCodingPair fixture. Require S01 followed by S02, exactly one dependency S02 -> S01, unique step/criterion/output IDs, no cycles, references only to existing predecessor outputs, and no ambiguous or forward input. Baseline artifact IDs and verifier IDs resolve from a host-reviewed catalog, never from user-supplied file/URL/command execution. Validate reference membership; descriptive context references are data, not fetch instructions.
- Each step's write paths must be the specific expected member of the existing reviewed scope and disjoint from accepted predecessor paths. Expected competencies and output IDs must be supported by the reviewed profile. A syntactically valid ID does not imply a registered capability. The new validator may recognize proposed verifier IDs for the read-only fixture but may not claim they are executable.
- Requested limits must fit approved profile ceilings; sums of step model/sandbox caps and final verification reservation must not exceed totals. Example caps 4 model requests, 5 guest runs including one final verification, 1800 seconds aggregate elapsed time; these are proposed synthetic profile bounds, not a change to existing two-attempt/12-minute V1 execution. Per-call/phase limits, persistent elapsed-time accounting and actual reservations are M5c. No arbitrary dependencies, paths, tool grants, project/SDK changes, model selection or network permissions in the plan.
- The host validates a proposed assignment against the plan and an independently approved capability/agent/provider catalog. For M5b use synthetic assignment fixtures only. Unknown capability, unavailable agent, unauthorized provider, wrong plan/step or revision rejects. No actual selection/model call in this slice.

## 3. Knowledge handover and provenance

Model the following separate records; names are semantic, not a mandate to create six interfaces or projects. W3C PROV is the reference vocabulary.

| Record | Required binding |
|---|---|
| ApprovedOrder | repository, issue ID, revision, exact plan hash, base, approval observation and policy hashes |
| AgentAssignment | job ID, order hash, step ID, assignment revision, eligible agent profile ID/version/digest, actual selected provider/model/configuration reference, rationale and decision time |
| StepResult | job/order/step/assignment/attempt IDs, exact consumed inputs, bounded structured proposed outputs, status and concise findings |
| StepEvidence | same identity, independently checked output artifacts, criterion/verifier identities and versions, actual outcomes, cleanup, input/output hashes and host acceptance decision |
| KnowledgeContribution | stable finding/decision ID, related step/attempt, claim, rationale/alternatives, evidence reference, status (proposed/verified/rejected), relevance to subsequent steps |

Map Entity to a versioned artifact, Activity to a specific execution or verification, Agent to the accountable human/agent/host verifier. Record used/generated/derived-from/associated-with links. A result and a verifier are not the same actor of trust. Rejected candidates and failures remain traceable; only host-accepted, hash-verified artifacts satisfy downstream inputs. Reassignment is a new assignment revision, not a rewrite of the old one. Never infer expertise from model self-description. No chat types or conversation IDs in these records.

Claim Check: an artifact descriptor binds logical artifact ID, media type, byte count, SHA-256, producing job/step/attempt and storage-class/host-resolved locator. Resolve only through the trusted repository/evidence store; reject traversal, symlinks, arbitrary URLs and wrong scope. The actual bytes and independent verification evidence must exist and match before reuse. A hash or model-proposed locator is not sufficient.

| Artifact class | Proposed retention and authority |
|---|---|
| Approved plan and observed approval | private owned immutable-by-convention snapshot before effects; public approved Git revision/reference when permitted |
| Candidate, patch, verification inputs/outputs | original private M5a-style bounded evidence, append new attempts without overwriting accepted artifacts |
| Redacted decisions and findings | original structured evidence; publish only approved safe projection into issue/repository in M5c |
| Operational status | mutable projection, never sole source of historical evidence |

No automatic deletion of required evidence during an active or recoverable job; retain accepted plan/results and referenced evidence after completion until an explicit retention policy and safe archival/restore are approved. No blanket publication of candidate data or raw prompts/diagnostics. Host-loss backup/restore and storage quotas/retention expiry require a separate accepted deployment policy. This is not a claim of tamper resistance against the same host account.

## 4. Reference case and recovery boundaries

S01 calculator-only acceptance; S02 formatter consumes accepted calculator bytes and cannot modify them; final existing Program.cs integration harness remains unchanged. Today both baseline classes throw NotImplementedException and the existing harness calls both. Dedicated trusted step verifiers do not yet exist. They must be reviewed and implemented in M5c before real execution, not replaced by generated tests. The schema/sample is a design fixture, not a runnable contract.

| Interruption | Future safe behavior (M5c) |
|---|---|
| Before an authorized effect | reserve operation and budget durably, then execute |
| Unknown model outcome | preserve consumed reservation; no blind repetition |
| Saved model output | verify and reuse exact stored bytes |
| Unknown guest outcome | reconcile known run identity; block if uncertain |
| Accepted S01 | verify retained artifact/manifest, then continue S02 without solving S01 again |
| S02 fails | bounded correction of S02 only; never change S01 silently |
| After local acceptance, before GitHub projection | persist publication intent and correlate/reconcile remote state; no duplicate effect |
| Missing bytes or stale/revoked approval | block, do not recreate a supposedly accepted artifact |

M5c must compare existing M5a state/evidence with durable Akka.Persistence before extending storage, preserve one canonical claim and one lifecycle owner, and test process interruption independently of event replay. M5d compares a standalone MAF adapter to direct MEAI on the same specialist contract. No parallel runtime.

## 5. M5b acceptance matrix

S04 implementation after review: valid fixture and synthetic approval/assignment/step evidence; reject malformed/duplicate/unknown fields, ambiguous fences, changed exact plan bytes/title/base/approval, missing or revoked approval, unauthorized actor or capability, invalid dependency or input, over-budget totals, wrong scope, wrong assignment/attempt identity, changed/missing output and non-independent verification. Verify V1 unchanged on Linux and Windows. No network/model/guest/publication calls from the new validation path. Schema package choice remains open; review before adding dependencies. S05 records exact test and CI evidence. Do not mark runtime recovery or host-loss restoration accepted on the basis of these tests.
