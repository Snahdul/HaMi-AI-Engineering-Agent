# Architecture decision 0003: Security architecture and credential isolation

**Status:** Proposed for Issue #55 / Draft PR #56, 24 November 2026.
**Related:** [ADR-0001](0001-commandline-akka-isolated-coding.md), [ADR-0002](0002-development-logging-and-explicit-cli.md), [SECURITY.md](../../SECURITY.md), [architecture guidelines](../architecture-guidelines.md), [prompt-injection threat model](../prompt-injection-threat-model.md), security baseline #1, sandbox evaluation #33, issue integration #40 and roadmap #48.

## Context

M4 introduces a new privileged capability: publication of an independently verified production candidate as a draft pull request. A successful build or a model recommendation alone is not authorization. Existing security requirements remain binding; this decision specifies their architectural application without replacing the existing threat model.

## Decision

1. **Least privilege, fail-safe defaults, complete mediation and separation of duties.** A reviewed baseline, versioned policy and explicit operator authorization delimit each capability. The host validates the exact issue, repository, base revision and candidate scope; neither issue text, prompts, model results nor guest output grant authority. Unknown, inconsistent or unavailable evidence denies the operation.
2. **Gatekeeper.** The existing publication validation independently binds the successful production attempt, accepted test output, confirmed guest removal, source bytes and patch integrity to the approved base and issue. The publisher checks the remote base and candidate Git objects before creating a branch or draft PR. No automatic merge, force push or protected-branch update. GitHub rulesets are a separate server-side control.
3. **Capability-scoped credentials.** The model adapter uses only the model credential, issue gateway only its dedicated issue credential, and publisher only its dedicated publication credential. The latter is resolved after candidate validation and held only for its publication scope. No credentials in model messages, actor messages, candidate snapshots, guest execution, reports or public logs. No fallback to personal CLI credentials. Windows Credential Manager and explicit temporary environment injection are supported mechanisms; GitHub Actions secrets may be injected only into a trusted workflow step. No privileged execution of unreviewed PR code. An environment variable is accessible to the host process even before the adapter reads it; delayed resolution is not short-lived issuance or memory isolation. Do not promise secure erasure of managed strings.
4. **Defense in depth.** Sandbox child processes receive a minimal explicit OS-dependent environment allowlist; unknown variables are excluded. The allowlist is not a guarantee that allowed paths/values or host tooling are trustworthy. The local sbx daemon/configuration, image/toolchain and host account are trusted dependencies. Verify effective guest network denial, absence of mounts, credential injection/forwarding, and other enabled channels independently of environment scrubbing. Each eligible attempt uses a fresh offline MicroVM, with verified removal before retry or publication. No generated code executes on the host.
5. **Trust boundaries.** Akka actors and C# adapters in one process are logical responsibility boundaries, not OS-enforced privilege separation. The MicroVM is the execution isolation boundary; the GitHub credential scopes and rulesets constrain remote authority. A compromised host or trusted executable remains outside the current protection claim. Do not add separate processes, a vault, a policy service or GitHub App solely in anticipation of later requirements.
6. **Audit and independent review.** Preserve fixed reason codes and redacted, correlated evidence with baseline, job, policy and result identity. Security checks are deterministic and mandatory; a model can identify risks but cannot authorize, waive, or override a failed gate. Human review and merge approval remain mandatory.

## Trust and responsibility matrix

| Component | Authority / data | Explicitly excluded |
|---|---|---|
| Operator / reviewed baseline | Approves task, scope and publication policy | Implicit rights from issue content |
| Akka coordinator | Job state, bounded retry, typed outcomes | Raw credentials; security overrides |
| Model adapter | Bounded task context and own model credential | Shell, GitHub write tools, other credentials |
| Sandbox runner | Fixed host commands, approved snapshot | Publication credentials and arbitrary model commands |
| MicroVM | Approved source and test harness, offline build/test | Host filesystem, network, secrets, MCP |
| Publication gate | Local evidence, scope and baseline verification | Model-based authorization |
| GitHub publisher | Verified bytes, dedicated credential, bounded GitHub API | Merge, force-push, unrelated writes |
| Issue gateway | Explicitly authorized issue journal | Publication token; raw logs |
| Future security reviewer | Redacted evidence and read-only architecture context | Credentials, policy editing, autonomous writes or approval |

## Future Security Architecture Agent (proposed, not implemented)

A distinct review capability can assess proposed changes against approved ADRs, threat models and security invariants; flag privilege expansion, new trust boundaries, credential flow, unsafe CI, dependency changes and missing negative tests. Start with a read-only, bounded review of a pinned diff and existing architecture decisions, reporting finding ID, severity, affected path, rule/reference, evidence, confidence and recommended test/remediation. A missing or inconclusive review is never a clean bill of health. Findings may request human review; no autonomous merge, policy changes, credentials or authority to override deterministic gates. The human decides whether and when a review is required. Do not introduce a second runtime agent, ActorSystem, persistent monitoring service or new framework in M4. Validate usefulness on actual historical issues before assigning implementation priority.

## M4 acceptance obligations (not yet verified)

- Offline regression: known and unknown secret variables removed, approved environment values retained; validate both Windows and Linux process behavior. Recheck actual sbx execution and effective isolation on the supported host.
- Reject unauthorized or failed jobs, mismatched issue/base, tampered source/patch/evidence, unconfirmed cleanup; no publication API request before local validation. Test missing/denied credentials with a fake transport and no real secrets.
- Verify exact candidate Git tree, parent, remote branch and draft identity; test repeated publication, conflicting remote state and interrupted operations. No blind retry with a new job ID. GitHub writes cannot be made atomically with local reports; ambiguous effects require reconciliation, not a claim of transactionality.
- Verify a trusted, separately approved test issue and actual remote base for any real write test. A local review mirror at an unmerged PR commit is not the remote main. Keep the remote-base check; do not relax it to make pre-merge acceptance pass. Use offline/mock tests and real secret-free MicroVM acceptance before merge; a separately approved live publication pilot on the reviewed merged baseline is required before marking M4 complete (or explicitly agree on an isolated test repository design).
- Confirm applicable repository ruleset and credential permissions, no secret-bearing untrusted PR workflows, and independently inspect the guest removal. CI success does not demonstrate real sandbox or GitHub credential isolation.

## Consequences

Existing security documentation remains authoritative for its scope. The new decision establishes testable cross-cutting boundaries; it is not a certification or proof of implementation. The current publisher and allowlist are review candidates until tests and acceptance are recorded. Short-lived GitHub App installation tokens and separate OS-enforced privileged workers may be evaluated when justified by actual deployment requirements.

## References

- Saltzer & Schroeder, The Protection of Information in Computer Systems (1975): https://www.cs.virginia.edu/~evans/cs551/saltzer/
- Provos et al., Preventing Privilege Escalation (USENIX 2003): https://www.usenix.org/legacy/event/sec03/tech/full_papers/provos_et_al/provos_et_al_html/
- Microsoft Gatekeeper pattern: https://learn.microsoft.com/en-us/azure/architecture/patterns/gatekeeper
- OWASP AI Agent Security: https://cheatsheetseries.owasp.org/cheatsheets/AI_Agent_Security_Cheat_Sheet.html
- OWASP Secrets Management: https://cheatsheetseries.owasp.org/cheatsheets/Secrets_Management_Cheat_Sheet.html
- Docker Sandboxes isolation: https://docs.docker.com/ai/sandboxes/security/isolation/
- Docker Sandboxes default security posture: https://docs.docker.com/ai/sandboxes/security/defaults
