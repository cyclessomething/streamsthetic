using System.ComponentModel;
using System.IO;
using System.Windows;

namespace PhoneCam;

/// <summary>
/// System tray icon (roadmap "Polish" 3): optionally keep Streamsthetic running in the tray when the window is closed, and
/// optionally start with Windows (straight into the tray). Both are off by default, so closing the window still ends
/// Streamsthetic (and the receiver with it) unless the owner turns this on in Settings.
/// </summary>
public partial class MainWindow
{
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _exiting;
    private bool _trayOnClose;
    private bool _startWithWindows;
    private bool _trayHintShown;

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "Streamsthetic";
    private const string OldRunName = "PhoneCam"; // the same start-with-Windows entry under the old name

    private void InitTray()
    {
        var cli = Environment.GetCommandLineArgs();
        if (cli.Contains("--snapshot") || cli.Contains("--selftest")) return;
        TrayBox.IsChecked = _trayOnClose;
        StartupBox.IsChecked = _startWithWindows;
        if (_startWithWindows) SetRunAtSignIn(true); // moves an entry made under the old name PhoneCam to the new one
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Streamsthetic", null, (_, _) => ShowFromTray());
        menu.Items.Add("Exit", null, (_, _) => { _exiting = true; Close(); });
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "Streamsthetic",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = _trayOnClose || _startWithWindows,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray(); };
        // started by Windows at sign-in: go straight to the tray
        if (cli.Contains("--tray") && _trayOnClose) ContentRendered += (_, _) => Hide();
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Closing the window with "keep running in the tray" on: hide instead of ending PhoneCam.</summary>
    private bool TryHideToTray(CancelEventArgs e)
    {
        if (_exiting || !_trayOnClose || _tray == null) return false;
        e.Cancel = true;
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloonTip(3000, "Streamsthetic is still running", "Click the tray icon to open it again, or choose Exit from its menu.", System.Windows.Forms.ToolTipIcon.Info);
        }
        return true;
    }

    private void OnTrayOptionChanged(object sender, RoutedEventArgs e)
    {
        _trayOnClose = TrayBox.IsChecked == true;
        _startWithWindows = StartupBox.IsChecked == true;
        if (_tray != null) _tray.Visible = _trayOnClose || _startWithWindows;
        SetRunAtSignIn(_startWithWindows);
        SaveSettings(CurrentSettings());
    }

    private static void SetRunAtSignIn(bool on)
    {
        if (Environment.GetCommandLineArgs().Contains("--snapshot")) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            key.DeleteValue(OldRunName, throwOnMissingValue: false);
            if (on && Environment.ProcessPath is { } exe) key.SetValue(RunName, $"\"{exe}\" --tray");
            else key.DeleteValue(RunName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Diagnostics.Log("app", $"could not change start with Windows: {ex.Message}");
        }
    }
}
