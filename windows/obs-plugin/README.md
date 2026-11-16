# Streamsthetic phone microphone (OBS plugin)

Adds the audio source **Streamsthetic phone microphone** to OBS Studio 30 or newer. It plays the sound from a phone's
microphone in OBS directly: no virtual audio cable, no driver.

## Use

1. In the phone app, turn on **Settings > Streaming & power > Send the microphone to the PC**.
2. In OBS, choose **Sources > + > Streamsthetic phone microphone**, then pick the camera whose phone you want (as many as Streamsthetic has, up to 16).
3. Sound arrives while that phone streams.

The Streamsthetic installer adds the plugin when OBS is installed. If OBS is installed later, open Streamsthetic,
**Settings > Audio > OBS audio source > Add to OBS**. Either way it is copied to
`C:\ProgramData\obs-studio\plugins\streamsthetic-mic\bin\64bit\` and OBS loads it the next time it starts.

## How it works

`StreamstheticReceiver.exe` writes each phone's audio (48 kHz, mono, 16-bit) to a shared-memory ring named
`Local\StreamstheticAudio<camera>` (layout in `shared_audio.h`). The plugin reads the ring of the chosen camera and
hands the samples to OBS. A slow reader skips ahead; it never delays the video.

## Build and test

```powershell
windows\obs-plugin\build.ps1            # fetches the libobs 30.0.0 headers once, builds x64\Release\streamsthetic-mic.dll
windows\obs-plugin\test\selftest.ps1    # real receiver + real plugin + fake obs.dll: format, sample count, no gaps, camera isolation
```

## License

The plugin's own source (this folder) is MIT, like the rest of Streamsthetic. The built `streamsthetic-mic.dll` links
to OBS's libobs, which is GPL-2.0-or-later, so that DLL is distributed under **GPL-2.0-or-later**; its complete source
is this folder. libobs headers: https://github.com/obsproject/obs-studio (GPL-2.0-or-later).
