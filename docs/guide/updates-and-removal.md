[Guide](README.md) / Help and maintenance

# Update or uninstall

Use the [GitHub releases page](https://github.com/cyclessomething/streamsthetic/releases) to find downloads and read what changed. Keep the Windows and Android apps on matching releases where possible.

## Update Windows

1. Finish any recording or call, then stop streaming on the phone and PC.
2. Exit Streamsthetic, including its tray icon if it is running in the background. Close apps using its camera.
3. Download the new **Windows offline installer** from the release page.
4. Run it and approve camera setup if prompted.
5. Reopen Streamsthetic and reconnect the phone.

You do not need to uninstall first. The installer replaces app files while keeping saved app settings.

**Settings → Help → Check for updates** checks a configured release feed. The current beta has no update feed configured, so use GitHub to check for downloads. This control does not install updates automatically.

## Update Android

1. Stop streaming.
2. Download the new APK from the same release page.
3. Open it and choose to update the existing app.

An in-place update requires a compatible signing key and version. The current APK uses a development signing key. If a future release changes that key, Android may require uninstalling the beta first; that removes saved phone settings and pairing. Follow that release's instructions rather than deleting the app immediately if an update fails.

## Uninstall the Windows app

Open **Windows Settings → Apps → Installed apps**, find **Streamsthetic**, and select **Uninstall**.

The standard uninstall removes the app, shortcuts and its saved settings from the default installation. It leaves the Windows camera components installed. The phone app is separate and is not removed.

## Remove the Windows cameras too

Use this option **before** removing the Windows app. It removes the app and all four Streamsthetic cameras, and needs administrator approval. At the default installation location, it also removes saved PC settings.

1. Close Streamsthetic and any apps using its cameras.
2. Open PowerShell and run the command below for the default installation.
3. Approve the administrator prompt and let removal finish.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\Streamsthetic\uninstall.ps1" -RemoveCamera
```

To keep saved PC settings for a later reinstall, add **`-KeepSettings`** to that command. If you already removed the app, reinstall it first so its removal tools are available. For a custom installation, use `uninstall.ps1` from that installation folder; its saved settings are retained. Previously exported diagnostic reports are not removed.

## Uninstall Android

Use Android's normal **Uninstall** action for Streamsthetic. This removes its phone settings and saved pairing. It does not uninstall the PC app or Windows cameras.

**Related:** [Install Streamsthetic](install.md) · [Troubleshooting](troubleshooting.md)
