# Testing coverage

The beta has been built and tested on development machines and a limited set of devices. That evidence does not establish compatibility with every Windows installation, GPU, Android phone or camera application.

## Completed development checks

- Windows Release builds and Android APK assembly/lint.
- Session identity, discovery, persistence and capacity checks, including loopback UDP discovery.
- Synthetic receiver tests for independent sessions, preview publication, rotation and mirroring.
- WPF preview and session-navigation checks.
- Android orientation-debounce and saved-choice regression checks.
- Installer preflight, isolated installation behavior and embedded-payload extraction checks.
- Live development-device streaming and UI checks, including Settings navigation and recreation.

An extraction check proves that an installer contains the expected bytes. A synthetic receiver test proves behavior for its supplied input. Neither is a substitute for running the installed camera on a fresh PC.

## Checks that still need completion

- Installation on a fresh Windows 11 x64 environment. A Windows Sandbox runner is prepared, but the environment requires activation and a restart before that test can run.
- Independent Media Foundation camera enumeration and frame delivery in that clean environment.
- Physical phone streaming, USB behavior and GPU compatibility on additional PCs and phones.
- Upgrade and full removal on a clean installation, including camera removal.

Sandbox is useful for installation isolation but cannot prove physical USB, phone, GPU or camera-frame behavior by itself. If the guest lacks Media Foundation, the runner reports an unsupported environment rather than a product failure or a pass.

## Rerun source-only checks

Install the build prerequisites in [BUILD.md](BUILD.md), then run from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File android/tests/run-behavior-checks.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/session_selftest.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/installer_selftest.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/clean_windows_test_selftest.ps1
```

The Sandbox preparation self-test uses inert fixtures. It checks the configuration and host-execution guard; it does not start Sandbox or run the real installer. The installer self-test uses temporary test locations and is not a full system camera-installation test.

Some integration scripts require a built receiver, Python packages, FFmpeg, unused receiver slots or a PCM1 recording. Private recordings are intentionally excluded from this repository. Read each script's header and arguments before running it; supply your own compatible recording where required. Device and camera scripts may interact with an installed camera or connected phone and are not part of the source-only list above.

## Prepare the clean Windows test

After building an offline release, supply its installer and the matching app from its `offline-package/Streamsthetic-PC` directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/prepare_clean_windows_test.ps1 `
  -SetupPath <offline-setup-exe> `
  -ExpectedAppPath <matching-staged-Streamsthetic.exe> `
  -OutputRoot <new-dedicated-test-directory>
```

The angle-bracket arguments are placeholders. Preparation creates a `.wsb` configuration but does not enable Windows features, restart Windows, launch Sandbox or install the app. Only a minimal read-only input directory and a dedicated writable evidence directory are mapped. Network, clipboard, microphone/video input and printer redirection are disabled.

Once Sandbox is available, read the generated instructions before opening the configuration. The guest runner uses the actual packaged installer, checks installed identity and bundled ADB, and renders the installed app. Its JSON result records individual outcomes and explicitly leaves untested camera and hardware behavior pending. Review the captured image as well as the JSON; a screenshot file alone does not prove a usable interface.

Keep raw logs and captures private until reviewed for connection details, pairing codes and identifying information.
