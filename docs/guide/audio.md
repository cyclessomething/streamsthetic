[Guide](README.md) / Picture and sound

# Use the phone microphone

Microphone audio is optional. Streamsthetic sends audio from **camera 1's phone** to a Windows playback output. It does not install a virtual microphone, and cameras 2–4 do not provide separate audio feeds.

## Listen on the PC

1. In the Android app, open **Settings > Streaming & power** and enable **Send the microphone to the PC**. Allow microphone access when asked.
2. On the PC, open **Settings > Audio**.
3. Set **Phone microphone** to **Default speakers or headphones**, or select a named playback device.
4. Stop and restart the phone stream and its PC connection so both use the new audio settings.

Use headphones if the phone is near the PC to avoid feedback. Audio adds about 1 Mbps to the connection.

## Send audio to OBS or a calling app

The Windows camera device carries video. Selecting it in another app does not automatically select the phone as a microphone.

To make the phone audio available as a microphone input, install a separate virtual audio cable. Follow the provider's installation and restart instructions. With [VB-CABLE](https://vb-audio.com/Cable/), for example:

1. In Streamsthetic's **Settings > Audio > Phone microphone**, select **CABLE Input** (the cable's playback device).
2. Restart the PC camera connection.
3. In OBS or your calling app, select **CABLE Output** (the cable's recording device) as the audio input.
4. Check that the receiving app's audio meter responds when you speak.

The input/output naming is from the virtual cable: Streamsthetic plays into its input, and the receiving app records its output. Device names can vary. The cable is a separate installation; Streamsthetic does not bundle it or an OBS audio plugin.

## Turn audio off

Set **Phone microphone** to **Off** on the PC and restart the connection. To stop sending microphone audio from the phone too, disable **Send the microphone to the PC** in its settings and restart its stream.

If audio is silent, check microphone permission on the phone, that the phone is assigned to camera 1, the selected PC output and the receiving app's input. Restart after changing audio settings.

Related: [Android app](android.md) · [Windows app](windows.md) · [Multiple phones](multiple-phones.md) · [Connect your phone](connect.md) · [Troubleshooting](troubleshooting.md) · [Guide and downloads](../../README.md)
