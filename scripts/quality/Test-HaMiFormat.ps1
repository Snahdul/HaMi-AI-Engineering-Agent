#Requires -Version 7.2
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
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
    # A controlled, newly added compile item rather than a production file with
    # unrelated formatting conventions. It remains in scope after correction.
    $relative = 'src/HaMi.Agentic.Cli/Features/Coding/HaMiFormatterRegressionFixture.cs'
    $target = Join-Path $worktree $relative
    $control = Join-Path $worktree 'src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs'
    $controlHash = (Get-FileHash -LiteralPath $control).Hash
    if (Test-Path -LiteralPath $target) { throw 'Fixture path already exists.' }
    $expected = @'
namespace HaMi.Agentic.Cli.Features.Coding;

internal static class HaMiFormatterRegressionFixture
{
    internal static int Value() => 42;
}
'@ + "`n"
    $bad = $expected.Replace('    internal static int Value()', ' internal static int Value()')
    if ($bad -ceq $expected) { throw 'Fixture whitespace anchor missing.' }
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
        $formattedHash = (Get-FileHash -LiteralPath $target).Hash
        $actual = [IO.File]::ReadAllText($target).Replace("`r`n", "`n")
        if ($formattedHash -ceq $badHash -or $actual -cne $expected.Replace("`r`n", "`n")) {
            throw 'Formatter did not produce the exact controlled source (ignoring platform line endings).'
        }
        & $script -Base $base -Mode Check
        & $script -Base $base -Mode Apply
        if ((Get-FileHash -LiteralPath $target).Hash -cne $formattedHash) {
            throw 'Repeated formatting changed already formatted bytes.'
        }
        if ((Get-FileHash -LiteralPath $control).Hash -cne $controlHash) {
            throw 'Unrelated source changed.'
        }
        $status = @(& git status --porcelain=v1 --untracked-files=all)
        if ($LASTEXITCODE -ne 0 -or $status.Count -ne 1 -or $status[0] -cne "?? $relative") {
            throw 'Fixture checkout contains unexpected changes.'
        }
        Remove-Item -LiteralPath $target
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
