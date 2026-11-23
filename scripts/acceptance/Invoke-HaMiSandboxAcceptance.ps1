#requires -Version 5.1
<#
Issue #33 / PR #35 - bundled HaMi11 sandbox acceptance.
Run interactively. Does not modify global policy or existing sandboxes.
#>
[CmdletBinding()]
param(
    [string]$ResultsRoot = 'G:\HaMi\Sandbox\PoC\Runs',
    [string]$CandidatesRoot = 'G:\HaMi\Sandbox\Candidates',
    [ValidateRange(2,10)][int]$Iterations = 3
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$cli = Join-Path $repo 'src\HaMi.Agentic.Cli'
$guid = [guid]::NewGuid().ToString('N')
$out = Join-Path $ResultsRoot ("acceptance-" + $guid)
$src = Join-Path $CandidatesRoot ("acceptance-" + $guid)
$canary = Join-Path $out 'host-canary.txt'
$checks = New-Object System.Collections.ArrayList
$start = (Get-Date).ToString('o')
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet fehlt.' }
if (-not (Get-Command sbx -ErrorAction SilentlyContinue)) { throw 'sbx fehlt.' }
New-Item -ItemType Directory -Path $out,$src -Force | Out-Null
[IO.File]::WriteAllText($canary, ('HOST-ONLY-CANARY-' + $guid))
$projectXml = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>'

function New-Snapshot([string]$name,[string]$program) {
    $dir = Join-Path $src $name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $dir 'Demo.csproj'), $projectXml)
    [IO.File]::WriteAllText((Join-Path $dir 'Program.cs'), $program)
    return $dir
}
function Invoke-Case([string]$name,[string]$snapshot) {
    $directory = Join-Path $out $name
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Write-Host ''
    Write-Host ('========== ' + $name + ' ==========') -ForegroundColor Cyan
    $argsForCli = @('run','--project',$cli,'-c','Release','--',
        'development','--repo',$repo,'--sandbox-run','--source',$snapshot,
        '--project','Demo.csproj','--mode','run','--results-dir',$directory)
    & dotnet @argsForCli | Out-Host
    $exitCode = $LASTEXITCODE
    $report = $null
    $path = $null
    $reports = Join-Path $directory 'reports'
    if (Test-Path -LiteralPath $reports) {
        $folders = @(Get-ChildItem -LiteralPath $reports -Directory)
        if ($folders.Count -eq 1) {
            $path = Join-Path $folders[0].FullName 'report.json'
            if (Test-Path -LiteralPath $path) {
                $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            }
        }
    }
    return [pscustomobject]@{ Exit=$exitCode; Report=$report; Path=$path }
}
function Get-TestLog($test,[string]$name) {
    if (-not $test.Path) { return '' }
    $path = Join-Path (Split-Path $test.Path -Parent) $name
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    return (Get-Content -LiteralPath $path -Raw)
}
function Add-Result([string]$name,[bool]$passed,[string]$details) {
    $state = if ($passed) { 'PASS' } else { 'FAIL' }
    [void]$checks.Add([pscustomobject]@{ Test=$name; Status=$state; Details=$details })
    $colour = if ($passed) { 'Green' } else { 'Red' }
    Write-Host ('[{0}] {1}: {2}' -f $state,$name,$details) -ForegroundColor $colour
}

$guestTemplate = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'HostIsolationProbe.cs.txt') -Raw
$driveRoot = [IO.Path]::GetPathRoot($canary)
$drive = $driveRoot.Substring(0,1).ToLowerInvariant()
$relative = $canary.Substring($driveRoot.Length).Replace('\','/')
$pathJson = ConvertTo-Json -InputObject $canary -Compress
$guest = $guestTemplate.Replace('__DRIVE__',$drive).Replace('__RELATIVE__',$relative).Replace('__WINJSON__',$pathJson)
$securitySrc = New-Snapshot 'security' $guest
$goodSrc = New-Snapshot 'normal' 'Console.WriteLine("HaMi .NET 11 acceptance OK");'
$badSrc = New-Snapshot 'compile-fail' 'Console.WriteLine(UnknownIdentifier);'
$blockedSrc = New-Snapshot 'rejected-snapshot' 'Console.WriteLine("must not execute");'
[IO.File]::WriteAllText((Join-Path $blockedSrc '.env'),'DUMMY-TEST-DATA')

$memoryCsv = Join-Path $out 'host-free-memory.csv'
$job = $null
$durations = @()
try {
    $job = Start-Job -ArgumentList $memoryCsv -ScriptBlock {
        param($file)
        while ($true) {
            try {
                $os = Get-CimInstance Win32_OperatingSystem
                ('{0},{1}' -f (Get-Date).ToString('o'),
                    [math]::Round($os.FreePhysicalMemory / 1024.0,0)) | Add-Content -LiteralPath $file
            } catch { }
            Start-Sleep -Milliseconds 750
        }
    }
    $security = Invoke-Case 'security' $securitySrc
    $guestLog = Get-TestLog $security 'execution.log'
    $securityOk = $security.Exit -eq 0 -and $null -ne $security.Report -and
        $security.Report.executionExitCode -eq 0 -and
        $security.Report.sandboxRemovalSucceeded -eq $true -and
        $guestLog.Contains('SECURITY_STATUS=PASS')
    Add-Result 'Host-Canary, Mounts, Secrets und Gastlimits' $securityOk ('CLI Exit={0}; SecurityPass={1}' -f $security.Exit,$guestLog.Contains('SECURITY_STATUS=PASS'))

    $compiler = Invoke-Case 'compile-fail' $badSrc
    $buildLog = Get-TestLog $compiler 'build.log'
    $compilerOk = $compiler.Exit -ne 0 -and $null -ne $compiler.Report -and
        $compiler.Report.restoreExitCode -eq 0 -and
        $compiler.Report.buildExitCode -ne 0 -and
        $compiler.Report.executionExitCode -eq -1 -and
        $compiler.Report.sandboxRemovalSucceeded -eq $true -and
        $buildLog.Contains('CS0103')
    Add-Result 'Compilerfehler CS0103 und automatisches Log' $compilerOk ('CLI Exit={0}; CS0103={1}' -f $compiler.Exit,$buildLog.Contains('CS0103'))

    $reject = Invoke-Case 'rejected-snapshot' $blockedSrc
    $rejectOk = $reject.Exit -ne 0 -and $null -eq $reject.Report
    Add-Result 'Unzulaessiger .env-Snapshot verworfen' $rejectOk ('CLI Exit={0}; Report={1}' -f $reject.Exit,($null -ne $reject.Report))

    for ($i=1; $i -le $Iterations; $i++) {
        $run = Invoke-Case ('perf-{0:D2}' -f $i) $goodSrc
        $ok = $run.Exit -eq 0 -and $null -ne $run.Report -and
            $run.Report.restoreExitCode -eq 0 -and
            $run.Report.buildExitCode -eq 0 -and
            $run.Report.executionExitCode -eq 0 -and
            $run.Report.sandboxRemovalSucceeded -eq $true -and
            $run.Report.timedOut -eq $false
        if ($ok) { $durations += [double]$run.Report.totalMilliseconds }
        $elapsed = if ($run.Report) { $run.Report.totalMilliseconds } else { 'kein Report' }
        Add-Result ('Wiederholung {0}/{1}' -f $i,$Iterations) $ok ('Gesamtzeit={0} ms' -f $elapsed)
    }
}
finally {
    if ($null -ne $job) {
        Stop-Job -Job $job -ErrorAction SilentlyContinue
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    }
}

$performance = $null
if ($durations.Count) {
    $performance = [ordered]@{
        count = $durations.Count
        samplesMs = $durations
        minMs = ($durations | Measure-Object -Minimum).Minimum
        meanMs = [math]::Round(($durations | Measure-Object -Average).Average,0)
        maxMs = ($durations | Measure-Object -Maximum).Maximum
    }
}
$hostRam = $null
if (Test-Path -LiteralPath $memoryCsv) {
    $ramSamples = @(Get-Content -LiteralPath $memoryCsv | ForEach-Object {
        $items = $_ -split ','
        if ($items.Length -eq 2) {
            $v = 0.0
            if ([double]::TryParse($items[1],[Globalization.NumberStyles]::Float,
                [Globalization.CultureInfo]::InvariantCulture,[ref]$v)) { $v }
        }
    })
    if ($ramSamples.Count) {
        $hostRam = [ordered]@{
            note = 'Host-wide free RAM; not VM-specific peak consumption'
            samples = $ramSamples.Count
            minFreeMiB = ($ramSamples | Measure-Object -Minimum).Minimum
            maxFreeMiB = ($ramSamples | Measure-Object -Maximum).Maximum
        }
    }
}
$failCount = @($checks | Where-Object { $_.Status -eq 'FAIL' }).Count
$summary = [ordered]@{
    schemaVersion = 1
    outcome = if ($failCount -eq 0) { 'PASS' } else { 'FAIL' }
    started = $start
    completed = (Get-Date).ToString('o')
    commit = (& git -C $repo rev-parse HEAD)
    tests = @($checks.ToArray())
    performance = $performance
    hostRam = $hostRam
    canary = $canary
    note = 'Prior timeout/Internet/LAN policy tests remain separate evidence in PR #35.'
}
$summaryPath = Join-Path $out 'acceptance-summary.json'
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-Host ''
Write-Host '============= HaMi Sandbox: Gesamtbewertung =============' -ForegroundColor Cyan
$checks | Format-Table Test,Status,Details -AutoSize | Out-Host
if ($performance) {
    Write-Host ('Gesamtlaufzeit min / Mittel / max: {0} / {1} / {2} ms' -f $performance.minMs,$performance.meanMs,$performance.maxMs)
}
if ($hostRam) {
    Write-Host ('Freier Host-RAM (global, keine VM-Zurechnung): {0} .. {1} MiB' -f $hostRam.minFreeMiB,$hostRam.maxFreeMiB)
} else { Write-Warning 'Host-RAM-Messung fehlt; Ressourcennachweis nur teilweise.' }
Write-Host ('Gesamtbericht: ' + $summaryPath)
Write-Host ('Fehlgeschlagene Pruefungen: ' + $failCount)
if ($failCount -gt 0) { throw 'Die Sammelabnahme ist fehlgeschlagen. Siehe Gesamtbericht.' }
Write-Host 'Sammeltests bestanden. Produktiver Einsatz / PR-Merge nur nach gesonderter menschlicher Freigabe.' -ForegroundColor Green
