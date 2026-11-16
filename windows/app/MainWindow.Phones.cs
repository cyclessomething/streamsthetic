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
        foreach (var phone in _sessions.Every) // all 16: cameras can be added later
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

    // ---- Number of cameras: how many Streamsthetic cameras Windows has (1-16), with an estimate for this PC ----

    private SystemCapacity? _capacity;
    private bool _showingCameraCount;

    /// <summary>Fills the Number of cameras section: the current count, the camera names and the estimate for this PC.</summary>
    private void ShowCameraCount()
    {
        if (CameraCountBox == null) return;
        _showingCameraCount = true;
        try
        {
            if (CameraCountBox.Items.Count == 0)
                for (var n = 1; n <= CameraSetup.MaxCameras; n++) CameraCountBox.Items.Add(n);
            var count = _sessions.Count;
            CameraCountBox.SelectedItem = count;
            CameraCountHint.Text = (count == 1 ? "Windows has 1 Streamsthetic camera" :
                $"Windows has {count} Streamsthetic cameras: Streamsthetic, Streamsthetic 2 to Streamsthetic {count}") +
                ". Each phone needs its own camera.";
            CameraCountButton.IsEnabled = false; // nothing to change until another number is picked
            _capacity ??= SystemCapacity.Measure();
            CapacityText.Text = $"This PC: {_capacity.Summary}.\n{_capacity.Recommendation} USB cables are steadier than Wi-Fi: each phone gets its own connection instead of sharing the router.";
        }
        finally { _showingCameraCount = false; }
    }

    /// <summary>Windows may have changed the cameras while the app was in the background (setup, another copy of the
    /// install script): follow the real number when the window comes back.</summary>
    private void SyncCameraCount()
    {
        if (_applyingCameraCount) return;
        var installed = CameraSetup.InstalledCount();
        if (installed <= 0 || installed == _sessions.Count) return;
        _sessions.Count = installed;
        if (_selectedSlot > _sessions.Count) SelectPhone(1);
        RefreshPhoneRows();
        ShowCameraCount();
    }

    private bool _applyingCameraCount;

    private void OnCameraCountPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_showingCameraCount || CameraCountBox.SelectedItem is not int picked) return;
        CameraCountButton.IsEnabled = picked != _sessions.Count;
    }

    private async void OnApplyCameraCount(object sender, RoutedEventArgs e)
    {
        if (CameraCountBox.SelectedItem is not int wanted || wanted == _sessions.Count) return;
        var current = _sessions.Count;
        var capacity = _capacity ??= SystemCapacity.Measure();
        string Names(int from, int to) => from == to ? $"Streamsthetic {from}" : $"Streamsthetic {from} to Streamsthetic {to}";
        var lines = new List<string>();
        if (wanted > current)
        {
            lines.Add($"Adds {Names(current + 1, wanted)} to the camera list of every video app.");
            lines.Add("Every phone you connect adds work for this PC and 10 to 20 Mbps of network traffic (up to 60 Mbps at 4K). Phones get warm while streaming.");
            if (wanted > capacity.UsbPhones)
                lines.Add($"This is more than the estimate for this PC ({capacity.UsbPhones} on USB cables, {capacity.WifiPhones} on Wi-Fi). With that many phones streaming, video may stutter: lower the resolution, frame rate or bitrate of the extra cameras if it does.");
            else if (wanted > capacity.WifiPhones)
                lines.Add($"Over Wi-Fi, more than {capacity.WifiPhones} phones will probably stutter on this PC. Connect the extra phones with USB cables.");
            lines.Add("USB cables are steadier than Wi-Fi: each phone gets its own connection instead of sharing the router.");
        }
        else
        {
            lines.Add($"Removes {Names(wanted + 1, current)}.");
            var affected = _sessions.All.Where(p => p.Slot > wanted && p.Endpoint != null).Select(p => p.Name).ToArray();
            if (affected.Length > 0) lines.Add($"Disconnects and forgets {string.Join(", ", affected)}.");
            lines.Add("OBS scenes and other apps that use those cameras will show nothing until you choose another camera.");
        }
        lines.Add("Windows will ask for administrator approval. Reopen video apps afterwards so they see the change.");
        var heavy = wanted > capacity.UsbPhones;
        var answer = MessageBox.Show(this, string.Join("\n\n", lines), $"Change to {wanted} camera{(wanted == 1 ? "" : "s")}?",
            MessageBoxButton.OKCancel, heavy ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) { ShowCameraCount(); return; }

        CameraCountButton.IsEnabled = false;
        _applyingCameraCount = true;
        // phones on cameras that are about to disappear stop first
        foreach (var phone in _sessions.All.Where(p => p.Slot > wanted)) phone.Receiver.Stop();
        PhonesNote.Text = "Changing the number of cameras… approve the Windows permission prompt.";
        var ok = await CameraSetup.ApplyAsync(wanted);
        _applyingCameraCount = false;
        var installed = CameraSetup.InstalledCount();
        if (installed > 0) _sessions.Count = installed;
        if (_selectedSlot > _sessions.Count) SelectPhone(1);
        SaveSettings(CurrentSettings());
        RefreshPhoneRows();
        ShowCameraCount();
        PhonesNote.Text = ok
            ? $"Windows now has {installed} Streamsthetic camera{(installed == 1 ? "" : "s")}. Reopen video apps to see the change."
            : $"The number of cameras was not changed (Windows has {installed}). If you declined the Windows prompt, choose Change again.";
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
        if (CameraCountBox != null && (CameraCountBox.Items.Count == 0 || !CameraCountButton.IsEnabled && CameraCountBox.SelectedItem is int shown && shown != _sessions.Count)) ShowCameraCount();
        _refreshingPhones = true;
        try
        {
            if (PhonesList.Items.Count != _sessions.Count)
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
            SelectedPhoneText.Text = $"Camera {_selectedSlot}: {SelectedPhone.Endpoint?.Name ?? SelectedPhone.Receiver.Status.PhoneName ?? "no phone connected"}";
            StopAllButton.IsEnabled = _sessions.All.Any(p => p.Running); // nothing to stop: the button rests
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
        if (phone == null) { PhonesNote.Text = $"All {_sessions.Count} cameras have a phone. Add cameras under Number of cameras, or disconnect and forget a phone."; return; }
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
                PhonesNote.Text = $"Windows does not have camera {phone.Slot} yet. Approve the Windows prompt to install the Streamsthetic cameras.";
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
