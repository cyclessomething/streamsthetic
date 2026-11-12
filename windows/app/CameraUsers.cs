using System.IO;
using Microsoft.Win32;

/// <summary>
/// Names the programs that have a camera open right now. Windows keeps this record itself (Settings, Privacy, Camera):
/// every program using a camera has a "last used" start and stop time, and a stop time of 0 means "still using it".
/// It does not say WHICH camera, so the caller only asks while Streamsthetic's camera is known to be open.
/// </summary>
public static class CameraUsers
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    public static string[] Current()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Key);
            if (root == null) return [];
            foreach (var sub in root.GetSubKeyNames())
            {
                if (sub.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    using var np = root.OpenSubKey(sub);
                    foreach (var app in np?.GetSubKeyNames() ?? [])
                        if (InUse(np!.OpenSubKey(app))) Add(names, FriendlyName(app.Replace('#', '\\')));
                }
                else if (InUse(root.OpenSubKey(sub)))
                {
                    Add(names, sub.Split('_')[0].Split('.').Last()); // "Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "WindowsCamera"
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return [.. names];
    }

    private static void Add(ISet<string> names, string name)
    {
        // Streamsthetic's own programs and Windows' camera service are not "another app"
        if (name.Contains("Streamsthetic", StringComparison.OrdinalIgnoreCase) || name.Contains("FrameServer", StringComparison.OrdinalIgnoreCase)) return;
        names.Add(name);
    }

    private static bool InUse(RegistryKey? key)
    {
        using (key)
        {
            if (key == null) return false;
            return key.GetValue("LastUsedTimeStart") is long start && start > 0 && key.GetValue("LastUsedTimeStop") is long stop && stop == 0;
        }
    }

    private static string FriendlyName(string path)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(info.ProductName)) return info.ProductName.Trim();
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException) { }
        return Path.GetFileNameWithoutExtension(path);
    }
}