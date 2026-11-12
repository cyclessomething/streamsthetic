[Guide](README.md) / Using the apps

# Use the Android app

Streamsthetic 0.2.4 beta turns an Android 11+ phone into a camera for your Windows PC. Start with [Connect your phone](connect.md) if you have not paired it yet.

## Find your way around

| Tab | What you can do |
| --- | --- |
| **Stream** | Choose USB connection, Wi-Fi or advanced USB debugging; open setup or pairing instructions; start and stop streaming; check status and heat warnings. |
| **Camera** | Choose Lens, Resolution, Frame rate and Bitrate. |
| **Rotate** | Enable Auto-rotate or turn the picture manually with Left and Right. Start streaming to use these controls. |
| **Settings** | Open Appearance, Streaming & power, Permissions or Help & requirements. |

Tap the **i** beside a setting for an explanation. Inside a Settings category, use **Back to Settings** or Android's Back action to return to the overview. Your open category and scroll position are preserved when appearance or size changes recreate the screen.

## Start or stop a stream

1. Open **Stream** and choose your connection method.
2. Follow the setup instructions. For Wi-Fi, scan the PC's QR code or use **Show pairing code (if you cannot scan)**.
3. Tap **Start**. If the PC does not connect automatically, press **Start** there.
4. Tap **Stop** when finished. The streaming notification also has a Stop action when notifications are available.

Use **Change connection** to choose another method. For picture adjustments, see [Camera controls](camera-controls.md).

## Appearance

Open **Settings → Appearance**. These settings change the phone interface, not the video sent to your PC.

| Setting | Choices or behavior |
| --- | --- |
| **Theme** | Studio, Creator, Gamer, Brutal, Dot, Prism or Contrast. |
| **Appearance** | System, Light or Dark on Android 12+. Android 11 follows the phone's setting; Gamer always uses dark appearance. |
| **Text size** | Normal, Large, Larger or Largest, in addition to the phone's own text scaling. |
| **Icon size** | Normal, Large or Larger. Also increases control size and spacing. |

Changing these settings redraws the interface; the streaming service continues running.

## Streaming & power

Open **Settings → Streaming & power**.

| Setting | What it does |
| --- | --- |
| **Send the microphone to the PC** | Sends microphone audio while streaming. Off by default; requires microphone permission. Stop and restart the stream after changing it. |
| **Dim the screen while streaming** | Reduces screen brightness while the camera keeps running. |
| **Dim level** | Sets brightness from 2–100% when dimming is enabled. Drag to preview; choose a level you can comfortably read. |
| **Cool down automatically** | Reduces bitrate, resolution or frame rate when Android reports elevated temperature. Restores the chosen settings after two minutes at a lower thermal level. Android may still limit performance independently. |
| **Auto-off** | Stops an idle stream after 10 minutes, 30 minutes or one hour. Default: 30 minutes. Choose Off to disable it. |

The idle timer applies when no PC is connected or the PC reports that no app is using the Streamsthetic camera. If the phone becomes very hot, stop streaming and let it cool.

Microphone audio needs an output selected on the PC. It does not create a Windows microphone device—see [Audio](audio.md) for listening and routing it into another app.

## Permissions

Open **Settings → Permissions** and tap **Allow** beside the permission you need. A granted permission shows **Allowed**; Android may open its app settings if you previously declined access.

| Permission | When it is needed |
| --- | --- |
| **Camera** | Required to stream video. |
| **Notifications** | Recommended for streaming status and the Stop action. The permission prompt applies on Android 13+. |
| **Microphone** | Optional; needed only to send microphone audio. |
| **Unrestricted battery** | Optional; helps prevent battery restrictions from interrupting long streams. |

## Help & requirements

Open **Settings → Help & requirements** to review device and connection requirements or choose **Report a problem**. Add a description, then choose **Share report** to open Android's sharing options.

Reports include recent activity, settings, phone information and any saved crash. They may contain connection details, so review them before sharing. They do not include recorded video, audio or photos.

## Related guides

[Overview](../../README.md) · [Connect](connect.md) · [Windows app](windows.md) · [Camera controls](camera-controls.md) · [Audio](audio.md) · [Troubleshooting](troubleshooting.md)
