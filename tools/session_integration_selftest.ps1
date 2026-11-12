# Two real receiver processes, two recorded fake phones, independent settings, and rapid session restarts.
# Uses slots 3/4 with --no-camera: never registers or opens an installed virtual camera.
param(
    [string]$Receiver = "$PSScriptRoot/../windows/receiver/x64/Release/StreamstheticReceiver.exe",
    [string]$Recording = "$PSScriptRoot/testdata/phone-1080p60-10s.pcm1"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$testDir = Join-Path ([IO.Path]::GetTempPath()) ('streamsthetic-session-integration-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDir | Out-Null
$includes = ('PhoneDiscovery.cs', 'PhoneSessions.cs', 'ReceiverController.cs' | ForEach-Object {
    '<Compile Include="' + [Security.SecurityElement]::Escape((Join-Path $root "windows/app/$_")) + '" />'
}) -join "`n"
$receiverPath = [Security.SecurityElement]::Escape((Resolve-Path -LiteralPath $Receiver).Path)
@"
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup>$includes
<None Include="$receiverPath" Link="StreamstheticReceiver.exe" CopyToOutputDirectory="Always" />
</ItemGroup></Project>
"@ | Set-Content (Join-Path $testDir 'SessionIntegration.csproj')
@'
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using PhoneCam;

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}
static async Task Until(Func<bool> predicate, string description, int timeout = 15000)
{
    var watch = Stopwatch.StartNew();
    while (!predicate())
    {
        if (watch.ElapsedMilliseconds > timeout) throw new Exception("Timed out: " + description + "\n" + string.Join("\n", Diagnostics.Lines.TakeLast(25)));
        await Task.Delay(50);
    }
    Console.WriteLine("PASS: " + description);
}
static void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine("PASS: " + description);
}
static long PreviewSequence(int slot)
{
    try
    {
        using var mapping = MemoryMappedFile.OpenExisting(@"Local\PhoneCamPreview" + slot, MemoryMappedFileRights.Read);
        using var view = mapping.CreateViewAccessor(0, 24, MemoryMappedFileAccess.Read);
        return view.ReadUInt32(0) == 0x50435056 && view.ReadInt32(4) > 0 && view.ReadInt32(8) > 0 ? view.ReadInt64(16) : 0;
    }
    catch (FileNotFoundException) { return 0; }
}
var portA = FreePort();
var portB = FreePort();
while (portB == portA) portB = FreePort();
var fakeScript = """
import sys, socket, threading, types
sys.path.insert(0, sys.argv[1])
import fake_phone
packets = fake_phone.load_packets(sys.argv[2])
options = types.SimpleNamespace(code='123456', drop_after=0, corrupt_after=0)
server = socket.create_server(('127.0.0.1', int(sys.argv[3])))
print('ready', flush=True)
while True:
    conn, addr = server.accept()
    conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    threading.Thread(target=fake_phone.serve_client, args=(conn, packets, options), daemon=True).start()
""";
Process Fake(int port, ConcurrentQueue<string> output)
{
    var start = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in new[] { "-B", "-u", "-c", fakeScript, args[0], args[1], port.ToString() }) start.ArgumentList.Add(arg);
    var process = new Process { StartInfo = start };
    process.OutputDataReceived += (_, line) => { if (line.Data != null) output.Enqueue(line.Data); };
    process.ErrorDataReceived += (_, line) => { if (line.Data != null) output.Enqueue("ERROR: " + line.Data); };
    process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
    return process;
}
var outputA = new ConcurrentQueue<string>();
var outputB = new ConcurrentQueue<string>();
using var fakeA = Fake(portA, outputA);
using var fakeB = Fake(portB, outputB);
using var sessions = new PhoneSessions();
try
{
    await Until(() => outputA.Contains("ready") && outputB.Contains("ready"), "both fake phones ready");
    sessions.Restore(new[] {
        new PhoneSlotAssignment(3, new PhoneEndpoint("integration-a", "Front phone", "127.0.0.1", portA)),
        new PhoneSlotAssignment(4, new PhoneEndpoint("integration-b", "Side phone", "127.0.0.1", portB))
    });
    var a = sessions.Find(3);
    var b = sessions.Find(4);
    var statuses = new ConcurrentQueue<(int Slot, ReceiverStatus Status)>();
    sessions.StatusChanged += (session, status) => statuses.Enqueue((session.Slot, status));
    a.Receiver.Preview = true;
    b.Receiver.Preview = true;
    a.Start("123456", "--no-camera");
    b.Start("123456", "--no-camera");
    await Until(() => a.LatestSettings.GetValueOrDefault("mbps") == "20" && b.LatestSettings.GetValueOrDefault("mbps") == "20", "both receivers exchange real phone settings");
    await Until(() => PreviewSequence(3) > 6 && PreviewSequence(4) > 6, "both receivers decode into independent preview buffers");
    Check(a.Receiver.Status.PhoneName == "Front phone" && b.Receiver.Status.PhoneName == "Side phone", "direct connection statuses retain each phone's name");
    a.Receiver.SendSettings("zoom=0.6;mbps=40");
    await Until(() => a.LatestSettings.GetValueOrDefault("zoom") == "0.6" && a.LatestSettings.GetValueOrDefault("mbps") == "40", "selected phone applies and echoes settings");
    Check(b.LatestSettings.GetValueOrDefault("zoom") == "1.00" && b.LatestSettings.GetValueOrDefault("mbps") == "20", "other phone settings remain unchanged");
    Check(outputA.Any(line => line.Contains("zoom=0.6;mbps=40")) && !outputB.Any(line => line.Contains("zoom=0.6;mbps=40")), "wire command reaches only selected phone");

    for (var attempt = 0; attempt < 3; attempt++)
    {
        a.Receiver.Stop();
        a.Start("123456", "--no-camera");
        await Until(() => a.LatestSettings.GetValueOrDefault("mbps") == "40" && a.Receiver.Status.State == LinkState.WaitingForApp,
            "immediate restart reconnects slot 3, cycle " + (attempt + 1));
    }
    await Until(() => PreviewSequence(3) > 6 && PreviewSequence(4) > 6, "both sessions still decode after repeated restarts");
    Check(!statuses.Any(s => s.Status.State == LinkState.Error), "retired process callbacks do not mark replacement sessions as failed");
    a.Receiver.Stop();
    var previousB = PreviewSequence(4);
    await Task.Delay(500);
    Check(a.Receiver.Status.State == LinkState.Stopped, "late callbacks cannot revive stopped session");
    Check(b.Receiver.Status.State == LinkState.WaitingForApp && PreviewSequence(4) > previousB, "stopping one session leaves the other streaming");
    Console.WriteLine("SESSION INTEGRATION SELF-TEST PASS");
}
finally
{
    sessions.Dispose();
    foreach (var process in new[] { fakeA, fakeB })
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
        catch (InvalidOperationException) { }
    }
}

namespace PhoneCam
{
    internal static class Diagnostics
    {
        public static ConcurrentQueue<string> Lines { get; } = new();
        public static void Log(string category, string message) => Lines.Enqueue(category + ": " + message);
        public static void RecordProblem(string category, string title, string description, string? dump = null) => Lines.Enqueue(category + ": " + description);
        public static string? FreshReceiverDump() => null;
    }
}
'@ | Set-Content (Join-Path $testDir 'Program.cs')
dotnet run --project (Join-Path $testDir 'SessionIntegration.csproj') -c Release -- $PSScriptRoot (Resolve-Path -LiteralPath $Recording).Path
$testExit = $LASTEXITCODE
if ($testExit -ne 0) { Write-Output "Test project retained at $testDir" }
exit $testExit
