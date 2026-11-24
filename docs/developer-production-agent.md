# HaMi Developer M3b: autonomous production expression correction

## Purpose

M3b links the **existing actor-driven model retry** to the **M3a reviewed real
production candidate verifier**. The agent now generates an actual local Git
diff against `src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs`, checks
the independent 268-test harness inside a newly isolated MicroVM for each
eligible attempt, and retries a correctable failure without further human
interaction. It **does not** create Git commits, push branches, open PRs,
merge, execute model-generated host code or modify the trusted `main` checkout.

The first production scope intentionally remains a single expression, not
arbitrary repository modifications. This is a functional and security
integration step, not a claim that arbitrary code is supported.

## Review authority

1. `contracts/development/agent-production-retry-budget.json` pins the
   task, model output, project, expected output and **two** attempts.
2. `contracts/development/agent-production-retry-budget-scope.json` is a
   versioned instance of the **existing** `DevelopmentContract` schema and
   authorizes exactly one path, `RetryBudget.cs`. Unlike the older
   `retry-budget-refinement.json` read-only demonstration, this new
   reviewed contract *explicitly authorizes model-generated source to be
   built and tested inside an offline MicroVM*. The old contract remains
   unchanged and cannot be used to escalate to model-driven execution.
3. `DevelopmentProductionExpression` accepts exactly one `expression`
   JSON string, with no extra keys, duplicate keys, paths, commands,
   unsupported identifiers or oversize contents. The host patches only
   the trusted existing method via `CodingTaskDefinition.ReplaceExpression`.
   Invalid output is a bounded `source-rejected` retry without any write.
4. Before generation and each attempt, the production runner checks the
   trusted GitHub origin, clean `main` branch, unaltered branch commit,
   committed reviewed scope, and external results directory. Each attempt
   uses its own detached Git worktree.
5. The M3a production verifier rechecks the candidate Git diff, exact
   one-file source modification, fixed host-owned `samples/RetryBudget`
   project, guest isolation, offline restore/build/execute, semantic output,
   removal, and unchanged source Git revisions.
6. Akka only retries a correctable compiler/test/semantic failure after
   confirmed MicroVM cleanup. Infrastructure, timeout, unsafe candidate and
   unconfirmed cleanup fail closed. Evidence (worktrees, diffs, logs, JSON
   reports) remains local and external to `main`.

## HaMi11 acceptance before merge

To test the PR **before merging its new contract into main**, do not run
the new task directly against the old main checkout. The reviewed
PowerShell acceptance script uses a **separate local Git mirror** on a
`main` branch at the verified PR commit, while the original main stays
clean. This reproduces the contract/branch ownership boundary without
prematurely merging code.

Create one detached review worktree at the PR branch, then run:

```powershell
$repo = 'I:\git\HaMi\HaMi-AI-Engineering-Agent'
$review = 'G:\HaMi\Sandbox\PoC\Runner-PR54'

git -C $repo fetch origin
if ($LASTEXITCODE -ne 0) { throw 'Git fetch failed.' }

if (-not (Test-Path $review)) {
    git -C $repo worktree add --detach $review origin/feature/issue53-agent-production-retry-loop
    if ($LASTEXITCODE -ne 0) { throw 'Review worktree setup failed.' }
} else {
    git -C $review switch --detach origin/feature/issue53-agent-production-retry-loop
    if ($LASTEXITCODE -ne 0) { throw 'Review worktree update failed.' }
}

& "$review\scripts\acceptance\Invoke-HaMiProductionAgentAcceptance.ps1" `
    -MainRepository $repo `
    -MirrorRepository 'G:\HaMi\Sandbox\PoC\M3b-PR54-VerifiedMain' `
    -ResultsRoot 'G:\HaMi\Sandbox\PoC\Runs'
```

The scripted (zero-model-cost) first attempt produces
`attempt < maxCalls`, which **compiles** but fails the trusted tests
for negative and zero inputs. After the actor passes the bounded guest
diagnostics back as data, the second attempt produces
`attempt > 0 && maxCalls > 0 && attempt < maxCalls`, which must pass
all 268 independent tests. Both guests must be independently verified
as removed by `sbx inspect`.

The acceptance script keeps the local mirror and all candidate worktrees
for inspection. If a previous PR test ended after creating a candidate
but before starting a MicroVM, the next run **fast-forwards the clean
review mirror to the updated reviewed PR commit** and starts a fresh,
separate agent job. It does not overwrite the previous detached candidate,
reuse its failed state, reset, force-update or delete any worktree. A
different origin, modified mirror checkout, unexpected branch, or
non-fast-forward commit causes an immediate safe rejection.

The offline self-test prints `PASS - erwartete Ablehnung` for a deliberately
blocked results path; this expected security check is **not a job failure**.
Any real pre-MicroVM production error now carries a fixed, safe stage code
such as `PRODUCTION_SCOPE_AND_MICROVM` without exposing raw model or
guest diagnostics.

No GitHub writes or live model invocation occur.

## Production execution after merge

Once approved, the tool can run against a clean checkout on `main`:

```powershell
dotnet run --project 'I:\git\HaMi\HaMi-AI-Engineering-Agent\src\HaMi.Agentic.Cli' -c Release -- `
    development --repo 'I:\git\HaMi\HaMi-AI-Engineering-Agent' `
    --agent-run --contract contracts/development/agent-production-retry-budget.json `
    --results-dir 'G:\HaMi\Sandbox\PoC\Runs'
```

Add `--live` for a **separately authorized** and potentially billable
real model invocation. The model has no tools and cannot authorize new
write paths. Production code still runs only in a fresh offline guest.

## M4 publication

The optional, explicitly authorized publication path is described in
[Developer M4: controlled Draft PR publication](developer-publication.md).
The M3b commands above remain secret-free with no publication flags.

## Remaining v1 milestones

This is the first **real production edit** with autonomous, bounded
correction, not general multi-file coding. Next expand supported
production tasks under independent acceptance, then implement
permission-scoped Draft PR delivery (M4), recovery (M5) and broader
end-to-end acceptance (M6). Education remains out of scope.
