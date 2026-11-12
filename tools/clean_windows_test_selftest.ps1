# Host-safe checks: parse scripts, generate configuration from inert fixtures, verify guest refusal.
$ErrorActionPreference = 'Stop'
$checks = 0
function Assert($condition, $message) { if (-not $condition) { throw $message }; $script:checks++; Write-Output "PASS: $message" }
foreach ($name in 'prepare_clean_windows_test.ps1', 'clean_windows_test.ps1') {
    $tokens = $null; $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $name), [ref]$tokens, [ref]$errors)
    Assert ($errors.Count -eq 0) "$name parses"
}
$work = Join-Path ([IO.Path]::GetTempPath()) ('Streamsthetic-sandbox-plan-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$fakeSetup = Join-Path $work 'inert-offline.exe'
$fakeApp = Join-Path $work 'Streamsthetic.exe'
[IO.File]::WriteAllText($fakeSetup, 'Inert fixture. Never executable.')
[IO.File]::WriteAllText($fakeApp, 'Inert app identity fixture.')
$root = Join-Path $work 'plan & xml escaping'
$plan = & "$PSScriptRoot\prepare_clean_windows_test.ps1" -SetupPath $fakeSetup -ExpectedAppPath $fakeApp -OutputRoot $root
[xml]$xml = Get-Content -LiteralPath $plan.Configuration -Raw
Assert (-not $plan.Launched) 'generator never launches Sandbox'
foreach ($name in 'Networking', 'ClipboardRedirection', 'AudioInput', 'VideoInput', 'PrinterRedirection') {
    Assert ($xml.Configuration.$name -eq 'Disable') "$name disabled"
}
Assert ($xml.Configuration.MemoryInMB -eq '4096') 'memory is bounded to 4 GB'
$maps = @($xml.Configuration.MappedFolders.MappedFolder)
Assert ($maps.Count -eq 2) 'exactly two dedicated mappings'
Assert ($maps[0].ReadOnly -eq 'true' -and $maps[1].ReadOnly -eq 'false') 'input read-only and evidence writable'
Assert ($maps[0].HostFolder -eq (Join-Path $root 'input') -and $maps[1].HostFolder -eq (Join-Path $root 'evidence')) 'mapping XML preserves escaped paths'
$files = @(Get-ChildItem -LiteralPath (Join-Path $root 'input') -File)
Assert ($files.Count -eq 3 -and @($files.Name | Where-Object { $_ -notin @('Setup-offline.exe', 'test-input.json', 'clean_windows_test.ps1') }).Count -eq 0) 'only minimal allowlisted input is exposed'
$config = Get-Content -LiteralPath (Join-Path $root 'input\test-input.json') -Raw | ConvertFrom-Json
Assert ($config.SetupSHA256 -eq (Get-FileHash -LiteralPath $fakeSetup).Hash) 'installer identity pinned'
Assert ($xml.Configuration.LogonCommand.Command.EndsWith('-Token ' + $config.Token)) 'logon command contains matching run marker'
$refused = $false
try { & "$PSScriptRoot\prepare_clean_windows_test.ps1" -SetupPath $fakeSetup -ExpectedAppPath $fakeApp -OutputRoot $root | Out-Null } catch { $refused = $_.Exception.Message -like '*new dedicated*' }
Assert $refused 'existing directories cannot be reused as mappings'
$refused = $false
try { & "$PSScriptRoot\clean_windows_test.ps1" -Token $config.Token } catch { $refused = $_.Exception.Message -like '*Guest guard refused*' }
Assert $refused 'runner refuses host before any installer launch'
Assert (@(Get-ChildItem -LiteralPath $plan.Evidence).Count -eq 0) 'host refusal writes no guest evidence'
Write-Output "SANDBOX PREPARATION CHECKS PASS ($checks). Inert fixture evidence retained at $work"
