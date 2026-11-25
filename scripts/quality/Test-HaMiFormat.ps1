#Requires -Version 7.2
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$worktree = Join-Path ([IO.Path]::GetTempPath()) ('hami-format-test-' + [Guid]::NewGuid().ToString('N'))
$base = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Fixture base unavailable.' }

# Apply must reject the primary checkout even before selecting source files.
$denied = $false
try { & (Join-Path $repo 'scripts/quality/Invoke-HaMiFormat.ps1') -Base $base -Mode Apply }
catch {
    if ($_.Exception.Message -notlike 'Apply requires a separate linked Git worktree*' -and
        $_.Exception.Message -notlike 'Apply forbidden on protected*') { throw }
    $denied = $true
}
if (-not $denied) { throw 'Apply accepted the primary checkout.' }

& git -C $repo worktree add --detach $worktree $base
if ($LASTEXITCODE -ne 0) { throw 'Fixture worktree creation failed.' }
try {
    Push-Location -LiteralPath $worktree
    try {
        $probeRelative = 'tests/HaMi.Agentic.JobState.Probe/Program.cs'
        $probe = Join-Path $worktree $probeRelative
        $relative = 'src/HaMi.Agentic.Cli/Features/Coding/RetryBudget.cs'
        $target = Join-Path $worktree $relative
        $control = Join-Path $worktree 'src/HaMi.Agentic.Cli/Features/Coding/CodingTaskDefinition.cs'
        $controlHash = (Get-FileHash -LiteralPath $control).Hash

        # A test-project-only change must hit the same non-mutating gate.
        $probeOriginal = [IO.File]::ReadAllText($probe)
        $probeBad = $probeOriginal.Replace('    private const int Issue = 62;', ' private const int Issue = 62;')
        if ($probeBad -ceq $probeOriginal) { throw 'Probe regression anchor missing.' }
        [IO.File]::WriteAllText($probe, $probeBad, [Text.UTF8Encoding]::new($false))
        $probeBadHash = (Get-FileHash -LiteralPath $probe).Hash
        $denied = $false
        try { $null = & ./scripts/quality/Invoke-HaMiFormat.ps1 -Base $base -Mode Check -Restore 2>&1 }
        catch {
            if ($_.Exception.Message -notlike 'Microsoft formatter failed for tests/HaMi.Agentic.JobState.Probe/*') { throw }
            $denied = $true
        }
        if (-not $denied -or (Get-FileHash -LiteralPath $probe).Hash -cne $probeBadHash) {
            throw 'Check did not reject bad test-project formatting without mutation.'
        }
        Write-Host 'PASS: test-project-only formatting is covered.'

        # Preserve the original failed candidate from CI 45460196255.
        # Manufacturer indentation behavior was investigated and approved
        # separately. Here only HaMi's integration contract is tested.
        $original = [IO.File]::ReadAllText($target)
        $bad = $original.Replace('    internal static bool HasAnotherAttempt(', ' internal static bool HasAnotherAttempt(')
        if ($bad -ceq $original) { throw 'Original regression anchor missing.' }
        [IO.File]::WriteAllText($target, $bad, [Text.UTF8Encoding]::new($false))
        $badHash = (Get-FileHash -LiteralPath $target).Hash

        # Expected negative check. Capture tool output in this test only so
        # GitHub does not annotate the deliberate WHITESPACE diagnostics as
        # failed production checks. Real failures still propagate.
        $denied = $false
        try { $null = & ./scripts/quality/Invoke-HaMiFormat.ps1 -Base $base -Mode Check -Restore 2>&1 }
        catch {
            if ($_.Exception.Message -notlike 'Microsoft formatter failed for*') { throw }
            $denied = $true
        }
        if (-not $denied -or (Get-FileHash -LiteralPath $target).Hash -cne $badHash) {
            throw 'Check did not reject bad formatting without mutation.'
        }
        Write-Host 'PASS: expected formatter rejection without mutation.'

        # Commit the bad candidate only in this disposable detached worktree.
        # Exercise Apply through the real CI patch helper: it calls the
        # Microsoft formatter and its non-mutating verification exactly once.
        & git add -- $relative $probeRelative
        if ($LASTEXITCODE -ne 0) { throw 'Fixture index setup failed.' }
        & git -c 'user.name=HaMi Formatter Test' -c 'user.email=formatter-test@example.invalid' -c commit.gpgsign=false commit --no-verify -m 'Temporary formatter regression fixture'
        if ($LASTEXITCODE -ne 0) { throw 'Fixture commit failed.' }
        $fixtureHead = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or $fixtureHead -ceq $base) { throw 'Fixture commit identity missing.' }
        $beforePatch = (Get-FileHash -LiteralPath $target).Hash
        $worktreesBefore = (@(& git worktree list --porcelain) -join "`n")
        if ($LASTEXITCODE -ne 0) { throw 'Fixture worktree inventory failed.' }
        $patchOutput = @(& ./scripts/quality/Show-HaMiFormatPatch.ps1 -Base $base)
        $start = [Array]::IndexOf($patchOutput, "FORMAT PATCH FOR HEAD $fixtureHead")
        $end = [Array]::IndexOf($patchOutput, 'END FORMAT PATCH')
        if ($start -lt 0 -or $end -le ($start + 1)) {
            throw 'Expected head-correlated formatter correction patch missing.'
        }
        $worktreesAfter = (@(& git worktree list --porcelain) -join "`n")
        if ($LASTEXITCODE -ne 0 -or $worktreesAfter -cne $worktreesBefore) { throw 'Patch worktree cleanup failed.' }
        $status = @(& git status --porcelain=v1 --untracked-files=all)
        if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0 -or
            (Get-FileHash -LiteralPath $probe).Hash -cne $probeBadHash -or
            (Get-FileHash -LiteralPath $target).Hash -cne $beforePatch -or
            (Get-FileHash -LiteralPath $control).Hash -cne $controlHash -or
            (& git rev-parse HEAD).Trim() -cne $fixtureHead) {
            throw 'Patch generator modified its calling checkout.'
        }

        $patchFile = Join-Path ([IO.Path]::GetTempPath()) ('hami-format-patch-test-' + [Guid]::NewGuid().ToString('N') + '.patch')
        try {
            $patch = $patchOutput[($start + 1)..($end - 1)] -join "`n"
            [IO.File]::WriteAllText($patchFile, $patch + "`n", [Text.UTF8Encoding]::new($false))
            & git apply --check -- $patchFile
            if ($LASTEXITCODE -ne 0) { throw 'Formatter correction patch is not applicable.' }
            & git apply -- $patchFile
            if ($LASTEXITCODE -ne 0) { throw 'Fixture patch application failed.' }
        }
        finally {
            if (Test-Path -LiteralPath $patchFile) { Remove-Item -LiteralPath $patchFile }
        }
        $status = @(& git status --porcelain=v1 --untracked-files=all)
        if ($LASTEXITCODE -ne 0 -or $status.Count -ne 2 -or
            $status -cnotcontains " M $relative" -or $status -cnotcontains " M $probeRelative" -or
            (Get-FileHash -LiteralPath $probe).Hash -ceq $probeBadHash -or
            (Get-FileHash -LiteralPath $target).Hash -ceq $beforePatch -or
            (Get-FileHash -LiteralPath $control).Hash -cne $controlHash -or
            (& git rev-parse HEAD).Trim() -cne $fixtureHead) {
            throw 'Applied correction did not stay within the expected source scope.'
        }
        # Verify the actual patch result against its committed bad baseline,
        # so scope cannot become empty even if the formatter restores HEAD's
        # earlier original formatting. No manufacturer-specific expected text.
        & ./scripts/quality/Invoke-HaMiFormat.ps1 -Base $fixtureHead -Mode Check
        Write-Host 'PASS: HaMi formatter integration, scoped applicable patch and checkout protection.'
    }
    finally { Pop-Location }
}
finally {
    & git -C $repo worktree remove --force $worktree
    if ($LASTEXITCODE -ne 0) { throw 'Fixture worktree cleanup failed.' }
}
