# M5c S01 – Akka.Persistence bridge experiment

Status: implemented as a disposable offline probe, CI and architectural decision pending. Issue #70; ADR-0004/0005 remain authoritative for production. This is not a new runtime, production persistence provider or migration.

## Design

Reuse the existing JobState.Probe executable and its canonical issue lease/process-launch isolation. Each case has its own temporary directory, SQLite database and stable persistence ID derived from the same synthetic JobId. The existing V1 Accepted record is **only an ownership sentinel**, never updated as a parallel workflow state. The journal is the sole authority for this fixture's workflow transitions. The actor folds string events into a small state; its commands acknowledge only from Persist callbacks. RecoveryCompleted gates commands; replay never starts effects. Synthetic result files are created exclusively and flushed; the first accepted digest is persisted before the parent kills the process. A new process must acquire the same lease, replay and compare the accepted bytes before proceeding to S02. An alternate case recovers a reserved but uncertain S01 and refuses further effects. The preexisting cross-process ownership and V1 tests remain mandatory.

Use maintained Akka.Persistence.Sql.Hosting 1.5.73 with Akka.Persistence and Akka.Persistence.Hosting 1.5.74, matching existing Akka.Hosting 1.5.74, and Microsoft.Data.Sqlite 11.0.0. All references are test-only; no custom journal implementation, no snapshots, no actual model/sandbox/GitHub operations. Use the current existing Linux and Windows probe invocation; no new CI workflow. Database opens ReadWrite (not create) on reentry. Actual package compatibility, resolved dependency/advisory/license review and runtime results remain pending.

Sources: https://getakka.net/articles/persistence/event-sourcing.html ; https://www.nuget.org/packages/Akka.Persistence.Sql.Hosting/1.5.73 ; https://www.nuget.org/packages/Akka.Persistence/1.5.74 ; https://www.nuget.org/packages/Microsoft.Data.Sqlite/11.0.0 ; https://linq2db.github.io/api/linq2db/linq2db--LinqToDB.ProviderName.html .

## Acceptance and limits

- Child holds existing issue lease; competing process receives BUSY. Kill child after persistence acknowledgment (accepted or reserved); parent confirms original claim remains, then starts a new process with the same database and identity.
- Accepted branch: replay recovers S01 artifact digest; verify actual bytes, create S02 output once; unchanged S01, no second generation effect. Unknown branch: reservation remains consumed, no output or retry.
- Test actual journal and native SQLite on both OS, not an in-memory journal or an ActorSystem-only restart. Existing V1 probe unchanged except invocation of the extra test.
- These are synthetic effects, not real external operation reconciliation or proof of durable storage through power loss. No proof of production ACLs/Unix file modes for database and sidecars, link/rollback protection, backups or migration. The simple event vocabulary is not the eventual production contract. No dual state authority; production decision and concrete private storage integration require S02 review. No CI bypass or separate workflow.

