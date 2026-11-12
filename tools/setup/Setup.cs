// Streamsthetic-Setup.exe: a tiny Windows program (built against the .NET Framework that every Windows 10/11 has) that carries
// the whole Streamsthetic zip inside itself. Double-click: it unpacks to a temporary folder and runs install.ps1, which installs
// the app for the current user (Start Menu + desktop icon), fetches the .NET Desktop Runtime if needed, and starts it.
// "Setup.exe --extract-only FOLDER" only unpacks (used by tests).
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Setup
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            bool extractOnly = args.Length >= 2 && args[0] == "--extract-only";
            string folder = extractOnly ? args[1] : Path.Combine(Path.GetTempPath(), "Streamsthetic-setup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(folder);
            string extractionRoot = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (Stream zip = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(folder, entry.FullName));
                    if (!target.StartsWith(extractionRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("A setup file has an invalid path. Download a fresh copy of the installer and try again.");
                    if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
            if (extractOnly) return 0;
            return RunInstaller(folder);
        }
        catch (Exception e)
        {
            if (args.Length >= 1 && args[0] == "--extract-only")
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
            MessageBox.Show("Streamsthetic setup could not finish.\n\n" + e.Message, "Streamsthetic Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    internal static int RunInstaller(string folder)
    {
            var info = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(folder, "install.ps1") + "\"")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
              RedirectStandardOutput = true, RedirectStandardError = true };
            var output = new StringBuilder();
            using (var p = new Process { StartInfo = info })
            {
                bool stdoutDone = false, stderrDone = false;
                p.OutputDataReceived += (sender, e) => { lock (output) { if (e.Data == null) stdoutDone = true; else output.AppendLine(e.Data); } };
                p.ErrorDataReceived += (sender, e) => { lock (output) { if (e.Data == null) stderrDone = true; else output.AppendLine(e.Data); } };
                p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
                // Descendants can inherit these pipes: wait for the installer, not the launched app.
                while (!p.WaitForExit(1000)) { }
                var drain = Stopwatch.StartNew();
                while (drain.ElapsedMilliseconds < 1000)
                {
                    lock (output) { if (stdoutDone && stderrDone) break; }
                    Thread.Sleep(20);
                }
                p.CancelOutputRead(); p.CancelErrorRead();
                if (p.ExitCode != 0)
                {
                    string details; lock (output) details = output.ToString();
                    File.WriteAllText(Path.Combine(folder, "setup-error.txt"), details);
                    if (details.Length > 2500) details = details.Substring(details.Length - 2500);
                    throw new InvalidOperationException("Installation stopped (exit code " + p.ExitCode + ").\n\n" + details
                        + "\nFull setup log: " + Path.Combine(folder, "setup-error.txt"));
                }
                return 0;
            }
    }

}
