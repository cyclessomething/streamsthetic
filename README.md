# Streamsthetic

Use an Android phone as a camera in Windows apps such as OBS and Discord. Connect over USB tethering or your local network, with up to four independent phone sessions and camera outputs.

**Testing release: 0.2.4.** This is an early beta, not a claim of compatibility with every phone or PC. Windows 11 x64 and Android 11 or later are required. Windows on ARM, Windows 10, iPhone and macOS are not supported by this release.

## Downloads

[Release notes and all downloads — v0.2.4-beta.1](https://github.com/cyclessomething/streamsthetic/releases/tag/v0.2.4-beta.1)

| Download | Use |
| --- | --- |
| [Windows offline installer](https://github.com/cyclessomething/streamsthetic/releases/download/v0.2.4-beta.1/Streamsthetic-Setup-offline.exe) | Recommended Windows download. Includes the app runtime and USB tools. |
| [Android APK](https://github.com/cyclessomething/streamsthetic/releases/download/v0.2.4-beta.1/Streamsthetic-phone.apk) | Install on the phone. |
| [Offline package ZIP](https://github.com/cyclessomething/streamsthetic/releases/download/v0.2.4-beta.1/Streamsthetic-offline-package.zip) | Alternative bundle containing the Windows files and Android APK. |
| [Release manifest](https://github.com/cyclessomething/streamsthetic/releases/download/v0.2.4-beta.1/release-manifest.json) | File sizes and SHA-256 checksums. |

The Windows installer is unsigned; Windows may display an unknown-publisher or SmartScreen warning. The Android APK uses a development signing key and is debuggable. Both are intended for testing. Download only from a source you trust and compare hashes with the release manifest.

## Get started

1. Run the Windows offline installer. Camera installation requires administrator approval. You do not need Visual Studio, an Android SDK or a separately installed .NET runtime to use this package.
2. Install the APK on your phone, allowing installation from your browser or file manager if Android requests it. Open Streamsthetic and allow camera access. Notifications are recommended; microphone access and unrestricted battery are optional.
3. Choose a connection method on both devices:
   - **USB connection:** use a data cable and enable USB tethering on the phone. USB debugging is an optional advanced alternative.
   - **Wi-Fi:** use the same local network; the PC can use Ethernet. Scan the PC's pairing QR code or enter the pairing code manually. QR scanning uses Google Play services; manual pairing is the fallback.
4. Press **Start** on the phone. If the PC does not connect automatically, press **Start** there.
5. In OBS, add a **Video Capture Device** and select **Streamsthetic (Windows Virtual Camera)**. Other camera apps can select the same Windows camera output.

Streaming stays between the phone and PC over the cable or local network. It does not require internet access. Initial dependency downloads, the optional QR scanner component and update checks may require internet access.

## What is included

- Up to four phone sessions, each with a separate Windows camera output.
- A live PC preview, saved phone assignments and optional automatic reconnection.
- Resolution, frame-rate and bitrate controls, plus supported camera adjustments.
- Automatic orientation or manual rotation, and mirroring.
- Optional microphone audio, screen dimming, thermal adjustments and idle shutoff.
- Themes, text and control sizing, and settings organized into categories.

Start with 1080p. Higher resolutions and frame rates require more processing power and bandwidth. If the picture stutters, reduce bitrate, frame rate or resolution. Available lenses and camera controls depend on the phone.

Microphone audio plays through the output selected in the Windows app. Streamsthetic does **not** install a Windows microphone device. Sending audio into another app requires suitable Windows audio routing, such as a virtual audio cable.

## Beta limitations

- Builds, automated tests and live development-device checks have passed, but a fresh Windows 11 installation test is still pending. The isolated Sandbox runner is prepared; its environment needs activation and a restart before execution. See [testing coverage](docs/TESTING.md).
- Device coverage is limited. Four-session synthetic tests are not a claim that four physical phones sustain every resolution on every PC.
- The Android development signer is not a production signing plan. A future change of signer may require uninstalling the beta and pairing again.
- Windows camera installation requires Media Foundation. Windows editions without the required media components need those components installed first.
- Diagnostic reports may contain connection details. Review reports before sharing them publicly.

## Build and contribute

See [build instructions](docs/BUILD.md) and [testing instructions](docs/TESTING.md). Report reproducible problems through [GitHub Issues](https://github.com/cyclessomething/streamsthetic/issues), including app version, OS versions, connection method and the steps that trigger the problem. Do not post pairing codes, device serials or unreviewed diagnostic archives.

See [LICENSE](LICENSE) and [third-party notices](THIRD_PARTY_NOTICES.md) for licensing details.
