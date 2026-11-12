# Streamsthetic 0.2.5 testing release

**Report a problem** now opens a pre-filled GitHub issue draft in [cyclessomething/streamsthetic](https://github.com/cyclessomething/streamsthetic/issues). Windows and Android users describe the problem, choose **Continue on GitHub**, then sign in, review and submit the issue. The app clearly states that opening a draft has not submitted a report.

Only the description and listed app/system details are included. Raw logs, settings, pairing codes and crash dumps are not attached automatically. Windows can separately save a diagnostic ZIP; Android can separately share a diagnostic report. Long descriptions are preserved locally before a shortened browser link is opened.

Camera setup also preserves the actual Windows error, failing stage and native exit code. It distinguishes a canceled administrator prompt from a later registration failure.

## Verification

- Windows Release build and Android APK assembly/lint completed.
- All 12 Windows report-helper checks and all 30 Android behavior checks passed.
- Installer fixture tests passed, including native-error and timeout-output preservation.
- The actual installed Windows app opened a GitHub draft with test text. No test issue was submitted. Further Firefox inspection was unavailable through the automation tool.
- APK signer continuity and package hashes/extraction were checked. The Android reporting flow has not yet been checked on a physical phone.
- The published offline installer completed on a fresh Windows 11 x64 Hyper-V VM without a preinstalled .NET SDK or runtime. The app rendered and all four virtual cameras delivered changing synthetic 720p frames. See the [clean VM report](testing/2026-11-12-windows-11-vm.md).

## Remaining release limits

This remains a testing prerelease. Windows executables are unsigned and the APK is development-signed and debuggable.

Physical phone streaming, USB behavior, GPU compatibility, upgrade and complete camera removal were outside the clean VM run. See [testing coverage](TESTING.md).

[User guide](guide/README.md) · [Downloads](https://github.com/cyclessomething/streamsthetic/releases/tag/v0.2.5-beta.1)
