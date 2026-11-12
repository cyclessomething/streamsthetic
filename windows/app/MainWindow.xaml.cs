using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace PhoneCam;

public partial class MainWindow : Window
{
    private readonly PhoneSessions _sessions = new();
    private int _selectedSlot = 1;
    private PhoneSession SelectedPhone => _sessions.Find(_selectedSlot);
    private ReceiverController _receiver => SelectedPhone.Receiver;
    private static readonly string SettingsPath =
        Path.Combine(AppData.Root, "settings.json");

    private sealed record Settings(bool Wifi, string Code, bool AutoStart = false, string Theme = "System", string Skin = "Studio", bool TrayOnClose = false, bool StartWithWindows = false, bool SetupDone = false, int UiScale = 0, string MicOutput = "", bool MatchFps = true, string DecodeMode = "auto", bool Tether = true, bool AutoConnect = true, string Paired = "", PhoneSlotAssignment[]? PhoneSlots = null);

    // Appearance: System follows Windows' light/dark setting (and changes with it); Light and Dark fix it
    private static readonly CameraChoice[] Appearances = [new("System", "System"), new("Light", "Light"), new("Dark", "Dark")];
    // the look of the app: Studio (orange control panel), Creator (soft, colourful), Gamer (neon yellow, always dark), Brutal, Dot
    private static readonly CameraChoice[] Skins = [new("Studio", "Studio"), new("Creator", "Creator"), new("Gamer", "Gamer"), new("Brutal", "Brutal"), new("Dot", "Dot"), new("Prism", "Prism"), new("Contrast", "Contrast")];
    private string _skin = "Studio";
    // Accessibility: Text and icon size (Settings). The whole window is scaled; chips: Normal, Large, Larger, Largest
    private static readonly CameraChoice[] Sizes = [new("Normal", "0"), new("Large", "1"), new("Larger", "2"), new("Largest", "3")];
    private static readonly double[] SizeFactors = [1.0, 1.2, 1.4, 1.7];
    private int _uiScale;
    // Phone microphone: "" = off, "default" = default output, otherwise (part of) the name of an audio output (a virtual cable)
    private string _micOutput = "";
    private bool _matchFps = true;
    private bool _autoConnect = true; // connect by itself when the phone starts (Streamsthetic waits for it as soon as it opens)
    // Video decoding: automatic (CPU up to 1080p, graphics card above), processor only, or graphics card
    private static readonly CameraChoice[] DecodeModes = [new("Automatic", "auto"), new("Processor", "cpu"), new("Graphics card", "gpu")];
    private string _decodeMode = "auto";

    private static string DecodeExplanation(string mode) => mode switch
    {
        "cpu" => "Uses the processor to decode video. This reduces video-decoding work on the graphics hardware but increases CPU load. If video stutters, lower the resolution or try Graphics card.",
        "gpu" => "Uses the graphics hardware to decode video. This can reduce CPU load, especially at higher resolutions. Performance depends on your graphics hardware, drivers and other apps.",
        _ => "Uses the processor at 1080p and below, and tries graphics decoding at higher resolutions. Start here, then try another option if video stutters.",
    };
    private bool _loadingMic;
    private string _theme = "System";

    private readonly UsbPhoneWatcher _usb = new();
    /// <summary>Shown under "Stopped" after a USB event (unplugged / plugged in); cleared when Streamsthetic starts.</summary>
    private string? _usbNote;
    private CancellationTokenSource? _unlockWait; // set while Start waits for the phone (Stop cancels it)

    /// <summary>One entry of a phone camera dropdown; Values is what the phone gets ("cam=0;zoom=0.6").</summary>
    private sealed record CameraChoice(string Label, string Values)
    {
        public override string ToString() => Label;
    }

    // Same choices as the phone app (android MainActivity), so both sides always show a matching entry.
    private static readonly CameraChoice[] Lenses =
    [
        new("Wide 0.6x", "cam=0;zoom=0.6"), new("Main 1x", "cam=0;zoom=1"), new("Tele 3x", "cam=0;zoom=3"),
        new("Tele 5x", "cam=0;zoom=5"), new("Front", "cam=1;zoom=1"),
    ];
    private static readonly CameraChoice[] Resolutions =
        [new("720p", "w=1280;h=720"), new("1080p", "w=1920;h=1080"), new("4K", "w=3840;h=2160")];
    private static readonly CameraChoice[] FrameRates = [new("30", "fps=30"), new("60", "fps=60")];
    private static readonly CameraChoice[] Qualities =
        [new("10", "mbps=10"), new("20", "mbps=20"), new("40", "mbps=40"), new("60", "mbps=60")];

    private static readonly CameraChoice[] FocusModes = [new("Auto", "focus=auto"), new("Locked", "focus=lock")];
    private static readonly CameraChoice[] WhiteBalances =
    [
        new("Auto", "wb=auto"), new("Daylight", "wb=daylight"), new("Cloudy", "wb=cloudy"),
        new("Tungsten", "wb=tungsten"), new("Fluorescent", "wb=fluorescent"),
    ];
    private static readonly CameraChoice[] FlickerModes =
        [new("Auto", "flicker=auto"), new("50 Hz", "flicker=50"), new("60 Hz", "flicker=60"), new("Off", "flicker=off")];

    private static readonly CameraChoice[] StabilizationModes =
        [new("Off", "stab=0"), new("On", "stab=1")];
    private static readonly CameraChoice[] MirrorModes = [new("Off", "mirror=0"), new("On", "mirror=1")];
    private int _rotation; // how far the phone says the picture is turned now (degrees clockwise)

    private bool _showingPhoneSettings;
    private double _evStep;

    public MainWindow()
    {
        InitializeComponent();
        Motion.AnimateTabs(MainTabs);
        SizeChanged += (_, _) => FitPreview();
        if (!Environment.GetCommandLineArgs().Any(a => a is "--snapshot" or "--selftest")) Loaded += (_, _) => Motion.RiseIn((FrameworkElement)Content, 14, 360);
        LensBox.ItemsSource = Lenses;
        ResolutionBox.ItemsSource = Resolutions;
        FrameRateBox.ItemsSource = FrameRates;
        QualityBox.ItemsSource = Qualities;
        FocusBox.ItemsSource = FocusModes;
        WhiteBalanceBox.ItemsSource = WhiteBalances;
        FlickerBox.ItemsSource = FlickerModes;
        StabilizationBox.ItemsSource = StabilizationModes;
        MirrorBox.ItemsSource = MirrorModes;
        LoadSettings();
        OnLinkChanged(this, new RoutedEventArgs()); // show the pairing box / auto-start row for the saved link choice
        InitTray();
        // snapshots show the look they were asked for, not the saved one
        var cli = Environment.GetCommandLineArgs();
        if (cli.Contains("--snapshot") && Array.IndexOf(cli, "--skin") is var at and >= 0 && at + 1 < cli.Length) _skin = cli[at + 1];
        if (cli.Contains("--snapshot"))
        {
            var requestedAppearance = cli.SkipWhile(arg => arg != "--theme").Skip(1).FirstOrDefault();
            _theme = requestedAppearance switch { "light" => "Light", "dark" => "Dark", _ => _theme };
        }
        AppearanceBox.ItemsSource = Appearances;
        AppearanceBox.SelectedItem = Appearances.First(a => a.Values == _theme);
        DecodeBox.ItemsSource = DecodeModes;
        DecodeBox.SelectedItem = DecodeModes.FirstOrDefault(m => m.Values == _decodeMode) ?? DecodeModes[0];
        DecodeExplain.Text = DecodeExplanation(_decodeMode);
        LoadMicChoices();
        SizeBox.ItemsSource = Sizes;
        SizeBox.SelectedItem = Sizes[_uiScale];
        SkinBox.ItemsSource = Skins;
        SkinBox.SelectedItem = Skins.First(a => a.Values == _skin);
        InitPhoneSessions();
        _usb.PhonePresentChanged += present => Dispatcher.Invoke(() => OnUsbChanged(present));
        _usb.ProblemChanged += problem => Dispatcher.Invoke(() => { _usbProblem = problem; Show(_receiver.Status); });
        // --snapshot only renders the window: it must not start Streamsthetic (auto-start) or touch the phone
        if (!Environment.GetCommandLineArgs().Contains("--snapshot")) { _usb.Start(); StartCableWatch(); ContentRendered += (_, _) => AutoConnectIfWanted(); }
        Show(_receiver.Status);
        ShowLastProblem();
        // first run: the setup guide (not for screenshots or self-tests)
        if (!_setupDone && !Environment.GetCommandLineArgs().Any(a => a is "--snapshot" or "--selftest")) ContentRendered += (_, _) => { if (!_setupDone) ShowSetupGuide(); };

        MaxHeight = SystemParameters.WorkArea.Height; // taller content scrolls instead of running off the screen
        _previewTimer.Tick += (_, _) => UpdatePreview();
        _previewTimer.Start();
        StateChanged += (_, _) => UpdatePreview();
        IsVisibleChanged += (_, _) => UpdatePreview();
    }

    // Status text and camera-user discovery need only occasional refreshes. Video is pulled from
    // the latest-frame buffer immediately before WPF renders, without a second polling delay.
    private readonly System.Windows.Threading.DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private PreviewReader _previewReader = new();
    private bool _previewRendering;
    private bool _previewDemo;
    private TimeSpan _lastPreviewRender = TimeSpan.MinValue;
    private string[] _previewCameraUsers = [];
    private long _nextCameraUsersCheck;
    private int _cameraUsersSlot;

    private void SetPreviewRendering(bool enabled)
    {
        if (_previewRendering == enabled) return;
        _previewRendering = enabled;
        _lastPreviewRender = TimeSpan.MinValue;
        if (enabled) CompositionTarget.Rendering += OnPreviewRendering;
        else CompositionTarget.Rendering -= OnPreviewRendering;
    }

    private void OnPreviewRendering(object? sender, EventArgs e)
    {
        if (!_previewRendering || _phonesClosing || !Running || !IsVisible || WindowState == WindowState.Minimized) return;
        if (e is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _lastPreviewRender) return;
            _lastPreviewRender = rendering.RenderingTime;
        }
        // The shared mapping contains one frame. If the UI was busy, take the newest picture;
        // never enqueue old pictures to catch up with after the stall.
        if (!_previewReader.Update()) return;
        if (!ReferenceEquals(PreviewImage.Source, _previewReader.Bitmap)) PreviewImage.Source = _previewReader.Bitmap;
        PreviewImage.Visibility = Visibility.Visible;
        PreviewHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The preview runs while Streamsthetic runs and the window can be seen (not minimised); the receiver only makes the
    /// small picture then. "No picture" shows a hint instead of a frozen image.
    /// </summary>
    private DateTime? _liveSince; // RUNTIME in the header: time since the picture first went live

    /// <summary>A big readout: the value in ink, or a dim "00" while there is nothing to show (an idle meter).</summary>
    private void SetReadout(System.Windows.Controls.TextBlock readout, string? value, string hue = "InkBrush")
    {
        readout.Text = value ?? "00";
        readout.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, value == null ? "Ink3Brush" : hue);
    }

    /// <summary>
    /// Screenshot aid (Streamsthetic.exe --snapshot FILE --demo): fills the window as if a phone were streaming, so the
    /// live state can be reviewed without one. Nothing is sent anywhere.
    /// </summary>
    public void ShowDemo()
    {
        _previewDemo = true;
        Show(new ReceiverStatus(LinkState.Live, "Live", 60, 92, "Galaxy S24 Ultra"));
        _liveSince = DateTime.Now.AddMinutes(-12).AddSeconds(-41);
        RuntimeText.Text = (DateTime.Now - _liveSince.Value).ToString(@"hh\:mm\:ss");
        ShowPhoneSettings(new Dictionary<string, string>
        {
            ["cam"] = "0", ["zoom"] = "1.00", ["w"] = "3840", ["h"] = "2160", ["fps"] = "60", ["mbps"] = "60", ["ev"] = "0",
            ["evmin"] = "-20", ["evmax"] = "20", ["evstep"] = "0.1", ["focus"] = "auto", ["wb"] = "auto", ["flicker"] = "50",
            ["stab"] = "0", ["orient"] = "auto", ["rot"] = "0", ["zmin"] = "0.60", ["zmax"] = "10.00",
        });
        // a stand-in picture: a soft gradient, so the live view is not an empty box
        var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, PixelFormats.Bgr32, null);
        var pixels = new byte[480 * 270 * 4];
        for (var y = 0; y < 270; y++)
            for (var x = 0; x < 480; x++)
            {
                var o = (y * 480 + x) * 4;
                pixels[o] = (byte)(40 + y / 6); pixels[o + 1] = (byte)(55 + x / 10); pixels[o + 2] = (byte)(70 + x / 6 + y / 8);
            }
        bitmap.WritePixels(new Int32Rect(0, 0, 480, 270), pixels, 480 * 4, 0);
        PreviewImage.Source = bitmap;
        PreviewImage.Visibility = Visibility.Visible;
        PreviewHint.Visibility = Visibility.Collapsed;
        _previewTimer.Stop();
        SetPreviewRendering(false);
    }

    private bool _previewAnyway;

    private void OnShowPreviewAnyway(object sender, RoutedEventArgs e)
    {
        _previewAnyway = !_previewAnyway;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_phonesClosing || _previewDemo) return;
        RuntimeText.Text = _liveSince is { } since ? (DateTime.Now - since).ToString(@"hh\:mm\:ss") : "00:00:00";
        // another app (OBS...) has the camera: the preview rests, the readouts below describe what that app gets
        if (_cameraUsersSlot != _selectedSlot)
        {
            _cameraUsersSlot = _selectedSlot;
            _previewCameraUsers = [];
            _nextCameraUsersCheck = 0;
        }
        if (!Running || !_receiver.CameraOpen)
        {
            _previewCameraUsers = [];
            _nextCameraUsersCheck = 0;
        }
        else if (Environment.TickCount64 >= _nextCameraUsersCheck)
        {
            _previewCameraUsers = CameraUsers.Current();
            _nextCameraUsersCheck = Environment.TickCount64 + 1000;
        }
        var users = _previewCameraUsers;
        var paused = users.Length > 0 && !_previewAnyway;
        if (users.Length == 0) _previewAnyway = false; // asked for once: the next app starts paused again
        PreviewAnywayButton.Visibility = users.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewAnywayButton.Content = _previewAnyway ? "Pause the preview" : "Show preview anyway";
        // plain words: showing the picture twice (here and in the other app) costs the PC some of its power
        PreviewWarn.Visibility = users.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewWarn.Text = _previewAnyway
            ? $"The preview is on while {string.Join(" and ", users)} uses the camera. Showing both views adds processing work. Pause this preview if video stutters."
            : "The preview is paused to reduce processing while another app uses the camera. Choose Show preview anyway for a quick check.";
        var wanted = Running && IsVisible && WindowState != WindowState.Minimized && !paused;
        _receiver.Preview = wanted;
        SetPreviewRendering(wanted);
        var fresh = wanted && DateTime.Now - _previewReader.LastFrame < TimeSpan.FromSeconds(2);
        PreviewImage.Visibility = fresh ? Visibility.Visible : Visibility.Hidden;
        PreviewHint.Text = !Running ? "The picture appears here while Streamsthetic is running."
            : paused ? $"The camera is in use by {string.Join(" and ", users)}.\nThe preview is paused here. Frame rate and delay below show what {(users.Length == 1 ? "it gets" : "they get")}."
            : "Waiting for the picture from the phone…";
        PreviewHint.Visibility = fresh ? Visibility.Collapsed : Visibility.Visible;
        if (paused) PreviewImage.Visibility = Visibility.Hidden;
    }

    /// <summary>A crash or unexpected stop in the last day is mentioned next to "Report a problem".</summary>
    private void ShowLastProblem()
    {
        var last = Diagnostics.RecentProblems(1).FirstOrDefault();
        LastProblemText.Text = last != null && last.Time > DateTime.Now.AddDays(-1)
            ? $"Streamsthetic had a problem {(last.Time.Date == DateTime.Today ? "at" : "on")} {(last.Time.Date == DateTime.Today ? last.Time.ToString("HH:mm") : last.Time.ToString("ddd HH:mm"))}. "
            : "";
    }

    private ReportWindow? _report;

    // not modal: Streamsthetic keeps running and stays usable while the report is written
    private void OnReportProblem(object sender, RoutedEventArgs e)
    {
        if (_report != null)
        {
            _report.Activate();
            return;
        }
        _report = new ReportWindow { Owner = this };
        _report.Closed += (_, _) =>
        {
            _report = null;
            ShowLastProblem();
        // first run: the setup guide (not for screenshots or self-tests)
        if (!_setupDone && !Environment.GetCommandLineArgs().Any(a => a is "--snapshot" or "--selftest")) ContentRendered += (_, _) => { if (!_setupDone) ShowSetupGuide(); };
        };
        _report.Show();
    }

    private bool Running => _receiver.Status.State is not (LinkState.Stopped or LinkState.Error);

    /// <summary>
    /// USB mode follows the cable: unplugging stops Streamsthetic (like pressing Stop); plugging in either starts it
    /// (when "Start automatically" is ticked) or says it is ready for Start.
    /// </summary>
    private string? _usbProblem; // unauthorized / offline / nodebug / noadb (UsbPhoneWatcher.Problem)

    /// <summary>What is wrong with the cable connection and what to do about it (roadmap "Errors with a fix button").</summary>
    /// <summary>USB tethering and Wi-Fi both reach the phone over a network (found by broadcast, paired with the code).</summary>
    private string LinkName => SelectedPhone.Endpoint != null ? "Wi-Fi / network" : TetherOption.IsChecked == true ? "USB connection" : WifiOption.IsChecked == true ? "Wi-Fi" : "USB";

    private bool NetworkLink => WifiOption?.IsChecked == true || TetherOption?.IsChecked == true;

    private string? UsbProblemText => NetworkLink ? null : _usbProblem switch
    {
        "unauthorized" => "Your phone is asking to allow USB debugging. Look at the phone and tap Allow (tick Always allow from this computer).",
        "nodebug" => "A phone is plugged in, but USB debugging is off. On the phone, open Settings, Developer options and switch on USB debugging. If Developer options is missing, tap Build number 7 times under About phone.",
        "offline" => "The phone is plugged in but not answering. Unlock it, or unplug and plug it back in.",
        "noadb" => "Streamsthetic could not find adb, the tool it uses for USB debugging. Reinstall Streamsthetic or install Android platform-tools.",
        _ => null,
    };

    private void OnFixUsb(object sender, RoutedEventArgs e)
    {
        FixButton.IsEnabled = false;
        Diagnostics.Log("usb", "fix button: restarting adb to ask the phone again");
        Task.Run(() => { UsbPhoneWatcher.RunAdb("kill-server"); UsbPhoneWatcher.RunAdb("start-server"); })
            .ContinueWith(_ => { FixButton.IsEnabled = true; _usbNote = "USB authorization restarted. Check the prompt on your phone."; Show(_receiver.Status); }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private string? _usbPhoneName; // the name the owner gave the phone (or its model), read over USB

    private void OnUsbChanged(bool present)
    {
        Diagnostics.Log("usb", present ? "phone plugged in" : "phone unplugged");
        if (!present) _usbPhoneName = null;
        else Task.Run(() =>
        {
            var name = UsbPhoneWatcher.RunAdb("shell settings get global device_name").Trim();
            if (string.IsNullOrEmpty(name) || name == "null") name = UsbPhoneWatcher.RunAdb("shell getprop ro.product.model").Trim();
            return name;
        }).ContinueWith(t =>
        {
            _usbPhoneName = string.IsNullOrEmpty(t.Result) ? null : t.Result;
            Show(_receiver.Status);
        }, TaskScheduler.FromCurrentSynchronizationContext());
        if (_selectedSlot != 1 || SelectedPhone.Endpoint != null || NetworkLink) return; // tethering and Wi-Fi do not depend on USB debugging
        if (!present && Running)
        {
            _receiver.Stop();
            _usbNote = "Phone unplugged, so Streamsthetic stopped. Plug it back in and press Start.";
        }
        else if (present && !Running)
        {
            if (AutoStartBox.IsChecked == true)
            {
                StartPhoneCam();
                return;
            }
            _usbNote = "Phone plugged in. Press Start.";
        }
        else if (!present)
        {
            _usbNote = null;
        }
        Show(_receiver.Status);
    }

    // ---- USB connection: stop when the cable is pulled (the phone's network adapter disappears from Windows) ----
    private readonly System.Windows.Threading.DispatcherTimer _cableTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _cableMissing;

    private void StartCableWatch()
    {
        _cableTimer.Tick += (_, _) => CheckCable();
        _cableTimer.Start();
    }

    private static bool HasAdapterNear(string host)
    {
        var prefix = host[..(host.LastIndexOf('.') + 1)];
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                    if (address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && address.Address.ToString().StartsWith(prefix)) return true;
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return true; } // cannot tell: do not stop anything
        return false;
    }

    private void CheckCable()
    {
        if (_selectedSlot != 1 || SelectedPhone.Endpoint != null) return;
        if (!Running && _waitForTether && _autoConnect && TetherOption.IsChecked == true && UsbTetherAdapterPresent())
        {
            ProbeTetherWait();
            return;
        }
        if (!Running || TetherOption.IsChecked != true || _receiver.PhoneHost is not string host) { _cableMissing = 0; return; }
        _cableMissing = HasAdapterNear(host) ? 0 : _cableMissing + 1;
        if (_cableMissing < 2) return; // two seconds without the adapter: a wiggled cable does not count
        _cableMissing = 0;
        _receiver.Stop();
        _usbNote = "The USB connection ended. Reconnect the cable and enable USB tethering. If it does not reconnect automatically, press Start.";
        _waitForTether = _autoConnect;
        Diagnostics.Log("app", "USB connection: cable unplugged, stopped");
        Show(_receiver.Status);
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        SaveSettings(CurrentSettings());
        if (AutoStartBox.IsChecked == true && _usb.PhonePresent && !Running && !NetworkLink) StartPhoneCam();
    }

    // Streamsthetic's defaults per section (docs/ROADMAP.md "Reset to defaults"); the phone validates and applies them
    private const string CameraDefaults = "cam=0;zoom=1;w=1920;h=1080;fps=60;mbps=20;orient=auto";
    private const string PictureDefaults = "ev=0;focus=auto;wb=auto;flicker=auto;stab=0;mirror=0";

    private void OnResetCamera(object sender, RoutedEventArgs e) =>
        Reset(CameraDefaults, "Phone camera settings are back to the defaults.");

    private void OnResetPicture(object sender, RoutedEventArgs e) =>
        Reset(PictureDefaults, "Picture settings are back to the defaults.");

    /// <summary>
    /// No "are you sure?": every value is one click away from being changed back. The controls update when the phone
    /// confirms (it reports its settings), and a line under the sections says what happened for a few seconds.
    /// </summary>
    private void Reset(string defaults, string done)
    {
        Diagnostics.Log("app", $"user reset: {defaults}");
        _receiver.SendSettings(defaults);
        CameraHint.Text = done;
        _noteUntil = DateTime.Now.AddSeconds(5);
        var tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        tick.Tick += (_, _) =>
        {
            tick.Stop();
            if (CameraHint.Text == done && CameraPanel.IsEnabled) CameraHint.Text = "Changes apply to the selected phone.";
        };
        tick.Start();
    }

    private DateTime _noteUntil = DateTime.MinValue; // a short note (e.g. after Reset) keeps the hint line until then

    /// <summary>Applies the chosen look to every Streamsthetic window at once and remembers it.</summary>
    private void OnAppearanceChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (AppearanceBox.SelectedItem is not CameraChoice choice) return;
        // --snapshot sets its own theme for the picture it renders
        _theme = choice.Values;
        if (!Environment.GetCommandLineArgs().Contains("--snapshot")) App.ApplyTheme(_skin, _theme); // --snapshot sets its own look
        if (Environment.GetCommandLineArgs().Contains("--snapshot")) return; // a screenshot run must never change the saved settings
        Diagnostics.Log("app", $"appearance: {_theme}");
        SaveSettings(CurrentSettings());
    }

    private void LoadMicChoices()
    {
        void Show(IEnumerable<string> names)
        {
            _loadingMic = true;
            var choices = new List<CameraChoice> { new("Off", ""), new("Default speakers or headphones", "default") };
            choices.AddRange(names.Select(n => new CameraChoice(n, n)));
            if (_micOutput is not ("" or "default") && !choices.Any(ch => ch.Values == _micOutput)) choices.Add(new(_micOutput, _micOutput));
            MicBox.ItemsSource = choices;
            MicBox.SelectedItem = choices.FirstOrDefault(ch => ch.Values == _micOutput) ?? choices[0];
            _loadingMic = false;
            // Windows has no built-in way to add a microphone: apps need a virtual cable. Say so, and say when one is installed.
            var cable = names.FirstOrDefault(n => n.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
            MicNoteText.Text = cable != null
                ? "VB-Cable found. Choose \"" + cable + "\" above, then pick \"CABLE Output\" as the microphone in OBS or Discord. "
                : "For a microphone input in OBS or a calling app, install a virtual audio cable such as VB-Cable. To listen locally, choose your speakers or headphones. ";
            MicNoteLink.Inlines.Clear(); if (cable == null) MicNoteLink.Inlines.Add("Get VB-Cable");
        }
        Show([]);
        Task.Run(() => ReceiverController.ListAudioOutputs()).ContinueWith(t => { if (t.IsCompletedSuccessfully) Show(t.Result); }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnOpenLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnMicChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingMic || MicBox.SelectedItem is not CameraChoice choice) return;
        _micOutput = choice.Values;
        if (Environment.GetCommandLineArgs().Contains("--snapshot")) return;
        Diagnostics.Log("app", $"phone microphone output: {(_micOutput == "" ? "off" : _micOutput)}");
        SaveSettings(CurrentSettings());
        _usbNote = _micOutput == "" ? null : "The audio output changes when you restart the connection.";
    }

    /// <summary>
    /// The live picture may not grow taller than the window leaves room for: a wide window used to make a 16:9 picture
    /// taller than the screen, pushing the readouts and the Start button out of sight. Letterboxed in black instead.
    /// </summary>
    private void FitPreview()
    {
        var scale = SizeFactors[Math.Clamp(_uiScale, 0, SizeFactors.Length - 1)];
        PreviewBox.MaxHeight = Math.Max(240, ActualHeight / scale - 470);
    }

    private void OnDecodeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DecodeBox.SelectedItem is not CameraChoice choice) return;
        _decodeMode = choice.Values;
        DecodeExplain.Text = DecodeExplanation(_decodeMode);
        if (Environment.GetCommandLineArgs().Contains("--snapshot")) return;
        Diagnostics.Log("app", $"video decoding: {choice.Label}");
        SaveSettings(CurrentSettings());
        if (Running) _usbNote = "The new decoding choice starts the next time you press Start.";
    }

    private DateTime _lastHeatNote = DateTime.MinValue;

    // ---- paired phones: the PC remembers every phone that connected (name and last time), so it can say "paired" before Start ----
    private string _paired = ""; // "name|ticks;name|ticks"
    private string? _connectedPhone;

    private void OnHeatAutoChanged(object sender, RoutedEventArgs e)
    {
        if (_showingPhoneSettings) return;
        var on = HeatAutoBox.IsChecked == true;
        Diagnostics.Log("app", $"cool down automatically: {on}");
        _receiver.SendSettings("heatauto=" + (on ? 1 : 0));
    }

    private List<(string Name, DateTime Seen)> PairedList() => _paired.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(e => e.Split('|')).Where(p => p.Length == 2 && long.TryParse(p[1], out var ticks) && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks)
        .Select(p => (p[0], new DateTime(long.Parse(p[1])))).ToList();

    private static string SeenText(DateTime t) => t.Date == DateTime.Today ? "today " + t.ToString("HH:mm") : t.ToString("d MMM");

    private void RefreshPaired()
    {
        if (PairedText == null) return;
        var list = PairedList();
        PairedText.Text = list.Count == 0
            ? "No phone paired yet. Scan the QR code with the phone, then press Start on the phone."
            : "Paired: " + string.Join(", ", list.Select(p => $"{p.Name} (last seen {SeenText(p.Seen)})")) + ". Press Start on the phone to connect.";
        ForgetButton.Visibility = list.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshPhoneRows();
    }

    private void RememberPhone(string name)
    {
        var list = PairedList().Where(p => p.Name != name).ToList();
        list.Add((name, DateTime.Now));
        _paired = string.Join(';', list.Select(p => $"{p.Name.Replace(';', ' ').Replace('|', ' ')}|{p.Seen.Ticks}"));
        RefreshPaired();
        SaveSettings(CurrentSettings());
    }

    private void OnAutoConnectChanged(object sender, RoutedEventArgs e)
    {
        _autoConnect = AutoConnectBox.IsChecked == true;
        Diagnostics.Log("app", $"connect automatically: {_autoConnect}");
        SaveSettings(CurrentSettings());
    }

    /// <summary>One press: opening Streamsthetic on the PC is enough, it waits for the phone (USB connection or Wi-Fi) and connects the moment the phone starts.</summary>
    private void AutoConnectIfWanted()
    {
        ResumeRememberedPhones();
        if (!_autoConnect || Running || !_setupDone || _unlockWait != null) return;
        if (Environment.GetCommandLineArgs().Any(a => a is "--snapshot" or "--selftest")) return;
        if (!NetworkLink) return; // USB debugging has its own "Start when plugged in"
        if (SelectedPhone.Endpoint == null) StartPhoneCam(auto: true);
    }

    private void OnMatchFpsChanged(object sender, RoutedEventArgs e)
    {
        _matchFps = MatchFpsBox.IsChecked == true;
        Diagnostics.Log("app", $"match frame rate to the app: {_matchFps}");
        SaveSettings(CurrentSettings());
    }

    private void ApplyUiScale()
    {
        FitPreview();
        var factor = SizeFactors[_uiScale];
        RootGrid.LayoutTransform = factor == 1.0 ? System.Windows.Media.Transform.Identity : new System.Windows.Media.ScaleTransform(factor, factor);
        MinWidth = Math.Min(1120 * factor, SystemParameters.WorkArea.Width);
        // a bigger layout needs a bigger window (never smaller than before, never bigger than the screen)
        if (WindowState == WindowState.Normal && IsLoaded)
        {
            Width = Math.Max(Width, Math.Min(1240 * factor, SystemParameters.WorkArea.Width));
            Height = Math.Max(Height, Math.Min(880 * factor, SystemParameters.WorkArea.Height));
        }
    }

    private void OnSizeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SizeBox.SelectedItem is not CameraChoice choice) return;
        _uiScale = int.Parse(choice.Values);
        ApplyUiScale();
        if (Environment.GetCommandLineArgs().Contains("--snapshot")) return;
        Diagnostics.Log("app", $"text and icon size: {choice.Label}");
        SaveSettings(CurrentSettings());
    }

    /// <summary>A new look (Studio, Creator, Gamer, Brutal, Dot): applied to every window at once and remembered.</summary>
    private void OnSkinChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SkinBox.SelectedItem is not CameraChoice choice) return;
        _skin = choice.Values;
        if (!Environment.GetCommandLineArgs().Contains("--snapshot")) App.ApplyTheme(_skin, _theme);
        // Gamer is always dark: Appearance does not apply
        AppearanceBox.IsEnabled = _skin != "Gamer";
        AppearanceNote.Visibility = _skin == "Gamer" ? Visibility.Visible : Visibility.Collapsed;
        if (Environment.GetCommandLineArgs().Contains("--snapshot")) return; // a screenshot run must never change the saved settings
        Diagnostics.Log("app", $"theme: {_skin}");
        SaveSettings(CurrentSettings());
    }

    /// <summary>Auto-rotate on: the picture follows the phone. Off: the Turn buttons decide (and become usable).</summary>
    private void OnAutoRotateChanged(object sender, RoutedEventArgs e)
    {
        if (TurnLeftButton == null) return; // still loading
        TurnLeftButton.IsEnabled = TurnRightButton.IsEnabled = AutoRotateBox.IsChecked != true;
        if (_showingPhoneSettings) return;
        var settings = AutoRotateBox.IsChecked == true ? "orient=auto" : "orient=manual";
        Diagnostics.Log("app", $"user changed setting: {settings}");
        _receiver.SendSettings(settings);
    }

    /// <summary>Manual mode: a quarter turn left (-90) or right (+90) from where the picture is now.</summary>
    private void OnTurn(object sender, RoutedEventArgs e)
    {
        var by = int.Parse((string)((FrameworkElement)sender).Tag);
        _rotation = (_rotation + by + 360) % 360;
        var settings = $"orient=manual;mrot={_rotation}";
        Diagnostics.Log("app", $"user changed setting: {settings}");
        _receiver.SendSettings(settings);
    }

    // when the user last changed each choice: the phone keeps echoing its OLD settings until it has applied the new
    // ones (a lens switch takes a moment), and that echo must not flip the control back to what it was
    private readonly Dictionary<object, DateTime> _userChangedAt = new();

    private void OnCameraSettingChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_showingPhoneSettings || sender is not System.Windows.Controls.Primitives.Selector { SelectedItem: CameraChoice choice }) return;
        _userChangedAt[sender] = DateTime.Now;
        Diagnostics.Log("app", $"user changed setting: {choice.Values}");
        _receiver.SendSettings(choice.Values);
    }

    private void Echo(System.Windows.Controls.Primitives.Selector box, CameraChoice[] choices, IReadOnlyDictionary<string, string> phone)
    {
        if (_userChangedAt.TryGetValue(box, out var at) && DateTime.Now - at < TimeSpan.FromSeconds(4)) return;
        box.SelectedItem = Matching(choices, phone);
    }

    /// <summary>Selects what the phone really streams, whoever changed it (this app or the phone's own screen).</summary>
    private void ShowPhoneSettings(IReadOnlyDictionary<string, string> phone)
    {
        // the phone reports its heat state (0 none ... 3 severe): from moderate on it lowers the quality by itself, say so here too
        if (Int(phone, "heat") is int heat && heat >= 2 && DateTime.Now - _lastHeatNote > TimeSpan.FromSeconds(60))
        {
            _lastHeatNote = DateTime.Now;
            CameraHint.Text = "The phone reduced video quality because it is warm. Let it cool before raising the settings again.";
            _noteUntil = DateTime.Now.AddSeconds(25);
        }

        _showingPhoneSettings = true;
        try
        {
            if (Int(phone, "heatauto") is int ha) HeatAutoBox.IsChecked = ha == 1;
            Echo(LensBox, Lenses, phone);
            Echo(ResolutionBox, Resolutions, phone);
            Echo(FrameRateBox, FrameRates, phone);
            Echo(QualityBox, Qualities, phone);
            Echo(FocusBox, FocusModes, phone);
            Echo(WhiteBalanceBox, WhiteBalances, phone);
            Echo(FlickerBox, FlickerModes, phone);
            Echo(StabilizationBox, StabilizationModes, phone);
            Echo(MirrorBox, MirrorModes, phone);
            // FORMAT readout and footer: what the phone streams now
            var format = Matching(Resolutions, phone)?.Label ?? (phone.TryGetValue("h", out var h) ? $"{h}p" : "--");
            SetReadout(FormatReadout, format, "ReadoutHue3Brush");
            FormatUnit.Text = phone.TryGetValue("fps", out var f) ? $"{f} fps" : "";
            FooterRight.Text = $"{LinkName}  ·  " +
                $"{(phone.TryGetValue("w", out var w) && phone.TryGetValue("h", out var hh) ? $"{w}×{hh}" : "")}  ·  " +
                $"{(phone.TryGetValue("mbps", out var m) ? $"{m} Mbps" : "")}  //////";
            AutoRotateBox.IsChecked = !(phone.TryGetValue("orient", out var orient) && orient == "manual");
            _rotation = Int(phone, "rot") ?? 0;
            TurnLeftButton.IsEnabled = TurnRightButton.IsEnabled = AutoRotateBox.IsChecked != true;
            // brightness range depends on the lens; 0..0 means the camera did not report one yet
            if (Int(phone, "evmin") is int min && Int(phone, "evmax") is int max && min < max)
            {
                _evStep = phone.TryGetValue("evstep", out var step) && double.TryParse(step, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0;
                BrightnessSlider.Minimum = min;
                BrightnessSlider.Maximum = max;
                if (!BrightnessSlider.IsMouseCaptureWithin) BrightnessSlider.Value = Int(phone, "ev") ?? 0;
                BrightnessSlider.IsEnabled = true;
            }
            else
            {
                BrightnessSlider.IsEnabled = false;
            }
            ShowBrightness();
            if (Number(phone, "zmin") is double zmin && Number(phone, "zmax") is double zmax && zmin < zmax)
            {
                ZoomSlider.Minimum = zmin;
                ZoomSlider.Maximum = zmax;
                // the phone echoes every step; while the user drags, an older echo must not pull the thumb back
                if (!ZoomSlider.IsMouseCaptureWithin) ZoomSlider.Value = Number(phone, "zoom") ?? 1;
                ZoomSlider.IsEnabled = true;
            }
            else
            {
                ZoomSlider.IsEnabled = false; // camera not open yet, or no zoom on this one
            }
            ZoomText.Text = $"{ZoomSlider.Value:0.0}x";
        }
        finally
        {
            _showingPhoneSettings = false;
        }
        CameraPanel.IsEnabled = true;
        PicturePanel.IsEnabled = true;
        // the phone confirming a reset must not hide the "back to the defaults" note straight away
        if (DateTime.Now >= _noteUntil) CameraHint.Text = "Changes apply to the selected phone.";
    }

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessText == null) return; // during InitializeComponent
        ShowBrightness();
        if (!_showingPhoneSettings) _receiver.SendSettings($"ev={(int)Math.Round(BrightnessSlider.Value)}");
    }

    private void ShowBrightness()
    {
        var ev = Math.Round(BrightnessSlider.Value) * _evStep;
        BrightnessText.Text = _evStep > 0 ? $"{ev:+0.0;-0.0;0} EV" : "";
    }

    private void OnZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ZoomText == null) return; // during InitializeComponent
        ZoomText.Text = $"{ZoomSlider.Value:0.0}x";
        if (!_showingPhoneSettings)
        {
            _receiver.SendSettings("zoom=" + ZoomSlider.Value.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static double? Number(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var text) && double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

    private static int? Int(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var text) && int.TryParse(text, out var value) ? value : null;

    private static CameraChoice? Matching(CameraChoice[] choices, IReadOnlyDictionary<string, string> phone) =>
        choices.FirstOrDefault(c => c.Values.Split(';').All(pair =>
        {
            var (key, value) = (pair.Split('=')[0], pair.Split('=')[1]);
            if (!phone.TryGetValue(key, out var actual)) return false;
            if (key == "zoom")
            {
                return double.TryParse(actual, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a)
                    && Math.Abs(a - double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)) < 0.01;
            }
            return actual == value;
        }));

    // ---- pairing code and its QR picture (Wi-Fi) ----
    private static string NewPairingCode() => System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>Only digits can be typed or pasted into the code box.</summary>
    private void OnCodeTyping(object sender, System.Windows.Input.TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void OnCodePasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !text.All(char.IsAsciiDigit)) e.CancelCommand();
    }

    private void OnCodeChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        DrawQr();
        if (CodeSaveButton != null) CodeSaveButton.IsEnabled = Regex.IsMatch(CodeBox.Text.Trim(), @"^\d{6}$");
        if (CodeSavedNote != null) CodeSavedNote.Visibility = Visibility.Collapsed;
    }

    /// <summary>"Use this code": keeps the typed code and says so (Enter in the box does the same).</summary>
    private void OnUseCode(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim();
        if (!Regex.IsMatch(code, @"^\d{6}$")) return;
        SaveSettings(CurrentSettings(true, code));
        CodeSavedNote.Text = "Pairing code saved. Press Start on the phone. If it does not connect automatically, press Start here.";
        CodeSavedNote.Visibility = Visibility.Visible;
    }

    private void OnCodeKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) OnUseCode(sender, e);
    }

    // ---- "Advanced": USB debugging stays hidden until asked for (open from the start when it is the chosen route) ----
    private void OnToggleAdvanced(object sender, RoutedEventArgs e) => SetAdvancedOpen(AdvancedPanel.Visibility != Visibility.Visible);

    private void SetAdvancedOpen(bool open)
    {
        Motion.Reveal(AdvancedPanel, open);
        AdvancedToggle.Content = open ? "Advanced  \u25B4" : "Advanced  \u25BE";
    }

    // ---- the Connection panel folds away once a phone is connected (arrow next to its title) ----
    private bool _connectionOpen = true;
    private bool _wasConnected;

    private void OnToggleConnection(object sender, RoutedEventArgs e) => SetConnectionOpen(!_connectionOpen);

    private void SetConnectionOpen(bool open)
    {
        _connectionOpen = open;
        ConnToggle.Content = open ? "▾" : "▸";
        Motion.Reveal(ConnectionBody, open);
        ConnSummary.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Folds the panel when the phone connects, opens it again when the connection ends. The user's own click wins until the next change.</summary>
    private void UpdateConnectionFold(ReceiverStatus s)
    {
        var connected = s.State is LinkState.Live or LinkState.WaitingForApp;
        // a reconnect in between (every resolution or frame-rate click restarts the stream for a moment) must not open the panel for a split second:
        // only a real end (Stopped, Error) counts as "not connected"
        if (!connected && s.State is not (LinkState.Stopped or LinkState.Error)) return;
        ConnToggle.Visibility = connected || !_connectionOpen ? Visibility.Visible : Visibility.Collapsed;
        ConnSummary.Text = $"{LinkName}  ·  {(s.PhoneName ?? "connected")}";
        if (connected != _wasConnected)
        {
            _wasConnected = connected;
            SetConnectionOpen(!connected);
            if (!connected) ConnToggle.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Draws the QR code the phone scans: a link the Streamsthetic app (or the phone's own camera) opens. Blank until the code has 6 digits.</summary>
    private void DrawQr()
    {
        if (QrImage == null || CodeBox == null) return;
        var code = CodeBox.Text.Trim();
        if (!Regex.IsMatch(code, @"^\d{6}$")) { QrImage.Source = null; return; }
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode($"streamsthetic://pair?code={code}", QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        QrImage.Source = image;
    }
    private void OnLinkChanged(object sender, RoutedEventArgs e)
    {
        // also runs while InitializeComponent is still creating the controls (the USB option starts checked)
        if (UsbOption?.IsChecked == true && AdvancedPanel != null && AdvancedPanel.Visibility != Visibility.Visible) SetAdvancedOpen(true);
        var wifi = NetworkLink;
        if (CodePanel != null) Motion.Reveal(CodePanel, WifiOption?.IsChecked == true);
        if (WifiOption?.IsChecked == true && CodeBox != null && !Regex.IsMatch(CodeBox.Text.Trim(), @"^\d{6}$")) CodeBox.Text = NewPairingCode(); // first time: make a code so the QR picture is ready
        if (AutoStartPanel != null) Motion.Reveal(AutoStartPanel, !wifi);
    }

    private void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_startingPhones.Contains(_selectedSlot)) { _connectEpoch++; _manualStops.Add(_selectedSlot); return; }
        if (_wifiWaiting && _selectedSlot == 1) { _connectEpoch++; _wifiWaiting = false; Show(_receiver.Status); return; }
        if (_unlockWait != null && _selectedSlot == 1)
        {
            _connectEpoch++;
            _unlockWait.Cancel(); // Stop while waiting for the phone to be unlocked
            return;
        }
        if (Running)
        {
            _connectEpoch++;
            _manualStops.Add(_selectedSlot);
            _receiver.Stop();
            return;
        }
        _manualStops.Remove(_selectedSlot);
        StartPhoneCam();
    }

    /// <summary>
    /// True while Windows has a working USB network adapter from a phone (what USB tethering creates: "Remote NDIS", RNDIS, NCM or
    /// "USB Ethernet"), with an address that is not the "no network" self-assigned one.
    /// </summary>
    private static bool UsbTetherAdapterPresent()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (!Regex.IsMatch(nic.Description, @"RNDIS|Remote NDIS|\bNCM\b|USB.*Ethernet|Ethernet.*USB", RegexOptions.IgnoreCase)) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.")) return true;
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return true; } // cannot tell: do not block
        return false;
    }

    private const string CameraClsid = "{31621596-4208-4D72-9066-1F9507074423}";

    /// <summary>True when the Streamsthetic virtual camera is registered with Windows (the one-time install step ran).</summary>
    private static bool CameraInstalled(int slot = 1)
    {
        if (Environment.GetCommandLineArgs().Contains("--pretend-no-camera")) return false; // for testing the offer below
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{(slot == 1 ? CameraClsid : $"{{31621596-4208-4D72-9066-1F950707442{slot + 2}}}")}");
            return key != null && File.Exists(@"C:\ProgramData\PhoneCam\vcam\VCamSampleSource.dll");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return true; } // cannot tell: let the receiver try
    }

    /// <summary>Runs the one-time camera install (Windows asks for permission). Returns true when the camera is registered afterwards.</summary>
    private async Task<bool> InstallCameraAsync()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "install-camera.ps1");
        if (!File.Exists(script)) return false;
        Show(new(LinkState.Starting, "Installing the Streamsthetic camera… approve the Windows permission prompt."));
        await Task.Run(() =>
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"") { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(120000);
        });
        var ok = CameraInstalled();
        Diagnostics.Log("app", $"camera install finished, installed: {ok}");
        return ok;
    }

    /// <summary>The phone's address on its USB network (it is the gateway the phone hands out), or null when there is no USB network.</summary>
    private static string? UsbTetherPhoneAddress()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (!Regex.IsMatch(nic.Description, @"RNDIS|Remote NDIS|\bNCM\b|USB.*Ethernet|Ethernet.*USB", RegexOptions.IgnoreCase)) continue;
                var ip = nic.GetIPProperties();
                var gw = ip.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                         ?? ip.DhcpServerAddresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (gw != null) return gw.ToString();
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        return null;
    }

    private bool _waitForTether; // auto-connect is waiting for the phone's USB network to appear

    private enum PhoneLink { Reachable, NotListening, Unreachable }

    /// <summary>
    /// Asks the phone's USB address whether anything answers (a quick connect to the stream port). "Not listening" is fine (the phone app
    /// has not been started yet); "Unreachable" means the USB network exists on this PC but the phone does not answer on it: USB tethering is
    /// stuck (found on a PC where Windows showed the USB adapter as Up with an unanswered ARP entry).
    /// </summary>
    private static PhoneLink CheckUsbPhone(string host)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            return tcp.ConnectAsync(host, 5556).Wait(1500) ? PhoneLink.Reachable : PhoneLink.Unreachable;
        }
        catch (AggregateException ex) when (ex.InnerException is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused })
        {
            return PhoneLink.NotListening;
        }
        catch (Exception) { return PhoneLink.Unreachable; }
    }

    private const string UsbStuckText = "USB tethering is on, but the phone does not answer over the cable.\n\n" +
        "1. On the phone, turn USB tethering off and on again (Settings > Connections > Mobile Hotspot and Tethering > USB tethering).\n" +
        "2. Wait about 10 seconds, then try again.\n" +
        "3. If it still does not connect, try another USB port or data cable, or use Wi-Fi.";

    private bool _tetherProbing;

    /// <summary>Auto-connect waiting for USB tethering: connects when the USB network is up AND the phone answers on it.</summary>
    private async void ProbeTetherWait()
    {
        if (_tetherProbing) return;
        _tetherProbing = true;
        try
        {
            var gw = UsbTetherPhoneAddress();
            if (gw == null) return;
            var link = await Task.Run(() => CheckUsbPhone(gw));
            if (!_waitForTether || Running) return;
            if (link != PhoneLink.Unreachable)
            {
                _waitForTether = false;
                Diagnostics.Log("app", "USB network answers: connecting");
                StartPhoneCam(auto: true);
            }
            else
            {
                var note = "USB tethering is on, but the phone does not answer over the cable. Turn USB tethering off and on again on the phone.";
                if (_usbNote != note) { _usbNote = note; Show(_receiver.Status); }
            }
        }
        finally { _tetherProbing = false; }
    }

    private async void StartPhoneCam(bool auto = false)
    {
        if (SelectedPhone.Endpoint != null && (_selectedSlot != 1 || WifiOption.IsChecked == true))
        { await StartNetworkPhone(SelectedPhone, auto); return; }
        if (_selectedSlot == 1 && SelectedPhone.Endpoint != null)
        {
            // Choosing a USB route replaces camera 1's network assignment, without affecting extra cameras.
            _sessions.Forget(1);
            SaveSettings(CurrentSettings());
        }
        if (WifiOption.IsChecked == true && _selectedSlot == 1) { await StartFirstNetworkPhone(auto); return; }
        if (_selectedSlot != 1) { PhonesNote.Text = "Find phones, select one, then connect it to this camera."; return; }
        if (_unlockWait != null || _startingPrimary) return;
        _startingPrimary = true;
        var receiver = _sessions.Find(1).Receiver;
        var epoch = _connectEpoch;
        void Display(ReceiverStatus status) { if (!_phonesClosing && _selectedSlot == 1) Show(status); }
        try
        {
            // the virtual camera must be installed first (a clean PC does not have it: the receiver then failed with a cryptic 0x80040154)
            if (!CameraInstalled())
            {
                if (auto)
                {
                    _usbNote = "The Streamsthetic camera is not installed on this PC yet. Press Start to install it.";
                    Display(receiver.Status);
                    return;
                }
                var answer = System.Windows.MessageBox.Show(this,
                    "Streamsthetic needs to add its camera to Windows before it can start. This happens once.\n\n" +
                    "Windows will ask for administrator permission. After approval, Streamsthetic continues automatically.\n\nInstall the camera now?",
                    "Install the Streamsthetic camera", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                if (answer != System.Windows.MessageBoxResult.Yes) { _usbNote = "Streamsthetic needs its camera installed. Press Start to try again."; Display(receiver.Status); return; }
                if (!await InstallCameraAsync())
                {
                    Display(new(LinkState.Error, "The camera could not be installed. Press Start to try again and approve the Windows permission prompt. If it keeps failing, use Report a problem."));
                    return;
                }
            }
            // the USB connection needs the phone's USB tethering switched on first: say so instead of starting and stopping again
            if (TetherOption.IsChecked == true && auto && !UsbTetherAdapterPresent())
            {
                // opened (or replugged) before USB tethering is on: wait for the phone's USB network, then connect by itself
                _waitForTether = true;
                _usbNote = "Waiting for USB tethering. Plug the phone in and turn on USB tethering (the phone app shows how).";
                Display(receiver.Status);
                return;
            }
            if (TetherOption.IsChecked == true && !auto && !UsbTetherAdapterPresent())
            {
                _usbNote = "USB tethering is not on yet. Turn it on in the phone app (Show the setup guide), then press Start.";
                Display(receiver.Status);
                System.Windows.MessageBox.Show(this,
                    "Your phone is not sharing its USB connection yet, so Streamsthetic cannot start.\n\n" +
                    "1. Plug the phone into this PC with a USB cable.\n" +
                    "2. On the phone, open Streamsthetic, choose USB connection and tap Show the setup guide. It takes you to the switch.\n" +
                    "    (Or open Settings > Connections > Mobile Hotspot and Tethering and turn on USB tethering.)\n" +
                    "3. Come back here and press Start again.",
                    "Turn on USB tethering on your phone", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }
            if (_phonesClosing || epoch != _connectEpoch) return;
            var wifi = NetworkLink;
            var code = CodeBox.Text.Trim();
            if (WifiOption.IsChecked == true && !Regex.IsMatch(code, @"^\d{6}$"))
            {
                if (!auto) Display(new(LinkState.Error, "Enter the 6-digit pairing code shown in the Streamsthetic app on your phone."));
                return;
            }
            SaveSettings(CurrentSettings(WifiOption.IsChecked == true, code));
            _usbNote = null;

            // On USB the PC also opens the Streamsthetic app on the phone, which starts streaming with the phone's remembered
            // settings. Asked of the phone itself each time: the cable watcher may not have reported it yet.
            if (!wifi)
            {
                using var wait = new CancellationTokenSource();
                _unlockWait = wait;
                Display(new(LinkState.WaitingForPhone, "Looking for the phone…"));
                var go = await Task.Run(() => OpenPhoneApp(wait.Token, () => Dispatcher.Invoke(() =>
                    Display(new(LinkState.WaitingForPhone, "Unlock your phone to start Streamsthetic. It starts by itself once unlocked.")))));
                _unlockWait = null;
                if (!go || _phonesClosing || epoch != _connectEpoch)
                {
                    Display(receiver.Status);
                    return;
                }
            }
            try
            {
                var audio = _micOutput == "" ? "" : $" --audio-device \"{_micOutput.Replace("\"", "")}\"";
                var decode = _decodeMode switch { "cpu" => " --cpu", "gpu" => " --gpu", _ => "" };
                // the USB connection goes straight to the phone's address on the USB network: a search over all networks could find the
                // phone on Wi-Fi first, where the cable needs no code but Wi-Fi does ("The phone did not accept that code", found on a PC with both)
                var usbPhone = TetherOption.IsChecked == true ? UsbTetherPhoneAddress() : null;
                if (usbPhone != null && await Task.Run(() => CheckUsbPhone(usbPhone)) == PhoneLink.Unreachable)
                {
                    // the receiver would sit for ~20 s on a dead connection and look like "starting and stopping"
                    Diagnostics.Log("app", $"USB phone address {usbPhone} does not answer");
                    if (auto) { _waitForTether = true; _usbNote = "USB tethering is on, but the phone does not answer over the cable. Turn USB tethering off and on again on the phone."; Display(receiver.Status); }
                    else
                    {
                        _usbNote = "The phone does not answer over the cable. Turn USB tethering off and on again on the phone.";
                        Display(receiver.Status);
                        System.Windows.MessageBox.Show(this, UsbStuckText, "The phone does not answer", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    }
                    return;
                }
                if (_phonesClosing || epoch != _connectEpoch) return;
                receiver.PhoneHost = usbPhone;
                var link = WifiOption.IsChecked == true ? $"--wifi --code {code}"
                    : usbPhone != null ? $"--host {usbPhone} --port 5556 --code 000000"
                    : wifi ? "--wifi --code 000000" : "";
                receiver.Start(link + audio + decode); // the cable needs no code: the phone trusts the USB link
            }
            catch (Exception ex)
            {
                Display(new(LinkState.Error, ex.Message));
            }
        }
        finally
        {
            _startingPrimary = false;
            UpdateLinkAvailability();
        }
    }

    /// <summary>
    /// Gets the phone app streaming before the receiver connects. Android lets the Streamsthetic app start the camera
    /// only while the phone is unlocked, so a locked phone that is not streaming yet is waited for. A phone that
    /// already streams needs nothing, locked or not. Returns false if cancelled (Stop) or unplugged meanwhile.
    /// </summary>
    private static bool OpenPhoneApp(CancellationToken cancel, Action onLocked)
    {
        bool PhoneThere() => UsbPhoneWatcher.RunAdb("get-state").Trim() == "device";
        if (!PhoneThere()) return true; // no phone yet: the receiver waits for it
        if (UsbPhoneWatcher.RunAdb("shell dumpsys activity services com.phonecam").Contains("CameraStreamService")) return true;
        var told = false;
        while (UsbPhoneWatcher.RunAdb("shell dumpsys window").Contains("isKeyguardShowing=true"))
        {
            if (!told)
            {
                onLocked();
                told = true;
            }
            if (cancel.WaitHandle.WaitOne(1000) || !PhoneThere()) return false;
        }
        UsbPhoneWatcher.RunAdb("shell am start -n com.phonecam/.MainActivity --ez autostart true");
        return true;
    }

    private void Show(ReceiverStatus s)
    {
        UpdateConnectionFold(s);
        CameraNameHint.Text = $"In OBS or another app, choose \"{(_selectedSlot == 1 ? "Streamsthetic" : $"Streamsthetic {_selectedSlot}")} (Windows Virtual Camera)\" for this phone.";
        var nowConnected = s.State is LinkState.Live or LinkState.WaitingForApp ? s.PhoneName : null;
        if (nowConnected != _connectedPhone) { _connectedPhone = nowConnected; RefreshPaired(); }
        StatusText.Text = s.State switch
        {
            LinkState.Live => s.PhoneName != null ? $"Live from {s.PhoneName}" : "Live",
            LinkState.Stopped => "Stopped",
            LinkState.Error => "Problem",
            LinkState.WaitingForApp => s.PhoneName != null ? $"Connected to {s.PhoneName}" : "Phone connected",
            _ => "Connecting",
        };
        DetailText.Text = s.State switch
        {
            // the numbers are in the readouts below the picture; this line says what it means
            LinkState.Live when s.Fps > 0 => "Video is available to apps using this camera.",
            LinkState.Stopped => UsbProblemText ?? _usbNote ?? (TetherOption.IsChecked == true
                ? "Plug in the phone, turn on USB tethering (the phone app shows how), then press Start."
                : WifiOption.IsChecked == true
                ? "Start the Streamsthetic app on your phone, then press Start."
                : "Plug in your phone, then press Start."),
            _ => s.Message,
        };
        // colors come from the theme tokens, so they follow light/dark
        var tone = s.State switch
        {
            LinkState.Live => "GoodBrush",
            LinkState.Error => "BadBrush",
            LinkState.Stopped => "IdleBrush",
            _ => "WaitBrush",
        };
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, tone);
        StatusChip.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, tone);
        StatusChipText.Text = s.State switch
        {
            LinkState.Live => "● Live",
            LinkState.Stopped => "Stopped",
            LinkState.Error => "Problem",
            LinkState.WaitingForApp => "Ready",
            _ => "Connecting",
        };
        // title: the phone's own name (Wi-Fi discovery reports it; on USB it is asked for over the cable)
        var name = s.PhoneName ?? (NetworkLink ? null : _usbPhoneName);
        PhoneText.Text = s.State switch
        {
            LinkState.Live or LinkState.WaitingForApp => name ?? "Your phone",
            LinkState.Stopped or LinkState.Error => (NetworkLink ? null : _usbPhoneName) ?? "Not connected",
            _ => "Looking for the phone…",
        };
        LinkText.Text = LinkName;
        SetReadout(FpsReadout, s.State is LinkState.Live or LinkState.WaitingForApp && s.Fps > 0 ? $"{s.Fps:0}" : null, "ReadoutHue1Brush");
        SetReadout(LatencyReadout, s.State is LinkState.Live or LinkState.WaitingForApp && !double.IsNaN(s.LatencyMs) ? $"{s.LatencyMs:0}" : null, "ReadoutHue2Brush");
        if (s.State is LinkState.Stopped or LinkState.Error)
        {
            SetReadout(FormatReadout, null, "ReadoutHue3Brush");
            FormatReadout.Text = "—"; // "00" would read as a resolution
            FormatUnit.Text = "";
        }
        if (s.State == LinkState.Live) _liveSince ??= DateTime.Now;
        else if (s.State is LinkState.Stopped or LinkState.Error) _liveSince = null;
        var running = s.State is not (LinkState.Stopped or LinkState.Error);
        StartButton.Content = running ? "Stop" : "Start";
        StartButton.Tag = running ? "Stop" : "Start"; // the theme paints the Stop state red
        FixButton.Visibility = !running && _usbProblem is "unauthorized" or "offline" && !NetworkLink ? Visibility.Visible : Visibility.Collapsed;
        UpdateLinkAvailability(running);
        if (!running || s.State == LinkState.WaitingForPhone)
        {
            // nothing to control until the phone (re)connects and reports its settings
            CameraPanel.IsEnabled = false;
            PicturePanel.IsEnabled = false;
            CameraHint.Text = "Connect a phone to adjust its camera settings.";
        }
    }

    private void LoadSettings()
    {
        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<Settings>(json);
            if (settings == null) return;
            WifiOption.IsChecked = settings.Wifi;
            // settings saved before tethering existed keep the route the user had (USB debugging)
            var tether = !settings.Wifi && settings.Tether && json.Contains("\"Tether\"");
            TetherOption.IsChecked = tether;
            UsbOption.IsChecked = !settings.Wifi && !tether;
            CodeBox.Text = settings.Code;
            AutoStartBox.IsChecked = settings.AutoStart;
            _theme = Appearances.Any(a => a.Values == settings.Theme) ? settings.Theme : "System";
            _skin = Skins.Any(a => a.Values == settings.Skin) ? settings.Skin : "Studio";
            _uiScale = Math.Clamp(settings.UiScale, 0, SizeFactors.Length - 1);
            _micOutput = settings.MicOutput ?? "";
            _autoConnect = settings.AutoConnect;
            _paired = settings.Paired ?? "";
            _sessions.Restore(settings.PhoneSlots);
            RefreshPaired();
            AutoConnectBox.IsChecked = _autoConnect;
            _matchFps = settings.MatchFps;
            _decodeMode = DecodeModes.Any(m => m.Values == settings.DecodeMode) ? settings.DecodeMode : "auto";
            MatchFpsBox.IsChecked = _matchFps;
            _trayOnClose = settings.TrayOnClose;
            _startWithWindows = settings.StartWithWindows;
            // someone who already has saved settings from an earlier version has set Streamsthetic up: no guide for them
            _setupDone = settings.SetupDone || !json.Contains("SetupDone");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // first run or unreadable settings: keep the defaults
        }
    }

    private static void SaveSettings(Settings settings)
    {
        if (Environment.GetCommandLineArgs().Contains("--snapshot")) return; // screenshot runs never change the saved settings
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not being able to remember the choice is not worth interrupting the user for
        }
    }

    private bool _setupDone;

    private void OnSetupGuide(object sender, RoutedEventArgs e) => ShowSetupGuide();

    private string? _updateUrl;

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        if (_updateUrl != null) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_updateUrl) { UseShellExecute = true }); return; }
        UpdateText.Text = "  Checking...";
        var result = await UpdateChecker.CheckAsync();
        _updateUrl = result.NewerAvailable ? result.Url : null;
        UpdateText.Text = "  " + result.Message + (_updateUrl != null ? " Click Check for updates again to open the download page." : "");
        Diagnostics.Log("app", "update check: " + result.Message);
    }

    private void ShowSetupGuide()
    {
        new SetupWindow { Owner = this }.ShowDialog();
        _setupDone = true;
        SaveSettings(CurrentSettings());
    }

    private Settings CurrentSettings(bool? wifi = null, string? code = null) =>
        new(wifi ?? WifiOption.IsChecked == true, code ?? CodeBox.Text.Trim(), AutoStartBox.IsChecked == true, _theme, _skin, _trayOnClose, _startWithWindows, _setupDone, _uiScale, _micOutput, _matchFps, _decodeMode, TetherOption.IsChecked == true, _autoConnect, _paired, _sessions.Assignments.ToArray());

    protected override void OnClosing(CancelEventArgs e)
    {
        if (TryHideToTray(e)) return;
        _tray?.Dispose();
        _usb.Dispose();
        _phonesClosing = true;
        _connectEpoch++;
        _unlockWait?.Cancel();
        _phoneScanCancel.Cancel();
        _previewTimer.Stop();
        SetPreviewRendering(false);
        _phoneReconnectTimer.Stop();
        _cableTimer.Stop();
        _previewReader.Dispose();
        _sessions.Dispose();
        base.OnClosing(e);
    }
}
