# Registers (or with -Remove unregisters) the per-user scheduled task that keeps the Claude app patched
# after it downloads new Claude Code versions. No administrator rights needed.
param([switch]$Remove)
$name = 'Claude autopatch'
if ($Remove) {
  Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
  Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'autopatch\.ps1' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
  "Removed scheduled task '$name'."
  return
}
$script = Join-Path $PSScriptRoot 'autopatch.ps1'
$a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$script`""
$t = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $name -Action $a -Trigger $t -Settings $s -Description 'Puts claude-trap.exe in front of new Claude Code versions downloaded by the Claude app (CPUs without SSE4.2).' -Force | Out-Null
Start-ScheduledTask -TaskName $name
"Registered and started scheduled task '$name'."
