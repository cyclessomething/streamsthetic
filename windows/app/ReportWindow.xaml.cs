using System.IO;
using System.Windows;

namespace PhoneCam;

/// <summary>
/// "Report a problem": the user picks a recorded problem (newest preselected) or "Something else", how many minutes
/// before it to include, and writes what they did; Streamsthetic packs that with its own records into one zip and opens
/// an email to the report address with the zip shown in Explorer to attach.
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
        SendButton.Content = address == null ? "Save report" : "Save report and open email";
        AddressText.Text = address == null
            ? "The report is saved in Documents\\Streamsthetic reports. You can share the zip file with the person helping you. Nothing is uploaded or sent automatically."
            : $"The report is saved in Documents\\Streamsthetic reports. Your email app opens a draft to {address}; review it and attach the zip before sending.";
        UpdateWindowText();
        CheckPhone();
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
                ResultText.Text = $"Report saved as {zip}. Share this zip file with the person helping you.";
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
