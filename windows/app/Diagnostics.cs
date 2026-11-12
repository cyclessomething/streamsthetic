using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneCam;

/// <summary>A crash or failure worth reporting, saved under diagnostics\problems as JSON.</summary>
public sealed record Problem(DateTime Time, string Kind, string Summary, string Details)
{
    /// <summary>Path of the saved record (and a .dmp beside it for receiver crashes, if one was written).</summary>
    public string? File { get; init; }

    public override string ToString() =>
        (Time.Date == DateTime.Today ? "Today" : Time.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : Time.ToString("ddd d MMM"))
        + $", {Time:HH:mm} — {Summary}";
}

/// <summary>
/// Records what Streamsthetic does, so a problem can be reported with evidence instead of a guess:
/// <list type="bullet">
/// <item>a rolling log of the app's and the receiver's messages (one file per app run, newest 20 runs kept);</item>
/// <item>problem records: app crashes, the receiver stopping unexpectedly or crashing (it writes a crash dump itself);</item>
/// <item>a report package (zip with REPORT.md first) for the "Report a problem" window.</item>
/// </list>
/// Files live in %LOCALAPPDATA%\Streamsthetic\diagnostics. Nothing leaves the PC unless the user sends a report.
/// </summary>
public static partial class Diagnostics
{
    public static readonly string Folder = Path.Combine(
        AppData.Root, "diagnostics");
    public static readonly string LogFolder = Path.Combine(Folder, "logs");
    /// <summary>The receiver writes its crash dumps here too (same path, see main.cpp).</summary>
    public static readonly string ProblemFolder = Path.Combine(Folder, "problems");
    public static string? ReportAddress
    {
        get
        {
            try
            {
                var address = Environment.GetEnvironmentVariable("STREAMSTHETIC_REPORT_EMAIL");
                var file = Path.Combine(AppContext.BaseDirectory, "report-email.txt");
                if (string.IsNullOrWhiteSpace(address) && File.Exists(file)) address = File.ReadAllText(file);
                address = address?.Trim();
                return System.Net.Mail.MailAddress.TryCreate(address, out var parsed)
                    && parsed.Address == address && !parsed.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)
                    ? address : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    private const int KeepRuns = 20;
    private const long MaxLogBytes = 20 * 1024 * 1024; // per run; a day of 5-second stats lines is ~5 MB
    private static readonly object Lock = new();
    private static StreamWriter? _log;
    private static long _logBytes;
    private static readonly Queue<string> Recent = new(); // last lines, attached to problem records

    /// <summary>Opens this run's log and removes old runs. Safe to call more than once.</summary>
    public static void Start()
    {
        lock (Lock)
        {
            if (_log != null) return;
            try
            {
                Directory.CreateDirectory(LogFolder);
                Directory.CreateDirectory(ProblemFolder);
                foreach (var old in new DirectoryInfo(LogFolder).GetFiles("run-*.log").OrderByDescending(f => f.Name).Skip(KeepRuns - 1))
                {
                    old.Delete();
                }
                var path = Path.Combine(LogFolder, $"run-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                _log = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Log("app", $"Streamsthetic app {AppVersion} started on Windows {Environment.OSVersion.Version}");
    }

    /// <summary>One line in the rolling log, e.g. Log("receiver", line). Never throws.</summary>
    public static void Log(string source, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] {message}";
        lock (Lock)
        {
            Recent.Enqueue(line);
            while (Recent.Count > 200) Recent.Dequeue();
            if (_log == null || _logBytes > MaxLogBytes) return;
            try
            {
                _log.WriteLine(line);
                _logBytes += line.Length + 2;
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Saves a problem record (with the latest log lines) and returns it. Never throws.</summary>
    public static Problem? RecordProblem(string kind, string summary, string details, string? fileBase = null)
    {
        Log("problem", $"{kind}: {summary}");
        string recent;
        lock (Lock) recent = string.Join('\n', Recent);
        var problem = new Problem(DateTime.Now, kind, summary, details + "\n\nLast log lines:\n" + recent);
        try
        {
            Directory.CreateDirectory(ProblemFolder);
            var path = Path.Combine(ProblemFolder, (fileBase ?? $"{problem.Time:yyyyMMdd-HHmmss}-{kind}") + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(problem, JsonOptions));
            return problem with { File = path };
        }
        catch (IOException) { return problem; }
        catch (UnauthorizedAccessException) { return problem; }
    }

    /// <summary>The receiver's crash dump from the last minute, if any, so its record can share the name.</summary>
    public static string? FreshReceiverDump()
    {
        if (!Directory.Exists(ProblemFolder)) return null;
        return new DirectoryInfo(ProblemFolder).GetFiles("*-receiver-crash.dmp")
            .Where(f => f.LastWriteTime > DateTime.Now.AddMinutes(-1))
            .OrderByDescending(f => f.LastWriteTime).FirstOrDefault() is { } dump ? Path.GetFileNameWithoutExtension(dump.Name) : null;
    }

    /// <summary>Saved problems, newest first. Receiver crash dumps without a JSON record are listed too.</summary>
    public static List<Problem> RecentProblems(int max = 30)
    {
        var problems = new List<Problem>();
        if (!Directory.Exists(ProblemFolder)) return problems;
        foreach (var file in new DirectoryInfo(ProblemFolder).GetFiles().OrderByDescending(f => f.LastWriteTime).Take(max * 2))
        {
            try
            {
                if (file.Extension == ".json")
                {
                    var p = JsonSerializer.Deserialize<Problem>(File.ReadAllText(file.FullName), JsonOptions);
                    if (p != null) problems.Add(p with { File = file.FullName });
                }
                else if (file.Extension == ".dmp" && !File.Exists(Path.ChangeExtension(file.FullName, ".json")))
                {
                    problems.Add(new Problem(file.LastWriteTime, "receiver-crash", "The video part of Streamsthetic crashed", "Crash dump written by the receiver.") { File = file.FullName });
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return problems.OrderByDescending(p => p.Time).Take(max).ToList();
    }

    /// <summary>
    /// Builds the report zip: REPORT.md (summary written for whoever fixes it), the log lines in the chosen time
    /// window, problem records and dumps from that window, the app settings (pairing code removed), PC details, and
    /// the phone's logs if it is connected by USB. Returns the zip path.
    /// </summary>
    public static string BuildReport(string userText, Problem? problem, DateTime from, DateTime to, bool includePhone, string folder)
    {
        Directory.CreateDirectory(folder);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(folder, $"Streamsthetic-report-{stamp}.zip");
        var code = ReadPairingCode();
        string Clean(string text) => code is { Length: 6 } ? text.Replace(code, "******") : text;

        var logLines = LogLinesBetween(from, to);
        var problems = RecentProblems(100).Where(p => p.Time >= from && p.Time <= to.AddMinutes(1)).ToList();
        if (problem != null && !problems.Any(p => p.File == problem.File)) problems.Insert(0, problem);
        var phone = includePhone ? PhoneDetails() : null;

        var md = new StringBuilder();
        md.AppendLine("# Streamsthetic problem report");
        md.AppendLine();
        md.AppendLine($"- Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (PC local time)");
        md.AppendLine($"- Time window included: {from:yyyy-MM-dd HH:mm:ss} to {to:yyyy-MM-dd HH:mm:ss}");
        md.AppendLine($"- Problem chosen by the user: {(problem == null ? "none (something else)" : $"{problem.Time:yyyy-MM-dd HH:mm:ss} {problem.Kind}: {problem.Summary}")}");
        md.AppendLine();
        md.AppendLine("## What the user says happened");
        md.AppendLine();
        md.AppendLine(string.IsNullOrWhiteSpace(userText) ? "(nothing written)" : Clean(userText.Trim()));
        md.AppendLine();
        md.AppendLine("## Environment");
        md.AppendLine();
        foreach (var line in PcDetails()) md.AppendLine($"- {line}");
        if (phone != null) foreach (var (key, value) in phone.Value.Facts) md.AppendLine($"- {key}: {value}");
        else md.AppendLine("- Phone: not included (USB debugging unavailable, or not selected)");
        md.AppendLine();
        md.AppendLine("## Problems in the selected time range");
        md.AppendLine();
        if (problems.Count == 0) md.AppendLine("None recorded.");
        foreach (var p in problems)
        {
            md.AppendLine($"### {p.Time:HH:mm:ss} {p.Kind}: {p.Summary}");
            md.AppendLine();
            md.AppendLine("```");
            md.AppendLine(Clean(p.Details.Split("\n\nLast log lines:")[0].Trim()));
            md.AppendLine("```");
            md.AppendLine();
        }
        md.AppendLine("## Timeline (problems, errors, connection changes)");
        md.AppendLine();
        md.AppendLine("```");
        foreach (var line in logLines.Where(IsTimelineLine).TakeLast(200)) md.AppendLine(Clean(line));
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("## Files in this report");
        md.AppendLine();
        md.AppendLine("- `pc-log.txt`: app and receiver messages in the selected time range (stats lines every 5 s: fps, delays)");
        md.AppendLine("- `problems/`: problem records (JSON) and receiver crash dumps (.dmp, open with WinDbg or Visual Studio)");
        md.AppendLine("- `settings.json`: the PC app's settings (pairing code removed)");
        if (phone != null) md.AppendLine("- `phone-log.txt`, `phone-crashes.txt`: the phone's recent Streamsthetic log and Android's crash records");

        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            void AddText(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
            AddText("REPORT.md", md.ToString());
            AddText("pc-log.txt", Clean(string.Join(Environment.NewLine, logLines)));
            var settings = Path.Combine(Path.GetDirectoryName(Folder)!, "settings.json");
            if (File.Exists(settings)) AddText("settings.json", Clean(File.ReadAllText(settings)));
            foreach (var p in problems.Where(p => p.File != null))
            {
                foreach (var file in new[] { p.File!, Path.ChangeExtension(p.File!, ".dmp") }.Distinct().Where(File.Exists))
                {
                    if (file.EndsWith(".json")) AddText("problems/" + Path.GetFileName(file), Clean(File.ReadAllText(file)));
                    else zip.CreateEntryFromFile(file, "problems/" + Path.GetFileName(file));
                }
            }
            if (phone != null)
            {
                AddText("phone-log.txt", Clean(phone.Value.Log));
                AddText("phone-crashes.txt", Clean(phone.Value.Crashes));
            }
        }
        Log("app", $"report saved: {zipPath} ({problems.Count} problems, {logLines.Count} log lines)");
        return zipPath;
    }

    /// <summary>Opens the user's mail app with the report summary, and shows the zip in Explorer to attach.</summary>
    public static void OpenEmail(string zipPath, Problem? problem)
    {
        var address = ReportAddress ?? throw new InvalidOperationException("Report email is not configured.");
        var subject = "Streamsthetic problem report" + (problem != null ? $": {problem.Summary}" : "");
        var body = $"Please attach this file before sending:\r\n{zipPath}\r\n\r\n(It opened in a folder window next to this email.)";
        Process.Start(new ProcessStartInfo($"mailto:{Uri.EscapeDataString(address)}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}") { UseShellExecute = true });
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zipPath}\"") { UseShellExecute = true });
    }

    public static string AppVersion =>
        typeof(Diagnostics).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static bool IsTimelineLine(string line) =>
        line.Contains("[problem]") || line.Contains("[status]") || line.Contains("[stderr]") ||
        line.Contains("fell ") || line.Contains("stream ended") || line.Contains("phone settings") || line.Contains("report saved") ||
        line.Contains(" started on ") || line.Contains("[usb]");

    private static List<string> LogLinesBetween(DateTime from, DateTime to)
    {
        var lines = new List<string>();
        if (!Directory.Exists(LogFolder)) return lines;
        foreach (var file in new DirectoryInfo(LogFolder).GetFiles("run-*.log").OrderBy(f => f.Name))
        {
            if (file.LastWriteTime < from) continue;
            try
            {
                using var reader = new StreamReader(new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length >= 23 && DateTime.TryParseExact(line[..23], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                        && t >= from && t <= to) lines.Add(line);
                }
            }
            catch (IOException) { }
        }
        return lines;
    }

    private static IEnumerable<string> PcDetails()
    {
        yield return $"Streamsthetic app: {AppVersion}, built {File.GetLastWriteTime(typeof(Diagnostics).Assembly.Location):yyyy-MM-dd HH:mm}";
        var receiver = ReceiverController.FindReceiver();
        yield return $"Receiver: {(receiver == null ? "not found" : $"{receiver} built {File.GetLastWriteTime(receiver):yyyy-MM-dd HH:mm}")}";
        yield return $"Windows: {Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})";
        yield return $"CPU: {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}, {Environment.ProcessorCount} threads";
        foreach (var gpu in Gpus()) yield return $"GPU: {gpu}";
        yield return $"System camera installed: {Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\PhoneCam", "SystemCamera", 0) is 1}";
    }

    /// <summary>"name, driver version" per graphics adapter, via PowerShell so the app needs no WMI package.</summary>
    private static IEnumerable<string> Gpus()
    {
        try
        {
            const string script = "Get-CimInstance Win32_VideoController | ForEach-Object { \"$($_.Name), driver $($_.DriverVersion)\" }";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var p = Process.Start(new ProcessStartInfo("powershell", $"-NoProfile -EncodedCommand {encoded}")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            });
            if (p == null) return [];
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(8000)) { p.Kill(); return []; }
            return output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    /// <summary>Phone facts and logs over USB (adb). Null if no phone is connected.</summary>
    private static (List<(string, string)> Facts, string Log, string Crashes)? PhoneDetails()
    {
        if (UsbPhoneWatcher.RunAdb("get-state").Trim() != "device") return null;
        string Prop(string name) => UsbPhoneWatcher.RunAdb($"shell getprop {name}").Trim();
        var package = UsbPhoneWatcher.RunAdb("shell dumpsys package com.phonecam");
        var facts = new List<(string, string)>
        {
            ("Phone", $"{Prop("ro.product.manufacturer")} {Prop("ro.product.model")}"),
            ("Android", $"{Prop("ro.build.version.release")} (SDK {Prop("ro.build.version.sdk")}), build {Prop("ro.build.display.id")}"),
            ("Phone app", VersionRegex().Match(package) is { Success: true } m ? m.Groups[1].Value : "not installed"),
            ("Phone app streaming now", UsbPhoneWatcher.RunAdb("shell dumpsys activity services com.phonecam").Contains("CameraStreamService").ToString()),
        };
        // threadtime format: "11-04 16:23:16.099  10195 29680 I PhoneCam: ..."; the phone keeps a few hours of it
        var log = UsbPhoneWatcher.RunAdb("logcat -d -v threadtime -s PhoneCam:V AndroidRuntime:E CameraService:W", 20000);
        var crashes = UsbPhoneWatcher.RunAdb("logcat -d -v threadtime -b crash", 20000);
        return (facts, log, string.IsNullOrWhiteSpace(crashes) ? "(no crash records on the phone)" : crashes);
    }

    private static string? ReadPairingCode()
    {
        try
        {
            var settings = Path.Combine(Path.GetDirectoryName(Folder)!, "settings.json");
            return File.Exists(settings) ? JsonDocument.Parse(File.ReadAllText(settings)).RootElement.GetProperty("Code").GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"versionName=(\S+)")]
    private static partial Regex VersionRegex();
}
