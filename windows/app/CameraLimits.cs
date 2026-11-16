namespace PhoneCam;

/// <summary>
/// How many Streamsthetic cameras there can be. Kept in its own file with no dependencies, so every part of the app
/// (and each self-test that compiles single files) can use it. Must match windows/vcam PhoneCamShared.h
/// PHONECAM_MAX_SLOTS and windows/obs-plugin shared_audio.h STREAMSTHETIC_MAX_CAMERAS.
/// </summary>
public static class CameraLimits
{
    public const int Max = 16;
    public const int Default = 4;
}
