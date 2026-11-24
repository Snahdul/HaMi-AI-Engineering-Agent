#Requires -Version 7.2
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$worktree = Join-Path ([IO.Path]::GetTempPath()) ('hami-format-test-' + [Guid]::NewGuid().ToString('N'))
$base = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Fixture base unavailable.' }
& git -C $repo worktree add --detach $worktree $base
if ($LASTEXITCODE -ne 0) { throw 'Fixture worktree creation failed.' }
try {
    $project = Join-Path $worktree 'src/HaMi.Agentic.Cli/HaMi.Agentic.Cli.csproj'
    $script = Join-Path $worktree 'scripts/quality/Invoke-HaMiFormat.ps1'
    $target = Join-Path $worktree 'src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs'
    $control = Join-Path $worktree 'src/HaMi.Agentic.Cli/Features/Coding/CodingTaskDefinition.cs'
    $controlHash = (Get-FileHash -LiteralPath $control).Hash
    $original = [IO.File]::ReadAllText($target)
    $bad = $original.Replace('    internal static bool HasAnotherAttempt(', ' internal static bool HasAnotherAttempt(')
    if ($bad -ceq $original) { throw 'Fixture whitespace anchor missing.' }
    [IO.File]::WriteAllText($target, $bad, [Text.UTF8Encoding]::new($false))
    $badHash = (Get-FileHash -LiteralPath $target).Hash
    Push-Location -LiteralPath $worktree
    try {
        & dotnet restore $project
        if ($LASTEXITCODE -ne 0) { throw 'Fixture restore failed.' }
        $denied = $false
        try { & $script -Base $base -Mode Check }
        catch {
            if ($_.Exception.Message -notlike 'Microsoft formatter failed for*') { throw }
            $denied = $true
        }
        if (-not $denied -or (Get-FileHash -LiteralPath $target).Hash -cne $badHash) {
            throw 'Check did not reject bad formatting without mutation.'
        }
        & $script -Base $base -Mode Apply
        if ((Get-FileHash -LiteralPath $target).Hash -ceq $badHash -or
            [IO.File]::ReadAllText($target) -cne $original) {
            throw 'Formatter did not restore the controlled source.'
        }
        & $script -Base $base -Mode Check
        if ((Get-FileHash -LiteralPath $control).Hash -cne $controlHash) {
            throw 'Unrelated source changed.'
        }
        $status = @(& git status --porcelain=v1 --untracked-files=all)
        if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) {
            throw 'Fixture checkout did not return to clean state.'
        }
        $denied = $false
        try { & (Join-Path $repo 'scripts/quality/Invoke-HaMiFormat.ps1') -Base $base -Mode Apply }
        catch {
            if ($_.Exception.Message -notlike 'Apply requires a separate linked Git worktree*' -and
                $_.Exception.Message -notlike 'Apply forbidden on protected*') { throw }
            $denied = $true
        }
        if (-not $denied) { throw 'Apply accepted the primary checkout.' }
        Write-Host 'PASS: Microsoft formatter rejects, corrects and verifies scoped source; primary checkout protected.'
    }
    finally { Pop-Location }
}
finally {
    & git -C $repo worktree remove --force $worktree
    if ($LASTEXITCODE -ne 0) { throw 'Fixture worktree cleanup failed.' }
}
