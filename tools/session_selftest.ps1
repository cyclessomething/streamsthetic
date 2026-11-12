# Real loopback UDP discovery plus session capacity, identity, and persistence checks. No phone or installed camera needed.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$testDir = Join-Path ([IO.Path]::GetTempPath()) ('streamsthetic-sessions-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDir | Out-Null
$files = 'PhoneDiscovery.cs', 'PhoneSessions.cs', 'ReceiverController.cs'
$includes = ($files | ForEach-Object { '<Compile Include="' + [Security.SecurityElement]::Escape((Join-Path $root "windows/app/$_")) + '" />' }) -join "`n"
@"
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup>$includes</ItemGroup>
</Project>
"@ | Set-Content (Join-Path $testDir 'SessionTests.csproj')
@'
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PhoneCam;

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine("PASS: " + description);
}
var id1 = "325e547a-2bf9-41c9-8eec-40b2c4892a10";
var id2 = "dc13fc46-00e8-48d5-9c3d-3dd68f50fbd0";
Check(PhoneDiscovery.ParseReply("PHONECAM2|bad|Phone|5556", IPAddress.Loopback) == null, "reject malformed stable identity");
Check(PhoneDiscovery.ParseReply("PHONECAM1|Phone|65536", IPAddress.Loopback) == null, "reject invalid port");
Check(PhoneDiscovery.ParseReply("PHONECAM1|bad\nname|5556", IPAddress.Loopback) == null, "reject control characters");
using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 5557));
using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var replies = Task.Run(async () =>
{
    for (var n = 0; n < 2; n++)
    {
        var request = await server.ReceiveAsync(cancel.Token);
        var query = Encoding.ASCII.GetString(request.Buffer);
        var messages = query == "PHONECAM2?"
            ? new[] { $"PHONECAM2|{id1}|Same model|5556", $"PHONECAM2|{id2}|Same model|5558" }
            : new[] { "PHONECAM1|Same model|5556", "PHONECAM1|Same model|5558" };
        foreach (var message in messages) await server.SendAsync(Encoding.ASCII.GetBytes(message), request.RemoteEndPoint, cancel.Token);
    }
});
var phones = await PhoneDiscovery.ScanAsync(new[] { IPAddress.Loopback }, TimeSpan.FromMilliseconds(500), cancel.Token);
await replies;
Check(phones.Count == 2 && phones.All(p => !p.Id.StartsWith("legacy:")), "real UDP retains two same-model phones and deduplicates old/new replies");
// One phone can answer over Wi-Fi and USB tethering. Keep both routes so reconciliation can
// preserve the chosen route rather than replacing it with whichever packet arrived first.
var secondAddress = IPAddress.Parse("127.0.0.2");
using var secondServer = new UdpClient(new IPEndPoint(secondAddress, 5557));
async Task AnswerBothProtocols(UdpClient responder)
{
    for (var n = 0; n < 2; n++)
    {
        var request = await responder.ReceiveAsync(cancel.Token);
        var query = Encoding.ASCII.GetString(request.Buffer);
        var message = query == "PHONECAM2?" ? $"PHONECAM2|{id1}|Dual-network phone|5556" : "PHONECAM1|Dual-network phone|5556";
        await responder.SendAsync(Encoding.ASCII.GetBytes(message), request.RemoteEndPoint, cancel.Token);
    }
}
var dualReplies = Task.WhenAll(AnswerBothProtocols(server), AnswerBothProtocols(secondServer));
var routes = await PhoneDiscovery.ScanAsync(new[] { IPAddress.Loopback, secondAddress }, TimeSpan.FromMilliseconds(500), cancel.Token);
await dualReplies;
Check(routes.Count == 2 && routes.All(p => p.Id == id1) && routes.Select(p => p.Host).Distinct().Count() == 2,
    "real UDP retains both addresses for one identity while deduplicating old/new replies per address");
using var dualNetwork = new PhoneSessions();
var chosenRoute = new PhoneEndpoint(id1, "Dual-network phone", secondAddress.ToString(), 5556);
dualNetwork.Assign(chosenRoute);
Check(dualNetwork.Reconcile(routes).Count == 0 && dualNetwork.Find(1).Endpoint == chosenRoute,
    "dual-network discovery cannot move a remembered phone to the first reply's address");
using var sessions = new PhoneSessions();
var first = sessions.Assign(phones[0])!;
var second = sessions.Assign(phones[1])!;
Check(first.Slot == 1 && second.Slot == 2 && !ReferenceEquals(first.Receiver, second.Receiver), "same-model phones get independent receiver slots");
var moved = phones[0] with { Host = "192.168.1.66" };
Check(ReferenceEquals(first, sessions.Assign(moved)) && first.Endpoint!.Host == moved.Host, "DHCP address change preserves camera slot by identity");
Check(sessions.Assign(new("third", "Three", "127.0.0.1", 5560))!.Slot == 3, "allocate camera three");
Check(sessions.Assign(new("fourth", "Four", "127.0.0.1", 5561))!.Slot == 4, "allocate camera four");
Check(sessions.Assign(new("fifth", "Five", "127.0.0.1", 5562)) == null, "fifth phone cannot overwrite another slot");
var json = JsonSerializer.Serialize(sessions.Assignments);
using var restored = new PhoneSessions();
restored.Restore(JsonSerializer.Deserialize<List<PhoneSlotAssignment>>(json));
Check(restored.Assignments.SequenceEqual(sessions.Assignments), "all slot assignments survive JSON save/reload");
restored.Restore(new[] { new PhoneSlotAssignment(9, phones[0]), new PhoneSlotAssignment(1, phones[1]) });
Check(restored.Assignments.SequenceEqual(sessions.Assignments), "invalid/duplicate restore cannot overwrite assigned slots");
restored.Restore(JsonSerializer.Deserialize<List<PhoneSlotAssignment>>("[null,{\"Slot\":2,\"Endpoint\":null},{\"Slot\":2,\"Endpoint\":{\"Id\":\"bad\",\"Name\":\"bad\",\"Host\":\"bad host\",\"Port\":-1}}]"));
Check(restored.Assignments.SequenceEqual(sessions.Assignments), "null and malformed saved assignments are ignored");
Check(restored.Assign(moved with { Id = "other-identity" }) == null, "different identity cannot duplicate an occupied endpoint");
restored.Forget(2);
Check(restored.Find(2).Endpoint == null && restored.Find(1).Endpoint == moved, "forget clears only selected slot");
Check(restored.Assign(new("fifth", "Five", "127.0.0.1", 5562))!.Slot == 2, "forgotten slot becomes reusable");
using var swapped = new PhoneSessions();
var front = new PhoneEndpoint(id1, "Front", "192.168.1.10", 5556);
var side = new PhoneEndpoint(id2, "Side", "192.168.1.11", 5556);
swapped.Assign(front);
swapped.Assign(side);
var frontMoved = front with { Host = side.Host };
var sideMoved = side with { Host = front.Host };
Check(swapped.Reconcile(new[] { frontMoved, sideMoved }).Count == 0
    && swapped.Find(1).Endpoint == frontMoved && swapped.Find(2).Endpoint == sideMoved,
    "one scan reconciles swapped DHCP addresses without changing camera slots");
Check(swapped.Assignments.Select(a => (a.Endpoint.Host, a.Endpoint.Port)).Distinct().Count() == 2,
    "DHCP swap never leaves duplicate assigned endpoints");
swapped.Reconcile(new[] { frontMoved with { Host = sideMoved.Host } });
Check(swapped.Find(1).Endpoint == frontMoved && swapped.Find(2).Endpoint == sideMoved,
    "partial scan cannot steal an absent remembered phone's endpoint");
swapped.Reconcile(new[] { frontMoved with { Host = "192.168.1.20" }, frontMoved with { Host = "192.168.1.21" } });
Check(swapped.Find(1).Endpoint == frontMoved, "ambiguous stable identity does not change its camera address");
swapped.Reconcile(new[] { frontMoved with { Host = "192.168.1.20" }, sideMoved with { Host = "192.168.1.20" } });
Check(swapped.Find(1).Endpoint == frontMoved && swapped.Find(2).Endpoint == sideMoved,
    "duplicate advertised endpoint does not move either remembered camera");
swapped.Reconcile(new[] { new PhoneEndpoint("unassigned", "New", "192.168.1.99", 5556) });
Check(swapped.Assignments.Count == 2, "discovery does not allocate unrequested phones");
var swapJson = JsonSerializer.Serialize(swapped.Assignments);
using var swapRestored = new PhoneSessions();
swapRestored.Restore(JsonSerializer.Deserialize<List<PhoneSlotAssignment>>(swapJson));
Check(swapRestored.Assignments.SequenceEqual(swapped.Assignments), "reconciled addresses survive save/reload");
using var stopped = new CancellationTokenSource();
stopped.Cancel();
try { await PhoneDiscovery.ScanAsync(new[] { IPAddress.Loopback }, TimeSpan.FromSeconds(1), stopped.Token); throw new Exception("cancellation ignored"); }
catch (OperationCanceledException) { Console.WriteLine("PASS: scan cancellation honored"); }
Console.WriteLine("SESSION SELF-TEST PASS");

namespace PhoneCam
{
    internal static class Diagnostics
    {
        public static void Log(string category, string message) { }
        public static void RecordProblem(string category, string title, string description, string? dump = null) { }
        public static string? FreshReceiverDump() => null;
    }
}
'@ | Set-Content (Join-Path $testDir 'Program.cs')
dotnet run --project (Join-Path $testDir 'SessionTests.csproj') -c Release
$testExit = $LASTEXITCODE
if ($testExit -ne 0) { Write-Output "Test project retained at $testDir" }
exit $testExit
