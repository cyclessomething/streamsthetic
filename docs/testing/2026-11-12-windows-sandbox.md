# Windows Sandbox test — 12 November 2026

**Result: partial runtime evidence; camera installation failed in both configurations.** These runs do not establish that Streamsthetic installs and streams successfully on a fresh Windows PC.

## Environment and package

- Fresh Windows Sandbox, Windows 11 build **26100.9974**, native x64.
- No .NET SDK, .NET Core runtime or Desktop Runtime found in the standard x64 installation directories; `dotnet.exe` was absent from PATH.
- Required Media Foundation DLLs were present.
- The first run disabled network, clipboard, microphone, video-input and printer redirection. A second fresh Sandbox enabled only video input, with the owner's approval. Only dedicated test input and evidence folders were mapped.
- Tested the published **v0.2.4-beta.1** offline installer, app version **0.2.4**.
- Installer SHA-256: `F7CEE82B20046F6999C4C31FF1BA484BF7F4F8EE7149BB65832472A121111EF3`.

Both fresh runs used the same real installer with camera setup enabled and no test bypass switches. Both disposable guests were closed after evidence collection.

## Observations

| Check | Result |
| --- | --- |
| No standard .NET SDK/Core/Desktop installation | Confirmed by the guest baseline inventory and PATH check. |
| Offline installer | Failed with exit code 1 during virtual camera registration in both runs. The installer displayed the failure. |
| Follow-up camera registration | Returned `0x80070005` (access denied) from an elevated process. |
| Camera-access probe | Windows reported `DeniedBySystem`. Media Foundation startup and virtual-camera object creation succeeded. |
| Installed app identity | SHA-256 and product version matched the expected packaged app. |
| App interface | Rendered successfully in a separate follow-up using the files left by setup. The captured interface was visually reviewed. |
| Bundled USB tool | Printed its ADB version information in the guest. No phone was attached. |
| Camera enumeration | The video-input-enabled guest exposed redirected host cameras and later local names. Neither proves successful guest camera startup. |
| Video frames | Not verified in either guest. Redirected host cameras were excluded from acceptance. |
| Physical phone, USB, Wi-Fi and microphone | Not tested in this guest. |
| Upgrade and complete removal | Not tested in this guest. |

The follow-up process objects did not retain their numeric exit codes in the diagnostic JSON. The interface and USB-tool observations are based on the actual rendered image and command output, not an assumed zero exit code.

## Camera-access investigation

The follow-up ran with administrator privileges. The relevant user and machine webcam consent values were `Allow`, but the Windows camera-access API reported **`DeniedBySystem`**.

A create-only probe first returned success from **MFStartup** and **MFCreateVirtualCamera**, including a non-null camera object. A later stage-by-stage probe in the video-input-enabled guest identified the failing call: **IMFVirtualCamera.Start returned `0x80070005` for each of the four camera slots**. Object creation succeeded for all four.

The elevated worker, all four COM registrations and caller-side COM activation were verified. The camera services were running during a repeat of the failing Start calls. These checks rule out a canceled administrator prompt, missing COM registration and merely stopped services as explanations for that run; they do not prove every service-side permission is correct.

Microsoft documents camera privacy controls for the virtual-camera API. Sandbox documentation warns that disabling video input can prevent camera applications from working. Enabling video input did **not** resolve this failure: the camera-access API still reported `DeniedBySystem`. The evidence is consistent with a restriction in this guest, but does not establish that every Sandbox is unsupported or that a full Windows installation will succeed. See [virtual-camera API permissions](https://learn.microsoft.com/en-us/windows/win32/api/mfvirtualcamera/nf-mfvirtualcamera-mfcreatevirtualcamera) and [Sandbox video-input configuration](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file#video-input).

## Installer diagnostics correction in 0.2.5

The old camera worker lost native error output and could incorrectly suggest that administrator approval had been canceled. The updated worker records elevation, the failing stage, native output and exit code, and preserves output after a timeout.

The packaged 0.2.5 diagnostic scripts were tested against the already-installed receiver in the second guest. They preserved `0x80070005`, the virtual-camera-registration stage and exit code 1 in both the camera log and outer output, with `elevated=True`. This verifies the error-reporting correction; it is **not** a fresh installation pass for 0.2.5.

## What the image proves

The installed app files can render their interface with the bundled runtime in this clean guest. The image shows a stopped, disconnected app after camera installation failed; it is not evidence of video delivery.

![Streamsthetic interface rendered in the clean guest, stopped and disconnected](images/sandbox-app.png)

## Remaining acceptance

Run the installation in a full Windows 11 VM or separate clean PC to distinguish the Sandbox result from ordinary Windows behavior. Then verify local camera enumeration and real frame delivery, followed by physical-phone connections, upgrade and full removal. Keep this release marked as a testing beta until those checks are complete.

[Testing coverage](../TESTING.md) · [User guide](../guide/README.md) · [Downloads](https://github.com/cyclessomething/streamsthetic/releases)
