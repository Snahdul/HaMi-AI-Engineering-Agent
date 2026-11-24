# Developer M3a: independent verification of real production changes

This step connects two already-tested components: the detached Git
worktree guard and the offline Docker MicroVM runner. It does NOT generate
production code with an AI model yet. That is the subsequent M3b step.

## Scope and authority

- The authoritative versioned scope is
  `contracts/development/retry-budget-refinement.json`, read through
  `DevelopmentWorktreeGuard.CheckAsync` from the trusted main Git object.
- The first production verification is intentionally narrower than the
  two-file contract: it accepts **only** a modified, unstaged, tracked
  `src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs`.
  `CodingTaskDefinition.cs` is forbidden for this particular execution
  because the existing independent harness does not cover that class.
- The source change may alter only the existing expression body of
  `RetryBudget.HasAnotherAttempt`; the existing
  `CodingTaskDefinition.RetryBudget.ReplaceExpression` is reused to
  prove the rest of the production file remains byte-for-byte unchanged.
  The expression uses only approved identifiers and operators.
- The main checkout must be clean; the candidate must be detached at
  exactly the main commit and contain no unrelated edits or staged files.
- The trusted test harness `samples/RetryBudget/Program.cs` and project
  `samples/RetryBudget/RetryBudget.csproj` are copied from **main**, not
  from the potentially untrusted candidate. The only candidate-sourced
  input is the approved production `RetryBudget.cs` file.
- The three-file snapshot is built and run **only in the sandbox**. The
  accepted output is exactly `RetryBudget: 268 tests, 0 failures.`
  with at most one terminating newline, subject to normal CRLF/LF
  normalization; return code zero alone is insufficient.
- All evidence remains in the external results directory:
  `production-report.json`, `candidate.patch`, sandbox report, and
  the trusted snapshot. No GitHub token or model API call is necessary.

## HaMi11 acceptance

1. Update the existing main checkout using `git pull --ff-only`.
2. Create a separate detached **review** worktree at this PR branch.
3. In the review worktree, execute:

```powershell
& "$review\scripts\acceptance\Invoke-HaMiProductionCandidateAcceptance.ps1" `
    -MainRepository 'I:\git\HaMi\HaMi-AI-Engineering-Agent' `
    -CandidateWorktree 'G:\HaMi\Sandbox\PoC\M3-Production-Candidate' `
    -ResultsRoot 'G:\HaMi\Sandbox\PoC\Runs'
```

The acceptance script creates an additional detached **candidate**
worktree based on current main, changes only the production expression
to a mathematically equivalent form, and runs the reviewed
`development --production-verify` CLI action. It then independently
checks the MicroVM removal, immutable original main, production patch,
and structured report. The candidate worktree is deliberately retained
for human inspection; the script refuses to overwrite an existing one.

## Security boundaries and next step

The current actor-based multi-file generation and correction are
unchanged. M3a is a real production-file **review gate**, not yet an
autonomous model-driven production edit. M3b will connect the bounded
model/Akka generator to this same verified production-candidate path
without weakening the original constraints. No automatic branch push,
Draft PR, or merge is provided in M3a.
