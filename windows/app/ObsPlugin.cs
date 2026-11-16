using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace PhoneCam;

/// <summary>
/// The OBS plugin that adds "Streamsthetic phone microphone" as an audio source in OBS (no virtual cable).
/// It goes in OBS's per-computer plugin folder, C:\ProgramData\obs-studio\plugins\streamsthetic-mic\bin\64bit,
/// which OBS 30 and newer read and which needs no administrator rights. install.ps1 does the same when OBS is found.
/// </summary>
public static class ObsPlugin
{
    public enum State { NoObs, NotAdded, Outdated, Added, Missing }

    public static string Target => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "obs-studio", "plugins", "streamsthetic-mic", "bin", "64bit", "streamsthetic-mic.dll");

    /// <summary>The plugin shipped with this app (installed folder), or the repo build during development.</summary>
    public static string? Source()
    {
        var installed = Path.Combine(AppContext.BaseDirectory, "obs-plugin", "streamsthetic-mic.dll");
        if (File.Exists(installed)) return installed;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var dev = Path.Combine(dir.FullName, "obs-plugin", "x64", "Release", "streamsthetic-mic.dll");
            if (File.Exists(dev)) return dev;
        }
        return null;
    }

    /// <summary>OBS's program folder, from its installer's registry entry or the default location.</summary>
    public static string? FindObs()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(@"SOFTWARE\OBS Studio");
            if (key?.GetValue(null) is string path && File.Exists(Path.Combine(path, "bin", "64bit", "obs64.exe"))) return path;
        }
        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio");
        return File.Exists(Path.Combine(standard, "bin", "64bit", "obs64.exe")) ? standard : null;
    }

    public static State Check()
    {
        var source = Source();
        if (source == null) return State.Missing;
        if (File.Exists(Target)) return SameFile(source, Target) ? State.Added : State.Outdated;
        return FindObs() == null ? State.NoObs : State.NotAdded;
    }

    /// <summary>Copies the plugin into OBS's plugin folder. OBS picks it up the next time it starts.</summary>
    public static void Add()
    {
        var source = Source() ?? throw new FileNotFoundException("The OBS plugin is missing from this installation.");
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.Copy(source, Target, overwrite: true);
    }

    private static bool SameFile(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return fa.Length == fb.Length && SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }
}
