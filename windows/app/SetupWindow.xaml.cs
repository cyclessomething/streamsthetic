using System.Windows;

namespace PhoneCam;

/// <summary>First-run guide: shown once after install, and again from Settings, Setup guide.</summary>
public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;
        if (!Environment.GetCommandLineArgs().Any(a => a is "--snapshot" or "--selftest")) Loaded += (_, _) => Motion.RiseIn((FrameworkElement)Content, 16, 380);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
