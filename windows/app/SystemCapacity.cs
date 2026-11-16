using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace PhoneCam;

/// <summary>
/// A rough, honest estimate of how many phones this PC can take at once, for the "Number of cameras" setting.
/// Each phone costs the PC one video decode (graphics card when it has a video engine, otherwise the processor) and
/// some copying, and costs the network 10-20 Mbps at 1080p (up to 60 Mbps at 4K). USB phones each have their own
/// cable; Wi-Fi phones all share the same router airtime, which is why Wi-Fi is recommended fewer phones.
/// Numbers assume 1080p at 30 fps for extra cameras (the app's default for them); higher quality lowers them.
/// </summary>
public sealed record SystemCapacity(int Threads, double MemoryGb, string Graphics, bool HardwareDecode,
    double? WifiMbps, double? EthernetMbps, int UsbPhones, int WifiPhones)
{
    public static SystemCapacity Measure()
    {
        var threads = Environment.ProcessorCount;
        var memoryGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024;
        var gpus = GraphicsAdapters();
        var graphics = gpus.Count > 0 ? string.Join(", ", gpus) : "no graphics driver found";
        // every NVIDIA, AMD and Intel graphics chip of the last ten years decodes H.264 in hardware; the basic
        // Windows display driver does not
        bool Discrete(string g) => g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || g.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) || g.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase)
            || g.Contains("Arc", StringComparison.OrdinalIgnoreCase);
        var hardware = gpus.Any(g => g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || g.Contains("AMD", StringComparison.OrdinalIgnoreCase) || g.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            || g.Contains("Intel", StringComparison.OrdinalIgnoreCase));

        double? wifi = null, ethernet = null;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.Speed <= 0) continue;
                // USB tethering adapters look like Ethernet; they are a phone, not the PC's own network
                if (System.Text.RegularExpressions.Regex.IsMatch(nic.Description, @"RNDIS|Remote NDIS|\bNCM\b|Hyper-V|Virtual|VPN|Tailscale|Loopback", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                var mbps = nic.Speed / 1_000_000.0;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) wifi = Math.Max(wifi ?? 0, mbps);
                else if (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet) ethernet = Math.Max(ethernet ?? 0, mbps);
            }
        }
        catch (NetworkInformationException) { }

        // Decoding: a graphics card's video engine handles roughly 12 (dedicated card) or 8 (built-in graphics)
        // 1080p30 streams; without one each stream costs about half a processor thread. Copying and scaling every
        // frame costs about a quarter of a thread more, so the processor also caps it.
        var decode = hardware ? (gpus.Any(Discrete) ? 12 : 8) : Math.Max(1, threads / 2);
        var processor = Math.Max(1, threads * 4 / 3);
        var pc = Math.Min(decode, processor);
        if (memoryGb < 8) pc = Math.Min(pc, 4);

        // USB: each phone has its own cable, so the PC is the limit; more than about 8 phones also runs out of
        // ports and USB power (use a powered hub)
        var usb = Math.Clamp(pc, 1, 8);
        // Wi-Fi: every phone uploads over the same router airtime; about a third of the PC's link speed is usable
        // for steady video, and beyond about 6 phones most home routers start dropping frames
        var airtime = wifi is double w ? (int)(w * 0.35 / 12) : ethernet != null ? 6 : 4;
        var wireless = Math.Clamp(Math.Min(pc, airtime), 1, 6);
        return new(threads, Math.Round(memoryGb), graphics, hardware, wifi, ethernet, usb, wireless);
    }

    /// <summary>Display adapters from the driver registry (no extra packages needed).</summary>
    private static List<string> GraphicsAdapters()
    {
        var names = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames() ?? [])
            {
                if (!int.TryParse(sub, out _)) continue;
                using var key = cls!.OpenSubKey(sub);
                // only real graphics chips: not the basic driver, USB/remote display adapters or virtual ones
                if (key?.GetValue("DriverDesc") is string name && !string.IsNullOrWhiteSpace(name)
                    && !System.Text.RegularExpressions.Regex.IsMatch(name, @"Basic Display|Basic Render|Virtual|Remote Display|Indirect|DisplayLink|Mirage|Parsec|Citrix|VMware|Hyper-V",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    && !names.Contains(name)) names.Add(name);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        return names;
    }

    /// <summary>One line about this PC, in plain words.</summary>
    public string Summary =>
        $"{Threads} processor threads, {MemoryGb:0} GB memory, {Graphics}" +
        (HardwareDecode ? " (hardware video decoding)" : " (no hardware video decoding found)") +
        (WifiMbps is double w ? $", Wi-Fi {w:0} Mbps" : "") + (EthernetMbps is double e ? $", Ethernet {e:0} Mbps" : "");

    public string Recommendation =>
        $"Estimate for this PC: up to {UsbPhones} phone{(UsbPhones == 1 ? "" : "s")} on USB cables, or up to {WifiPhones} on Wi-Fi, " +
        "at 1080p and 30 fps. 4K or 60 fps lowers these numbers.";
}
