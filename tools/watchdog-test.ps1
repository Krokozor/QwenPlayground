# Тест watchdog'а на подставном процессе: смерть без маркера + чистый выход.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot  # скрипт лежит в tools/, корень — родитель
$wdExe = Join-Path $root "launcher\QwenPlayground.Watchdog.exe"
$testLogs = Join-Path $root "run\watchdog-test"
if (Test-Path $testLogs) { Remove-Item $testLogs -Recurse -Force }
New-Item -ItemType Directory -Path $testLogs | Out-Null

# --- Сценарий 1: смерть без чистого маркера ---
$ping = Start-Process -FilePath "cmd" -ArgumentList "/c ping -n 60 127.0.0.1 >nul" -PassThru -WindowStyle Hidden
$wd1 = Start-Process -FilePath $wdExe -ArgumentList "$($ping.Id) cmd `"$testLogs\clean1.txt`" `"$testLogs`"" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2
Stop-Process -Id $ping.Id -Force
Start-Sleep -Seconds 3
$wd1Exited = $wd1.HasExited
Write-Output "== scenario 1 (unplanned death): watchdog exited=$wd1Exited"

# --- Сценарий 2: чистый выход (маркер записан до смерти) ---
$ping2 = Start-Process -FilePath "cmd" -ArgumentList "/c ping -n 60 127.0.0.1 >nul" -PassThru -WindowStyle Hidden
$wd2 = Start-Process -FilePath $wdExe -ArgumentList "$($ping2.Id) cmd `"$testLogs\clean2.txt`" `"$testLogs`"" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2
Set-Content -Path "$testLogs\clean2.txt" -Value (Get-Date).ToString("O")
Stop-Process -Id $ping2.Id -Force
Start-Sleep -Seconds 3
$wd2Exited = $wd2.HasExited
Write-Output "== scenario 2 (clean exit): watchdog exited=$wd2Exited"

Write-Output "== watchdog.log:"
Get-Content (Join-Path $testLogs "watchdog.log")
Write-Output "== crash log (channel 'crash'):"
$crash = Get-ChildItem $testLogs -Filter "crash-*.log" | Select-Object -First 1
if ($crash) { Get-Content $crash.FullName } else { Write-Output "(none)" }
Write-Output "== last-crash.log:"
if (Test-Path "$testLogs\last-crash.log") { Get-Content "$testLogs\last-crash.log" } else { Write-Output "(none)" }
