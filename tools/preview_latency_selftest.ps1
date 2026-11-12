# Isolated preview timing probe. Uses GUID-named synthetic shared memory only; never opens a phone,
# receiver, registered camera, or the live app's shared-memory names. No production files are changed.
param([int]$SecondsPerCase = 4)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$testDir = Join-Path ([IO.Path]::GetTempPath()) ('streamsthetic-preview-timing-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDir | Out-Null
$includes = ('PreviewReader.cs', 'CameraUsers.cs' | ForEach-Object {
    '<Compile Include="' + [Security.SecurityElement]::Escape((Join-Path $root "windows/app/$_")) + '" />'
}) -join "`n"
@"
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><AllowUnsafeBlocks>true</AllowUnsafeBlocks><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup>$includes</ItemGroup>
</Project>
"@ | Set-Content (Join-Path $testDir 'PreviewTiming.csproj')
@'
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PhoneCam;

internal static class Program
{
    private static int _exit;
    private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
    [STAThread]
    public static int Main(string[] args)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try
            {
                var seconds = args.Length > 0 ? int.Parse(args[0]) : 4;
                var usersCost = new List<double>();
                for (var i = 0; i < 20; i++)
                {
                    var before = Stopwatch.GetTimestamp();
                    CameraUsers.Current(); // Read-only lookup; do not disclose application names.
                    usersCost.Add((Stopwatch.GetTimestamp() - before) * TickMs);
                }
                Console.WriteLine(JsonSerializer.Serialize(new { Case = "CameraUsers.Current read-only cost", MedianMs = Percentile(usersCost, .5), P95Ms = Percentile(usersCost, .95), MaxMs = usersCost.Max() }));
                await Measure("current: producer66ms / UI66ms", 66, 66, seconds);
                await Measure("proposal: producer66ms / UI16ms", 66, 16, seconds);
                await Measure("render proposal: producer66ms / Rendering", 66, 0, seconds);
                await Measure("combined proposal: producer16ms / Rendering", 16, 0, seconds);
                Console.WriteLine("PREVIEW LATENCY PROBE PASS (synthetic publication-to-bitmap timing; excludes phone capture, receiver scaling, and screen composition)");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); _exit = 1; }
            finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        });
        Dispatcher.Run();
        return _exit;
    }

    private static async Task Measure(string name, int producerMs, int pollMs, int seconds)
    {
        const int width = 1280, height = 720, headerSize = 24;
        var mappingName = @"Local\StreamstheticPreviewProbe-" + Guid.NewGuid().ToString("N");
        using var mapping = MemoryMappedFile.CreateNew(mappingName, headerSize + width * height * 3 / 2);
        using var view = mapping.CreateViewAccessor();
        view.Write(0, 0x50435056u); view.Write(4, width); view.Write(8, height); view.Write(16, 1L);
        var pixels = new byte[width * height * 3 / 2];
        Array.Fill(pixels, (byte)96, 0, width * height);
        Array.Fill(pixels, (byte)128, width * height, width * height / 2);
        using var reader = new PreviewReader();
        // Private test seam changes only this reader instance to our isolated map; production names stay untouched.
        typeof(PreviewReader).GetField("_mappingName", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(reader, mappingName);
        var sequenceField = typeof(PreviewReader).GetField("_lastSeq", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var publicationTimes = new ConcurrentDictionary<long, long>();
        using var cancel = new CancellationTokenSource();
        var producer = Task.Factory.StartNew(() =>
        {
            long sequence = 0;
            var periodTicks = (long)(producerMs / TickMs);
            var next = Stopwatch.GetTimestamp() + (long)(20 / TickMs);
            while (!cancel.IsCancellationRequested)
            {
                var remaining = (next - Stopwatch.GetTimestamp()) * TickMs;
                if (remaining > 2) { Thread.Sleep(1); continue; }
                if (remaining > 0) { Thread.SpinWait(100); continue; }
                sequence += 2;
                Publish(view, pixels, sequence, publicationTimes);
                next += periodTicks;
            }
        }, TaskCreationOptions.LongRunning);
        var ages = new List<double>();
        var work = new List<double>();
        var tickIntervals = new List<double>();
        var lastTick = Stopwatch.GetTimestamp();
        var start = lastTick;
        var emptyPolls = 0;
        var verifyAfterStall = false;
        var stallRecovered = false;
        long skippedAfterStall = 0;
        long lastShown = 0;
        var testImage = new Image();
        Window? window = pollMs == 0 ? new Window {
            Width = 320, Height = 180, Left = -12000, Top = -12000,
            ShowActivated = false, ShowInTaskbar = false, Content = testImage
        } : null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(pollMs) };
        EventHandler render = (_, _) =>
        {
            var before = Stopwatch.GetTimestamp();
            var latestBefore = publicationTimes.Keys.DefaultIfEmpty(0).Max();
            var changed = reader.Update();
            var after = Stopwatch.GetTimestamp();
            if (changed && window != null) testImage.Source = reader.Bitmap;
            if ((before - start) * TickMs < 500) { lastTick = before; return; } // warm JIT and bitmap allocation
            tickIntervals.Add((before - lastTick) * TickMs);
            lastTick = before;
            if (!changed) { emptyPolls++; return; }
            work.Add((after - before) * TickMs);
            var sequence = (long)sequenceField.GetValue(reader)!;
            if (!publicationTimes.TryGetValue(sequence, out var published)) throw new Exception("Unknown displayed sequence");
            ages.Add((after - published) * TickMs);
            if (verifyAfterStall)
            {
                if (sequence < latestBefore) throw new Exception("Preview replayed stale queued frames after UI stall");
                skippedAfterStall = Math.Max(0, (sequence - lastShown) / 2 - 1);
                stallRecovered = true;
                verifyAfterStall = false;
            }
            lastShown = sequence;
        };
        if (window == null) { timer.Tick += render; timer.Start(); }
        else { window.Show(); CompositionTarget.Rendering += render; }
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(seconds * 500));
            Thread.Sleep(180); // Only this test dispatcher stalls; the synthetic producer keeps publishing.
            verifyAfterStall = true;
            await Task.Delay(TimeSpan.FromMilliseconds(seconds * 500));
        }
        finally
        {
            timer.Stop(); CompositionTarget.Rendering -= render; window?.Close();
            cancel.Cancel(); await producer;
        }
        if (ages.Count < 10 || reader.Bitmap == null) throw new Exception("Insufficient preview frames");
        if (!stallRecovered) throw new Exception("Preview did not resume after UI stall");
        Console.WriteLine(JsonSerializer.Serialize(new {
            Case = name, Samples = ages.Count, EmptyPolls = emptyPolls,
            PublicationToBitmapMedianMs = Percentile(ages, .5), PublicationToBitmapP95Ms = Percentile(ages, .95),
            ConversionAndCopyMedianMs = Percentile(work, .5), ConversionAndCopyP95Ms = Percentile(work, .95),
            DispatcherTickMedianMs = Percentile(tickIntervals, .5), DispatcherTickP95Ms = Percentile(tickIntervals, .95),
            ResumedWithLatestFrame = stallRecovered, SkippedFramesAfter180MsUiStall = skippedAfterStall
        }));
    }

    // Match the native receiver's memcpy + seqlock rather than benchmarking accessor.WriteArray.
    private static unsafe void Publish(MemoryMappedViewAccessor view, byte[] pixels, long sequence,
        ConcurrentDictionary<long, long> publicationTimes)
    {
        byte* memory = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref memory);
        try
        {
            memory += view.PointerOffset;
            ref var counter = ref *(long*)(memory + 16);
            Volatile.Write(ref counter, sequence - 1);
            pixels.AsSpan().CopyTo(new Span<byte>(memory + 24, pixels.Length));
            publicationTimes[sequence] = Stopwatch.GetTimestamp();
            Volatile.Write(ref counter, sequence);
        }
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
    }

    private static double Percentile(List<double> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        return Math.Round(sorted[(int)Math.Clamp(Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)], 3);
    }
}
'@ | Set-Content (Join-Path $testDir 'Program.cs')
dotnet run --project (Join-Path $testDir 'PreviewTiming.csproj') -c Release -- $SecondsPerCase
$testExit = $LASTEXITCODE
if ($testExit -ne 0) { Write-Output "Test project retained at $testDir" }
exit $testExit
