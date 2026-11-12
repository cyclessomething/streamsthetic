using System.IO;
using System.Windows;

namespace PhoneCam;

/// <summary>
/// "Report a problem": the user picks a recorded problem (newest preselected) or "Something else", how many minutes
/// before it to include, and writes what they did. GitHub receives only an explicit summary; a diagnostic ZIP
/// remains a separate local action, with optional configured email support.
/// </summary>
public partial class ReportWindow : Window
{
    private sealed record Choice(Problem? Problem, string Label)
    {
        public override string ToString() => Label;
    }

    public static readonly string ReportFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Streamsthetic reports");

    public ReportWindow()
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;
        var choices = Diagnostics.RecentProblems().Select(p => new Choice(p, p.ToString())).ToList();
        choices.Add(new Choice(null, "Something else (use recent logs)"));
        ProblemList.ItemsSource = choices;
        // an old recorded problem must not hide what happened just now: it is preselected only when it is recent (found when a report
        // about a hot phone covered an earlier failure instead of the last 15 minutes)
        var newest = Diagnostics.RecentProblems().FirstOrDefault();
        ProblemList.SelectedIndex = newest != null && DateTime.Now - newest.Time < TimeSpan.FromMinutes(10) ? 0 : choices.Count - 1;
        var address = Diagnostics.ReportAddress;
        SendButton.Content = address == null ? "Save diagnostic report" : "Save diagnostic report and open email";
        AddressText.Text = address == null
            ? "Diagnostic ZIPs stay in Documents\\Streamsthetic reports. They may contain private connection details or crash dumps. Review the files before attaching a ZIP to GitHub; it is never attached automatically."
            : $"Diagnostic ZIPs stay in Documents\\Streamsthetic reports. The secondary action also opens an email draft to {address}. Review the files before attaching or sending; they may contain private connection details or crash dumps.";
        UpdateWindowText();
        if (Environment.GetCommandLineArgs().Contains("--snapshot"))
        {
            IncludePhoneBox.IsEnabled = false;
            IncludePhoneBox.IsChecked = false;
            PhoneHint.Text = "Phone logs are only collected when saving a diagnostic ZIP.";
        }
        else CheckPhone();
    }

    private Problem? Chosen => (ProblemList.SelectedItem as Choice)?.Problem;

    /// <summary>The window ends at the problem (plus a minute, for what followed it) or now.</summary>
    private (DateTime From, DateTime To) TimeWindow()
    {
        var to = Chosen is { } p ? p.Time.AddMinutes(1) : DateTime.Now;
        if (to > DateTime.Now) to = DateTime.Now;
        return (to.AddMinutes(-MinutesSlider.Value), to);
    }

    private void OnProblemChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateWindowText();

    private void OnMinutesChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateWindowText();

    private void UpdateWindowText()
    {
        if (MinutesText == null || WindowText == null) return; // still loading
        MinutesText.Text = $"{MinutesSlider.Value:0} minutes";
        var (from, to) = TimeWindow();
        var day = from.Date == DateTime.Today ? "" : $"{from:ddd d MMM}, ";
        WindowText.Text = $"Includes {day}{from:HH:mm} to {to:HH:mm}" + (Chosen != null ? ", ending just after the problem." : ", ending now.");
    }

    private async void CheckPhone()
    {
        PhoneHint.Text = "Checking USB debugging connection…";
        var connected = await Task.Run(() => UsbPhoneWatcher.RunAdb("get-state").Trim() == "device");
        IncludePhoneBox.IsEnabled = connected;
        IncludePhoneBox.IsChecked = connected;
        PhoneHint.Text = connected ? "USB debugging connected; phone logs are available." : "Phone logs require USB debugging and authorization on the phone. For USB tethering or Wi-Fi, you can save phone logs separately from Report a problem in the phone app.";
    }

    private void OnGitHub(object sender, RoutedEventArgs e)
    {
        var (from, to) = TimeWindow();
        var version = Version.TryParse(Diagnostics.AppVersion.Split('+')[0], out var parsed) ? parsed : new Version(0, 0);
        var draft = GitHubIssueDraft.Create(UserText.Text, version, Environment.OSVersion.Version,
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture, from, to, Chosen?.Kind);
        string? saved = null;
        try
        {
            if (draft.DescriptionShortened)
            {
                Directory.CreateDirectory(ReportFolder);
                saved = Path.Combine(ReportFolder, $"GitHub-draft-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.md");
                File.WriteAllText(saved, draft.FullBody);
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(draft.Url.AbsoluteUri) { UseShellExecute = true });
            ResultText.Text = saved == null
                ? "GitHub opened a draft. Review it, sign in if needed, then submit the issue. No issue has been submitted by Streamsthetic."
                : $"GitHub opened a shortened draft. Your complete description is saved in {saved}. Paste the remaining text into GitHub before submitting. No issue has been submitted by Streamsthetic.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ResultText.Text = "Could not open the GitHub draft. Your description remains in this window. "
                + (saved == null ? "" : $"The full draft is saved in {saved}. ") + ex.Message;
        }
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        SendButton.IsEnabled = false;
        ResultText.Text = "Collecting diagnostic logs…";
        var (from, to) = TimeWindow();
        var problem = Chosen;
        var text = UserText.Text;
        var phone = IncludePhoneBox.IsChecked == true && IncludePhoneBox.IsEnabled;
        try
        {
            var zip = await Task.Run(() => Diagnostics.BuildReport(text, problem, from, to, phone, ReportFolder));
            if (Diagnostics.ReportAddress == null)
            {
                ResultText.Text = $"Report saved as {zip}. Review its contents before attaching it manually to your GitHub issue. Nothing was uploaded.";
                return;
            }
            ResultText.Text = $"Report saved as {Path.GetFileName(zip)}. Your email app is opening. Attach the file from the folder window and press Send.";
            try
            {
                Diagnostics.OpenEmail(zip, problem);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                ResultText.Text = $"Report saved as {zip}. No email app is set up on this PC, so please email the file to {Diagnostics.ReportAddress}.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ResultText.Text = $"The report could not be saved: {ex.Message}";
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }
}
