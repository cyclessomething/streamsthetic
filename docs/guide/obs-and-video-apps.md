[Guide](README.md) / Getting started

# Use Streamsthetic in video apps

First [connect the phone](connect.md) and confirm that Streamsthetic receives video.

## Add the camera in OBS

1. Select the OBS scene you want to use.
2. In **Sources**, select **+ → Video Capture Device** and create a source.
3. In **Device**, choose **Streamsthetic (Windows Virtual Camera)**.
4. Start with **Resolution/FPS Type: Device Default**. If needed, choose **Custom** and select a resolution and frame rate offered by the device. Start with 1920×1080 at 30 fps.
5. Select **OK** and check the picture before recording or broadcasting.

OBS documents these controls in its [Video Capture Sources guide](https://obsproject.com/kb/video-capture-sources/). Unsupported resolution or frame-rate combinations can produce no picture. For delay in OBS, try **Buffering: Disable** in source properties; restore the previous setting if playback becomes less smooth.

## Use another video app

1. Open the app's camera or video settings.
2. Select **Streamsthetic (Windows Virtual Camera)**.
3. Check its preview or make a test call. If the camera was installed while the app was open, close and reopen that app.

Some calling apps mirror their own preview. Check the picture received by another participant before changing Streamsthetic's mirror control. See [camera controls](camera-controls.md).

## Add more phones or audio

1. Connect additional phones using the [multiple-phone guide](multiple-phones.md).
2. Add a separate OBS Video Capture Device source for each output: **Streamsthetic 2**, **Streamsthetic 3** or **Streamsthetic 4**, with the Windows Virtual Camera suffix.
3. Configure microphone audio separately using the [audio guide](audio.md). Selecting the camera does not create a Windows microphone input.

The PC preview may pause while another app uses the camera. **Show preview anyway** resumes it; a paused preview alone does not mean the camera output has stopped.

See [troubleshooting](troubleshooting.md), [installation](install.md), [Windows settings](windows.md), [Android settings](android.md), or the [README](../../README.md).
