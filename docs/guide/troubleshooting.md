[Guide](README.md) / Help and maintenance

# Troubleshooting

Find your symptom below, then follow the steps in order. For download requirements and current beta limitations, see [installation](install.md) and [testing coverage](../TESTING.md).

## Setup or the PC app will not open

1. Confirm Windows 11 x64 on an Intel or AMD PC. Windows 10 and Windows on ARM are unsupported.
2. Use **Streamsthetic-Setup-offline.exe** to include the .NET runtime. The standard installer needs the x64 .NET 10 Desktop Runtime or a working runtime download through Windows App Installer.
3. On Windows N, install **Media Feature Pack** through Windows Optional features, restart Windows and retry setup.
4. Read the setup error and its log location. Approve camera installation's administrator prompt; canceling it leaves camera setup incomplete.

Only proceed past an unsigned-app warning for a download you trust. Do not install developer tools to solve an app-runtime problem.

## The phone does not connect

1. Confirm both apps use the same connection method and the phone has camera permission.
2. For USB connection, unlock the phone, check the data cable and enable USB tethering. Toggle tethering off and on if needed.
3. For Wi-Fi, use the same local network and matching pairing code. The PC may use Ethernet. Guest-network isolation, a VPN or firewall rules can prevent local discovery; check the relevant network settings rather than disabling security wholesale.
4. Press **Start** on the phone and PC. For a selected additional camera, use **Phones → Find phones**, select the phone and choose **Connect selected phone**.
5. For USB debugging, approve the phone's authorization prompt. Use **Retry USB authorization** when offered on the PC.

See [connection steps](connect.md) and [multiple phones](multiple-phones.md). Google Play services are needed for QR scanning, not manual-code pairing.

## Camera missing or picture black in another app

1. Complete camera installation, then close and reopen the video app.
2. Select the matching Streamsthetic camera output and verify the phone is streaming.
3. Check Windows **Settings → Privacy & security → Camera**, including access for desktop apps.
4. In OBS, return to a supported resolution/frame rate or **Device Default**. See [video-app setup](obs-and-video-apps.md).

If the PC says its preview is paused because another app uses the camera, check that app's picture first. **Show preview anyway** restores the PC preview.

## Video stutters, heats the phone or looks wrong

1. Start with 1080p at 30 fps; lower bitrate, resolution or frame rate if needed.
2. Try USB tethering or improve the local Wi-Fi connection. Close unnecessary processing-heavy apps.
3. Check [Android streaming and power settings](android.md): thermal adjustments and idle shutoff can change or stop streaming.
4. For rotation, mirroring or cropping questions, check [camera controls](camera-controls.md). For missing microphone audio, follow [audio setup](audio.md).

Higher settings are not guaranteed on every phone, PC or connection. Decoding and microphone-output changes in [Windows settings](windows.md) may require stopping and starting the connection.

## Save a problem report

1. On the PC, open **Settings → Help → Report a problem**, describe the problem and select the relevant time window.
2. Choose **Save report**. The ZIP is saved under **Documents\Streamsthetic reports**. If a report email address is configured, the button instead saves the report and opens an email draft; attach the ZIP yourself.
3. On Android, use **Settings → Help & requirements → Report a problem** to prepare a report for sharing.
4. Review reports before sharing. They can contain settings, device and connection details, recent logs and crash information. Nothing is sent automatically by the PC report dialog.

If the PC app cannot open, the offline ZIP includes **Collect problem info.cmd**. Finish active camera sessions first: it starts and closes a test app process, runs the video component and performs local network tests. It saves **Streamsthetic-problem-report.txt** on the Desktop. Review PC/user names, device/network details and logs before sharing.

Include the app version, Windows/Android versions, connection method and exact steps to reproduce. Do not post pairing codes, device serials or unreviewed archives publicly.
