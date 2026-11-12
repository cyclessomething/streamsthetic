# Side-effect-free prerequisite decisions and injectable runtime verification, shared with installer tests.
function Assert-StreamstheticPlatform {
    param([int]$Build, [string]$Architecture, [bool]$MediaFoundationAvailable, [bool]$NeedsCamera = $true)
    if ($Build -lt 22000) { throw 'Streamsthetic requires Windows 11 (build 22000 or newer). This installer does not support Windows 10.' }
    if ($Architecture -notin @('AMD64', 'X64')) { throw 'This installer requires x64 Windows on an Intel or AMD PC. Windows on ARM is not supported by this camera component.' }
    if ($NeedsCamera -and -not $MediaFoundationAvailable) { throw 'Windows media components are missing. On Windows 11 N, install Media Feature Pack from Settings > Apps > Optional features, restart Windows, then run setup again.' }
}
function Assert-StreamstheticRuntimeSource {
    param([bool]$SelfContained, [bool]$RuntimeAvailable, [bool]$WingetAvailable)
    if (-not $SelfContained -and -not $RuntimeAvailable -and -not $WingetAvailable) {
        throw 'Microsoft .NET 10 Desktop Runtime is missing and Windows App Installer (winget) is unavailable. Use the Streamsthetic offline installer, which includes the runtime, or install the x64 .NET 10 Desktop Runtime and run setup again.'
    }
}
function Install-StreamstheticRuntime {
    param([scriptblock]$Install, [scriptblock]$IsAvailable)
    $code = & $Install
    if ($code -ne 0) { throw "Microsoft .NET 10 Desktop Runtime installation failed (exit code $code). Use the Streamsthetic offline installer or install the x64 runtime, then run setup again." }
    if (-not (& $IsAvailable)) { throw 'Runtime setup reported success, but the x64 .NET 10 Desktop Runtime is still unavailable. Use the Streamsthetic offline installer or install the x64 runtime, then run setup again.' }
}
function Assert-StreamstheticCameraResult {
    param([int]$ExitCode)
    if ($ExitCode -ne 0) { throw 'Streamsthetic camera setup did not finish. App files may already be installed. Review the Windows error in %TEMP%\streamsthetic-install-camera.txt before retrying.' }
}

# Shared by the elevated worker and harmless native-fixture tests. File redirection avoids
# PowerShell 5 treating native stderr as a terminating error under ErrorActionPreference=Stop.
function Invoke-StreamstheticNativeStep {
    param([string]$FilePath, [string[]]$Arguments, [string]$Stage, [string]$LogPath,
        [ValidateRange(1, 120000)][int]$TimeoutMilliseconds = 120000)
    $capture = Join-Path ([IO.Path]::GetTempPath()) ('streamsthetic-native-' + [Guid]::NewGuid().ToString('N'))
    $stdout = $capture + '.stdout'; $stderr = $capture + '.stderr'
    $process = $null; $code = $null; $timedOut = $false
    Add-Content -LiteralPath $LogPath -Value "Stage: $Stage" -Encoding UTF8
    try {
        $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -ErrorAction Stop
        $null = $process.Handle # Retain the process handle so Windows PowerShell can retrieve its exit code.
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            $timedOut = $true
            Add-Content -LiteralPath $LogPath -Value "Timed out after $TimeoutMilliseconds ms; requesting process termination." -Encoding UTF8
            $process.Kill()
            if (-not $process.WaitForExit(5000)) { throw "$Stage timed out; process termination was not confirmed. Capture files will be retained." }
            $code = $process.ExitCode
            throw "$Stage timed out after $TimeoutMilliseconds ms. Details: $LogPath"
        }
        $code = $process.ExitCode
        if ($code -ne 0) { throw "$Stage failed (native exit code $code). Details: $LogPath" }
    } catch {
        Add-Content -LiteralPath $LogPath -Value ($_ | Out-String) -Encoding UTF8
        throw
    } finally {
        $captured = $true
        # Harvest each stream independently, including after timeout or a failed Kill call.
        foreach ($stream in @(@('stdout', $stdout), @('stderr', $stderr))) {
            try {
                Add-Content -LiteralPath $LogPath -Value ($stream[0] + ':') -Encoding UTF8 -ErrorAction Stop
                if (Test-Path -LiteralPath $stream[1]) { Get-Content -LiteralPath $stream[1] -ErrorAction Stop | Add-Content -LiteralPath $LogPath -Encoding UTF8 -ErrorAction Stop }
            } catch { $captured = $false; Write-Warning "Could not append $($stream[0]); capture retained at $($stream[1])." }
        }
        try {
            $exitText = if ($null -eq $code) { 'unavailable' } else { [string]$code }
            Add-Content -LiteralPath $LogPath -Value "Native exit code: $exitText; timed out=$timedOut" -Encoding UTF8 -ErrorAction Stop
        } catch { $captured = $false }
        $exited = $null -eq $process -or $process.HasExited
        if ($captured -and $exited) {
            # Exact per-call files only; useful evidence is removed only after successful harvesting.
            Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
        } else {
            Write-Warning "Native capture files retained: $stdout ; $stderr"
        }
        if ($null -ne $process) { $process.Dispose() }
    }
}

function Get-StreamstheticElevationFailure {
    param([Exception]$Exception)
    for ($cause = $Exception; $null -ne $cause; $cause = $cause.InnerException) {
        if ($cause -is [ComponentModel.Win32Exception] -and $cause.NativeErrorCode -eq 1223) {
            return 'Camera setup was canceled at the Windows administrator prompt. Run setup again and approve that prompt to install the cameras.'
        }
    }
    return ('Could not start the elevated camera setup. Windows reported: ' + $Exception.Message)
}
