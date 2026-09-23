[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Repository)

$git = Get-Command git -ErrorAction SilentlyContinue
if (-not $git) { Write-Host '[update] Git is not installed; starting the installed version.'; return $false }
if (-not (Test-Path -LiteralPath (Join-Path $Repository '.git'))) {
    Write-Host '[update] This is not a Git checkout (for example, a ZIP download); starting the installed version.'
    return $false
}

$root = & $git.Source -C $Repository rev-parse --show-toplevel 2>$null
if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($root) -ne [IO.Path]::GetFullPath($Repository)) {
    Write-Host '[update] Git checkout could not be verified; starting the installed version.'
    return $false
}
$branch = & $git.Source -C $Repository symbolic-ref --quiet --short HEAD 2>$null
$upstream = & $git.Source -C $Repository rev-parse --abbrev-ref --symbolic-full-name '@{upstream}' 2>$null
$remote = & $git.Source -C $Repository remote get-url origin 2>$null
$trustedRemote = $remote -in @(
    'https://github.com/Aspectofthe/relicframe.git',
    'https://github.com/Aspectofthe/relicframe',
    'git@github.com:Aspectofthe/relicframe.git'
)
if ($branch -ne 'csharp-rewrite' -or $upstream -ne 'origin/csharp-rewrite' -or -not $trustedRemote) {
    Write-Host '[update] Branch, upstream, or origin is not the expected RelicFrame GitHub checkout; starting without updating.'
    return $false
}
$changes = @(& $git.Source -C $Repository status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) {
    Write-Host '[update] Local changes or untracked files are present; skipping update to protect them.'
    return $false
}

Write-Host '[update] Checking GitHub for a newer csharp-rewrite commit...'
$previousPrompt = $env:GIT_TERMINAL_PROMPT
$env:GIT_TERMINAL_PROMPT = '0'
try {
    & $git.Source -C $Repository -c http.lowSpeedLimit=1000 -c http.lowSpeedTime=10 fetch --quiet --no-tags origin csharp-rewrite
    if ($LASTEXITCODE -ne 0) { Write-Warning '[update] GitHub could not be reached; starting the installed version.'; return $false }
}
finally { $env:GIT_TERMINAL_PROMPT = $previousPrompt }

$ahead = & $git.Source -C $Repository rev-list --count 'origin/csharp-rewrite..HEAD'
$behind = & $git.Source -C $Repository rev-list --count 'HEAD..origin/csharp-rewrite'
if ($LASTEXITCODE -ne 0 -or -not ($ahead -match '^\d+$') -or -not ($behind -match '^\d+$')) {
    Write-Warning '[update] Could not compare Git revisions; starting the installed version.'
    return $false
}
if ([int]$ahead -gt 0) { Write-Host '[update] Local commits are ahead or diverged; skipping update.'; return $false }
if ([int]$behind -eq 0) { Write-Host '[update] Already up to date.'; return $false }

& $git.Source -C $Repository merge --ff-only origin/csharp-rewrite
if ($LASTEXITCODE -ne 0) { Write-Warning '[update] Fast-forward failed; local files were not overwritten.'; return $false }
Write-Host "[update] Applied $behind new commit(s); building the updated bot."
return $true
