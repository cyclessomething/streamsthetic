using System.Windows;
using System.Windows.Controls;

namespace PhoneCam;

public partial class MainWindow
{
    public void ShowPhonesTab() => MainTabs.SelectedIndex = 1;
    private bool _refreshingPhones, _phonesClosing, _findingPhones;
    private bool _startingPrimary;
    private int _connectEpoch;
    private bool _wifiWaiting;
    private readonly CancellationTokenSource _phoneScanCancel = new();
    private readonly Dictionary<int, DateTime> _slotLowered = new(), _slotRemembered = new();
    private readonly HashSet<int> _extraDefaults = new();
    private readonly HashSet<int> _startingPhones = new();
    private readonly Dictionary<int, DateTime> _slotLiveSince = new();
    private readonly HashSet<int> _manualStops = new();
    private bool _resumingPhones;
    private readonly System.Windows.Threading.DispatcherTimer _phoneReconnectTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private void UpdateLinkAvailability(bool pendingStatus = false) =>
        UsbOption.IsEnabled = WifiOption.IsEnabled = TetherOption.IsEnabled = CodeBox.IsEnabled =
            !pendingStatus && !_startingPrimary && !_wifiWaiting && _startingPhones.Count == 0 && !_sessions.All.Any(p => p.Running);

    private void OnPhoneUi(Action action)
    {
        if (_phonesClosing) return;
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(() => { if (!_phonesClosing) action(); });
    }

    private void InitPhoneSessions()
    {
        _sessions.StatusChanged += (phone, status) => OnPhoneUi(() =>
        {
            if (_phonesClosing) return;
            if (status.State == LinkState.Live) _slotLiveSince.TryAdd(phone.Slot, DateTime.Now);
            if (status.State is LinkState.Stopped or LinkState.Error) _slotLiveSince.Remove(phone.Slot);
            if (phone.Slot == _selectedSlot && status.State is LinkState.Stopped or LinkState.Error) _previewReader.Dispose();
            if (status.State is LinkState.Live or LinkState.WaitingForApp &&
                (!_slotRemembered.TryGetValue(phone.Slot, out var seen) || DateTime.Now - seen > TimeSpan.FromSeconds(30)))
            {
                _slotRemembered[phone.Slot] = DateTime.Now;
                RememberPhone(phone.Name);
            }
            if (phone.Slot == _selectedSlot) Show(status with { PhoneName = status.PhoneName ?? phone.Endpoint?.Name });
            RefreshPhoneRows();
        });
        _sessions.SettingsChanged += (phone, settings) => OnPhoneUi(() =>
        {
            if (_phonesClosing) return;
            // Extra cameras start conservatively once per connection, then remain under the user's control.
            if (_extraDefaults.Remove(phone.Slot)) phone.Receiver.SendSettings("w=1920;h=1080;fps=30;mbps=10");
            if (phone.Slot == _selectedSlot) ShowPhoneSettings(settings);
        });
        foreach (var phone in _sessions.All)
        {
            phone.Receiver.CannotKeepUp += () => OnPhoneUi(() => LowerPhoneQuality(phone));
            phone.Receiver.AppReadsSlower += (reads, sent) => OnPhoneUi(() =>
            {
                if (!_phonesClosing && _matchFps && sent >= 50 && reads is >= 20 and <= 36 && Int(phone.LatestSettings, "fps") == 60)
                    phone.Receiver.SendSettings("fps=30");
            });
        }
        RefreshPhoneRows();
        _phoneReconnectTimer.Tick += (_, _) => ResumeRememberedPhones();
        if (!Environment.GetCommandLineArgs().Any(a => a is "--snapshot" or "--selftest")) _phoneReconnectTimer.Start();
    }

    private async void ResumeRememberedPhones()
    {
        if (_resumingPhones || _phonesClosing || !_autoConnect || !_setupDone) return;
        var pending = _sessions.All.Where(p => p.Endpoint != null && !_manualStops.Contains(p.Slot)
            && p.Receiver.Status.State is LinkState.Stopped or LinkState.WaitingForPhone).ToArray();
        if (pending.Length == 0) return;
        _resumingPhones = true;
        var epoch = _connectEpoch;
        try
        {
            var found = await PhoneDiscovery.ScanAsync(_phoneScanCancel.Token);
            if (_phonesClosing || epoch != _connectEpoch || !_autoConnect) return;
            var restart = _sessions.Reconcile(found);
            foreach (var phone in pending.Concat(restart).Distinct())
            {
                if (epoch != _connectEpoch || _phonesClosing) break;
                if (!_manualStops.Contains(phone.Slot) && found.Any(e => e.Id == phone.Endpoint?.Id))
                    await StartNetworkPhone(phone, auto: true, discovered: true);
            }
            SaveSettings(CurrentSettings());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Log("discovery", "Could not refresh remembered phones: " + ex.Message); }
        finally { _resumingPhones = false; }
    }

    private void LowerPhoneQuality(PhoneSession phone)
    {
        if (_phonesClosing || phone.Receiver.Status.State is LinkState.Stopped or LinkState.Error) return;
        if (_slotLowered.TryGetValue(phone.Slot, out var last) && DateTime.Now - last < TimeSpan.FromSeconds(15)) return;
        var settings = phone.LatestSettings;
        var mbps = Int(settings, "mbps") ?? 20;
        var change = mbps > 20 ? $"mbps={(mbps > 40 ? 40 : 20)}"
            : (Int(settings, "h") ?? 1080) > 1080 ? "w=1920;h=1080"
            : (Int(settings, "fps") ?? 30) > 30 ? "fps=30"
            : mbps > 10 ? "mbps=10" : null;
        if (change == null) return;
        _slotLowered[phone.Slot] = DateTime.Now;
        phone.Receiver.SendSettings(change);
        Diagnostics.Log("app", $"camera {phone.Slot} cannot keep up: {change}");
        if (phone.Slot == _selectedSlot)
        {
            CameraHint.Text = "Video settings were reduced to help this connection keep up. You can adjust them again.";
            _noteUntil = DateTime.Now.AddSeconds(20);
        }
    }

    private void RefreshPhoneRows()
    {
        if (PhonesList == null || PhoneForgetOneButton == null) return;
        _refreshingPhones = true;
        try
        {
            if (PhonesList.Items.Count != 4)
            {
                PhonesList.Items.Clear();
                foreach (var phone in _sessions.All) PhonesList.Items.Add(new ListBoxItem { Tag = phone.Slot });
            }
            foreach (var phone in _sessions.All)
            {
                var status = phone.Receiver.Status;
                var name = phone.Endpoint?.Name ?? status.PhoneName;
                var state = status.State switch
                {
                    LinkState.Starting => "Starting",
                    LinkState.WaitingForPhone => "Connecting",
                    LinkState.WaitingForApp => "Ready for an app",
                    LinkState.Live => "Live",
                    LinkState.Error => "Needs attention",
                    _ => "Stopped",
                };
                var row = (ListBoxItem)PhonesList.Items[phone.Slot - 1];
                var seen = !phone.Running && phone.LastSeen is DateTime last ? $" · {SeenText(last)}" : "";
                var label = name == null && status.State == LinkState.Stopped
                    ? $"Camera {phone.Slot} · Available"
                    : $"Camera {phone.Slot} · {name ?? "Your phone"} · {state}{seen}";
                row.Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
                row.ToolTip = label;
                if (phone.Slot == _selectedSlot) PhonesList.SelectedItem = row;
            }
            PhoneForgetOneButton.IsEnabled = SelectedPhone.Endpoint != null;
            PhonesForgetButton.IsEnabled = _sessions.Assignments.Count > 0 || PairedList().Count > 0;
            SelectedPhoneText.Text = $"Camera {_selectedSlot} — {SelectedPhone.Endpoint?.Name ?? SelectedPhone.Receiver.Status.PhoneName ?? "No phone connected"}";
        }
        finally { _refreshingPhones = false; }
    }

    private void OnPhoneSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingPhones || PhonesList.SelectedItem is not ListBoxItem { Tag: int slot }) return;
        SelectPhone(slot);
    }

    private void SelectPhone(int slot)
    {
        if (_selectedSlot != slot)
        {
            _receiver.Preview = false;
            _previewReader.Dispose();
            _previewReader = new PreviewReader(slot);
            _selectedSlot = slot;
            _previewAnyway = false;
            PreviewImage.Source = null;
            _userChangedAt.Clear();
            _noteUntil = _lastHeatNote = DateTime.MinValue;
            _liveSince = _slotLiveSince.TryGetValue(slot, out var since) ? since : null;
            CameraPanel.IsEnabled = PicturePanel.IsEnabled = false;
            SetReadout(FormatReadout, null);
            FormatUnit.Text = "";
        }
        Show(_receiver.Status with { PhoneName = _receiver.Status.PhoneName ?? SelectedPhone.Endpoint?.Name });
        if (SelectedPhone.LatestSettings.Count > 0 && Running) ShowPhoneSettings(SelectedPhone.LatestSettings);
        RefreshPhoneRows();
        UpdatePreview();
    }

    private async void OnFindPhones(object sender, RoutedEventArgs e)
    {
        if (_findingPhones) return;
        _findingPhones = true;
        FindPhonesButton.IsEnabled = false;
        PhonesNote.Text = "Searching your local network… Press Start in Streamsthetic on each phone.";
        try
        {
            var phones = await PhoneDiscovery.ScanAsync(_phoneScanCancel.Token);
            if (_phonesClosing) return;
            var restart = _sessions.Reconcile(phones);
            foreach (var phone in restart) await StartNetworkPhone(phone, discovered: true);
            SaveSettings(CurrentSettings());
            RefreshPhoneRows();
            AvailablePhones.Items.Clear();
            foreach (var phone in phones)
                AvailablePhones.Items.Add(new ListBoxItem { Tag = phone, Content = $"{phone.Name} · {phone.Host}" });
            PhonesNote.Text = phones.Count == 0
                ? "No phones found. Connect each phone to the same network, press Start in its app, then search again."
                : "Select a phone and choose Connect selected phone. Each phone must use the pairing code shown on this PC.";
            if (phones.Count > 0) AvailablePhones.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PhonesNote.Text = "Could not find phones: " + ex.Message; }
        finally { _findingPhones = false; FindPhonesButton.IsEnabled = true; }
    }

    private async void OnConnectFoundPhone(object sender, RoutedEventArgs e)
    {
        if (AvailablePhones.SelectedItem is not ListBoxItem { Tag: PhoneEndpoint endpoint })
        { PhonesNote.Text = "Find phones and select one first."; return; }
        var phone = _sessions.Assign(endpoint);
        if (phone == null) { PhonesNote.Text = "All four camera slots are assigned. Disconnect and forget a phone to free a slot."; return; }
        if (phone.Slot == 1)
        {
            WifiOption.IsChecked = true;
            TetherOption.IsChecked = UsbOption.IsChecked = false;
        }
        SelectPhone(phone.Slot);
        _manualStops.Remove(phone.Slot);
        SaveSettings(CurrentSettings());
        await StartNetworkPhone(phone, discovered: true);
    }

    private async Task StartNetworkPhone(PhoneSession phone, bool auto = false, bool discovered = false)
    {
        if (_phonesClosing || !_startingPhones.Add(phone.Slot)) return;
        var epoch = _connectEpoch;
        try
        {
            if (!discovered && phone.Endpoint != null)
            {
                var found = await PhoneDiscovery.ScanAsync(_phoneScanCancel.Token);
                if (_phonesClosing || epoch != _connectEpoch) return;
                var restart = _sessions.Reconcile(found);
                foreach (var other in restart.Where(p => p != phone)) await StartNetworkPhone(other, auto, discovered: true);
                if (!found.Any(e => e.Id == phone.Endpoint?.Id))
                { PhonesNote.Text = "This phone could not be reached. Press Start in its app and check the network, then try Find phones."; return; }
            }
            if (phone.Running)
            {
                if (phone.Endpoint?.Host == phone.Receiver.PhoneHost && phone.Endpoint?.Port == phone.Receiver.PhonePort) return;
                phone.Receiver.Stop(); // discovery found this same phone at a new address
            }
            var code = CodeBox.Text.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(code, @"^[0-9]{6}$"))
            { PhonesNote.Text = "Choose Wi-Fi in Connection and enter the six-digit code shared by your phones."; return; }
            if (!CameraInstalled(phone.Slot))
            {
                PhonesNote.Text = "Install the updated Streamsthetic camera component to enable all four cameras.";
                if (auto) return;
                if (!await InstallCameraAsync() || !CameraInstalled(phone.Slot)) return;
            }
            if (_phonesClosing || epoch != _connectEpoch || phone.Endpoint == null) return;
            var decode = _decodeMode switch { "cpu" => " --cpu", "gpu" => " --gpu", _ => "" };
            // Only the primary camera routes microphone audio; extra angles must not mix duplicate sound.
            var audio = phone.Slot == 1 && _micOutput != "" ? $" --audio-device \"{_micOutput.Replace("\"", "")}\"" : "";
            if (phone.Slot > 1) _extraDefaults.Add(phone.Slot);
            phone.Start(code, decode + audio);
            SaveSettings(CurrentSettings());
            PhonesNote.Text = $"Connecting {phone.Name} to camera {phone.Slot}. Select its row to preview or change its settings.";
        }
        catch (Exception ex) { PhonesNote.Text = "Could not connect: " + ex.Message; }
        finally
        {
            _startingPhones.Remove(phone.Slot);
            if (!_phonesClosing && phone.Slot == _selectedSlot) Show(phone.Receiver.Status);
            RefreshPhoneRows();
        }
    }

    private async Task StartFirstNetworkPhone(bool auto)
    {
        if (_wifiWaiting) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(CodeBox.Text.Trim(), @"^[0-9]{6}$"))
        { Show(new(LinkState.Error, "Enter the six-digit pairing code shown on your phone.")); return; }
        _wifiWaiting = true;
        var epoch = _connectEpoch;
        var failed = false;
        try
        {
            while (!_phonesClosing && _wifiWaiting && epoch == _connectEpoch)
            {
                if (_selectedSlot == 1) Show(new(LinkState.WaitingForPhone, "Looking for a phone on your network. Press Stop to cancel."));
                var endpoints = await PhoneDiscovery.ScanAsync(_phoneScanCancel.Token);
                if (_phonesClosing || !_wifiWaiting || epoch != _connectEpoch) return;
                var endpoint = endpoints.FirstOrDefault(e => !_sessions.All.Any(p => p.Endpoint?.Id == e.Id));
                if (endpoint != null)
                {
                    var phone = _sessions.Assign(endpoint);
                    if (phone != null) await StartNetworkPhone(phone, auto, discovered: true);
                    return;
                }
                await Task.Delay(1000, _phoneScanCancel.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { failed = true; if (!_phonesClosing) Show(new(LinkState.Error, "Could not search for phones: " + ex.Message)); }
        finally
        {
            _wifiWaiting = false;
            if (!_phonesClosing && _selectedSlot == 1 && !failed) Show(_receiver.Status);
            if (!_phonesClosing) UpdateLinkAvailability();
        }
    }

    private void OnForgetSelectedPhone(object sender, RoutedEventArgs e)
    {
        _connectEpoch++;
        var name = SelectedPhone.Name;
        _sessions.Forget(_selectedSlot);
        _paired = string.Join(';', PairedList().Where(p => p.Name != name).Select(p => $"{p.Name}|{p.Seen.Ticks}"));
        _previewReader.Dispose();
        SelectPhone(_selectedSlot);
        RefreshPaired();
        SaveSettings(CurrentSettings());
    }

    private void OnForgetPhones(object sender, RoutedEventArgs e)
    {
        _connectEpoch++;
        foreach (var phone in _sessions.All) _sessions.Forget(phone.Slot);
        _paired = "";
        _previewReader.Dispose();
        SelectPhone(1);
        RefreshPaired();
        SaveSettings(CurrentSettings());
    }

    private void OnStopAllPhones(object sender, RoutedEventArgs e)
    {
        _connectEpoch++;
        _waitForTether = false;
        _unlockWait?.Cancel();
        foreach (var phone in _sessions.All) { _manualStops.Add(phone.Slot); phone.Receiver.Stop(); }
        RefreshPhoneRows();
    }
}
