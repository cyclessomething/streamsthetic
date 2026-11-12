[Guide](README.md) / Picture and sound

# Adjust the camera picture

This guide covers Streamsthetic 0.2.4 beta. Camera capabilities vary by phone; a listed lens or setting is not a guarantee that every device supports it.

## Choose the phone to adjust

1. Connect and start the phone.
2. If several phones are connected, select the intended phone in the Windows **Phones** tab.
3. Use Windows **Camera** for framing and stream settings, or **Picture** for exposure, focus and color.

The Android **Camera** tab has the main stream settings, and **Rotate** has orientation controls. Additional picture controls are on the PC. Live changes affect the selected phone's stream, including the video other apps receive. Lens, resolution, frame-rate or stabilization changes may briefly interrupt the picture.

## Frame the shot

| Control | Where | How to use it |
| --- | --- | --- |
| **Lens** | Android Camera; Windows Camera | Main 1x is a good starting point. Wide 0.6x fits more into the shot; Tele 3x and Tele 5x magnify distant subjects; Front uses the selfie camera. Actual lens switching depends on the phone. |
| **Zoom** | Windows Camera | Drag to frame the shot more precisely. The available range comes from the phone. Zoom beyond optical capability is digital and can soften detail. |

## Balance detail and smoothness

| Control | Choices | Starting point |
| --- | --- | --- |
| **Resolution** | 720p, 1080p, 4K | Start with 1080p. 4K has four times as many pixels and needs more processing power and bandwidth. Try 720p when resources are limited. |
| **Frame rate** / **Frame rate · fps** | 30 or 60 | 60 gives smoother motion; 30 reduces processing load and can help in low light. |
| **Bitrate** / **Bitrate · Mbps** | 10, 20, 40 or 60 Mbps | Start around 20 Mbps for 1080p or 40–60 Mbps for 4K. Higher bitrate preserves more detail but uses more bandwidth. |

These controls are available on both devices; Windows includes the units in the label. If video stutters, lower bitrate, frame rate or resolution. The receiving app can also limit its own resolution and frame rate. Phone heat adjustments or the PC's automatic frame-rate setting may lower the stream settings while running.

Windows **Reset phone camera settings** returns to Main 1x, 1080p, 60 fps, 20 Mbps and Auto-rotate.

## Set exposure, focus and color

Open Windows **Picture** while connected.

| Control | What it changes |
| --- | --- |
| **Brightness** | Exposure compensation relative to automatic exposure. Raise it for a dark subject; lower it for washed-out highlights. **0 EV** leaves automatic exposure unchanged. The phone supplies the available range. This is not a manual shutter-speed control. |
| **Focus** | **Auto** follows the scene. For a fixed position, let the picture become sharp, then choose **Locked** to hold the focus distance. If the phone cannot provide a focus distance, it stays on Auto. |
| **White balance** | Choose Auto, Daylight, Cloudy, Tungsten or Fluorescent. Use a matching preset if the picture looks too orange or blue, or if you want fewer color shifts. |
| **Anti-flicker** | Start with Auto. Try 50 Hz or 60 Hz to match local mains-powered lighting if bands remain; Off disables correction. Some LED lights can still flicker. |
| **Stabilization** | On can reduce camera shake, but may crop the picture or add delay. Leave it Off on a stable stand. |

The **Reset picture settings** control restores brightness to 0, automatic focus, automatic white balance, automatic anti-flicker, stabilization Off and Mirror Off. It does not reset orientation.

## Rotate or mirror the picture

**Follow the phone automatically**

1. Open Android **Rotate** or the orientation controls in Windows **Camera**.
2. Turn on **Auto-rotate**.
3. Hold the phone steady in the new orientation for about a second. Flat or diagonal positions do not trigger a turn.

**Keep a fixed orientation**

1. Turn off **Auto-rotate**. The picture stays at its current orientation.
2. Use **Left** or **Right** to rotate by 90°.

The rotated picture fits the receiving app's frame without cropping; black bars may appear.

For a horizontal flip, use **Mirror → On** in Windows **Picture**. This changes the outgoing video, not just the local preview, so text appears backwards. Calling apps may also mirror their own preview—check what viewers actually receive before applying a second flip.

## Related guides

[Overview](../../README.md) · [Connect](connect.md) · [Android app](android.md) · [Windows app](windows.md) · [Audio](audio.md) · [Troubleshooting](troubleshooting.md)
