# Architecture decision 0001: Typed CLI and actor-driven isolated coding

**Status:** Implemented for the limited Issue #36 vertical slice, 23 November 2026.
**Prerequisite:** Issue #33, merged PR #35, real HaMi11 Docker Sandbox acceptance.

## Decision

1. **System.CommandLine 3.0.0** becomes the parsing, command discovery and dispatch boundary. Historical option-first Replay invocations remain compatible. Legacy handlers stay operational while command parsing is migrated incrementally.
2. A **versioned human-reviewed contract** defines the first task. It permits producing exactly one source file (Program.cs) for a fixed .NET 11 console project and a maximum of two model attempts. This cannot grant host shell, GitHub write, extra file or network privileges.
3. **Akka.Hosting 1.5.74** manages an in-process ActorSystem and one coordinator actor for each coding job. The actor owns attempt numbering, state and terminal decisions. It does not compile code itself. Remote, cluster, distributed scheduling, durable persistence and automatic actor restart are excluded.
4. **IDevelopmentCandidateGenerator** isolates model access. The default offline fake intentionally generates a compiler error then repairs it using bounded feedback. The opt-in live generator uses Microsoft.Extensions.AI IChatClient, structured JSON responses, no model tools and store=false.
5. **IDevelopmentIsolatedRunner** is the only execution port. The adapter checks a trusted two-file fixture, stages it outside the production repository and modifies ONLY Program.cs in the temporary copy. Build and run occur inside a fresh Docker MicroVM via the existing DevelopmentSandboxRunner from PR #35. No arbitrary candidate code or MSBuild target executes on the Windows host.
6. Actor-level reports and individual MicroVM reports remain separate for audit.

## State and retry semantics

Start -> Generating -> Generated -> Executing -> Executed. Successful execution terminates. Compiler/runtime failure feeds a sanitized bounded diagnostic back to a new generation if attempts remain. A timeout or unconfirmed MicroVM cleanup terminates immediately. Cancellation and exceptional failures also terminate. Stale result messages from earlier attempts are ignored by attempt ID and state.

Supervision NEVER restarts a previously attempted MicroVM automatically; replay and external side effects are not idempotent by default. A future durable queue requires an explicit design and acceptance decision.

## Security invariants

- Generated code cannot select tool invocations, new dependencies, files, paths, local host commands, GitHub rights or network exceptions.
- Snapshot guards, explicit attempt caps, offline restore, mountless guest, denied network and empty guest credentials remain enforced by the sandbox runner. Each attempt owns its own isolated guest.
- Compiler and test outputs are untrusted DATA, never instructions; only a bounded sanitized excerpt is sent to the model.
- Existing Git worktree-guard rules remain unchanged; this initial vertical slice writes only external temporary candidate snapshots and does not change any Git repository content.
- Historical expression-only CodingCommand is not expanded into arbitrary host code execution.

## Responsibilities

| Component | Role |
|---|---|
| CommandLineRouter | Typed CLI parsing and dispatch |
| DevelopmentAgentContract | Reviewed task boundary |
| DevelopmentJobActor | Single-job state, retry, cancellation, terminal outcomes |
| IDevelopmentCandidateGenerator | Schema-limited model response |
| IDevelopmentIsolatedRunner | Isolated build/run and feedback adapter |
| DevelopmentJobReport | Final immutable attempt summary |

Pinned packages: System.CommandLine 3.0.0, Akka.Hosting 1.5.74 (Apache-2.0), Microsoft.Extensions.Hosting 11.0.0.

References:
- https://www.nuget.org/packages/System.CommandLine/3.0.0
- https://www.nuget.org/packages/Akka.Hosting/1.5.74
- https://github.com/akkadotnet/Akka.Hosting
- https://learn.microsoft.com/en-us/dotnet/standard/commandline/get-started-tutorial

## Verification

Offline CI and local selftest: development --repo . --agent-self-test.
Local HaMi11 test: development --repo . --agent-run --results-dir G:\HaMi\Sandbox\PoC\Runs.
Opt-in live model run: add --live --model gpt-5.4-mini (API charges possible).

No permission to merge, publish or execute arbitrary host code follows from these commands.

## Deferred scope

Additional reviewed source paths and multi-file contracts, analyzers, realistic agent-generated test projects, model prompt-injection hardening, cancellation propagation into the entire sbx lifecycle, persistent actor recovery and production-ready retry policies are future separately accepted changes. The first slice intentionally demonstrates a complete bounded fix-and-build loop, not a full general-purpose autonomous coder.
