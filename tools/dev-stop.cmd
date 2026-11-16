@echo off
rem Closes every ExploreTogether server/client started by dev-run.cmd.
rem Matches Godot processes for this project whose arguments include --server or --connect,
rem so a Godot editor you have open is left alone. Smoke-test processes (--smoke-test) are left alone too.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$procs = Get-CimInstance Win32_Process -Filter \"Name LIKE 'Godot%%'\" | Where-Object { $_.CommandLine -like '*GodotExploreTogether*' -and $_.CommandLine -match ' --(server|connect)\b' -and $_.CommandLine -notmatch '--smoke-test' };" ^
  "foreach ($p in $procs) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue };" ^
  "Write-Host ('dev-stop: closed ' + @($procs).Count + ' process(es)')"
