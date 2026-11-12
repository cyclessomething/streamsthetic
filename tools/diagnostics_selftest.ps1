# Checks the crash recording behind "Report a problem", without a phone:
#  1. the app crashes (--simulate-crash): a problem record with the stack trace is saved;
#  2. the receiver crashes on its own: it writes a crash dump and exits with the crash code;
#  3. the receiver crashes under the app (self-test mode): the app records it, named after the dump.
# Uses the dev builds (windows\app\bin\Release, windows\receiver\x64\Release). Prints PASS or FAIL per step.
param([switch]$Keep)
$start = Get-Date
$root = Split-Path $PSScriptRoot -Parent
$app = Get-ChildItem "$root\windows\app\bin\Release" -Recurse -Filter Streamsthetic.exe | Select-Object -First 1 -ExpandProperty FullName
$receiver = "$root\windows\receiver\x64\Release\StreamstheticReceiver.exe"
$problems = "$env:LOCALAPPDATA\Streamsthetic\diagnostics\problems"
New-Item -ItemType Directory -Force $problems | Out-Null
$failed = 0
function Check($name, $ok, $detail) {
    "{0}: {1}  {2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $detail
    if (-not $ok) { $script:failed++ }
}
function NewFiles($since, $filter) { Get-ChildItem $problems -Filter $filter | Where-Object { $_.LastWriteTime -ge $since } }

# 1. app crash
$t = Get-Date
$p = Start-Process $app -ArgumentList '--simulate-crash' -PassThru
if (-not $p.WaitForExit(30000)) { $p.Kill() }
$record = NewFiles $t '*-app-crash.json' | Select-Object -First 1
$ok = $record -and (Get-Content $record.FullName -Raw) -match 'simulated crash'
Check 'app crash recorded' $ok "exit $($p.ExitCode), $($record.Name)"

# 2. receiver crash on its own
$t = Get-Date
$p = Start-Process $receiver -ArgumentList '--simulate-crash' -PassThru -WindowStyle Hidden
$p.WaitForExit(15000) | Out-Null
$dump = NewFiles $t '*-receiver-crash.dmp' | Select-Object -First 1
Check 'receiver writes crash dump' ($dump -and $dump.Length -gt 1000 -and $p.ExitCode -eq -1073741819) ("exit 0x{0:X8}, {1} ({2} bytes)" -f $p.ExitCode, $dump.Name, $dump.Length)

# 3. receiver crash seen by the app
Start-Sleep 1
$t = Get-Date
$result = Join-Path $env:TEMP 'phonecam-diag-selftest.txt'
$p = Start-Process $app -ArgumentList '--selftest', "`"$result`"", '--', '--simulate-crash' -PassThru
if (-not $p.WaitForExit(40000)) { $p.Kill() }
$record = NewFiles $t '*-receiver-crash.json' | Select-Object -First 1
$paired = $record -and (Test-Path ([IO.Path]::ChangeExtension($record.FullName, '.dmp')))
Check 'app records receiver crash next to its dump' $paired "$($record.Name)"

# remove this test's records, so the user's "Report a problem" list only shows real problems (-Keep to inspect)
if (-not $Keep) { NewFiles $start '*' | Where-Object { $_.Name -match '-(app|receiver)-crash\.(json|dmp)$' } | Remove-Item }

if ($failed) { "DIAGNOSTICS SELF-TEST FAIL ($failed)"; exit 1 } else { 'DIAGNOSTICS SELF-TEST PASS' }
