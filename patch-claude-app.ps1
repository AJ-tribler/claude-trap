# Puts claude-trap.exe in front of every copy of Claude Code downloaded by the Claude desktop app
# (Code tab -> Local), so it runs on CPUs without SSE4.2.
#   powershell -ExecutionPolicy Bypass -File patch-claude-app.ps1            # patch / refresh
#   powershell -ExecutionPolicy Bypass -File patch-claude-app.ps1 -Restore   # undo
# The app does not need to be closed: a claude.exe that is in use is only renamed aside;
# new Code sessions pick up the launcher.
param([switch]$Restore)
$log = Join-Path $PSScriptRoot "patch.log"
function L($m) { "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $m" | Tee-Object -FilePath $log -Append }

$launcher = Join-Path $PSScriptRoot "claude-trap.exe"
if (-not $Restore -and -not (Test-Path $launcher)) { L "Missing $launcher - run build.cmd first"; exit 1 }

$bases = Get-ChildItem "$env:LOCALAPPDATA\Claude-Data\claude_*\claude-code", "$env:APPDATA\Claude\claude-code", "$env:LOCALAPPDATA\Packages\Claude_*\LocalCache\Roaming\Claude\claude-code" -Directory -ErrorAction SilentlyContinue
if (-not $bases) { L "Claude app's claude-code folder not found"; exit 1 }

$dirs = foreach ($b in $bases) { Get-ChildItem $b.FullName -Recurse -Include claude.exe, claude-real.exe -File | ForEach-Object { $_.Directory.FullName } }
$dirs = $dirs | Sort-Object -Unique
if (-not $dirs) { L "No claude.exe found in the app's folders"; exit 1 }

# Delete a file, or rename it aside if it is in use by a running process.
function Move-Aside($path) {
  if (-not (Test-Path $path)) { return }
  try { Remove-Item $path -Force -ErrorAction Stop }
  catch { $old = "$path.old-$(Get-Date -Format yyyyMMddHHmmss)"; Rename-Item $path (Split-Path $old -Leaf) -ErrorAction Stop; L "  (in use, renamed to $(Split-Path $old -Leaf))" }
}

foreach ($d in $dirs) {
  Get-ChildItem $d -Filter "claude.exe.old-*" -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
  $exe = Join-Path $d "claude.exe"; $real = Join-Path $d "claude-real.exe"
  try {
    if ($Restore) {
      if (Test-Path $real) { Move-Aside $exe; Rename-Item $real "claude.exe"; L "RESTORED $d" }
      continue
    }
    if (Test-Path $real) {
      if ((Test-Path $exe) -and ((Get-FileHash $exe).Hash -eq (Get-FileHash $launcher).Hash)) { L "OK (up to date) $d"; continue }
      Move-Aside $exe; Copy-Item $launcher $exe; L "REFRESHED launcher $d"
      continue
    }
    $size = (Get-Item $exe).Length
    Rename-Item $exe "claude-real.exe"
    Copy-Item $launcher $exe
    L "PATCHED $d (original kept as claude-real.exe, $size bytes)"
  } catch { L "ERROR $d : $($_.Exception.Message)" }
}
