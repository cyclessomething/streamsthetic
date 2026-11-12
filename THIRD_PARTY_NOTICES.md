# Third-party software in Streamsthetic

- **VCamSample** (virtual camera media source, `windows/vcam`) - MIT License, Copyright (c) 2024-2026 Simon Mourier. Full text: `windows/vcam/LICENSE`.
- **Android platform-tools / adb** (shipped as `adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll`, `libwinpthread-1.dll`) - Apache License 2.0 and other licenses; the notices are in `adb-NOTICE.txt` next to the program.
- **Windows Implementation Library (WIL)** (NuGet package Microsoft.Windows.ImplementationLibrary, used by the receiver) - MIT License, Copyright (c) Microsoft Corporation.
- **Fonts** (Anton, Archivo, Chakra Petch, Doto, Michroma, IBM Plex Mono, Poppins; bundled in both apps) - SIL Open Font License 1.1 (each font's copyright belongs to its designers).
- **.NET / Windows Desktop Runtime** are included in the offline Windows installer and offline package. Their license and third-party notices are included under `runtime-notices/` in those packages. The smaller online installer uses a separately installed .NET Desktop Runtime. **Windows Media Foundation** is supplied by Windows and is not redistributed here.
- **QRCoder** (NuGet package, draws the pairing QR code in the PC app) - MIT License, Copyright (c) 2013-2026 Raffael Herrmann.
- **Google code scanner (ML Kit)** (`com.google.android.gms:play-services-code-scanner`, scans the pairing QR code in the phone app) - provided under the Google Play services terms; not redistributed in source form.
