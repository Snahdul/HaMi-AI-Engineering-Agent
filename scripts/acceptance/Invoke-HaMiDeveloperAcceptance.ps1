#requires -Version 5.1
<#
.SYNOPSIS
  Full local HaMi Developer actor/sandbox and logging acceptance (Issues #36/#38).
.DESCRIPTION
  Performs offline Akka/CommandLine selftests followed by two genuine
  fresh Docker MicroVM attempts. Does not run the model, merge PRs, change
  Docker network policy or delete any existing worktree/sandbox.
#>
[CmdletBinding()]
param(
    [string]$ResultsRoot = 'G:\HaMi\Sandbox\PoC\Runs',
    [string]$ExpectedRef = 'origin/feature/issue38-development-narrative-logging-cleanup',
    [ValidateSet('contracts/development/agent-console-retry.json',
                 'contracts/development/agent-pair.json')]
    [string]$ContractPath = 'contracts/development/agent-console-retry.json',
    [switch]$VerifyOnly,
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $repo 'src\HaMi.Agentic.Cli'
$contract = Get-Content -LiteralPath (Join-Path $repo $ContractPath) -Raw | ConvertFrom-Json
if ($contract.taskId -notin @('sandbox-console-retry', 'sandbox-pair')) {
    throw 'Contract task is not approved.'
}
$head = (& git -C $repo rev-parse HEAD)
if ($LASTEXITCODE -ne 0) { throw 'Git checkout unavailable.' }
$expected = (& git -C $repo rev-parse --verify $ExpectedRef)
if ($LASTEXITCODE -ne 0 -or $head -ne $expected) {
    throw ('Worktree HEAD is not equal to {0}. Fetch and update the review worktree first.' -f $ExpectedRef)
}
if (-not (Get-Command sbx -ErrorAction SilentlyContinue)) { throw 'Docker sbx CLI fehlt.' }
$before = @(& git -C $repo status --porcelain=v1)
if ($LASTEXITCODE -ne 0) { throw 'Cannot capture current Git changes.' }

if ($VerifyOnly) {
    if ([string]::IsNullOrWhiteSpace($ReportPath) -or
        -not [IO.Path]::IsPathFullyQualified($ReportPath) -or
        [IO.Path]::GetFileName($ReportPath) -ne 'agent-report.json') {
        throw '-VerifyOnly requires an absolute path to an existing agent-report.json.'
    }
    $reportPath = (Resolve-Path -LiteralPath $ReportPath).Path
    Write-Host ''
    Write-Host '=== Verify existing agent report; NO new MicroVMs or model calls ===' -ForegroundColor Cyan
} else {
    if ($ReportPath) { throw '-ReportPath can only be supplied with -VerifyOnly.' }
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
        development --repo $repo --agent-run --contract $ContractPath --results-dir $ResultsRoot
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
}

$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json

Write-Host ''
Write-Host '=== 3/3 Verify isolated attempts, exact stdout and unchanged Git checkout ===' -ForegroundColor Cyan
if ($report.status -ne 'passed' -or $report.modelCalls -ne 2 -or
    @($report.attempts).Count -ne 2 -or
    $report.attempts[0].status -ne 'build-failed' -or
    $report.attempts[1].status -ne 'passed' -or
    $report.attempts[1].outputAccepted -ne $true) {
    throw 'Agent result does not prove a failed build followed by successful correction.'
}
$microVmReports = @()
foreach ($entry in @($report.attempts)) {
    if ($entry.cleanupSucceeded -ne $true -or
        -not (Test-Path -LiteralPath $entry.reportPath)) {
        throw ('MicroVM cleanup/report missing on attempt {0}.' -f $entry.number)
    }
    $vm = Get-Content -LiteralPath $entry.reportPath -Raw | ConvertFrom-Json
    if ($vm.runId -notmatch '^[a-f0-9]{32}$' -or
        $vm.sandboxRemovalSucceeded -ne $true -or
        $vm.restoreExitCode -ne 0 -or $vm.timedOut -ne $false) {
        throw ('Invalid or unclean MicroVM report on attempt {0}.' -f $entry.number)
    }
    $microVmReports += $vm
    $sandboxName = 'hami-code-' + $vm.runId.Substring(0, 12)
    $inspect = (& sbx inspect $sandboxName 2>&1 | Out-String)
    $inspectExit = $LASTEXITCODE
    # sbx prints the sandbox name and 'not found' on SEPARATE lines.
    # Nonzero exit + exact requested sandbox name + a not-found diagnosis
    # is strong independent evidence the guest is no longer present.
    if ($inspectExit -eq 0 -or
        $inspect -notmatch [regex]::Escape($sandboxName) -or
        $inspect -notmatch '(?is)\bnot\s+found\b') {
        throw ('Cleanup could not be independently confirmed for {0}: {1}' -f $sandboxName, $inspect.Trim())
    }
    Write-Host ('PASS: ' + $sandboxName + ' was removed (independent sbx inspect).')
}
if ($microVmReports[0].buildExitCode -eq 0 -or
    $microVmReports[0].executionExitCode -ne -1 -or
    $microVmReports[1].buildExitCode -ne 0 -or
    $microVmReports[1].executionExitCode -ne 0) {
    throw 'Expected first failed compiler phase, then clean build+run in second isolated VM.'
}
# Semantic acceptance must be based on the actual guest execution
# report, never merely on a successful exit code or a model claim.
$successReportDir = [IO.Path]::GetDirectoryName($report.attempts[1].reportPath)
$executionLog = Join-Path $successReportDir 'execution.log'
if (-not (Test-Path -LiteralPath $executionLog)) {
    throw 'Successful sandbox attempt is missing execution.log.'
}
$actual = [IO.File]::ReadAllText($executionLog).Replace("`r`n", "`n").Replace("`r", "`n")
if ($actual.EndsWith("`n", [StringComparison]::Ordinal)) {
    $actual = $actual.Substring(0, $actual.Length - 1)
}
if ($actual -cne $contract.expectedStdout) {
    throw 'Semantic acceptance failed: guest stdout differs from the reviewed contract.'
}
Write-Host 'PASS: exact reviewed console output verified from guest execution.log.'

# For the pair case verify immutable harness/project copies and the
# exact two generated target paths, rather than trusting a green model run.
if ($contract.taskId -eq 'sandbox-pair') {
    $fixture = Join-Path $repo 'samples\SandboxCodingPair'
    $jobRoot = [IO.Path]::GetDirectoryName($reportPath)
    $allowed = @(
        'src/HaMi.Agentic.Cli/Features/Development/PairCalculator.cs',
        'src/HaMi.Agentic.Cli/Features/Development/PairFormatter.cs'
    )
    foreach ($attempt in 1..2) {
        $candidate = Join-Path $jobRoot ('candidates\{0:d2}' -f $attempt)
        foreach ($trusted in @('Program.cs', 'Demo.csproj')) {
            $sourceHash = (Get-FileHash -LiteralPath (Join-Path $fixture $trusted) -Algorithm SHA256).Hash
            $actualHash = (Get-FileHash -LiteralPath (Join-Path $candidate $trusted) -Algorithm SHA256).Hash
            if ($sourceHash -ne $actualHash) {
                throw ('Immutable trusted harness changed: {0}, attempt {1}' -f $trusted, $attempt)
            }
        }
        $actualFiles = @(
            Get-ChildItem -LiteralPath $candidate -File -Recurse |
                ForEach-Object {
                    $_.FullName.Substring($candidate.Length + 1).Replace('\', '/')
                }
        )
        $expectedFiles = @('Demo.csproj', 'Program.cs') + $allowed
        if (@(Compare-Object $actualFiles $expectedFiles).Count -ne 0) {
            throw ('Candidate manifest differs from approved pair on attempt {0}' -f $attempt)
        }
    }
    Write-Host 'PASS: exactly two allowlisted C# files and immutable harness verified for both attempts.'
}

$after = @(& git -C $repo status --porcelain=v1)
if ($LASTEXITCODE -ne 0) { throw 'Could not read Git checkout status.' }
if ($VerifyOnly) {
    # The original Git baseline was not saved with the previous job report.
    # We can inspect CURRENT tracked files, but cannot retrospectively
    # prove unchanged pre-existing untracked files.
    & git -C $repo diff --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Current tracked working files contain changes.' }
    & git -C $repo diff --cached --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Current Git index contains staged changes.' }
    Write-Host 'Current tracked Git files are clean; original untracked-file baseline not independently available.'
} elseif (Compare-Object $before $after) {
    throw 'The source Git checkout changed during the agent run.'
}
$lastHead = (& git -C $repo rev-parse HEAD)
if ($lastHead -ne $head) {
    throw 'The source Git HEAD changed during the agent run.'
}
if ($VerifyOnly) {
    Write-Host 'PASS: Stored report, semantic stdout, compiler fail/correct and 2 independently removed MicroVMs.' -ForegroundColor Green
} else {
    Write-Host 'PASS: CLI/Akka, exact semantic stdout, compiler correction, 2 MicroVM cleanups, unchanged Git.' -ForegroundColor Green
}
Write-Host ('Generator invocations (scripted offline, no API charges): ' + $report.modelCalls)
Write-Host ('Current HEAD: ' + $head)
Write-Host ('Full acceptance report: ' + $reportPath)
