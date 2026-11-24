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
    # Preserve the original failing input from CI 45460196255.
    # Its exact revised expectation is justified by the independently
    # reproduced SDK output on Linux/Windows (CI 45463667966), and
    # approved by the project owner in issue #19, comment 7141290857.
    $originalHash = (Get-FileHash -LiteralPath $target).Hash
    $originalBytes = [IO.File]::ReadAllBytes($target)
    $original = [IO.File]::ReadAllText($target)
    $declaration = '    internal static bool HasAnotherAttempt('
    $expression = '        attempt >= 1 && maxCalls >= 1 && attempt < maxCalls;'
    if ([regex]::Matches($original, [regex]::Escape($declaration)).Count -ne 1 -or
        [regex]::Matches($original, [regex]::Escape($expression)).Count -ne 1) {
        throw 'Original fixture shape changed; review the regression contract.'
    }
    $oneSpace = $original.Replace($declaration, ' internal static bool HasAnotherAttempt(')
    $cases = @(
        @{
            Name = 'original-relative-indent-7'
            Input = $oneSpace
            Expected = $original.Replace($expression, '           attempt >= 1 && maxCalls >= 1 && attempt < maxCalls;')
            ExpectedStatus = ' M src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs'
        }
        @{
            Name = 'preserved-relative-indent-4'
            Input = $oneSpace.Replace($expression, '     attempt >= 1 && maxCalls >= 1 && attempt < maxCalls;')
            Expected = $original
            ExpectedStatus = ''
        }
    )
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
    Push-Location -LiteralPath $worktree
    try {
        & dotnet restore $project
        if ($LASTEXITCODE -ne 0) { throw 'Fixture restore failed.' }
        foreach ($case in $cases) {
            Write-Host "=== RetryBudget case $($case.Name) ==="
            [IO.File]::WriteAllText($target, $case.Input, [Text.UTF8Encoding]::new($false))
            $badHash = (Get-FileHash -LiteralPath $target).Hash
            $badBytes = [IO.File]::ReadAllBytes($target)
            $expectedBytes = [Text.UTF8Encoding]::new($false).GetBytes($case.Expected)
            $expectedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($expectedBytes))
            if ($badHash -ceq $originalHash -or $badHash -ceq $expectedHash) {
                throw 'Regression input must differ from both original and expected output.'
            }
            Show-SourceEvidence 'perturbed' $badBytes
            Show-SourceEvidence 'expected' $expectedBytes
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
            # It is deliberately not a substitute for the exact expected-byte assertion.
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
            # Exact expected bytes, not a tolerance or normalization.
            [IO.File]::WriteAllBytes($target, $formattedBytes)
            if ((Get-FileHash -LiteralPath $target).Hash -cne $expectedHash -or
                [IO.File]::ReadAllText($target) -cne $case.Expected) {
                throw "Formatter output differs from approved exact expectation: $($case.Name)."
            }
            & $script -Base $base -Mode Check
            & $script -Base $base -Mode Apply
            if ((Get-FileHash -LiteralPath $target).Hash -cne $expectedHash) {
                throw 'Repeated wrapper formatting changed accepted bytes.'
            }
            # The second case returns to HEAD, so the wrapper scope is empty.
            # Exercise the direct SDK on the explicit file in BOTH cases.
            & dotnet format whitespace $project --no-restore --include src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs
            if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $target).Hash -cne $expectedHash) {
                throw 'Repeated direct formatting changed accepted bytes.'
            }
            if ((Get-FileHash -LiteralPath $control).Hash -cne $controlHash) {
                throw 'Unrelated source changed.'
            }
            $status = @(& git status --porcelain=v1 --untracked-files=all)
            if ($LASTEXITCODE -ne 0) { throw 'Fixture Git status failed.' }
            if ($case.ExpectedStatus -ceq '') {
                if ($status.Count -ne 0 -or $expectedHash -cne $originalHash) {
                    throw 'Preserved-relative-indent case did not exactly restore original checkout.'
                }
            } elseif ($status.Count -ne 1 -or $status[0] -cne $case.ExpectedStatus) {
                throw 'Original regression changed unexpected paths or status.'
            }
            Write-Host "PASS: RetryBudget $($case.Name), exact expected bytes, direct SDK parity and idempotence."
        }
        [IO.File]::WriteAllBytes($target, $originalBytes)
        $status = @(& git status --porcelain=v1 --untracked-files=all)
        if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) {
            throw 'Fixture checkout did not return to clean state after test-only restoration.'
        }
        $denied = $false
        try { & (Join-Path $repo 'scripts/quality/Invoke-HaMiFormat.ps1') -Base $base -Mode Apply }
        catch {
            if ($_.Exception.Message -notlike 'Apply requires a separate linked Git worktree*' -and
                $_.Exception.Message -notlike 'Apply forbidden on protected*') { throw }
            $denied = $true
        }
        if (-not $denied) { throw 'Apply accepted the primary checkout.' }
        Write-Host 'PASS: both RetryBudget regressions, exact source expectations and scope.'
    }
    finally { Pop-Location }
}
finally {
    & git -C $repo worktree remove --force $worktree
    if ($LASTEXITCODE -ne 0) { throw 'Fixture worktree cleanup failed.' }
}
