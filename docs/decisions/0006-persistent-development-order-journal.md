# ADR-0006: Persistent development order journal

**Status:** Architecture approved by project owner on 25 November 2026, [Issue #70](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/70#issuecomment-7162605006); implementation and acceptance pending. Extends ADR-0004/0005 for new multi-step orders only. No V1 migration, no production dispatch or new external effects authorized by this decision.

## Decision and evidence

Use a domain bridge to the maintained Akka.Persistence SQL journal with SQLite. S01 test-only prototype at 111b66935c99b6035d4173324ab3a950c11275aa passed actual process kill and reentry on Windows and Linux ([CI 45613428906](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/actions/runs/45613428906)); existing V1 claim/lease tests also passed. Reuse the host's canonical issue lease, protected account root and evidence store. Keep the host as authorization/lifecycle/effect owner. Only the journal owns mutable workflow state; the actor is a replayed view. A new job's immutable version-2 claim uses the same issue-N.json path and same issue-N.lock as V1. No second state file or lease. V1 can never resume a version-2 job; updated V1 lookup recognizes valid version-2 claims and rejects matching IDs while retaining V1 lookup of other jobs. Old binaries fail closed on unknown state schemas (including registry scan); downgrade is not a supported recovery procedure.

Prefer one private SQLite journal per job initially; bind stable persistence ID, approved archive and versioned events. No custom journal provider, cluster, DB server, snapshots or automatic migration. Keep current V1 publication recovery unchanged. Do not persist prompts or secrets. The host stores verified bytes and evidence before acceptance events, reserves operation identity and budget before dispatch, and reconciles uncertain effects instead of replaying them. RecoveryCompleted does not establish a valid job (empty journal also recovers). Validate expected journal identity, event history, artifact bytes and current authorization before further effects. Missing/ambiguous state fails closed. No automatic recreation, rollback, deletion or repair. Same-account/admin compromise, host loss and power loss remain outside the accepted process-crash boundary.

## S03 implementation increments

S03.1 creates a strict immutable claim and historical approval archive from the existing validated approval observation. Archive stores normalized plan, exact selected approval comment/identity/times and observed title/time, with all reviewed digests in the binding. It intentionally excludes unrelated Issue comments/raw discussion. Archive is not authenticated current authority. Claim first, then exclusively create and flush archive: an interrupted or missing archive blocks that issue; retain incomplete state. Use existing same-directory atomic claim replacement primitive (initial move without overwrite), private file creation and readback. The canonical claim and archive encode only host-produced records, with exact serialization checks and bounded size; no import or CLI path override. Fixed schema rejects duplicates/unknown fields and unrecognized versions. S03.1 adds deterministic offline tests; no SQLite production startup or external dispatch yet.

S03.2 must implement and prove protected journal bootstrap/reentry, ACL/UID/permissions and sidecars, link checks, real provider connection durability settings, exact artifact binding, missing/empty/wrong journal rejection and bounded native dependency/advisory/license review. S04 adds operational events, stable reservations and budgets; S05 verifiers; S06 actual orchestration; S07 full review. Do not enable a new production path before these gates. The existing test-only prototype remains separate from these production primitives until integration is verified.

## Alternatives

- Extend JSON checkpoints and own event replay: technically feasible, but repeats tested framework capabilities and increases custom journal code.
- Implement custom Akka journal provider: unnecessary with working standard SQLite provider; would require full replay/atomic write/sequence contract.
- Move all ownership into actors: insufficient cross-process and approval authority; retain host lease.
- Store both mutable JSON workflow checkpoint and journal: inconsistent after crash between writes; rejected.

Source: [S02 comparison](https://github.com/Snahdul/HaMi-AI-Engineering-Agent/issues/70#issuecomment-7162562667), [Akka.Persistence](https://getakka.net/articles/persistence/event-sourcing.html), [SQLite durability](https://www.sqlite.org/pragma.html). Actual resolved dependency graph/security, provider configuration and private-root integration remain acceptance work.
