#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$Base,
    [ValidateSet('Check', 'Apply')][string]$Mode = 'Check'
)
$ErrorActionPreference = 'Stop'
# Handle native exit codes explicitly, including expected check failures in CI.
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

# Trusted implementation worktrees only. Do not run this script against
# untrusted generated projects: dotnet format loads MSBuild.
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location -LiteralPath $repo
try {
    function Invoke-GitText([string[]]$Arguments) {
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = 'git'
        $start.WorkingDirectory = $repo
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($arg in $Arguments) { $start.ArgumentList.Add($arg) }
        $process = [Diagnostics.Process]::Start($start)
        try {
            $output = $process.StandardOutput.ReadToEndAsync()
            $errors = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(30000)) {
                $process.Kill($true)
                throw 'Git metadata query timed out.'
            }
            $result = $output.GetAwaiter().GetResult()
            $null = $errors.GetAwaiter().GetResult()
            if ($process.ExitCode -ne 0) { throw 'Git metadata query failed.' }
            return $result
        }
        finally { $process.Dispose() }
    }

    function Get-GitPaths([string[]]$Arguments) {
        $text = Invoke-GitText $Arguments
        return $text.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)
    }

    function Resolve-SafePath([string]$Relative) {
        if ([IO.Path]::IsPathRooted($Relative) -or $Relative.Contains('\') -or
            $Relative.Contains(':') -or $Relative -match '[\x00-\x1f\x7f]' -or
            ($Relative.Split('/') | Where-Object { $_ -in @('', '.', '..') })) {
            throw 'Invalid Git path.'
        }
        $path = $repo
        foreach ($part in $Relative.Split('/')) {
            $path = Join-Path $path $part
            $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
            if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Link or junction rejected: $Relative"
            }
        }
        return $path
    }

    # Check all checkout ancestors as well as the selected paths.
    $dir = [IO.DirectoryInfo]::new($repo)
    while ($null -ne $dir) {
        if ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Checkout traverses a link or junction.'
        }
        $dir = $dir.Parent
    }

    $top = [IO.Path]::GetFullPath((Invoke-GitText @('rev-parse', '--show-toplevel')).Trim())
    if ($top -ne $repo) { throw 'Expected repository root.' }
    $head = (Invoke-GitText @('rev-parse', 'HEAD')).Trim()
    $branch = (Invoke-GitText @('rev-parse', '--abbrev-ref', 'HEAD')).Trim()
    $null = Invoke-GitText @('cat-file', '-e', "${Base}^{commit}")
    $null = Invoke-GitText @('merge-base', '--is-ancestor', $Base, $head)

    if ($Mode -eq 'Apply') {
        if ($branch -in @('main', 'master')) { throw 'Apply forbidden on protected development checkout.' }
        $gitDir = [IO.Path]::GetFullPath((Invoke-GitText @('rev-parse', '--absolute-git-dir')).Trim())
        $commonDir = [IO.Path]::GetFullPath((Invoke-GitText @('rev-parse', '--path-format=absolute', '--git-common-dir')).Trim())
        if ($gitDir -eq $commonDir) { throw 'Apply requires a separate linked Git worktree.' }
    }

    # Include staged, unstaged and untracked source changes relative to the
    # explicitly approved base; never format deleted files or the whole repo.
    $changed = @(Get-GitPaths @('diff', '--name-only', '-z', '--no-renames', '--diff-filter=ACM', $Base, '--'))
    $changed += @(Get-GitPaths @('ls-files', '-z', '--others', '--exclude-standard'))
    $scope = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $projectGroups = [ordered]@{
        'src/HaMi.Agentic.Cli/HaMi.Agentic.Cli.csproj' = 'src/HaMi.Agentic.Cli/'
        'tests/HaMi.Agentic.JobState.Probe/HaMi.Agentic.JobState.Probe.csproj' = 'tests/HaMi.Agentic.JobState.Probe/'
    }
    foreach ($file in $changed) {
        if (-not $file.EndsWith('.cs', [StringComparison]::Ordinal)) { continue }
        foreach ($prefix in $projectGroups.Values) {
            if ($file.StartsWith($prefix, [StringComparison]::Ordinal)) {
                if ($file -match '/(?:bin|obj)/') { throw 'Generated source path rejected.' }
                $full = Resolve-SafePath $file
                if (-not [IO.File]::Exists($full)) { throw "Source missing: $file" }
                $null = $scope.Add($file)
            }
        }
    }
    if ($scope.Count -eq 0) {
        Write-Host 'No changed C# files in supported implementation projects.'
        return
    }

    # Fingerprint every tracked/nonignored untracked path, including the
    # Git index. Never silently stage, commit, reset or push anything.
    function Get-Snapshot {
        $map = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
        $paths = @(Get-GitPaths @('ls-files', '-z', '--cached', '--others', '--exclude-standard'))
        foreach ($file in $paths) {
            $full = Resolve-SafePath $file
            if ([IO.Directory]::Exists($full)) { throw "Unsupported directory entry: $file" }
            $map[$file] = if ([IO.File]::Exists($full)) {
                (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
            } else { 'MISSING' }
        }
        $index = [IO.Path]::GetFullPath((Invoke-GitText @('rev-parse', '--path-format=absolute', '--git-path', 'index')).Trim())
        $map[':index'] = (Get-FileHash -LiteralPath $index -Algorithm SHA256).Hash
        $map[':head'] = (Invoke-GitText @('rev-parse', 'HEAD')).Trim()
        $map[':branch'] = (Invoke-GitText @('rev-parse', '--abbrev-ref', 'HEAD')).Trim()
        return ,$map
    }

    $before = Get-Snapshot
    $failure = $null
    try {
        foreach ($project in $projectGroups.Keys) {
            $prefix = $projectGroups[$project]
            $files = @($scope | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) } | Sort-Object -CaseSensitive)
            if ($files.Count -eq 0) { continue }
            $null = Resolve-SafePath $project
            if (-not [IO.File]::Exists((Join-Path $repo $project))) { throw "Project missing: $project" }
            $arguments = @('format', 'whitespace', $project, '--no-restore')
            if ($Mode -eq 'Check') { $arguments += '--verify-no-changes' }
            $arguments += '--include'
            $arguments += $files
            & dotnet @arguments
            if ($LASTEXITCODE -ne 0) { throw "Microsoft formatter failed for $project (exit $LASTEXITCODE)." }
            if ($Mode -eq 'Apply') {
                & dotnet @arguments --verify-no-changes
                if ($LASTEXITCODE -ne 0) { throw "Formatter verification failed for $project." }
            }
        }
    }
    catch { $failure = $_ }
    finally {
        $after = Get-Snapshot
        foreach ($key in @($before.Keys) + @($after.Keys)) {
            if ($before.ContainsKey($key) -and $after.ContainsKey($key) -and $before[$key] -ceq $after[$key]) { continue }
            if ($Mode -eq 'Check' -or -not $scope.Contains($key) -or
                -not $before.ContainsKey($key) -or -not $after.ContainsKey($key) -or
                $after[$key] -eq 'MISSING') {
                throw "Unexpected formatter side effect: $key. Preserve worktree for review; do not commit."
            }
        }
    }
    if ($null -ne $failure) { throw $failure }
    Write-Host "Formatter $Mode passed for $($scope.Count) source file(s)."
}
finally { Pop-Location }
