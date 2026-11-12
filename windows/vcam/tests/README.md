# Camera slot check

Build the Release camera DLL first. From an x64 Visual Studio Developer Command Prompt at the repository root:

```bat
cl /nologo /EHsc /std:c++17 windows\vcam\tests\SlotTests.cpp /Fowindows\vcam\x64\Release\SlotTests.obj /Fewindows\vcam\x64\Release\SlotTests.exe /link mfplat.lib mfuuid.lib ole32.lib
windows\vcam\x64\Release\SlotTests.exe windows\vcam\x64\Release\VCamSampleSource.dll
```

The check loads the built DLL directly. It checks all four COM identities, distinct concurrent media sources, the legacy slot-1 shared name, isolated extra slot names, and all advertised video formats. It does not install or register cameras, change shared frame buffers, or require a phone or administrator rights.

Registration, frames delivered through Windows Frame Server, and simultaneous OBS sources require separate integration testing after installing the updated DLL.

# Negotiated frame-rate check

From the same x64 Developer Command Prompt:

```bat
cl /nologo /EHsc /std:c++17 windows\vcam\tests\SamplePacerTests.cpp /Fowindows\vcam\x64\Release\SamplePacerTests.obj /Fewindows\vcam\x64\Release\SamplePacerTests.exe /link mfplat.lib
windows\vcam\x64\Release\SamplePacerTests.exe
```

This checks exact rational 30/60 fps timestamps, consumer-timebase rounding, and skipped deadlines after a stall. It also runs a real 60 fps producer timer against a 30 fps sample timer and requires about one second for 31 output samples. No camera is registered or opened.

After deploying the DLL, `python tools/camera_slots_e2e.py` checks the full registered-camera path with four fake phones. The live phone test additionally verifies that a 30 fps camera consumer does not receive the phone's full 60 fps stream.
