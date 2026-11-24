#requires -Version 5.1
<#
.SYNOPSIS
  M3b: agent-generated production change, correction, independent MicroVM tests.
.DESCRIPTION
  Run the reviewed PR tool against an isolated local Git mirror at the exact
  PR commit. The original main checkout is never altered or used for model code.
  No live model calls, GitHub writes, automatic commits or deletions.
#>
[CmdletBinding()]
param(
    [string]$MainRepository = 'I:\git\HaMi\HaMi-AI-Engineering-Agent',
    [string]$MirrorRepository = 'G:\HaMi\Sandbox\PoC\M3b-PR54-VerifiedMain',
    [string]$ResultsRoot = 'G:\HaMi\Sandbox\PoC\Runs',
    [string]$ExpectedRef = 'origin/feature/issue53-agent-production-retry-loop'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$review = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$main = (Resolve-Path -LiteralPath $MainRepository).Path
if (-not (Get-Command sbx -ErrorAction SilentlyContinue)) { throw 'Docker sbx CLI fehlt.' }

$reviewHead = (& git -C $review rev-parse --verify HEAD)
$expectedHead = (& git -C $review rev-parse --verify $ExpectedRef)
if ($LASTEXITCODE -ne 0 -or $reviewHead -ne $expectedHead) {
    throw 'Review HEAD does not match the fetched M3b branch.'
}
if ((& git -C $main branch --show-current) -ne 'main') {
    throw 'Original source checkout must remain on main.'
}
$originalHead = (& git -C $main rev-parse HEAD)
$originalStatus = @(& git -C $main status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $originalStatus.Count -ne 0) {
    throw 'Original main must be clean.'
}

if (Test-Path -LiteralPath $MirrorRepository) {
    Write-Host 'Checking existing reviewed mirror (no overwrite).' -ForegroundColor Cyan
} else {
    & git clone --local --no-hardlinks --branch main $main $MirrorRepository
    if ($LASTEXITCODE -ne 0) { throw 'Could not create independent local Git mirror.' }
    & git -C $MirrorRepository remote set-url origin https://github.com/Snahdul/HaMi-AI-Engineering-Agent.git
    if ($LASTEXITCODE -ne 0) { throw 'Could not pin canonical Git origin.' }
}
$mirrorHead = (& git -C $MirrorRepository rev-parse HEAD)
$mirrorBranch = (& git -C $MirrorRepository branch --show-current)
$mirrorStatus = @(& git -C $MirrorRepository status --porcelain=v1 --untracked-files=all)
$mirrorOrigin = (& git -C $MirrorRepository remote get-url origin)
if ($LASTEXITCODE -ne 0 -or
    $mirrorBranch -ne 'main' -or $mirrorStatus.Count -ne 0 -or
    $mirrorOrigin -ne 'https://github.com/Snahdul/HaMi-AI-Engineering-Agent.git') {
    throw 'Reviewed mirror has an unexpected branch, dirty checkout or Git origin.'
}
# An interrupted prior acceptance may have left detached candidate worktrees.
# A clean mirror may fast-forward to the new reviewed PR head without touching
# those old candidates, their Git metadata, or any previously saved reports.
if ($mirrorHead -ne $reviewHead) {
    & git -C $MirrorRepository fetch --no-tags $review HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not fetch reviewed HEAD into mirror.' }
    & git -C $MirrorRepository merge-base --is-ancestor $mirrorHead FETCH_HEAD
    if ($LASTEXITCODE -ne 0) {
        throw 'Existing mirror cannot be fast-forwarded to the reviewed PR head.'
    }
    & git -C $MirrorRepository merge --ff-only FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Safe mirror fast-forward failed.' }
    Write-Host 'PASS - reviewed mirror updated by fast-forward; previous candidates preserved.' -ForegroundColor Cyan
}
$mirrorHead = (& git -C $MirrorRepository rev-parse HEAD)
$mirrorStatus = @(& git -C $MirrorRepository status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $mirrorHead -ne $reviewHead -or $mirrorStatus.Count -ne 0) {
    throw 'Reviewed mirror does not match the exact PR commit.'
}

Write-Host '=== 1/3 Offline parser, contract and Akka self-tests ===' -ForegroundColor Cyan
$project = Join-Path $review 'src\HaMi.Agentic.Cli'
& dotnet run --project $project -c Release -- development --repo $MirrorRepository --agent-self-test
if ($LASTEXITCODE -ne 0) { throw 'Offline production self-test failed.' }

Write-Host '=== 2/3 Autonomous production correction in two fresh MicroVMs ===' -ForegroundColor Cyan
New-Item -ItemType Directory -Path $ResultsRoot -Force | Out-Null
$existing = @(Get-ChildItem -LiteralPath $ResultsRoot -Directory -Filter 'agent-job-*' | ForEach-Object FullName)
& dotnet run --project $project -c Release -- development --repo $MirrorRepository --agent-run --contract contracts/development/agent-production-retry-budget.json --results-dir $ResultsRoot
if ($LASTEXITCODE -ne 0) { throw 'Production correction failed; inspect the local agent report.' }
$newJobs = @(Get-ChildItem -LiteralPath $ResultsRoot -Directory -Filter 'agent-job-*' | Where-Object { $existing -notcontains $_.FullName })
if ($newJobs.Count -ne 1) { throw 'Expected exactly one new agent report.' }
$reportPath = Join-Path $newJobs[0].FullName 'agent-report.json'
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json

Write-Host '=== 3/3 Independent MicroVM, worktree and Git-diff evidence ===' -ForegroundColor Cyan
if ($report.status -ne 'passed' -or $report.modelCalls -ne 2 -or
    @($report.attempts).Count -ne 2 -or
    $report.attempts[0].status -ne 'run-failed' -or
    $report.attempts[1].status -ne 'passed' -or
    $report.attempts[1].outputAccepted -ne $true) {
    throw 'Semantic failure and correction were not proven.'
}

$seenSandboxes = @()
foreach ($entry in @($report.attempts)) {
    if (-not $entry.cleanupSucceeded -or
        -not (Test-Path -LiteralPath $entry.reportPath) -or
        -not (Test-Path -LiteralPath $entry.candidateWorktree) -or
        -not (Test-Path -LiteralPath $entry.diffPath)) {
        throw ('Attempt {0} has missing evidence.' -f $entry.number)
    }
    $vm = Get-Content -LiteralPath $entry.reportPath -Raw | ConvertFrom-Json
    if ($vm.runId -notmatch '^[a-f0-9]{32}$' -or
        $vm.sandboxRemovalSucceeded -ne $true -or
        $vm.restoreExitCode -ne 0 -or
        $vm.buildExitCode -ne 0 -or
        $vm.timedOut -ne $false) {
        throw ('Attempt {0} failed MicroVM report checks.' -f $entry.number)
    }
    $sandboxName = 'hami-code-' + $vm.runId.Substring(0, 12)
    if ($seenSandboxes -contains $sandboxName) { throw 'MicroVM reused.' }
    $seenSandboxes += $sandboxName
    $inspect = (& sbx inspect $sandboxName 2>&1 | Out-String)
    $inspectExit = $LASTEXITCODE
    if ($inspectExit -eq 0 -or
        $inspect -notmatch [regex]::Escape($sandboxName) -or
        $inspect -notmatch '(?is)\bnot\s+found\b') {
        throw ('MicroVM cleanup not independently verified: {0}' -f $sandboxName)
    }
    $changed = @(& git -C $entry.candidateWorktree status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $changed.Count -ne 1 -or
        $changed[0] -cne ' M src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs' -or
        (& git -C $entry.candidateWorktree rev-parse HEAD) -ne $mirrorHead) {
        throw ('Candidate worktree not properly isolated: attempt {0}' -f $entry.number)
    }
    $patch = [IO.File]::ReadAllText($entry.diffPath)
    if ($patch -notmatch 'RetryBudget.cs' -or $patch -match 'CodingTaskDefinition.cs') {
        throw ('Candidate diff is outside the reviewed scope: attempt {0}' -f $entry.number)
    }
    Write-Host ('PASS: attempt {0}, removed VM {1}, approved production diff' -f $entry.number, $sandboxName)
}
if ($report.attempts[0].runExitCode -eq 0 -or
    $report.attempts[1].runExitCode -ne 0 -or
    $seenSandboxes.Count -ne 2) {
    throw 'Expected one failing independent test followed by a successful repair.'
}
$afterMain = @(& git -C $main status --porcelain=v1 --untracked-files=all)
$afterMirror = @(& git -C $MirrorRepository status --porcelain=v1 --untracked-files=all)
if ((& git -C $main rev-parse HEAD) -ne $originalHead -or
    $afterMain.Count -ne 0 -or
    (& git -C $MirrorRepository rev-parse HEAD) -ne $mirrorHead -or
    $afterMirror.Count -ne 0) {
    throw 'Original main or reviewed mirror changed.'
}
Write-Host 'PASS: original main and reviewed mirror unchanged.'
Write-Host ('Review HEAD: ' + $reviewHead)
Write-Host ('Report: ' + $reportPath)
Write-Host ('Accepted candidate: ' + $report.attempts[1].candidateWorktree)
Write-Host ('Accepted diff: ' + $report.attempts[1].diffPath)
