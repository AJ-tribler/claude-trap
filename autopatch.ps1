# Runs in the background (scheduled task "Claude autopatch", started at logon). Every few seconds it
# checks whether the Claude app downloaded a new Claude Code version (a big claude.exe without a
# claude-real.exe next to it) and, if so, runs patch-claude-app.ps1. Log: patch.log next to this script.
$ErrorActionPreference = 'SilentlyContinue'
$mutex = New-Object System.Threading.Mutex($false, 'Local\ClaudeAutopatch')
if (-not $mutex.WaitOne(0)) { exit 0 }   # another copy is already running

$patch = Join-Path $PSScriptRoot 'patch-claude-app.ps1'
function Get-Unpatched {
  foreach ($b in (Get-ChildItem "$env:LOCALAPPDATA\Claude-Data\claude_*\claude-code", "$env:APPDATA\Claude\claude-code", "$env:LOCALAPPDATA\Packages\Claude_*\LocalCache\Roaming\Claude\claude-code" -Directory)) {
    foreach ($f in (Get-ChildItem $b.FullName -Recurse -Filter claude.exe -File)) {
      if ($f.Length -lt 5MB) { continue }
      if (Test-Path (Join-Path $f.DirectoryName 'claude-real.exe')) { continue }
      $f
    }
  }
}
function Test-Unlocked($path) {
  try { $s = [IO.File]::Open($path, 'Open', 'Read', 'None'); $s.Close(); return $true } catch { return $false }
}

$seen = @{}
while ($true) {
  foreach ($f in @(Get-Unpatched)) {
    $key = $f.FullName
    # wait until the app has verified the download (.verified) or 30 s have passed, and the file is not locked
    $verified = Test-Path (Join-Path $f.DirectoryName '.verified')
    if (-not $seen.ContainsKey($key)) { $seen[$key] = Get-Date }
    $age = ((Get-Date) - $seen[$key]).TotalSeconds
    if (($verified -or $age -gt 30) -and (Test-Unlocked $key)) {
      & powershell -NoProfile -ExecutionPolicy Bypass -File $patch | Out-Null
      $seen.Remove($key)
    }
  }
  Start-Sleep -Seconds 2
}
