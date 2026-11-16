using System.IO;
using Microsoft.Win32;

namespace PhoneCam;

/// <summary>
/// How many Streamsthetic cameras Windows has, and changing that number. The camera component can provide up to
/// <see cref="MaxCameras"/>; install-camera.ps1 -Count N creates cameras 1..N (one administrator approval) and removes
/// the ones above. Each created camera leaves a marker in HKLM\SOFTWARE\PhoneCam (SystemCamera, SystemCamera2, ...).
/// </summary>
public static class CameraSetup
{
    public const int MaxCameras = CameraLimits.Max;
    public const int DefaultCameras = CameraLimits.Default;
    private const string ComponentPath = @"C:\ProgramData\PhoneCam\vcam\VCamSampleSource.dll";

    /// <summary>Camera N's COM class: the base ID with N-1 added to its last byte (hex: camera 8 is ...442A).</summary>
    public static string Clsid(int camera) => $"{{31621596-4208-4D72-9066-1F95070744{0x23 + camera - 1:X2}}}";

    private static string Marker(int camera) => camera == 1 ? "SystemCamera" : $"SystemCamera{camera}";

    /// <summary>Cameras 1..N that Windows has (counted from 1 up to the first missing one). 0 = none installed.</summary>
    public static int InstalledCount()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PhoneCam");
            if (key == null) return 0;
            var count = 0;
            while (count < MaxCameras && key.GetValue(Marker(count + 1)) is int value && value == 1) count++;
            return count;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return DefaultCameras; }
    }

    /// <summary>True when camera <paramref name="camera"/> exists in Windows (its component is registered and it was created).</summary>
    public static bool Installed(int camera)
    {
        if (camera is < 1 or > MaxCameras) return false;
        try
        {
            using var com = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{Clsid(camera)}");
            if (com == null || !File.Exists(ComponentPath)) return false;
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PhoneCam");
            // cameras from before the per-camera markers (one-camera installs) only have the COM class
            return key?.GetValue(Marker(camera)) is int value ? value == 1 : camera == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return true; } // cannot tell: let the receiver try
    }

    /// <summary>
    /// Runs install-camera.ps1 (Windows asks for administrator approval). <paramref name="count"/> = null keeps the
    /// current number (first install: 4). Returns true when Windows has exactly the wanted cameras afterwards.
    /// </summary>
    public static async Task<bool> ApplyAsync(int? count)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "install-camera.ps1");
        if (!File.Exists(script)) return false;
        var arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"" + (count is int n ? $" -Count {Math.Clamp(n, 1, MaxCameras)}" : "");
        await Task.Run(() =>
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell", arguments)
            { UseShellExecute = false, CreateNoWindow = true });
            // no time limit: Windows keeps its approval prompt open until it is answered, and stopping to wait
            // early made the app miss a later approval (it then showed the old number of cameras)
            p?.WaitForExit();
        });
        var installed = InstalledCount();
        Diagnostics.Log("app", $"camera setup finished: wanted {count?.ToString() ?? "current"}, Windows has {installed}");
        return count is int wanted ? installed == Math.Clamp(wanted, 1, MaxCameras) : installed > 0;
    }
}
