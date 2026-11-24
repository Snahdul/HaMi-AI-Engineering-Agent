#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$Base)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$head = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Head unavailable.' }
$worktree = Join-Path ([IO.Path]::GetTempPath()) ('hami-format-patch-' + [Guid]::NewGuid().ToString('N'))
& git -C $repo worktree add --detach $worktree $head
if ($LASTEXITCODE -ne 0) { throw 'Formatter worktree creation failed.' }
try {
    Push-Location -LiteralPath $worktree
    try {
        foreach ($project in @(
            'src/HaMi.Agentic.Cli/HaMi.Agentic.Cli.csproj',
            'tests/HaMi.Agentic.JobState.Probe/HaMi.Agentic.JobState.Probe.csproj'
        )) {
            if (Test-Path -LiteralPath $project -PathType Leaf) {
                & dotnet restore $project
                if ($LASTEXITCODE -ne 0) { throw 'Formatter workspace restore failed.' }
            }
        }
        & ./scripts/quality/Invoke-HaMiFormat.ps1 -Base $Base -Mode Apply
        # No commit, index update, credentials or push. The existing failed
        # quality gate stays failed; this is only a reviewable correction.
        $patch = @(& git -c core.quotePath=false diff --no-color --no-ext-diff --no-textconv HEAD --)
        if ($LASTEXITCODE -ne 0) { throw 'Formatter patch failed.' }
        if ($patch.Count -eq 0) {
            Write-Host 'No whitespace correction; inspect analyzer or other gate failures.'
        } elseif (($patch -join "`n").Length -gt 100000) {
            throw 'Formatter correction exceeds review log limit.'
        } else {
            Write-Output "FORMAT PATCH FOR HEAD $head"
            $patch | Write-Output
            Write-Output 'END FORMAT PATCH'
        }
    }
    finally { Pop-Location }
}
finally {
    & git -C $repo worktree remove --force $worktree
    if ($LASTEXITCODE -ne 0) { throw 'Formatter workspace cleanup failed.' }
}
