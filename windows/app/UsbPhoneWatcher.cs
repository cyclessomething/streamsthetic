using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace PhoneCam;

/// <summary>
/// Tells when an Android phone with USB debugging is plugged in or unplugged, by asking the adb server to push device
/// list changes ("host:track-devices"; the same protocol AdvancedSharpAdbClient's DeviceMonitor and dotnet/android's
/// AdbDeviceTracker use). Events arrive on a background thread.
/// </summary>
public sealed class UsbPhoneWatcher : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private bool? _present;

    /// <summary>true = a phone is plugged in and authorized, false = none.</summary>
    public event Action<bool>? PhonePresentChanged;
    public bool PhonePresent => _present == true;

    /// <summary>
    /// Why a plugged-in phone cannot be used yet: "unauthorized" (the phone is waiting for you to tap Allow on its USB
    /// debugging prompt), "offline" (connected but not answering), "nodebug" (a phone is on the cable but USB debugging is off), "noadb" (the adb tool is missing), or null.
    /// </summary>
    public string? Problem { get; private set; }
    public event Action<string?>? ProblemChanged;

    private string? _adbProblem;
    private bool _cableOnly; // a phone is on the cable (Windows sees it) but adb lists nothing: USB debugging is off

    private void SetProblem(string? problem)
    {
        _adbProblem = problem;
        Publish();
    }

    private void Publish()
    {
        var wanted = _adbProblem ?? (_cableOnly && _present != true ? "nodebug" : null);
        if (Problem == wanted) return;
        Problem = wanted;
        ProblemChanged?.Invoke(wanted);
    }

    public void Start()
    {
        new Thread(Run) { IsBackground = true, Name = "usb-watch" }.Start();
        new Thread(WatchCable) { IsBackground = true, Name = "usb-cable" }.Start();
    }

    /// <summary>
    /// adb cannot tell "no phone" from "phone with USB debugging off" (both give an empty list), so ask Windows whether
    /// a phone is on the cable: a Samsung/Android USB device that is present. Checked every few seconds, only while no
    /// phone is usable.
    /// </summary>
    private void WatchCable()
    {
        while (!_stop.IsCancellationRequested)
        {
            if (_present != true)
            {
                var found = PhoneOnCable();
                if (found != _cableOnly) { _cableOnly = found; Publish(); }
            }
            else if (_cableOnly) { _cableOnly = false; Publish(); }
            _stop.Token.WaitHandle.WaitOne(4000);
        }
    }

    private static bool PhoneOnCable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"if (Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'USB*VID_*' -and ($_.InstanceId -like '*MS_COMP_MTP*' -or $_.InstanceId -like '*ANDROID*' -or $_.FriendlyName -match 'Android|SAMSUNG Mobile|Galaxy') }) { 'yes' }\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            });
            if (process == null) return false;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(15000)) { try { process.Kill(); } catch (InvalidOperationException) { } return false; }
            return output.Result.Contains("yes");
        }
        catch (Exception) { return false; }
    }

    public void Dispose() => _stop.Cancel();

    private void Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                StartAdbServer();
                using var client = new TcpClient("127.0.0.1", 5037);
                using var stream = client.GetStream();
                using var cancel = _stop.Token.Register(() => client.Close());
                Send(stream, "host:track-devices");
                if (ReadExactly(stream, 4) != "OKAY") throw new IOException("adb refused track-devices");
                while (!_stop.IsCancellationRequested)
                {
                    var length = Convert.ToInt32(ReadExactly(stream, 4), 16);
                    var list = length > 0 ? ReadExactly(stream, length) : "";
                    // one line per device: "<serial>\t<state>"; only "device" is usable ("unauthorized", "offline" are not)
                    var lines = list.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    SetProblem(lines.Any(l => l.EndsWith("\tunauthorized")) ? "unauthorized" : lines.Any(l => l.EndsWith("\toffline")) ? "offline" : null);
                    var present = list.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Any(line => line.EndsWith("\tdevice"));
                    Report(present);
                }
            }
            catch (Exception) when (!_stop.IsCancellationRequested)
            {
                // adb missing, server restarting or killed: no phone as far as we can tell; try again shortly
                SetProblem(FindAdb() == null ? "noadb" : null);
                Report(false);
                _stop.Token.WaitHandle.WaitOne(2000);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    private void Report(bool present)
    {
        if (_present == present) return;
        _present = present;
        PhonePresentChanged?.Invoke(present);
    }

    private static void Send(NetworkStream stream, string command)
    {
        var bytes = Encoding.ASCII.GetBytes($"{command.Length:X4}{command}");
        stream.Write(bytes);
    }

    private static string ReadExactly(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return Encoding.ASCII.GetString(buffer);
    }

    /// <summary>adb next to the app, in the Android SDK, or on PATH (same order as the receiver).</summary>
    public static string? FindAdb()
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "adb.exe") };
        var home = Environment.GetEnvironmentVariable("ANDROID_HOME");
        if (!string.IsNullOrEmpty(home)) candidates.Add(Path.Combine(home, "platform-tools", "adb.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir.Trim(), "adb.exe"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Runs adb with arguments, hidden; returns its output (empty if adb is missing or times out).</summary>
    public static string RunAdb(string arguments, int timeoutMs = 10000)
    {
        var adb = FindAdb();
        if (adb == null) return "";
        using var process = Process.Start(new ProcessStartInfo(adb, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (process == null) return "";
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return "";
        }
        return output.Result;
    }

    private static void StartAdbServer() => RunAdb("start-server");
}
