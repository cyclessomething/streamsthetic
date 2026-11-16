# Update or uninstall

Use the [GitHub releases page](https://github.com/cyclessomething/streamsthetic/releases) to find downloads and read what changed. Keep the Windows and Android apps on matching releases where possible.

## Update Windows

1. Finish any recording or call, then stop streaming on the phone and PC.
2. Exit Streamsthetic, including its tray icon if it is running in the background. Close apps using its camera.
3. Download the new **Windows offline installer** from the release page.
4. Run it and approve camera setup if prompted.
5. Reopen Streamsthetic and reconnect the phone.

You do not need to uninstall first. The installer replaces app files while keeping saved app settings.

**Settings → Help → Check for updates** asks GitHub whether a newer version exists (beta releases included) and links to it. It does not download or install anything by itself. The phone has the same check under **Settings → Help & requirements → Updates**.

## Update Android

1. Stop streaming.
2. Download the new APK from the same release page.
3. Open it and choose to update the existing app.

From 0.2.6 on, the APK is signed with the Streamsthetic release key, and later versions install over it. Versions up to 0.2.5 used a temporary testing key: Android will not update those in place. Uninstall the old app once (this removes its settings and pairing), install the new APK, then pair again.

## Uninstall the Windows app

Open **Windows Settings → Apps → Installed apps**, find **Streamsthetic**, and select **Uninstall**.

The standard uninstall removes the app, shortcuts, the OBS microphone source and its saved settings from the default installation. It leaves the Windows camera components installed. The phone app is separate and is not removed.

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
