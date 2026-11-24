# Architecture decision 0002: Narrated development jobs and explicit CLI dispatch

**Status:** Proposed in Issue #38 / Draft PR #39, 23 November 2026.
**Supersedes:** The implicit option-first Replay fallback in ADR-0001. The original
entrypoint's historical behavior is not a required compatibility contract.

## Context

The accepted Akka-based agent can run independently for multiple attempts.
Console-only progress and `Agent job failed (InvalidOperationException)` do not
explain what happened, where an execution stopped, why a retry was authorized,
or where the auditable evidence is stored. Existing Coding logging records
errors and decisions but did not cover Development.

## Decision

1. `Microsoft.Extensions.Logging.ILogger` with source-generated
   `[LoggerMessage]` methods is the single application-facing event API.
   The logging session routes the same events to a readable single-line Console
   provider, a readable rolling Serilog text file and Serilog Compact JSON
   with rotation and bounded retention.
2. Event IDs 3600–3610 form a small chronological narrative:
   requested parameters, approved contract and safety preflight, generation,
   MicroVM build/run/cleanup classification, bounded retry decision,
   terminal failure or completed report. The common `JobId` is shared with
   `agent-job-<JobId>/agent-report.json` and the Actor job.
3. `Information` logs milestones, decisions and successes;
   `Warning` means a recoverable failed attempt;
   `Error` means no safe continuation or a failed phase;
   `Debug` captures discarded stale messages only on demand.
   `logSuccessfulSteps=true` and `logDecisionSummaries=true` are now defaults;
   both remain independently configurable.
4. Errors are actionable, not just exception names. They include an explicitly
   classified stage, reason code, exception type (when available), operational
   effect where known, and a bounded next action. Report paths are displayed
   only after a report was actually written.
5. Raw model responses, prompts, generated code, sandbox compiler text,
   secrets, raw exception messages and stacktraces remain excluded from the
   durable operational log. Safe compiler codes (e.g. `CS0103`), exit codes,
   elapsed milliseconds and confirmed cleanup status are allowed. Full guest
   diagnostics remain in the existing isolated, bounded run reports.
6. `System.CommandLine` requires a **named subcommand**. A bare
   `--check .` is no longer silently dispatched to Replay; use
   `replay --check .`. The explicit `replay`, `workflow`, `coding`, and
   sandbox commands remain because they are separate implemented use cases,
   not merely compatibility shims. The sandbox/allowlist/security APIs are
   reused, not deleted or reimplemented.

## Non-goals

No compiler-experimental C# interception, Akka.Cluster, distributed tracing,
model reasoning disclosure, unbounded error text, automatic sandbox retry after
cleanup failure, or rewriting of proven security constraints.

## Verification

The `development --repo . --agent-self-test` offline test includes an
actor-coordinated two-attempt narrative with `JobId` correlation, error-code
presence and raw-data absence. Linux and Windows CI test the application
and existing log file-lock rules. On HaMi11, run
`development --repo <checkout> --agent-run --results-dir <external>`
after review; the real MicroVM smoke test is a separate gate.

**Merge remains human-approved.**


## Follow-up: trusted phase events

The standalone sandbox command retains its console behavior.
When the Development job supplies a typed progress observer,
only fixed host-defined phase codes, sandbox IDs, timings and exit
codes become structured `SandboxPhase` events (3611). Raw guest and
compiler output remains exclusively in separate per-attempt diagnostics.
The agent path suppresses the old direct console preview. Routine
Akka shutdown INFO messages are filtered; warnings and errors remain.
The final log uses `generatorCalls` while the JSON report preserves
`modelCalls` for stored report compatibility. The offline-scripted mode
never contacts a paid model.
