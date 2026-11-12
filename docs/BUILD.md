# Building Streamsthetic

These instructions are for development. To use the app, download the Windows offline installer and Android APK from the [beta release](https://github.com/cyclessomething/streamsthetic/releases/tag/v0.2.4-beta.1).

## Tools

- Windows 11 x64.
- Visual Studio 2022 or Build Tools with Desktop development with C++, the v143 toolset and a Windows SDK compatible with the projects.
- .NET 10 SDK for the WPF application.
- JDK 17.
- Android SDK Platform 35, compatible Android build tools and platform-tools. Set `ANDROID_HOME` to your SDK directory.
- Internet access for the first Gradle, NuGet and SDK dependency downloads.

The repository contains the Gradle wrapper; a separate Gradle installation is not required. It does not contain SDKs, generated build output, private signing keys or private recording fixtures.

## Build components

Run the Windows commands from a Visual Studio developer PowerShell with MSBuild on PATH, at the repository root.

```powershell
# Android development APK
Push-Location android
.\gradlew.bat assembleDebug lintDebug
Pop-Location

# Windows virtual camera and its NuGet dependencies
msbuild windows/vcam/VCamSample.sln -t:Restore -p:RestorePackagesConfig=true
msbuild windows/vcam/VCamSample.sln -p:Configuration=Release -p:Platform=x64 -p:PlatformToolset=v143

# Native receiver
msbuild windows/receiver/PhoneCamReceiver.vcxproj -p:Configuration=Release -p:Platform=x64

# Windows application
dotnet build windows/app/PhoneCam.csproj -c Release
```

The Android APK is written to `android/app/build/outputs/apk/debug/app-debug.apk`. Building components does not register cameras or install the applications. Use the packaged installer for a complete installation; a WPF build alone is not the complete product.

## Build a distributable package

With `ANDROID_HOME` set, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/make_offline_package.ps1
```

This rebuilds the components, publishes the Windows app with its runtime, bundles platform-tools, verifies installer extraction and creates a new directory under `dist/releases`. Existing release directories are preserved. The output includes the offline Windows installer, APK, ZIP and SHA-256 release manifest. Packaging does not install the camera.

`make_dist.ps1` creates the smaller framework-dependent distribution. Prefer `make_offline_package.ps1` for testing on a PC without a .NET runtime.

## Signing and updates

The current pipeline builds a debuggable, development-signed Android APK. It does not provide a production Android signing configuration. Keep signing keys outside the repository, back them up securely and never commit passwords or signing material. An unrelated signing key cannot normally update an existing Android installation in place.

Windows binaries are unsigned unless signing is explicitly configured. Do not present a development certificate as a publicly trusted publisher identity.

Release builds should use a stable application identity, increasing Android version codes and a consistent signing strategy. Verify the exact packaged files, not only the build directory, before uploading a release.
