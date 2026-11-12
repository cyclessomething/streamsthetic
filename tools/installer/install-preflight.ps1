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
    if ($ExitCode -ne 0) { throw 'Streamsthetic camera setup did not finish. Approve the Windows administrator prompt and run setup again. App files may already be installed. Camera setup details are in %TEMP%\streamsthetic-install-camera.txt when available.' }
}
