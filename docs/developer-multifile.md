# HaMi Developer M1/M2 – reviewed two-file sandbox slice

Status: Draft-PR #50 for Issue #49. This is a bounded staging-fixture integration, **not** production code modification.

## Contracts and ownership

The pinned runtime contract contracts/development/agent-pair.json refers to contracts/development/sandbox-pair.json. The latter uses the existing DevelopmentContract parser and is the authoritative allowlist for exactly two generated C# files; the model and the GitHub Issue cannot change it. The host also pins the .NET 11 fixture directory, target framework, project, test mode, attempt budget and required output. Unknown contract paths or versions fail closed.

The model returns structured JSON containing exactly two named source files. DevelopmentCandidateFiles validates exact path membership, uniqueness, complete contents and size. Only the two staged copies can change. The trusted Program.cs and Demo.csproj remain unchanged; the original fixture and production checkout are never written. Generated code runs exclusively inside the existing offline, credential-free Docker MicroVM.

The two production-shaped paths inside samples/SandboxCodingPair are fixture names only. The real retry-budget-refinement.json production scope has **not** yet been wired into the autonomous runner; that remains the next production-code milestone.

## HaMi11 acceptance

Create a detached review worktree at the branch head. Run this **single offline acceptance**, which performs parser/actor selftests and two real MicroVM attempts (compiler error then correction):

    & "$review\scripts\acceptance\Invoke-HaMiDeveloperAcceptance.ps1" `
        -ExpectedRef 'origin/feature/issue49-development-multifile-actor' `
        -ContractPath 'contracts/development/agent-pair.json' `
        -ResultsRoot 'G:\HaMi\Sandbox\PoC\Runs'

The script also verifies the resulting JSON report, exact stdout, two independent MicroVM cleanup checks with sbx inspect, unchanged Git HEAD/status, both approved C# file paths and the byte-identical trusted Program.cs and Demo.csproj across both attempts. No live model call, API costs, GitHub write, automated merge or host build of candidate code is involved.

The original single-Program.cs contract and its acceptance command remain supported.

## Next milestones

M3: use a reviewed *real* production-code scope, independent tests and a diff suitable for human review. M4: safely create a Draft PR from the accepted diff; M5: durable recovery and job ownership; M6: end-to-end product acceptance. See Roadmap Issue #48. Education and premature shared-agent frameworks remain out of scope.
