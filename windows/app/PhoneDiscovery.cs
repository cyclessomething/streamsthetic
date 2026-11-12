using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace PhoneCam;

// Advertisements are discovery hints, never proof of pairing. The TCP handshake still checks the code.
public sealed record PhoneEndpoint(string Id, string Name, string Host, int Port);

public static class PhoneDiscovery
{
    public static PhoneEndpoint? ParseReply(string text, IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || text.Length > 512) return null;
        var parts = text.Trim().Split('|');
        string id, name, port;
        if (parts.Length == 4 && parts[0] == "PHONECAM2" && Guid.TryParse(parts[1], out var uuid))
        {
            id = uuid.ToString("D"); name = parts[2]; port = parts[3];
        }
        else if (parts.Length == 3 && parts[0] == "PHONECAM1")
        {
            name = parts[1]; port = parts[2]; id = $"legacy:{address}:{port}";
        }
        else return null;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || name.Any(char.IsControl)
            || !int.TryParse(port, out var number) || number is < 1 or > 65535) return null;
        return new(id, name, address.ToString(), number);
    }

    public static async Task<IReadOnlyList<PhoneEndpoint>> ScanAsync(CancellationToken cancellationToken = default)
        => await ScanAsync(Broadcasts(), TimeSpan.FromSeconds(2), cancellationToken);

    // The explicit destinations and window also let the offline test exercise real UDP request/reply behavior.
    public static async Task<IReadOnlyList<PhoneEndpoint>> ScanAsync(IEnumerable<IPAddress> destinations,
        TimeSpan window, CancellationToken cancellationToken = default)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(window);
        var found = new Dictionary<string, PhoneEndpoint>();
        foreach (var address in destinations.Distinct())
        {
            foreach (var query in new[] { "PHONECAM2?", "PHONECAM?" })
            {
                try { await udp.SendAsync(Encoding.ASCII.GetBytes(query), new IPEndPoint(address, 5557), deadline.Token); }
                catch (SocketException) { /* one unavailable adapter must not hide the other networks */ }
            }
        }
        try
        {
            while (true)
            {
                var reply = await udp.ReceiveAsync(deadline.Token);
                var phone = ParseReply(Encoding.ASCII.GetString(reply.Buffer), reply.RemoteEndPoint.Address);
                if (phone == null) continue;
                var key = $"{phone.Host}:{phone.Port}";
                // The same modern phone answers both queries: retain its stable identity regardless of reply order.
                if (!found.TryGetValue(key, out var old) || !phone.Id.StartsWith("legacy:")) found[key] = phone;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
        // Keep every distinct route for a stable identity. A phone may answer over both Wi-Fi
        // and USB tethering; reconciliation must see that ambiguity to preserve its chosen route.
        return found.Values.OrderBy(p => p.Name).ThenBy(p => p.Id).ThenBy(p => p.Host).ThenBy(p => p.Port).ToArray();
    }

    private static IEnumerable<IPAddress> Broadcasts()
    {
        var result = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.PrefixLength is < 1 or > 30) continue;
                var ip = address.Address.GetAddressBytes();
                var mask = address.IPv4Mask.GetAddressBytes();
                result.Add(new IPAddress(ip.Zip(mask, (a, m) => (byte)(a | ~m)).ToArray()));
            }
        }
        return result;
    }
}
