using System.IO;

namespace PhoneCam;

/// <summary>
/// Where Streamsthetic keeps its settings and logs: %LOCALAPPDATA%\Streamsthetic. Copies made under the old name
/// (%LOCALAPPDATA%\PhoneCam) are moved over the first time, so nothing the owner set up is lost.
/// </summary>
public static class AppData
{
    private static readonly Lazy<string> Folder = new(Resolve);

    public static string Root => Folder.Value;

    private static string Resolve()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = Path.Combine(local, "Streamsthetic");
        var old = Path.Combine(local, "PhoneCam");
        try
        {
            if (!Directory.Exists(current) && Directory.Exists(old)) Directory.Move(old, current);
        }
        catch (IOException) { /* another copy of the app is using the old folder: start fresh below rather than fail */ }
        catch (UnauthorizedAccessException) { }
        Directory.CreateDirectory(current);
        return current;
    }
}
