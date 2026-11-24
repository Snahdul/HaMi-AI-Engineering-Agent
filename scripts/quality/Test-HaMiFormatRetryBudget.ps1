#Requires -Version 7.2
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$worktree = Join-Path ([IO.Path]::GetTempPath()) ('hami-format-retry-budget-' + [Guid]::NewGuid().ToString('N'))
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
    # Original failed regression from run 45460196255. Keep its exact
    # perturbation and strict expectation until the discrepancy is explained.
    $originalHash = (Get-FileHash -LiteralPath $target).Hash
    $originalBytes = [IO.File]::ReadAllBytes($target)
    $original = [IO.File]::ReadAllText($target)
    $bad = $original.Replace('    internal static bool HasAnotherAttempt(', ' internal static bool HasAnotherAttempt(')
    if ($bad -ceq $original) { throw 'Fixture whitespace anchor missing.' }
    [IO.File]::WriteAllText($target, $bad, [Text.UTF8Encoding]::new($false))
    $badHash = (Get-FileHash -LiteralPath $target).Hash
    $badBytes = [IO.File]::ReadAllBytes($target)
    # Only this fixed, public, small C# file is emitted; never arbitrary
    # workspace data, build diagnostics, credentials or environment values.
    function Show-SourceEvidence([string]$Label, [byte[]]$Bytes) {
        if ($Bytes.Length -gt 8192) { throw 'Unexpected fixture size.' }
        Write-Host "=== RetryBudget $Label ==="
        $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes))
        Write-Host "SHA256=$sha; bytes=$($Bytes.Length)"
        Write-Host "BASE64=$([Convert]::ToBase64String($Bytes))"
        $text = [Text.Encoding]::UTF8.GetString($Bytes)
        $lines = $text.Split("`n")
        for ($line = 0; $line -lt $lines.Length; $line++) {
            $visible = $lines[$line].Replace(' ', '<SP>').Replace("`t", '<TAB>').Replace("`r", '<CR>')
            Write-Host ('{0,3}: {1}' -f ($line + 1), $visible)
        }
        Write-Host "=== End RetryBudget $Label ==="
    }
    Show-SourceEvidence 'original' $originalBytes
    Show-SourceEvidence 'perturbed' $badBytes
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
        $formattedBytes = [IO.File]::ReadAllBytes($target)
        $formattedHash = (Get-FileHash -LiteralPath $target).Hash
        Show-SourceEvidence 'formatted-wrapper' $formattedBytes
        Write-Host "OriginalByteMatch=$($originalHash -ceq $formattedHash)"
        Write-Host "OriginalTextMatch=$([IO.File]::ReadAllText($target) -ceq $original)"
        Write-Host '=== Git diff after wrapper ==='
        & git diff --no-color --no-ext-diff --no-textconv HEAD -- src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs
        if ($LASTEXITCODE -ne 0) { throw 'Diagnostic diff failed.' }

        # Independent manufacturer-tool invocation on the exact same input.
        # It is deliberately not a substitute for the original assertion.
        [IO.File]::WriteAllBytes($target, $badBytes)
        if ((Get-FileHash -LiteralPath $target).Hash -cne $badHash) {
            throw 'Direct comparison input does not match the original perturbation.'
        }
        & dotnet format whitespace $project --no-restore --include src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs
        if ($LASTEXITCODE -ne 0) { throw 'Direct Microsoft formatter failed.' }
        $directBytes = [IO.File]::ReadAllBytes($target)
        $directHash = (Get-FileHash -LiteralPath $target).Hash
        Show-SourceEvidence 'formatted-direct' $directBytes
        Write-Host "WrapperDirectByteMatch=$($directHash -ceq $formattedHash)"
        & dotnet format whitespace $project --no-restore --verify-no-changes --include src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs
        if ($LASTEXITCODE -ne 0) { throw 'Direct formatter verification failed.' }
        if ((Get-FileHash -LiteralPath $target).Hash -cne $directHash) {
            throw 'Direct non-mutating verification changed the source.'
        }
        if ($directHash -cne $formattedHash) { throw 'Wrapper and direct formatter differ.' }
        if ((Get-FileHash -LiteralPath $control).Hash -cne $controlHash) {
            throw 'Unrelated source changed.'
        }
        # Restore the measured wrapper result before the original assertion.
        [IO.File]::WriteAllBytes($target, $formattedBytes)
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
        Write-Host 'PASS: original RetryBudget regression, strict source restoration and scope.'
    }
    finally { Pop-Location }
}
finally {
    & git -C $repo worktree remove --force $worktree
    if ($LASTEXITCODE -ne 0) { throw 'Fixture worktree cleanup failed.' }
}
