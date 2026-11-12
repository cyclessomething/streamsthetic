using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace PhoneCam;

public enum LinkState { Stopped, Starting, WaitingForApp, WaitingForPhone, Live, Error }

public sealed record ReceiverStatus(LinkState State, string Message, double Fps = 0, double LatencyMs = double.NaN, string? PhoneName = null);

/// <summary>
/// Runs StreamstheticReceiver.exe (the tested C++ core) and turns its status lines into <see cref="ReceiverStatus"/> updates.
/// Events arrive on background threads.
/// </summary>
public sealed partial class ReceiverController : IDisposable
{
    private Process? _process;
    private string? _phoneName;
    private int _generation;

    public event Action<ReceiverStatus>? StatusChanged;
    /// <summary>What the phone streams now (keys cam, zoom, w, h, fps, mbps); sent on connect and after each change.</summary>
    public event Action<IReadOnlyDictionary<string, string>>? PhoneSettingsChanged;
    /// <summary>The app that reads the camera (OBS...) takes fewer frames than the phone sends: (frames it reads, frames sent) per second.</summary>
    public event Action<int, int>? AppReadsSlower;
    public ReceiverStatus Status { get; private set; } = new(LinkState.Stopped, "Stopped");

    /// <summary>Next to the app when installed; the repo's build output during development.</summary>
    public static string? FindReceiver()
    {
        var installed = Path.Combine(AppContext.BaseDirectory, "StreamstheticReceiver.exe");
        if (File.Exists(installed)) return installed;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var dev = Path.Combine(dir.FullName, "receiver", "x64", "Release", "StreamstheticReceiver.exe");
            if (File.Exists(dev)) return dev;
        }
        return null;
    }

    /// <summary>Names of the audio outputs (the receiver lists them), for the Phone microphone choice.</summary>
    public static List<string> ListAudioOutputs()
    {
        var exe = FindReceiver();
        if (exe == null) return [];
        using var process = Process.Start(new ProcessStartInfo(exe, "--list-audio")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
        });
        if (process == null) return [];
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public void Start(string arguments, string? phoneName = null)
    {
        Stop();
        var exe = FindReceiver() ?? throw new FileNotFoundException("StreamstheticReceiver.exe was not found next to the app.");
        var generation = _generation;
        var context = SynchronizationContext.Current;
        _phoneName = phoneName;
        _idleSent = null;
        _fellBehind.Clear();
        _phoneConnected = false;
        _cameraOpen = false;
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                StandardInputEncoding = new System.Text.UTF8Encoding(false), // no byte order mark before the first command
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        // Serialize callbacks onto the caller's context (WPF) and discard queued output from a retired process.
        void Deliver(Action action)
        {
            void Apply() { if (generation == _generation && ReferenceEquals(_process, process)) action(); }
            if (context != null) context.Post(_ => Apply(), null);
            else Apply();
        }
        process.OutputDataReceived += (_, e) => Deliver(() =>
        {
            if (e.Data == null) return;
            Diagnostics.Log("receiver", e.Data);
            OnLine(e.Data);
        });
        process.ErrorDataReceived += (_, e) => Deliver(() =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            Diagnostics.Log("stderr", e.Data);
            // 0x80040154 = "class not registered": the Streamsthetic camera was never installed on this PC (found on a clean PC)
            Set(new(LinkState.Error, e.Data.Contains("0x80040154", StringComparison.OrdinalIgnoreCase)
                ? "The Streamsthetic camera is not installed on this PC yet. Press Start again: Streamsthetic then offers to install it (one time, it asks Windows for permission)."
                : e.Data.Replace("error: ", "")));
        });
        process.Exited += (_, _) => Deliver(() =>
        {
            var code = SafeExitCode(process);
            // 0xC0000005 and friends: the receiver crashed (it writes a crash dump to the problems folder itself)
            var crashed = code is < 0 or > 255;
            Diagnostics.RecordProblem(crashed ? "receiver-crash" : "receiver-stopped",
                crashed ? "The video part of Streamsthetic crashed" : "The video part of Streamsthetic stopped unexpectedly",
                $"StreamstheticReceiver.exe {arguments} exited with code {code} (0x{code:X8}) while running.",
                crashed ? Diagnostics.FreshReceiverDump() : null);
            if (Status.State != LinkState.Error) Set(new(LinkState.Error, "Streamsthetic stopped unexpectedly."));
        });
        Diagnostics.Log("app", $"starting receiver {exe} {arguments}");
        _process = process;
        Set(new(LinkState.Starting, "Starting…"));
        try
        {
            process.Start();
            KillWithApp.Add(process);
            if (_preview) SendLine("preview on"); // a new receiver starts with the preview off
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        var process = Interlocked.Exchange(ref _process, null);
        _phoneConnected = false;
        _cameraOpen = false;
        _idleSent = null;
        if (process != null)
        {
            try
            {
                if (!process.HasExited) process.Kill();
                // This bounded overload waits for the OS process, not output-handler completion.
                // Release the receiver's slot mutex before an immediate Start creates its replacement.
                process.WaitForExit(3000);
            }
            catch (InvalidOperationException) { }
            // Never wait on output callbacks from the UI thread: a callback may be queued to that same UI.
            _ = Task.Run(async () =>
            {
                try { await process.WaitForExitAsync(); }
                catch (InvalidOperationException) { }
                finally { process.Dispose(); }
            });
        }
        Set(new(LinkState.Stopped, "Stopped"));
    }

    public void Dispose() => Stop();

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return 0; }
    }

    /// <summary>Asks the phone to change settings while streaming, e.g. "cam=0;zoom=0.6". The phone validates them.</summary>
    public void SendSettings(string settings) => SendLine("set " + settings);

    private bool _preview;

    /// <summary>Whether the receiver makes the small preview picture (only while someone can see it).</summary>
    public bool Preview
    {
        get => _preview;
        set
        {
            if (_preview == value) return;
            _preview = value;
            SendLine(value ? "preview on" : "preview off");
        }
    }

    private void SendLine(string line)
    {
        try
        {
            if (_process is { HasExited: false }) _process.StandardInput.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // receiver just exited; the status already says so
        }
    }

    // Lines printed by StreamstheticReceiver.exe (windows/receiver/main.cpp). The phone connection and the camera
    // being open in an app are independent; the status combines both.
    private bool _phoneConnected;
    private bool _cameraOpen;

    /// <summary>True while an app (OBS, Discord...) has Streamsthetic's camera open.</summary>
    public bool CameraOpen => _cameraOpen;

    private void OnLine(string line)
    {
        if (line.StartsWith("phone settings: "))
        {
            var settings = new Dictionary<string, string>();
            foreach (var pair in line["phone settings: ".Length..].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2) settings[parts[0].Trim()] = parts[1].Trim();
            }
            PhoneSettingsChanged?.Invoke(settings);
        }
        else if (AppReadsRegex().Match(line) is { Success: true } reads)
        {
            AppReadsSlower?.Invoke(int.Parse(reads.Groups[1].Value), int.Parse(reads.Groups[2].Value));
        }
        else if (line.StartsWith("the phone refused the pairing code"))
        {
            Set(new(LinkState.Error, "The phone did not accept that code. Check the code on your phone (tap Show pairing code). If it still does not work, tap \"Generate new code\" on the phone and enter the new one here."));
        }
        else if (line.StartsWith("waiting for an app to open Streamsthetic"))
        {
            _cameraOpen = false;
            ShowCombined();
        }
        else if (line.StartsWith("virtual camera serves"))
        {
            _cameraOpen = true;
            ShowCombined();
        }
        else if (FoundRegex().Match(line) is { Success: true } found)
        {
            _phoneName = found.Groups[1].Value;
            PhoneHost = found.Groups[2].Value;
            if (int.TryParse(found.Groups[3].Value, out var port)) PhonePort = port;
            Set(new(LinkState.WaitingForPhone, $"Found {_phoneName}. Connecting…", PhoneName: _phoneName));
        }
        else if (line.StartsWith("receiving from the phone"))
        {
            _phoneConnected = true;
            ShowCombined();
        }
        else if (StatsRegex().Match(line) is { Success: true } stats && _phoneConnected)
        {
            // show the rate the phone delivers (all decoded); frames the app never takes are not copied on purpose,
            // so the written rate (group 1, older receivers only) looked like a drop when OBS reads 30 at 4K
            var fpsGroup = stats.Groups[3].Success ? stats.Groups[3] : stats.Groups[1];
            var fps = double.Parse(fpsGroup.Value, CultureInfo.InvariantCulture);
            // delay: capture -> the camera frame an app took (an app has the camera open), else capture -> decoded on this PC
            var latency = stats.Groups[2].Success ? double.Parse(stats.Groups[2].Value, CultureInfo.InvariantCulture)
                : _cameraOpen ? double.NaN
                : stats.Groups[4].Success ? double.Parse(stats.Groups[4].Value, CultureInfo.InvariantCulture) : double.NaN;
            if (_cameraOpen) Set(new(LinkState.Live, "Live", fps, latency, _phoneName));
            else Set(new(LinkState.WaitingForApp, WaitingForAppMessage, fps, latency, _phoneName)); // numbers move even before OBS opens the camera
        }
        else if (line.StartsWith("waiting for the phone"))
        {
            _phoneConnected = false;
            Set(new(LinkState.WaitingForPhone, "Waiting for the phone. Press Start in the Streamsthetic app on your phone.", PhoneName: _phoneName));
        }
        else if (line.StartsWith("phone stream ended"))
        {
            _phoneConnected = false;
            Set(new(LinkState.WaitingForPhone, "Connection dropped. Reconnecting…", PhoneName: _phoneName));
        }
        else if (line.StartsWith("fell ") && line.Contains("behind the phone"))
        {
            var now = DateTime.Now;
            _fellBehind.Enqueue(now);
            while (_fellBehind.Count > 0 && now - _fellBehind.Peek() > TimeSpan.FromSeconds(8)) _fellBehind.Dequeue();
            if (_fellBehind.Count >= 4) { _fellBehind.Clear(); CannotKeepUp?.Invoke(); }
            Set(Status with { Message = "Caught up after falling behind." });
        }
    }

    /// <summary>The address the phone was found at (network connections), so the app can tell when its cable is pulled.</summary>
    public string? PhoneHost { get; set; }
    public int PhonePort { get; set; } = 5556;

    /// <summary>Raised when the PC fell more than a second behind the phone several times in a few seconds: the connection (or the phone) cannot keep up with the quality asked for.</summary>
    public event Action? CannotKeepUp;
    private readonly Queue<DateTime> _fellBehind = new();

    private bool? _idleSent;

    private void ShowCombined()
    {
        // tell the phone whether any app is using the camera: it turns itself off after a while when nobody is
        if (_phoneConnected)
        {
            var idle = !_cameraOpen;
            if (_idleSent != idle) { _idleSent = idle; SendSettings(idle ? "idle=1" : "idle=0"); }
        }
        else _idleSent = null;
        if (_phoneConnected && _cameraOpen) Set(new(LinkState.Live, "Live", PhoneName: _phoneName));
        else if (_phoneConnected) Set(new(LinkState.WaitingForApp, "Phone connected. Choose \"Streamsthetic\" as the camera in OBS, Discord or another app.", PhoneName: _phoneName));
        else Set(new(LinkState.WaitingForPhone, "Looking for the phone…", PhoneName: _phoneName));
    }

    private void Set(ReceiverStatus status)
    {
        if (status.State != Status.State || status.Message != Status.Message) Diagnostics.Log("status", $"{status.State}: {status.Message}");
        Status = status;
        StatusChanged?.Invoke(status);
    }

    /// <summary>
    /// Windows ends the receiver when this app ends, even if it crashes or is killed: otherwise a leftover receiver
    /// kept the phone's stream and the next app start fought it ("Connection dropped"). A job object that kills its
    /// processes when its last handle closes (the standard pattern); the receiver's own helpers (adb) are not in it.
    /// </summary>
    private static class KillWithApp
    {
        private static readonly IntPtr Job = Create();

        public static void Add(Process process)
        {
            if (Job != IntPtr.Zero) AssignProcessToJobObject(Job, process.Handle);
        }

        private static IntPtr Create()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return job;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ | 0x1000 /* SILENT_BREAKAWAY_OK */;
            var size = System.Runtime.InteropServices.Marshal.SizeOf(info);
            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            try
            {
                System.Runtime.InteropServices.Marshal.StructureToPtr(info, buffer, false);
                SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, buffer, (uint)size);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }
            return job;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }

    [GeneratedRegex(@"^found (.+) at ([0-9.]+):(\d+)")]
    private static partial Regex FoundRegex();

    [GeneratedRegex(@"the app reads (\d+) of (\d+) frames per second")]
    private static partial Regex AppReadsRegex();

    private const string WaitingForAppMessage = "Phone connected. Choose \"Streamsthetic\" as the camera in OBS, Discord or another app.";

    [GeneratedRegex(@"^\s*([\d.]+) fps written .*capture->vcam median (?:([\d.]+) ms|n/a[^|]*)(?:.*from phone ([\d.]+) fps)?(?:.*capture->decoded median ([\d.]+) ms)?")]
    private static partial Regex StatsRegex();
}
