[Guide](README.md) / Help and maintenance

# Privacy and diagnostic reports

Streamsthetic sends video, and optional microphone audio, directly between your phone and PC over USB or the local network. You do not need an online account to stream.

## Choose what the phone shares

Camera access is required for video. Microphone access is optional: enable **Send the microphone to the PC** only when you want sound from the phone. Stop streaming when you are finished.

Keep your pairing code private and use a network you trust. Pairing is part of finding and connecting your devices; this guide does not promise encrypted transport.

Streaming does not require internet access. Downloading the app, obtaining the optional Google Play services QR scanner, checking a configured update feed and opening help links or GitHub drafts may use the internet.

## Open a public GitHub draft

1. Open **Settings → Help → Report a problem** on Windows, or **Settings → Help & requirements → Report a problem** on Android.
2. Describe what you were doing and what went wrong.
3. Choose **Continue on GitHub** to open a draft in [cyclessomething/streamsthetic](https://github.com/cyclessomething/streamsthetic/issues).
4. Sign in if needed, review the draft and submit it yourself. Issues are public. Streamsthetic does not submit an issue automatically.

The browser receives your description and a small set of details:

| App | Details added to the draft |
| --- | --- |
| Windows | App version, Windows version and architecture, selected problem type and time range. |
| Android | App version, Android version, phone model and connection type. |

Neither action adds raw logs, pairing codes, saved settings, addresses, serial numbers or crash dumps automatically. Check your own description for private information before continuing.

If a long description is shortened for the browser link, the full summary is saved first. Windows shows the saved draft's location. On Android, reopen **Report a problem** and use **Share saved GitHub summary**; the file stays private until you share it, and a later shortened report replaces it. Add any missing text on GitHub before submitting. If saving fails, the shortened draft does not open.

## Share diagnostics only when needed

**Save diagnostic report** on Windows creates a local ZIP under **Documents\Streamsthetic reports**. **Share diagnostic report** on Android opens sharing options for a separate text report. These are optional actions, separate from the GitHub draft.

Diagnostics may include settings, device and connection details, logs and saved crashes. Windows reports can include crash dumps and phone logs collected through an authorized USB debugging connection. Review the archive or text before attaching or sending it. Diagnostic files are not attached to GitHub automatically.

Remove pairing codes, device serials, private addresses and personal information from anything you post publicly. Saving a Windows report does not upload it. If a report email address is configured, **Save diagnostic report and open email** also opens a draft; review it and attach the ZIP yourself. On Android, choose a sharing destination where you can review the report before sending it.

## Write a useful issue

Open [GitHub Issues](https://github.com/cyclessomething/streamsthetic/issues) and include:

- The Streamsthetic version on both devices.
- Windows and Android versions, phone model and connection method.
- The steps that reproduce the problem.
- What you expected and what happened instead.

Start with that description. Attach only reviewed screenshots or logs that are needed to explain the issue.

**Related:** [Troubleshooting](troubleshooting.md) · [Android permissions](android.md) · [Microphone audio](audio.md)
