# Developer M4: controlled Draft PR publication

Status: implementation in review (#55 / PR #56). This document describes the intended operating procedure, not a claim of completed real publication acceptance. [ADR-0003](decisions/0003-security-architecture-and-credential-isolation.md) defines the security boundaries. [M3b](developer-production-agent.md) remains the reference for isolated production generation.

## Authorization and credentials

- The existing reviewed production contract permits only the RetryBudget expression. The committed `contracts/development/production-publication.json` additionally constrains the repository, task, path and target branch. Explicit operator authorization binds the actual issue and exact base SHA; neither the model nor issue text can grant write privileges.
- Publication requires `--publish-draft`, `--publication-base <full SHA>`, the production contract, and an owner-authored open issue with `workflow:in-progress` and the matching contract marker (`HaMi-Agent-Task: production-retry-budget`). The issue journal is separately enabled with `--publish-issue`. No implicit publication.
- Use a dedicated fine-grained GitHub PAT, restricted to this repository: Contents read/write, Pull requests read/write, Metadata read (automatic). Do not grant administration, workflow or merge-bypass privileges. These API permissions are broader than one branch; server-side rulesets and host enforcement remain essential. The existing Issues credential is unchanged.
- Windows: create a Generic Credential in the signed-in user's Windows Credential Manager, target `HaMi-AI-Engineering-Agent/GitHub-Publication/Snahdul`, user `Snahdul`, password = token. Never provide a secret value in chat, Git, CLI arguments or logs.
- Cross-platform: a trusted host may inject the temporary process variable `HAMI_AGENT_GITHUB_PUBLICATION_TOKEN`; this takes precedence over the Windows store. No fallback to personal gh credentials. Do not persist the variable or include it in environment dumps. A GitHub Actions secret may be explicitly injected only into a trusted step on a reviewed baseline, never an untrusted PR workflow. No new privileged workflow is introduced here.
- The publisher resolves the token only after candidate validation. Inherited environment injection is not OS-level credential isolation; sandbox children receive an explicit allowlist and the guest remains offline without credentials. See ADR-0003 for limitations.

## Before merge: secret-free real MicroVM acceptance

The existing acceptance script supports an explicit review branch. Use a dedicated M4 mirror and external result root; never reuse the M3b mirror. On HaMi11, choose existing local paths for the variables below. Start from a clean main checkout, fetch the exact reviewed feature-branch HEAD, and run in a separate detached review worktree:

```powershell
$repo = '<main checkout>'
$review = '<external review worktree>'
$mirror = '<external dedicated M4 mirror>'
$results = '<external results root>'
$ref = 'origin/feature/issue55-verified-draft-publication'

git -C $repo fetch origin
if ($LASTEXITCODE -ne 0) { throw 'Fetch failed' }
# New worktree only; if already present, verify it is clean before switching.
git -C $repo worktree add --detach $review $ref
if ($LASTEXITCODE -ne 0) { throw 'Review worktree setup failed' }

& "$review\scripts\acceptance\Invoke-HaMiProductionAgentAcceptance.ps1" `
    -MainRepository $repo -MirrorRepository $mirror -ResultsRoot $results `
    -ExpectedRef $ref
```

No publication flags, real credentials or paid model calls are required. Confirm the two independent MicroVM removals, semantic correction and accepted output, unchanged checkouts and production evidence. Verify the effective sbx security configuration (no secret injection/credential proxy, no mounts, no MCP, network denied), not only empty environment variables. Record evidence without dumping secret values or private paths. CI and this test must be successful before explicit merge approval.

## After the approved merge: separate live publication pilot

Do not run this procedure until the operator separately approves the specific pilot issue, exact merged remote base and GitHub write scope. Do not use the PR mirror as remote main. Use a clean, byte-identical checkout of the reviewed main commit. Git checkout/clean filters (including autocrlf) can transform source bytes even with a clean status. The gate rejects such transformations rather than publish a different diff; use a fresh reviewed clone with `core.autocrlf=false` if needed, and do not reconfigure or reset the production checkout automatically.

Example invocation (placeholders must be explicitly supplied, not inferred):

```powershell
dotnet run --project "$repo\src\HaMi.Agentic.Cli" -c Release -- `
    development --repo $repo --agent-run `
    --contract contracts/development/agent-production-retry-budget.json `
    --results-dir $results --issue <approved-pilot-number> `
    --publish-draft --publication-base <approved-full-main-sha>
```

Add `--publish-issue` only if the journal write is also authorized. The default generator is scripted; no live model call is required for this pilot. The publisher uploads exact accepted bytes, verifies Git objects and creates only `hami-agent/job-<job-id>` and a draft PR against main. No local commit, force-push, main update or merge. Verify the actual PR diff, base/head SHA, job marker, redacted report, optional issue journal and unchanged remote main. Human review and merge are separate.

## Failure and repeated delivery

A local job result is recorded before publication. Publication failure is not evidence that no remote mutation occurred: the server may have created an object, branch or PR before an interrupted response. The gateway checks existing job branches and PR identity on repeated calls with the same job. GitHub operations and local reports are not one atomic transaction. The CLI currently creates a new job ID on every invocation; do not rerun it blindly to recover a partial publication. Reconcile the existing job ID, branch, PR and journal with the operator. Durable recovery/reentry belongs to M5. Any conflicting branch, PR, base, credential or evidence must stop the operation. Missing publication credentials are deliberately detected late, after the successful local gate.

The offline agent self-test exercises the local gate and fake GitHub transport. CI uses no publication secret. Passing fake tests does not establish real GitHub permissions, actual microVM isolation or production readiness. M4 remains open until the separately approved live pilot succeeds.
