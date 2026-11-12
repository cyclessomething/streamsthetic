using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhoneCam;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Diagnostics.Start();

        // Our themed dropdowns (Theme/Styles.xaml Dropdown) did not open from a real mouse click (only from keyboard /
        // automation), so the click is handled here: a press on the box itself opens or closes the list; a press on an
        // entry of the list is left alone so it can be chosen.
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ComboBox), UIElement.PreviewMouseLeftButtonDownEvent,
            new System.Windows.Input.MouseButtonEventHandler((sender, args) =>
            {
                if (sender is not System.Windows.Controls.ComboBox box || !box.IsEnabled) return;
                for (var d = args.OriginalSource as DependencyObject; d != null && d != box; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                    if (d is System.Windows.Controls.ComboBoxItem) return;
                box.IsDropDownOpen = !box.IsDropDownOpen;
                args.Handled = true;
            }));

        // Headless check used by tools/app_selftest.ps1:
        //   Streamsthetic.exe --selftest RESULT_FILE -- <receiver arguments>
        // Passes when the receiver reports live video at >= 50 fps within 25 seconds.
        if (e.Args.Length >= 2 && e.Args[0] == "--selftest")
        {
            var receiverArgs = string.Join(' ', e.Args.SkipWhile(a => a != "--").Skip(1));
            Shutdown(Task.Run(() => RunSelfTest(e.Args[1], receiverArgs)).GetAwaiter().GetResult() ? 0 : 1);
            return;
        }

        // Renders the window to an image without screen capture (UI review, docs):
        //   Streamsthetic.exe --snapshot FILE.png [--theme light|dark] [--window report]
        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot")
        {
            var theme = e.Args.SkipWhile(a => a != "--theme").Skip(1).FirstOrDefault();
            var skin = e.Args.SkipWhile(a => a != "--skin").Skip(1).FirstOrDefault() ?? "Studio";
            ApplyTheme(skin, theme == "light" ? "Light" : theme == "dark" ? "Dark" : "System");
            var which = e.Args.SkipWhile(a => a != "--window").Skip(1).FirstOrDefault();
            Window window = which == "report" ? new ReportWindow() : which == "setup" ? new SetupWindow() : new MainWindow();
            if (e.Args.Contains("--demo") && window is MainWindow demo) demo.ShowDemo();
            if (e.Args.Contains("--phones") && window is MainWindow phones) phones.ShowPhonesTab();
            window.ContentRendered += async (_, _) =>
            {
                // Snapshot-only navigation: exercise the real tabs without connecting to a phone.
                var tabName = e.Args.SkipWhile(a => a != "--tab").Skip(1).FirstOrDefault();
                if (window is MainWindow mainSnapshot && tabName != null)
                {
                    var tabs = (System.Windows.Controls.TabControl)mainSnapshot.FindName("MainTabs");
                    tabs.SelectedIndex = tabName switch { "phones" => 1, "picture" => 2, "settings" => 3, _ => 0 };
                    window.UpdateLayout();
                }
                var sectionName = e.Args.SkipWhile(a => a != "--settings-section").Skip(1).FirstOrDefault();
                if (window is MainWindow settingsSnapshot && sectionName != null)
                {
                    ((System.Windows.Controls.TabControl)settingsSnapshot.FindName("MainTabs")).SelectedIndex = 3;
                    ((System.Windows.Controls.ListBox)settingsSnapshot.FindName("SettingsCategories")).SelectedIndex =
                        sectionName switch { "streaming" => 1, "audio" => 2, "help" => 3, _ => 0 };
                    window.UpdateLayout();
                }
                var scaleName = e.Args.SkipWhile(a => a != "--ui-scale").Skip(1).FirstOrDefault();
                if (window is MainWindow scaleSnapshot && int.TryParse(scaleName, out var scaleIndex))
                {
                    ((System.Windows.Controls.ListBox)scaleSnapshot.FindName("SizeBox")).SelectedIndex = Math.Clamp(scaleIndex, 0, 3);
                    window.UpdateLayout();
                }
                if (e.Args.Contains("--scroll-end"))
                {
                    void ScrollEnd(DependencyObject parent)
                    {
                        if (parent is System.Windows.Controls.ScrollViewer scroll && scroll.IsVisible) scroll.ScrollToEnd();
                        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) ScrollEnd(VisualTreeHelper.GetChild(parent, i));
                    }
                    ScrollEnd(window);
                    window.UpdateLayout();
                }
                await Task.Delay(600); // Let tab/selection transitions settle before rendering evidence.
                window.UpdateLayout();
                SaveSnapshot(window, e.Args[1]);
                Shutdown(0);
            };
            window.Show();
            return;
        }

        WatchForCrashes();
        var main = new MainWindow();
        main.Show();

        // Test of the crash recording (tools/diagnostics_selftest.ps1): Streamsthetic.exe --simulate-crash
        if (e.Args.Contains("--simulate-crash"))
        {
            main.Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("simulated crash (--simulate-crash)")),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>
    /// A crash is saved as a problem record (stack trace and the latest log lines), so "Report a problem" can offer
    /// it next time. The app then closes; Windows ends the receiver with it.
    /// </summary>
    private void WatchForCrashes()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Crashed(e.Exception);
            e.Handled = true;
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crashed(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Diagnostics.RecordProblem("app-error", "Something went wrong in the background", e.Exception.ToString());
            e.SetObserved();
        };
    }

    private static int _crashed;

    private static void Crashed(Exception? ex)
    {
        if (Interlocked.Exchange(ref _crashed, 1) == 1) return;
        Diagnostics.RecordProblem("app-crash", "The Streamsthetic app crashed", ex?.ToString() ?? "unknown error");
        if (Environment.GetCommandLineArgs().Contains("--simulate-crash")) return; // no dialog in the automated test
        MessageBox.Show("Streamsthetic encountered a problem and needs to close.\n\nDiagnostic details were saved. Reopen the app and choose \"Report a problem\" to create a report.",
            "Streamsthetic", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>
    /// Applies a look: <paramref name="skin"/> is the theme ("Studio", "Creator", "Gamer", "Brutal", "Dot") and
    /// <paramref name="appearance"/> is "Light", "Dark" or "System" (follows Windows, also when Windows changes later;
    /// Gamer is always dark). Sets Fluent's mode for the standard controls and swaps Streamsthetic's token set
    /// (Theme/&lt;skin&gt;-&lt;Light|Dark&gt;.xaml).
    /// </summary>
    public static void ApplyTheme(string skin, string appearance)
    {
        var app = (App)Current;
        app._skin = skin;
        app._appearance = appearance;
        if (skin == "Gamer") appearance = "Dark";
        app.ThemeMode = appearance switch { "Light" => ThemeMode.Light, "Dark" => ThemeMode.Dark, _ => ThemeMode.System };
        var dark = appearance == "Dark" || (appearance != "Light" && WindowsUsesDarkApps());
        // find the token set by name: Fluent adds its own dictionaries to the same list, so positions shift
        var dictionaries = app.Resources.MergedDictionaries;
        var wanted = $"Theme/{skin}-{(dark ? "Dark" : "Light")}.xaml";
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var name = dictionaries[i].Source?.OriginalString ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"Theme/\w+-(Light|Dark)\.xaml$")) continue;
            if (!name.EndsWith(wanted)) dictionaries[i] = new ResourceDictionary { Source = new Uri(wanted, UriKind.Relative) };
            break;
        }
        if (!app._watchingWindows)
        {
            app._watchingWindows = true;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == Microsoft.Win32.UserPreferenceCategory.General && app._appearance == "System")
                    app.Dispatcher.BeginInvoke(() => ApplyTheme(app._skin, "System"));
            };
        }
    }

    private string _skin = "Studio";
    private string _appearance = "System";
    private bool _watchingWindows;
    private static bool WindowsUsesDarkApps() =>
        Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is 0;

    private static void SaveSnapshot(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        // Include the actual interface scale; a VisualBrush otherwise normalizes it back to 100%.
        var bounds = content.LayoutTransform.TransformBounds(new Rect(content.RenderSize));
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(bounds.Width * dpi.DpiScaleX), (int)Math.Ceiling(bounds.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // draw the window background behind the content so the image matches what the user sees
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? Brushes.Transparent, null, new Rect(0, 0, bounds.Width, bounds.Height));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, bounds.Width, bounds.Height));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static bool RunSelfTest(string resultFile, string receiverArgs)
    {
        using var controller = new ReceiverController();
        var seen = new List<string>();
        var done = new ManualResetEventSlim();
        ReceiverStatus? passed = null;
        controller.StatusChanged += s =>
        {
            lock (seen) seen.Add($"{s.State}: {s.Message} fps={s.Fps:0.#} latency={s.LatencyMs:0.#}");
            if (s.State == LinkState.Live && s.Fps >= 50)
            {
                passed = s;
                done.Set();
            }
        };
        controller.Start(receiverArgs);
        done.Wait(TimeSpan.FromSeconds(25));
        controller.Stop();
        lock (seen)
        {
            File.WriteAllLines(resultFile, [passed != null ? "PASS" : "FAIL", .. seen]);
        }
        return passed != null;
    }
}
