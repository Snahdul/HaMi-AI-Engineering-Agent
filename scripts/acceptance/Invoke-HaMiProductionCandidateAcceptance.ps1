#requires -Version 5.1
<#
.SYNOPSIS
  Issue #51: Verify a real production-source change in a detached Git worktree
  and a fresh offline MicroVM, without modifying main.
#>
[CmdletBinding()]
param(
    [string]$MainRepository = 'I:\git\HaMi\HaMi-AI-Engineering-Agent',
    [string]$CandidateWorktree = 'G:\HaMi\Sandbox\PoC\M3-Production-Candidate',
    [string]$ResultsRoot = 'G:\HaMi\Sandbox\PoC\Runs',
    [string]$ExpectedRef = 'origin/feature/issue51-production-candidate-verifier'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$review = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$main = (Resolve-Path -LiteralPath $MainRepository).Path
if (-not (Get-Command sbx -ErrorAction SilentlyContinue)) { throw 'Docker sbx CLI fehlt.' }
if ((& git -C $main branch --show-current) -ne 'main') {
    throw 'Production source checkout must be on main.'
}
$mainStatus = @(& git -C $main status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $mainStatus.Count -ne 0) {
    throw 'Main checkout is not clean.'
}
$expectedHead = (& git -C $review rev-parse --verify $ExpectedRef)
$actualHead = (& git -C $review rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $actualHead -ne $expectedHead) {
    throw 'Review worktree HEAD differs from the fetched PR branch.'
}
if (Test-Path -LiteralPath $CandidateWorktree) {
    throw 'Candidate worktree already exists. Inspect it before proceeding.'
}
$mainHead = (& git -C $main rev-parse --verify HEAD)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read main HEAD.' }
& git -C $main worktree add --detach $CandidateWorktree $mainHead
if ($LASTEXITCODE -ne 0) { throw 'Could not create detached production candidate.' }

$target = Join-Path $CandidateWorktree 'src\HaMi.Agentic.Cli\Features\Coding\RetryBudget.cs'
$baseline = [IO.File]::ReadAllText($target)
$oldExpression = 'attempt >= 1 && maxCalls >= 1 && attempt < maxCalls'
$newExpression = 'attempt > 0 && maxCalls > 0 && attempt < maxCalls'
if ($baseline.IndexOf($oldExpression, [StringComparison]::Ordinal) -lt 0) {
    throw 'Trusted production baseline expression is missing.'
}
[IO.File]::WriteAllText($target, $baseline.Replace($oldExpression, $newExpression))

Write-Host '=== 1/3 Offline production scope verification ===' -ForegroundColor Cyan
& dotnet run --project (Join-Path $review 'src\HaMi.Agentic.Cli') -c Release -- development --repo $main --worktree-check --contract contracts/development/retry-budget-refinement.json --worktree $CandidateWorktree
if ($LASTEXITCODE -ne 0) { throw 'Detached production worktree preflight failed.' }

Write-Host '=== 2/3 Production code in a fresh offline MicroVM ===' -ForegroundColor Cyan
New-Item -ItemType Directory -Path $ResultsRoot -Force | Out-Null
$before = @(Get-ChildItem -LiteralPath $ResultsRoot -Directory -Filter 'production-check-*' | ForEach-Object FullName)
& dotnet run --project (Join-Path $review 'src\HaMi.Agentic.Cli') -c Release -- development --repo $main --production-verify --contract contracts/development/retry-budget-refinement.json --worktree $CandidateWorktree --results-dir $ResultsRoot
if ($LASTEXITCODE -ne 0) { throw 'Production verification failed. Inspect the local MicroVM report.' }

$newJobs = @(Get-ChildItem -LiteralPath $ResultsRoot -Directory -Filter 'production-check-*' | Where-Object { $before -notcontains $_.FullName })
if ($newJobs.Count -ne 1) { throw 'Expected exactly one new production report.' }
$reportPath = Join-Path $newJobs[0].FullName 'production-report.json'
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json

Write-Host '=== 3/3 Independent evidence and cleanup ===' -ForegroundColor Cyan
if ($report.status -ne 'passed' -or $report.baseCommit -ne $mainHead -or
    $report.restoreExitCode -ne 0 -or $report.buildExitCode -ne 0 -or
    $report.runExitCode -ne 0 -or $report.outputAccepted -ne $true -or
    $report.checkoutUnchanged -ne $true -or $report.cleanupSucceeded -ne $true) {
    throw 'Report does not prove all acceptance gates.'
}
if (-not (Test-Path -LiteralPath $report.diffPath) -or
    -not ([IO.File]::ReadAllText($report.diffPath).Contains('RetryBudget.cs'))) {
    throw 'Auditable approved production diff is missing.'
}
$sbx = Get-Content -LiteralPath $report.sandboxReport -Raw | ConvertFrom-Json
if ($sbx.runId -notmatch '^[a-f0-9]{32}$' -or $sbx.sandboxRemovalSucceeded -ne $true) {
    throw 'MicroVM report lacks a valid cleanup confirmation.'
}
$sandboxName = 'hami-code-' + $sbx.runId.Substring(0, 12)
$inspect = (& sbx inspect $sandboxName 2>&1 | Out-String)
$inspectExit = $LASTEXITCODE
if ($inspectExit -eq 0 -or $inspect -notmatch [regex]::Escape($sandboxName) -or
    $inspect -notmatch '(?is)\bnot\s+found\b') {
    throw 'Independent MicroVM removal was not confirmed.'
}
Write-Host "PASS: $sandboxName independently confirmed removed."
$afterMain = @(& git -C $main status --porcelain=v1 --untracked-files=all)
$afterCandidate = @(& git -C $CandidateWorktree status --porcelain=v1 --untracked-files=all)
if ($afterMain.Count -ne 0 -or $afterCandidate.Count -ne 1 -or
    $afterCandidate[0] -ne ' M src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs' -or
    (& git -C $main rev-parse HEAD) -ne $mainHead) {
    throw 'Main or candidate Git status changed unexpectedly.'
}
Write-Host 'PASS: main clean; detached candidate contains only the reviewed production diff.'
Write-Host 'PASS: independent .NET 11 boundary tests succeeded inside the MicroVM.'
Write-Host ('Base HEAD: ' + $mainHead)
Write-Host ('Review HEAD: ' + $actualHead)
Write-Host ('Local diff: ' + $report.diffPath)
Write-Host ('Full report: ' + $reportPath)
