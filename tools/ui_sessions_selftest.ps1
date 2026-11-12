# Runs the real WPF controls/dispatcher against two actual receivers and fake phones, without registered cameras.
param(
    [string]$Receiver = "$PSScriptRoot/../windows/receiver/x64/Release/StreamstheticReceiver.exe",
    [string]$Recording = "$PSScriptRoot/testdata/phone-1080p60-10s.pcm1"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$testDir = Join-Path ([IO.Path]::GetTempPath()) ('streamsthetic-ui-sessions-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDir | Out-Null
$projectPath = [Security.SecurityElement]::Escape((Join-Path $root 'windows/app/PhoneCam.csproj'))
$receiverPath = [Security.SecurityElement]::Escape((Resolve-Path -LiteralPath $Receiver).Path)
@"
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><NoWarn>WPF0001</NoWarn></PropertyGroup>
<ItemGroup><ProjectReference Include="$projectPath" />
<None Include="$receiverPath" Link="StreamstheticReceiver.exe" CopyToOutputDirectory="Always" />
</ItemGroup></Project>
"@ | Set-Content (Join-Path $testDir 'UiSessions.csproj')
@'
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PhoneCam;

internal static class Program
{
    private static int _result;
    [STAThread]
    public static int Main(string[] args)
    {
        var application = new App();
        application.InitializeComponent();
        App.ApplyTheme("Studio", "Light");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try { await Run(args); }
            catch (Exception ex) { Console.Error.WriteLine(ex); _result = 1; }
            finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        });
        Dispatcher.Run(); // Do not run App.OnStartup: the harness owns the only MainWindow.
        return _result;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    private static async Task Until(Func<bool> predicate, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.ElapsedMilliseconds > 15000) throw new Exception("Timed out: " + description);
            await Task.Delay(50);
        }
        Console.WriteLine("PASS: " + description);
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        Console.WriteLine("PASS: " + description);
    }
    private static PreviewReader Reader(MainWindow window) =>
        (PreviewReader)typeof(MainWindow).GetField("_previewReader", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static long DisplayedSequence(MainWindow window) =>
        (long)typeof(PreviewReader).GetField("_lastSeq", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Reader(window))!;
    private static bool RenderingEnabled(MainWindow window) =>
        (bool)typeof(MainWindow).GetField("_previewRendering", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static long PublishedSequence(int slot)
    {
        using var map = MemoryMappedFile.OpenExisting(@"Local\PhoneCamPreview" + slot, MemoryMappedFileRights.Read);
        using var view = map.CreateViewAccessor(0, 24, MemoryMappedFileAccess.Read);
        return view.ReadInt64(16) & ~1L; // if the writer is busy, the preceding complete frame is sufficient
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static async Task Run(string[] args)
    {
        Check(Environment.GetCommandLineArgs().Contains("--snapshot"), "snapshot mode disables auto-connect and settings writes");
        Check(Environment.GetCommandLineArgs().Contains("--pretend-no-camera")
            && !File.Exists(Path.Combine(AppContext.BaseDirectory, "install-camera.ps1")),
            "UI connection checks cannot install or register a camera");
        var portA = FreePort();
        var portB = FreePort();
        while (portB == portA) portB = FreePort();
        var fakeScript = """
import sys, socket, threading, types
sys.path.insert(0, sys.argv[1])
import fake_phone
fake_phone.SETTINGS.update(zmin='0.6', zmax='10.0')
packets = fake_phone.load_packets(sys.argv[2])
options = types.SimpleNamespace(code='123456', drop_after=0, corrupt_after=0)
server = socket.create_server(('127.0.0.1', int(sys.argv[3])))
print('ready', flush=True)
while True:
    conn, addr = server.accept()
    conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    threading.Thread(target=fake_phone.serve_client, args=(conn, packets, options), daemon=True).start()
""";
        Process Fake(int port, ConcurrentQueue<string> output)
        {
            var start = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-B", "-u", "-c", fakeScript, args[0], args[1], port.ToString() }) start.ArgumentList.Add(arg);
            var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, line) => { if (line.Data != null) output.Enqueue(line.Data); };
            process.ErrorDataReceived += (_, line) => { if (line.Data != null) output.Enqueue("ERROR: " + line.Data); };
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            return process;
        }
        var outputA = new ConcurrentQueue<string>();
        var outputB = new ConcurrentQueue<string>();
        using var fakeA = Fake(portA, outputA);
        using var fakeB = Fake(portB, outputB);
        MainWindow? window = null;
        PhoneSessions? sessions = null;
        var settingsPath = Path.Combine(AppData.Root, "settings.json");
        var originalSettings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        try
        {
            await Until(() => outputA.Contains("ready") && outputB.Contains("ready"), "two fake phones ready");
            window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Left = -12000, Top = -12000 };
            window.Show();
            window.ShowPhonesTab();
            sessions = (PhoneSessions)typeof(MainWindow).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            foreach (var phone in sessions.All) sessions.Forget(phone.Slot);
            sessions.Restore(new[] {
                new PhoneSlotAssignment(3, new PhoneEndpoint("ui-a", "Front phone", "127.0.0.1", portA)),
                new PhoneSlotAssignment(4, new PhoneEndpoint("ui-b", "Side phone", "127.0.0.1", portB))
            });
            var a = sessions.Find(3);
            var b = sessions.Find(4);
            a.Start("123456", "--no-camera");
            b.Start("123456", "--no-camera");
            await Until(() => a.LatestSettings.Count > 0 && b.LatestSettings.Count > 0, "dispatcher delivers settings for both sessions");
            var list = (ListBox)window.FindName("PhonesList");
            var quality = (ComboBox)window.FindName("QualityBox");
            var zoom = (Slider)window.FindName("ZoomSlider");
            var preview = (Image)window.FindName("PreviewImage");
            list.SelectedIndex = 2;
            await Until(() => preview.Source != null && preview.Visibility == Visibility.Visible, "selecting camera 3 displays its decoded preview");
            Check(RenderingEnabled(window), "actual MainWindow subscribes to render-driven preview while visible");
            var beforeStall = DisplayedSequence(window);
            long minimumAfterStall = 0, firstAfterStall = 0;
            EventHandler observeRender = (_, _) =>
            {
                var shown = DisplayedSequence(window);
                if (minimumAfterStall > 0 && firstAfterStall == 0 && shown > beforeStall) firstAfterStall = shown;
            };
            CompositionTarget.Rendering += observeRender;
            try
            {
                Thread.Sleep(180); // Only this test app stalls; receiver/fake phone continue producing.
                minimumAfterStall = PublishedSequence(3);
                await Until(() => firstAfterStall > 0, "actual render callback resumes after a 180ms UI stall");
                Check(firstAfterStall >= minimumAfterStall, "first resumed render skips stale frames and takes the latest published frame");
            }
            finally { CompositionTarget.Rendering -= observeRender; }
            window.Hide();
            Check(!RenderingEnabled(window) && !a.Receiver.Preview, "hiding the app unsubscribes rendering and disables receiver preview");
            var hiddenSequence = DisplayedSequence(window);
            await Task.Delay(180);
            Check(DisplayedSequence(window) == hiddenSequence, "hidden app does not consume preview frames");
            window.Show();
            await Until(() => RenderingEnabled(window) && a.Receiver.Preview && DisplayedSequence(window) > hiddenSequence,
                "showing the app restores its render subscription and fresh frames");
            window.WindowState = WindowState.Minimized;
            Check(!RenderingEnabled(window) && !a.Receiver.Preview, "minimizing unsubscribes rendering and disables receiver preview");
            var minimizedSequence = DisplayedSequence(window);
            await Task.Delay(180);
            Check(DisplayedSequence(window) == minimizedSequence, "minimized app does not consume preview frames");
            window.WindowState = WindowState.Normal;
            await Until(() => RenderingEnabled(window) && a.Receiver.Preview && DisplayedSequence(window) > minimizedSequence,
                "restoring the app resumes fresh render-driven preview");
            var firstPreview = preview.Source;
            Check(quality.SelectedItem?.ToString() == "20" && zoom.Value == 1, "camera 3 controls show its current settings");
            quality.SelectedIndex = 2; // 40 Mbps, through the actual SelectionChanged handler.
            zoom.Value = 2;
            await Until(() => a.LatestSettings.GetValueOrDefault("mbps") == "40" && a.LatestSettings.GetValueOrDefault("zoom") == "2.0", "camera 3 control changes reach and echo from its phone");
            Check(b.LatestSettings.GetValueOrDefault("mbps") == "20" && b.LatestSettings.GetValueOrDefault("zoom") == "1.00", "camera 4 retains its independent settings");

            list.SelectedIndex = 3;
            await Until(() => preview.Source != null && !ReferenceEquals(preview.Source, firstPreview) && preview.Visibility == Visibility.Visible, "selecting camera 4 replaces the preview source");
            Check(quality.SelectedItem?.ToString() == "20" && zoom.Value == 1, "switching phones restores camera 4 controls without old camera values");
            Check(!a.Receiver.Preview && b.Receiver.Preview, "only the selected receiver produces a preview");
            quality.SelectedIndex = 0; // 10 Mbps
            zoom.Value = 3;
            await Until(() => b.LatestSettings.GetValueOrDefault("mbps") == "10" && b.LatestSettings.GetValueOrDefault("zoom") == "3.0", "camera 4 control changes reach its own phone");
            Check(a.LatestSettings.GetValueOrDefault("mbps") == "40" && a.LatestSettings.GetValueOrDefault("zoom") == "2.0", "camera 3 values survive edits to camera 4");
            Check(outputA.Any(line => line.Contains("mbps=40")) && !outputB.Any(line => line.Contains("mbps=40"))
                && outputB.Any(line => line.Contains("mbps=10")) && !outputA.Any(line => line.Contains("mbps=10")), "wire commands are isolated by selected phone");

            list.SelectedIndex = 2;
            Check(quality.SelectedItem?.ToString() == "40" && zoom.Value == 2, "switching back restores camera 3 edited settings");
            // Category navigation retains real controls and never changes the two active streams.
            var mainTabs = (TabControl)window.FindName("MainTabs");
            var categories = (ListBox)window.FindName("SettingsCategories");
            var sectionNames = new[] { "Appearance", "Streaming", "Audio", "Help" };
            var representativeNames = new[] { "SkinBox", "DecodeBox", "MicBox", "ReportLink" };
            var controlsBefore = representativeNames.Select(name => window.FindName(name)).ToArray();
            var decode = (ListBox)window.FindName("DecodeBox");
            var mic = (ComboBox)window.FindName("MicBox");
            var decodeBefore = decode.SelectedItem;
            var micBefore = mic.SelectedItem;
            var aBeforeNavigation = a.LatestSettings.ToDictionary(entry => entry.Key, entry => entry.Value);
            var bBeforeNavigation = b.LatestSettings.ToDictionary(entry => entry.Key, entry => entry.Value);
            mainTabs.SelectedIndex = 3;
            foreach (var index in new[] { 0, 1, 2, 3, 0, 2 })
            {
                categories.SelectedIndex = index;
                window.UpdateLayout();
                await Task.Delay(40);
                Check(sectionNames.Select((name, n) => ((FrameworkElement)window.FindName(name + "SettingsSection")).IsVisible == (n == index)).All(x => x),
                    "only " + sectionNames[index] + " settings are visible after category selection");
                Check(representativeNames.Select((name, n) => ReferenceEquals(controlsBefore[n], window.FindName(name))).All(x => x),
                    "category selection retains all existing setting control instances");
            }
            Check(ReferenceEquals(decodeBefore, decode.SelectedItem) && ReferenceEquals(micBefore, mic.SelectedItem)
                && quality.SelectedItem?.ToString() == "40" && zoom.Value == 2,
                "category navigation preserves decoding, microphone and selected phone settings");
            mainTabs.SelectedIndex = 0;
            mainTabs.SelectedIndex = 3;
            window.UpdateLayout();
            Check(categories.SelectedIndex == 2 && ((FrameworkElement)window.FindName("AudioSettingsSection")).IsVisible,
                "leaving Settings and returning retains the selected category");
            var appearance = (ListBox)window.FindName("AppearanceBox");
            var skin = (ComboBox)window.FindName("SkinBox");
            var size = (ListBox)window.FindName("SizeBox");
            var oldAppearance = appearance.SelectedIndex; var oldSkin = skin.SelectedIndex; var oldSize = size.SelectedIndex;
            skin.SelectedIndex = 2; // Actual Gamer selection handler, including the disabled appearance control.
            appearance.SelectedIndex = 2;
            size.SelectedIndex = 2;
            window.UpdateLayout();
            Check(categories.SelectedIndex == 2 && ((FrameworkElement)window.FindName("AudioSettingsSection")).IsVisible
                && !appearance.IsEnabled && ((FrameworkElement)window.FindName("RootGrid")).LayoutTransform is ScaleTransform scale && scale.ScaleX == 1.4,
                "theme and interface-size handlers retain category and apply Gamer/size state");
            skin.SelectedIndex = oldSkin; appearance.SelectedIndex = oldAppearance; size.SelectedIndex = oldSize;
            Check(a.Running && b.Running
                && aBeforeNavigation.All(entry => a.LatestSettings.GetValueOrDefault(entry.Key) == entry.Value)
                && bBeforeNavigation.All(entry => b.LatestSettings.GetValueOrDefault(entry.Key) == entry.Value),
                "category, theme and size changes preserve both active receiver sessions and phone values");
            Check(originalSettings == null ? !File.Exists(settingsPath) : File.ReadAllBytes(settingsPath).SequenceEqual(originalSettings),
                "snapshot navigation and theme-size checks do not write user preferences");
            mainTabs.SelectedIndex = 1;
            // Exercise reconciliation with actual running receivers, not a fabricated status.
            var movedA = a.Endpoint! with { Port = portB };
            var movedB = b.Endpoint! with { Port = portA };
            var reconnect = sessions.Reconcile(new[] { movedA, movedB });
            Check(reconnect.Count == 2 && reconnect.Contains(a) && reconnect.Contains(b)
                && a.Receiver.Status.State == LinkState.Stopped && b.Receiver.Status.State == LinkState.Stopped,
                "address reconciliation stops both old receivers before a swapped endpoint is reused");
            foreach (var phone in reconnect) phone.Start("123456", "--no-camera");
            await Until(() => a.LatestSettings.Count > 0 && b.LatestSettings.Count > 0,
                "reconciled receivers reconnect to their updated endpoints");
            Check(a.Receiver.PhonePort == portB && b.Receiver.PhonePort == portA
                && sessions.Find(3) == a && sessions.Find(4) == b,
                "reconciliation preserves camera slots while replacing actual receiver addresses");

            // Manually invoke the same timer callback after enabling auto-connect for this harness.
            // A manual stop must return without even starting a discovery scan.
            typeof(MainWindow).GetField("_setupDone", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            ((CheckBox)window.FindName("AutoConnectBox")).IsChecked = true;
            var resume = typeof(MainWindow).GetMethod("ResumeRememberedPhones", BindingFlags.Instance | BindingFlags.NonPublic)!;
            bool IsResuming() => (bool)typeof(MainWindow).GetField("_resumingPhones", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            list.SelectedIndex = 2;
            ((Button)window.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => a.Receiver.Status.State == LinkState.Stopped, "selected Stop ends only its receiver");
            Check(b.Running, "stopping camera 3 leaves camera 4 connected");
            resume.Invoke(window, null);
            Check(!IsResuming() && a.Receiver.Status.State == LinkState.Stopped,
                "auto-connect suppresses manually stopped camera instead of restarting it");
            var stopAll = Descendants(window).OfType<Button>().Single(button => button.Content?.ToString() == "Stop all cameras");
            stopAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => sessions.All.All(phone => phone.Receiver.Status.State == LinkState.Stopped), "Stop all cameras stops every real receiver");
            await Task.Delay(300);
            Check(preview.Source == null || preview.Visibility != Visibility.Visible, "stopped cameras do not leave a live-looking preview");
            resume.Invoke(window, null);
            Check(!IsResuming() && sessions.All.All(phone => !phone.Running),
                "Stop all suppresses automatic restart for every camera");

            // Start from the default USB route, then use the actual discovery connection button.
            // The missing-camera guard ends before any real process or installation can start.
            ((RadioButton)window.FindName("UsbOption")).IsChecked = true;
            ((RadioButton)window.FindName("TetherOption")).IsChecked = false;
            ((RadioButton)window.FindName("WifiOption")).IsChecked = false;
            ((TextBox)window.FindName("CodeBox")).Text = "123456";
            var primary = new PhoneEndpoint("ui-primary", "Primary phone", "127.0.0.1", FreePort());
            var found = (ListBox)window.FindName("AvailablePhones");
            found.Items.Add(new ListBoxItem { Tag = primary, Content = primary.Name });
            found.SelectedIndex = 0;
            Descendants(window).OfType<Button>().Single(button => button.Content?.ToString() == "Connect selected phone")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((RadioButton)window.FindName("WifiOption")).IsChecked == true
                && ((RadioButton)window.FindName("UsbOption")).IsChecked == false
                && ((RadioButton)window.FindName("TetherOption")).IsChecked == false
                && sessions.Find(1).Endpoint == primary,
                "connecting discovered primary phone switches USB route to remembered Wi-Fi");
            ((Button)window.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(sessions.Find(1).Endpoint == primary && !sessions.Find(1).Running,
                "primary Start retains network assignment after a failed camera install");
            resume.Invoke(window, null);
            Check(IsResuming(), "remembered stopped phone begins a reconnect scan");
            ((Button)window.FindName("PhoneForgetOneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !IsResuming(), "forget invalidates the already-running reconnect scan");
            resume.Invoke(window, null);
            Check(sessions.Find(1).Endpoint == null && !IsResuming() && !sessions.Find(1).Running,
                "forgotten primary phone is excluded from later reconnect ticks");
            var demo = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Left = -12000, Top = -12000 };
            try
            {
                demo.ShowDemo(); demo.Show();
                var demoImage = (Image)demo.FindName("PreviewImage");
                Check(demoImage.Source != null && demoImage.Visibility == Visibility.Visible && !RenderingEnabled(demo),
                    "showing a demo window preserves its stand-in picture without live rendering");
            }
            finally { demo.Close(); }
            list.SelectedIndex = 2;
            a.Start("123456", "--no-camera");
            await Until(() => RenderingEnabled(window) && DisplayedSequence(window) > 0, "preview rendering is active before close cleanup check");
            window.Close();
            var stoppedTimer = (DispatcherTimer)typeof(MainWindow).GetField("_previewTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Check(!RenderingEnabled(window) && sessions.All.All(phone => !phone.Running)
                && !stoppedTimer.IsEnabled,
                "closing unsubscribes rendering, stops the preview timer, and stops owned receivers");
            window = null;
        }
        catch
        {
            Console.Error.WriteLine("Phone A: " + string.Join(" | ", outputA));
            Console.Error.WriteLine("Phone B: " + string.Join(" | ", outputB));
            throw;
        }
        finally
        {
            sessions?.Dispose();
            window?.Close();
            foreach (var process in new[] { fakeA, fakeB })
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
                catch (InvalidOperationException) { }
            }
        }
        Check(originalSettings == null ? !File.Exists(settingsPath) : File.ReadAllBytes(settingsPath).SequenceEqual(originalSettings), "user settings file remains unchanged");
        Console.WriteLine("UI SESSION SELF-TEST PASS");
    }
}
'@ | Set-Content (Join-Path $testDir 'Program.cs')
dotnet run --project (Join-Path $testDir 'UiSessions.csproj') -c Release -- $PSScriptRoot (Resolve-Path -LiteralPath $Recording).Path --snapshot --pretend-no-camera
$testExit = $LASTEXITCODE
if ($testExit -ne 0) { Write-Output "Test project retained at $testDir" }
exit $testExit
