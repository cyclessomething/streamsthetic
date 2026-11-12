STREAMSTHETIC - use your Android phone as a Windows camera
========================================================
PRIVATE TESTING RELEASE
This offline package includes the Windows app, its .NET runtime, USB tools and
the Android installer. Windows media components may need to be installed separately.

IN THIS PACKAGE
  Streamsthetic-PC\           Windows app and installer
  Streamsthetic-phone.apk     Android app
  READ ME FIRST.txt           Setup instructions
  Collect problem info.cmd   Diagnostic report tool

REQUIREMENTS
  - An x64 Windows 11 PC with an Intel or AMD processor. Windows on ARM and Windows 10
    are not supported by this camera component.
    Windows N editions need Microsoft's Media Feature Pack.
  - An Android 11 or newer phone. Google Play services are needed only for QR scanning;
    you can enter the pairing code manually instead.
  - USB: a cable that carries data. Charge-only cables do not carry video.
  - Wi-Fi: phone and PC on the same local network. The PC can connect by Ethernet.

1. INSTALL ON WINDOWS
  For the simplest Windows-only download, use Streamsthetic-Setup-offline.exe.
  It includes the .NET runtime and installs the PC app with a double-click.
  The ZIP package below is an alternative that also includes the Android APK.
  1. Extract the package (right-click the ZIP, then Extract All).
  2. Open Streamsthetic-PC and double-click Install Streamsthetic.cmd.
  3. This testing build is unsigned; no paid signing certificate is used.
     If Windows shows "Windows protected your PC", select More info, then Run anyway
     for a copy you trust.
  4. Approve the Windows administrator prompt to install the camera component.
  5. Open Streamsthetic from the desktop or Start Menu. The setup guide opens on first launch.

2. INSTALL ON ANDROID
  1. Copy Streamsthetic-phone.apk to the phone and open it.
  2. If Android blocks installation from that source, follow its Settings prompt to allow
     the app you are using (such as Files) to install it, then open the APK again.
  3. Open Streamsthetic and allow Camera. Notifications are recommended; Microphone is optional.
     You can review permissions in Settings > Permissions.

3. CONNECT YOUR PHONE
  USB connection
    1. Connect the phone to the PC with a data cable.
    2. On the phone, choose USB connection, then Show the setup guide.
       Follow the guide to enable USB tethering. Settings names vary by phone.
    3. On the PC, choose USB connection.
    4. Press Start on the phone. If the PC is not already waiting, press Start there too.
       This connection does not require a pairing code.

  Wi-Fi
    1. Connect both devices to the same local network. The PC can use Ethernet.
    2. On the PC, choose Wi-Fi to display its QR code.
    3. On the phone, choose Wi-Fi, then Scan QR code from the PC.
    4. If scanning is unavailable, choose Show pairing code on the phone and enter that
       six-digit code in the PC app's Pairing code field.
    5. Press Start on the phone. If the PC is not already waiting, press Start there too.

  USB debugging (advanced)
    This is an alternative to USB tethering. Choose USB debugging on the phone and
    follow Show the setup guide. On the PC, expand Advanced and choose USB debugging.

4. USE THE CAMERA
  In OBS, Discord or another camera app, select:
      Streamsthetic (Windows Virtual Camera)

  To stop a connection, press Stop on the phone or PC.
  Closing the PC window leaves Streamsthetic running when
  "Keep running in the tray when I close the window" is enabled.

  Multiple phones (up to four)
    Scan the same PC QR code on each phone, then press Start on each phone.
    In the PC app, open Phones > Find phones. Select a phone, then Connect selected phone.
    Repeat for additional phones. Select a camera row to preview it or change its settings.

    Each phone has its own Windows camera: Streamsthetic, Streamsthetic 2, Streamsthetic 3
    or Streamsthetic 4. Windows adds "(Windows Virtual Camera)" to these names.
    Add each camera as a separate OBS video source. Stop all cameras stops every connection.

    Start with 1080p at 30 fps on additional phones. If video stutters, lower the bitrate,
    frame rate or resolution. Performance depends on the phones, PC and local network.

TROUBLESHOOTING
  In the PC app, Report a problem saves a diagnostic report for you to review and share.

  If the app cannot open, run Collect problem info.cmd from this package.
  This tool starts and closes a test copy of the PC app, runs the video component,
  and performs local network connection tests. It can briefly start a phone connection.
  Finish any active camera session before running it.

  The tool saves Streamsthetic-problem-report.txt on the Desktop. It includes device and
  connection details, PC/user names, settings and recent logs. It does not upload the report.
  Review the file before sharing it with the person helping you.

UNINSTALL
  Remove the PC app through Windows Settings > Apps > Installed apps > Streamsthetic > Uninstall.
  The Windows camera devices remain installed by default. To remove them too, run
  uninstall.ps1 -RemoveCamera from the installed app folder before removing the app;
  Windows asks for administrator approval.
  Remove the Android app separately on the phone.

Streamsthetic is free software under the MIT license. Third-party notices are in Streamsthetic-PC.
