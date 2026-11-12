# Collects diagnostics on the PC where Streamsthetic has a problem.
# Starts and closes a test app process, runs the receiver, and performs local network connection tests.
# Finish active camera sessions first: the test app can briefly connect to a phone.
# Saves Streamsthetic-problem-report.txt on the Desktop; it does not upload the report.
# The report includes device/connection details, PC/user names, settings and logs. Review it before sharing.
$ErrorActionPreference = 'Continue'
Write-Host 'Collecting a Streamsthetic diagnostic report.'
Write-Host 'This test starts and closes the app, runs the video component and checks local network connections.'
Write-Host 'It may briefly connect to your phone. The report is saved locally for you to review before sharing.'
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Streamsthetic-problem-report.txt'
$sb = New-Object System.Text.StringBuilder
function Say($t) { [void]$sb.AppendLine([string]$t) }
function Head($t) { Say ''; Say ('=' * 70); Say $t; Say ('=' * 70) }
function Try-Do($name, [scriptblock]$code) {
    Head $name
    try { $r = & $code | Out-String -Width 220; Say $r.TrimEnd() } catch { Say ("FAILED: " + $_.Exception.Message) }
}

Say "Streamsthetic problem report"
Say ("Made: " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + "   Computer: $env:COMPUTERNAME   User: $env:USERNAME")

Try-Do 'Windows' {
    $os = Get-CimInstance Win32_OperatingSystem
    "Name:        $($os.Caption)"
    "Version:     $($os.Version)  build $($os.BuildNumber)   $($os.OSArchitecture)"
    "Edition ID:  $((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID)"
    "Language:    $((Get-Culture).Name)"
    "RAM (GB):    $([math]::Round($os.TotalVisibleMemorySize / 1MB, 1))"
    "CPU:         $((Get-CimInstance Win32_Processor | Select-Object -First 1).Name)"
    "GPU:         $((Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name + ' (driver ' + $_.DriverVersion + ')' }) -join '; ')"
}

Try-Do 'Media features (Windows N editions lack these)' {
    foreach ($f in 'mfplat.dll', 'mf.dll', 'mfreadwrite.dll', 'mfsensorgroup.dll', 'msmpeg2vdec.dll') {
        $p = Join-Path $env:WINDIR "System32\$f"
        "{0,-22} {1}" -f $f, $(if (Test-Path $p) { 'present' } else { 'MISSING' })
    }
}

Try-Do '.NET runtimes on this PC (the offline package needs none)' {
    $d = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($d) { & dotnet --list-runtimes } else { 'dotnet not found (fine for the offline package)' }
}

Try-Do 'Streamsthetic install folders' {
    $dirs = @("$env:LOCALAPPDATA\Programs\Streamsthetic", "$env:LOCALAPPDATA\Programs\PhoneCam", "$env:ProgramFiles\Streamsthetic")
    foreach ($dir in $dirs) {
        if (Test-Path $dir) {
            "FOUND $dir  ($((Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count) files)"
            foreach ($f in 'Streamsthetic.exe', 'Streamsthetic.dll', 'coreclr.dll', 'hostfxr.dll', 'StreamstheticReceiver.exe', 'QRCoder.dll', 'adb.exe', 'vcam\VCamSampleSource.dll') {
                $p = Join-Path $dir $f
                if (Test-Path $p) {
                    $i = Get-Item $p
                    $blocked = if (Get-Item $p -Stream Zone.Identifier -ErrorAction SilentlyContinue) { '  [BLOCKED: downloaded from the internet]' } else { '' }
                    "   {0,-30} {1,12:N0} bytes  {2}{3}" -f $f, $i.Length, $i.LastWriteTime.ToString('yyyy-MM-dd HH:mm'), $blocked
                } else { "   {0,-30} MISSING" -f $f }
            }
        } else { "not found: $dir" }
    }
}

Try-Do 'Virtual camera (Streamsthetic Windows Virtual Camera)' {
    $t = 'C:\ProgramData\PhoneCam\vcam'
    "Camera files: " + $(if (Test-Path "$t\VCamSampleSource.dll") { "present in $t" } else { "MISSING in $t (the camera install step did not run)" })
    $clsid = '{31621596-4208-4D72-9066-1F9507074423}'
    "COM registration: " + $(if (Test-Path "HKLM:\SOFTWARE\Classes\CLSID\$clsid") { 'registered' } else { 'NOT registered' })
    Get-Service FrameServer, FrameServerMonitor -ErrorAction SilentlyContinue | Format-Table Name, Status, StartType -AutoSize
    "Cameras Windows knows:"
    Get-PnpDevice -Class Camera, Image -ErrorAction SilentlyContinue | Format-Table Status, FriendlyName -AutoSize
}

Try-Do 'Streamsthetic settings and recent diagnostic logs' {
    $base = "$env:LOCALAPPDATA\Streamsthetic"
    if (-not (Test-Path $base)) { "No saved settings or diagnostics folder found at ${base}."; return }
    "--- settings.json"
    (Get-Content "$base\settings.json" -Raw -ErrorAction SilentlyContinue) -replace '"Code":"\d+"', '"Code":"<hidden>"'
    foreach ($sub in 'diagnostics\logs', 'diagnostics\problems') {
        $d = Join-Path $base $sub
        if (Test-Path $d) {
            Get-ChildItem $d -File | Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object {
                "--- $($_.FullName)  ($($_.Length) bytes)"
                Get-Content $_.FullName -Tail 120 -ErrorAction SilentlyContinue
            }
        } else { "no $sub folder" }
    }
}

Try-Do 'Windows Event Log: application errors, last 3 days' {
    $since = (Get-Date).AddDays(-3)
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since; Level = 1, 2 } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'Streamsthetic|PhoneCam|VCamSample|dotnet|\.NET|coreclr|hostfxr|PresentationFramework|FrameServer' } |
        Select-Object -First 12 | ForEach-Object {
            "[{0}] {1} id {2}" -f $_.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss'), $_.ProviderName, $_.Id
            ($_.Message -split "`n" | Select-Object -First 14) -join "`n"
            ''
        }
}

Try-Do 'Security software blocks (Defender / SmartScreen)' {
    Get-MpThreatDetection -ErrorAction Stop | Where-Object { $_.Resources -match 'Streamsthetic|PhoneCam' } | Select-Object -First 5 | Format-List InitialDetectionTime, ThreatID, ActionSuccess, Resources
    Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Windows Defender/Operational'; Id = 1116, 1117, 1119; StartTime = (Get-Date).AddDays(-3) } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'Streamsthetic|PhoneCam' } | Select-Object -First 3 | ForEach-Object { $_.Message }
}

Try-Do 'App startup test (runs for up to 12 seconds, then closes the test process)' {
    $exe = "$env:LOCALAPPDATA\Programs\Streamsthetic\Streamsthetic.exe"
    if (-not (Test-Path $exe)) { "Not installed at $exe, skipping."; return }
    $mark = Get-Date
    $p = Start-Process $exe -PassThru
    Start-Sleep 12
    if ($p.HasExited) { "The app exited. Exit code: $($p.ExitCode)" }
    else { "The app is running (window title: '$($p.MainWindowTitle)', responding: $($p.Responding)). Closing it."; $p.Kill() }
    "Errors logged by Windows since the start:"
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $mark; Level = 1, 2 } -ErrorAction SilentlyContinue |
        Select-Object -First 6 | ForEach-Object { ($_.Message -split "`n" | Select-Object -First 12) -join "`n"; '' }
}

Try-Do 'Video component test (lists audio outputs, then exits)' {
    $r = "$env:LOCALAPPDATA\Programs\Streamsthetic\StreamstheticReceiver.exe"
    if (-not (Test-Path $r)) { "Not installed, skipping."; return }
    $proc = Start-Process $r -ArgumentList '--list-audio' -PassThru -NoNewWindow -RedirectStandardOutput "$env:TEMP\ss_recv_out.txt" -RedirectStandardError "$env:TEMP\ss_recv_err.txt"
    if (-not $proc.WaitForExit(10000)) { $proc.Kill(); 'did not finish in 10 s' } else { "exit code $($proc.ExitCode)" }
    Get-Content "$env:TEMP\ss_recv_out.txt", "$env:TEMP\ss_recv_err.txt" -ErrorAction SilentlyContinue | Select-Object -First 20
}

Try-Do 'Network adapters (USB tethering shows up here)' {
    Get-NetAdapter -ErrorAction SilentlyContinue | Format-Table Name, InterfaceDescription, Status -AutoSize
}

Try-Do 'Firewall configuration' {
    Get-NetFirewallProfile | Format-Table Name, Enabled, DefaultInboundAction, DefaultOutboundAction -AutoSize
    "Rules that mention Streamsthetic or the receiver:"
    Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue | Where-Object { $_.Program -match 'Streamsthetic|PhoneCam' } |
        ForEach-Object { $r = Get-NetFirewallRule -AssociatedNetFirewallApplicationFilter $_; "{0}  enabled={1} action={2} direction={3}  {4}" -f $r.DisplayName, $r.Enabled, $r.Action, $r.Direction, $_.Program }
    "Network category of each connection (Public is the strictest):"
    Get-NetConnectionProfile -ErrorAction SilentlyContinue | Format-Table Name, InterfaceAlias, NetworkCategory, IPv4Connectivity -AutoSize
}

Try-Do 'Phone connection test (requires Start on the phone)' {
    # the same question the PC app asks: a broadcast "PHONECAM?" on UDP 5557; the phone answers with its model and port
    $found = @()
    foreach ($nic in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($nic.OperationalStatus -ne 'Up') { continue }
        foreach ($ua in $nic.GetIPProperties().UnicastAddresses) {
            if ($ua.Address.AddressFamily -ne 'InterNetwork' -or $ua.Address.ToString().StartsWith('169.254.') -or $ua.Address.ToString().StartsWith('127.')) { continue }
            $local = $ua.Address
            $ipBytes = $local.GetAddressBytes(); $maskBytes = $ua.IPv4Mask.GetAddressBytes()
            $bcast = [System.Net.IPAddress]::new([byte[]](0..3 | ForEach-Object { $ipBytes[$_] -bor (255 - $maskBytes[$_]) }))
            $udp = $null
            try {
                $udp = New-Object System.Net.Sockets.UdpClient (New-Object System.Net.IPEndPoint($local, 0))
                $udp.EnableBroadcast = $true; $udp.Client.ReceiveTimeout = 2500
                $msg = [Text.Encoding]::ASCII.GetBytes('PHONECAM?')
                [void]$udp.Send($msg, $msg.Length, (New-Object System.Net.IPEndPoint($bcast, 5557)))
                $ep = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
                $reply = $udp.Receive([ref]$ep)
                $text = [Text.Encoding]::ASCII.GetString($reply)
                "{0,-28} {1,-15} -> ANSWER from {2}: {3}" -f $nic.Description, $local, $ep.Address, $text
                $found += [pscustomobject]@{ Ip = $ep.Address.ToString(); Port = [int]($text.Split('|')[2]) }
            } catch { "{0,-28} {1,-15} -> no answer ({2})" -f $nic.Description, $local, $_.Exception.InnerException.Message }
            finally { if ($udp) { $udp.Close() } }
        }
    }
    foreach ($p in $found) {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $ok = $tcp.ConnectAsync($p.Ip, $p.Port).Wait(3000)
        "TCP connect to {0}:{1} -> {2}" -f $p.Ip, $p.Port, $(if ($ok -and $tcp.Connected) { 'OK' } else { 'FAILED (blocked or the phone is not listening)' })
        $tcp.Close()
    }
    if (-not $found) { "No phone replied. Check that Streamsthetic is running with Start pressed on the phone, and that USB tethering is enabled or both devices use the same local network. Network isolation or firewall rules can block discovery." }
    # the phone is the gateway of its own USB network: ask it directly too, without any broadcast
    foreach ($nic in Get-NetIPConfiguration -ErrorAction SilentlyContinue) {
        if ($nic.InterfaceDescription -notmatch 'RNDIS|Remote NDIS|NCM|USB') { continue }
        $gw = $nic.IPv4DefaultGateway.NextHop
        if (-not $gw) { "{0}: no gateway address (the phone did not hand out network settings)" -f $nic.InterfaceAlias; continue }
        $tcp = New-Object System.Net.Sockets.TcpClient
        $ok = $tcp.ConnectAsync($gw, 5556).Wait(3000)
        "DIRECT TCP to the phone's USB address {0}:5556 -> {1}" -f $gw, $(if ($ok -and $tcp.Connected) { 'OK' } else { 'FAILED' })
        $tcp.Close()
    }
}
Try-Do 'USB network details (only when a phone USB network exists)' {
    $rndis = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceDescription -match 'RNDIS|Remote NDIS|NCM|USB' -and $_.Status -eq 'Up' }
    if (-not $rndis) { 'No USB network adapter is up.'; return }
    foreach ($a in $rndis) {
        "--- $($a.Name) ($($a.InterfaceDescription)) ifIndex $($a.ifIndex)"
        Get-NetIPAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 | Format-Table IPAddress, PrefixLength, PrefixOrigin, AddressState -AutoSize
        Get-NetIPInterface -InterfaceIndex $a.ifIndex -AddressFamily IPv4 | Format-Table InterfaceMetric, Dhcp, ConnectionState, Forwarding -AutoSize
        "Routes on this adapter:"
        Get-NetRoute -InterfaceIndex $a.ifIndex -AddressFamily IPv4 | Format-Table DestinationPrefix, NextHop, RouteMetric -AutoSize
        $gw = (Get-NetIPConfiguration -InterfaceIndex $a.ifIndex).IPv4DefaultGateway.NextHop
        if ($gw) {
            "Ping to the phone's USB address ${gw}:"
            Test-Connection -ComputerName $gw -Count 3 -ErrorAction SilentlyContinue | Format-Table Address, ResponseTime -AutoSize
            "Neighbour (ARP) entry:"
            Get-NetNeighbor -IPAddress $gw -ErrorAction SilentlyContinue | Format-Table IPAddress, LinkLayerAddress, State -AutoSize
            "Which adapter would Windows use to reach it:"
            Find-NetRoute -RemoteIPAddress $gw -ErrorAction SilentlyContinue | Format-Table InterfaceAlias, NextHop, DestinationPrefix -AutoSize
        }
    }
}
Set-Content -Path $out -Value $sb.ToString() -Encoding UTF8
Write-Host ''
Write-Host "Report saved to your Desktop: $out" -ForegroundColor Green
Write-Host 'The report contains device, connection and diagnostic information. Review it before sharing it with the person helping you.'
