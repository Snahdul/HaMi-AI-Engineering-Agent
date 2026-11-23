#requires -Version 5.1
<#
.SYNOPSIS
  Full local HaMi Developer actor/sandbox acceptance (Issue #36).
.DESCRIPTION
  Performs offline Akka/CommandLine selftests followed by two genuine
  fresh Docker MicroVM attempts. Does not run the model, merge PRs, change
  Docker network policy or delete any existing worktree/sandbox.
#>
[CmdletBinding()]
param(
    [string]$ResultsRoot = 'G:\HaMi\Sandbox\PoC\Runs'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $repo 'src\HaMi.Agentic.Cli'
$head = (& git -C $repo rev-parse HEAD)
if ($LASTEXITCODE -ne 0) { throw 'Git checkout unavailable.' }
$before = @(& git -C $repo status --porcelain=v1)
if ($LASTEXITCODE -ne 0) { throw 'Cannot capture current Git changes.' }

Write-Host ''
Write-Host '=== 1/3 Parser, ActorSystem, retry and timeout selftests (offline) ===' -ForegroundColor Cyan
& dotnet run --project $project -c Release -- development --repo $repo --agent-self-test
if ($LASTEXITCODE -ne 0) { throw 'Offline CLI/Actor self-test failed.' }

Write-Host ''
Write-Host '=== 2/3 Real MicroVM attempt 1 + automatic correction/attempt 2 ===' -ForegroundColor Cyan
New-Item -ItemType Directory -Path $ResultsRoot -Force | Out-Null
$existing = @(
    Get-ChildItem -LiteralPath $ResultsRoot -Directory -Filter 'agent-job-*' |
        ForEach-Object FullName
)

& dotnet run --project $project -c Release -- `
    development --repo $repo --agent-run --results-dir $ResultsRoot
if ($LASTEXITCODE -ne 0) {
    throw 'The real MicroVM correction loop failed; inspect its agent-job report.'
}

$newJobs = @(Get-ChildItem -LiteralPath $ResultsRoot -Directory -Filter 'agent-job-*' |
    Where-Object { $existing -notcontains $_.FullName })
if ($newJobs.Count -ne 1) {
    throw ('Expected one new agent job directory; got {0}.' -f $newJobs.Count)
}

$reportPath = Join-Path $newJobs[0].FullName 'agent-report.json'
if (-not (Test-Path -LiteralPath $reportPath)) {
    throw 'Agent did not create the structured completion report.'
}
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json

Write-Host ''
Write-Host '=== 3/3 Verify two isolated attempts and unchanged Git checkout ===' -ForegroundColor Cyan
if ($report.status -ne 'passed' -or $report.modelCalls -ne 2 -or
    @($report.attempts).Count -ne 2 -or
    $report.attempts[0].status -ne 'build-failed' -or
    $report.attempts[1].status -ne 'passed') {
    throw 'Agent result does not prove a failed build followed by successful correction.'
}
foreach ($entry in @($report.attempts)) {
    if ($entry.cleanupSucceeded -ne $true -or
        -not (Test-Path -LiteralPath $entry.reportPath)) {
        throw ('MicroVM cleanup/report missing on attempt {0}.' -f $entry.number)
    }
}
$after = @(& git -C $repo status --porcelain=v1)
if ($LASTEXITCODE -ne 0 -or (Compare-Object $before $after)) {
    throw 'The source Git checkout changed during the agent run.'
}
$lastHead = (& git -C $repo rev-parse HEAD)
if ($lastHead -ne $head) {
    throw 'The source Git HEAD changed during the agent run.'
}
Write-Host 'PASS: CLI/parser + Akka host, compiler fail/correct, 2 MicroVM cleanups, unchanged Git.' -ForegroundColor Green
Write-Host ('Model calls (scripted, no API charges): ' + $report.modelCalls)
Write-Host ('Current HEAD: ' + $head)
Write-Host ('Full acceptance report: ' + $reportPath)
