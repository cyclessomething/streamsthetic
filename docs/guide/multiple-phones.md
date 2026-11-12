[Guide](README.md) / Using the apps

# Use multiple phones

Streamsthetic supports up to four phone cameras at once. Each phone appears as a separate Windows camera, so you can add different angles in OBS or another app.

## Add phones over Wi-Fi

1. Connect the PC and phones to the same local network. The PC can use Ethernet.
2. In the PC's **Camera** tab, choose **Wi-Fi**. Pair each phone with the same PC QR code, so all phones use the same six-digit pairing code. See [Connect your phone](connect.md).
3. Press **Start** in Streamsthetic on each phone.
4. Open **Phones** on the PC and choose **Find phones**.
5. Select a phone in the results and choose **Connect selected phone**. Repeat for the other phones.

New phones fill available camera slots automatically. Selecting a camera row chooses which phone to preview and control; it does not move a phone into that slot. Assignments are remembered for later connections.

| Slot | Windows camera name |
| --- | --- |
| Camera 1 | Streamsthetic |
| Camera 2 | Streamsthetic 2 |
| Camera 3 | Streamsthetic 3 |
| Camera 4 | Streamsthetic 4 |

Windows or your receiving app may append **(Windows Virtual Camera)** to these names. Choose the corresponding device for each video source. If numbered cameras are missing, install the current Streamsthetic camera component.

## Adjust one phone

Select its row under **Your phones**. The preview and **Camera** and **Picture** controls now follow that phone. Changing the selected row leaves the other cameras running.

Extra cameras start at 1080p, 30 fps and 10 Mbps. Increase settings only if the phones, network and PC can keep up. Reduce resolution, frame rate or bitrate if video stutters or a phone gets warm. Microphone audio comes from camera 1 only.

## Stop or remove a phone

| Action | Result |
| --- | --- |
| Select a camera, then press **Stop** | Stops that PC connection and keeps its assignment. Press **Start** to resume it. |
| **Stop all cameras** | Stops all PC camera connections and keeps their assignments. |
| Select a camera, then **Disconnect and forget** | Stops that connection and frees its slot. |
| **Forget all phones** | Stops connections and removes remembered phone assignments from this PC. |

Stopping on the PC does not revoke pairing or necessarily stop the phone app. Use **Stop** on the phone when you are finished to reduce battery use.

Forgetting removes a saved entry; it does not revoke pairing. To replace the old shared code, stop the streams first. Generate a new code on a phone you want to keep, enter it on the PC and choose **Use this code**. Scan the updated PC QR code on the other phones you want to keep, then reconnect them. Changing a code alone does not disconnect an active stream.

If automatic connection is enabled, remembered phones can reconnect when they start streaming. A camera you manually stop on the PC stays stopped until you start it again.

Related: [Windows app](windows.md) · [Audio](audio.md) · [Camera controls](camera-controls.md) · [Troubleshooting](troubleshooting.md) · [Guide and downloads](../../README.md)
