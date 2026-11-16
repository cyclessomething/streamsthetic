# What works with Streamsthetic

Streamsthetic turns an Android phone into a **camera** that Windows apps can pick, and can send the phone's **microphone** to the PC. The camera works far more widely than the microphone, because Windows lets apps add cameras but not microphones.

## The camera (video)

Streamsthetic adds standard Windows cameras called **Streamsthetic (Windows Virtual Camera)**, **Streamsthetic 2**, and so on. Any Windows 11 app that lets you choose a camera can use them.

| App | Camera |
| --- | --- |
| OBS Studio | Works: add a **Video Capture Device** source. Tested. |
| Discord, Microsoft Teams, Zoom | Works: choose the Streamsthetic camera in the app's video settings. |
| Web browsers (Google Meet and other web calls) | Works: allow camera access and pick the Streamsthetic camera. |
| The Windows Camera app | Works. |

If an app was already open when you installed Streamsthetic or changed the [number of cameras](multiple-phones.md#use-more-than-four-phones), close and reopen it so it sees the cameras.

## The phone's microphone (sound)

| Where | Phone microphone |
| --- | --- |
| **OBS Studio 30 or newer** | Works with **no extra software**: add the **Streamsthetic phone microphone** source. Each camera's phone has its own sound. See [Microphone audio](audio.md). |
| **Discord, Teams, Zoom, browsers and other apps** | Not directly. Windows does not let apps create a microphone without a signed audio driver, which Streamsthetic does not install. Use a free **virtual audio cable** (such as VB-Cable): Streamsthetic plays the sound into the cable, and the app records from it. This works for camera 1's phone. See [Send audio to a calling app](audio.md#send-audio-to-a-calling-app). |
| Listening on the PC | Choose your speakers or headphones in **Settings › Audio**. |

!!! tip "Only need the sound in a call?"
    Most calling apps can use your PC's own microphone or headset while showing the phone as the camera. That needs no cable at all.

## Devices and systems

| | Supported |
| --- | --- |
| Windows 11 on an Intel or AMD PC (x64) | Yes |
| Windows 10 | No: the camera component needs Windows 11. |
| Windows on ARM (for example Snapdragon laptops) | No |
| Android 11 or newer | Yes |
| iPhone | Not yet (planned) |
| Mac | Not yet (planned) |
| Linux | Not yet (planned) |

QR-code pairing on the phone uses Google Play services. Phones without them can pair by typing the six-digit code instead.

## How many phones

Windows gets 4 Streamsthetic cameras to start with, and you can choose any number from 1 to 16. The real limit is your PC and network: see [Use more than four phones](multiple-phones.md#use-more-than-four-phones), which also explains why USB cables handle more phones than Wi-Fi.
