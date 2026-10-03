using FordDiag.Comms;
using FordDiag.Comms.Simulation;
using FordDiag.Comms.Sniffer;

namespace FordDiag.Comms.Tests;

public class SnifferTests
{
    private static readonly (uint, byte[])[] Traffic =
    {
        (0x100u, new byte[] { 1, 2, 3, 4 }),
        (0x200u, new byte[] { 5, 6 }),
        (0x18DAF110u, new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 }),
    };

    [Theory]
    [InlineData("7E8 03 7F 22 11", true, 0x7E8u, false, "037F2211")]
    [InlineData("18DAF110 02 10 C0", true, 0x18DAF110u, true, "0210C0")]
    [InlineData("03 7F 22 11", false, null, false, "037F2211")]
    public void Line_parsing(string line, bool headers, uint? id, bool ext, string data)
    {
        Assert.True(CanSniffer.TryParseLine(line, headers, 0, out var f));
        Assert.Equal(id, f.Id); Assert.Equal(ext, f.Extended); Assert.Equal(data, Hex.ToString(f.Data));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<DATA ERROR")]
    [InlineData("BUFFER FULL")]
    [InlineData("12 03 7F")]       // id of wrong length
    [InlineData("7E8 ZZ")]
    public void Bad_lines_are_rejected(string line) => Assert.False(CanSniffer.TryParseLine(line, true, 0, out _));

    [Fact]
    public async Task Streams_frames_and_stops_cleanly()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, new EcuScript().On("22F186", "62F18603"))));
        var sniffer = new CanSniffer(rig.Elm);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var gen = new CancellationTokenSource();
        var frames = new List<SniffedFrame>();
        var genTask = Task.Run(async () =>
        {
            await CanAddressingTests.WaitUntil(() => rig.Bus.IsMonitored);
            await rig.Bus.GenerateTrafficAsync(Traffic, TimeSpan.FromMilliseconds(2), gen.Token);
        });
        await foreach (var f in sniffer.FramesAsync(cts.Token))
        {
            frames.Add(f);
            if (frames.Count == 9) break;
        }
        gen.Cancel(); await genTask;
        Assert.Equal(9, frames.Count);
        Assert.Equal(0x100u, frames[0].Id);
        Assert.Equal("01 02 03 04", frames[0].DataHex);
        Assert.True(frames[2].Extended);
        Assert.Equal("18DAF110", frames[2].IdHex);
        Assert.False(rig.Bus.IsMonitored);
        Assert.Contains("ATMA", rig.Device.Commands);
        // the diagnostic channel works again, addressing is re-programmed automatically
        await rig.Elm.ConnectEcuAsync(EcuAddress.Can("E", "7E0", "7E8"));
        Assert.Equal(Hex.Parse("62F18603"), await rig.Elm.RequestAsync(Hex.Parse("22F186")));
    }

    [Fact]
    public async Task Batches_are_throttled()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, new EcuScript())));
        var sniffer = new CanSniffer(rig.Elm, new SnifferOptions { FilterId = "100" });
        using var gen = new CancellationTokenSource();
        var genTask = Task.Run(async () =>
        {
            await CanAddressingTests.WaitUntil(() => rig.Bus.IsMonitored);
            await rig.Bus.GenerateTrafficAsync(Traffic, TimeSpan.FromMilliseconds(1), gen.Token);
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int batches = 0, total = 0;
        await foreach (var batch in sniffer.BatchesAsync(TimeSpan.FromMilliseconds(50)))
        {
            batches++; total += batch.Count;
            if (sw.ElapsedMilliseconds > 400) break;
        }
        gen.Cancel(); await genTask;
        Assert.InRange(batches, 2, 12);       // <= ~20 Hz at 50 ms, never one callback per frame
        Assert.True(total > batches);          // batches really aggregate several frames
        Assert.Contains("ATCRA 100", rig.Device.Commands);
    }

    [Fact]
    public async Task Aggregated_view_counts_per_identifier()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, new EcuScript())));
        var sniffer = new CanSniffer(rig.Elm);
        using var gen = new CancellationTokenSource();
        var genTask = Task.Run(async () =>
        {
            await CanAddressingTests.WaitUntil(() => rig.Bus.IsMonitored);
            await rig.Bus.GenerateTrafficAsync(Traffic, TimeSpan.FromMilliseconds(1), gen.Token);
        });
        var counts = new Dictionary<uint, long>();
        await foreach (var rows in sniffer.AggregatedAsync(TimeSpan.FromMilliseconds(30)))
        {
            foreach (var r in rows) counts[r.Id!.Value] = r.Count;
            if (counts.Count == 3 && counts.Values.All(c => c >= 3)) break;
        }
        gen.Cancel(); await genTask;
        Assert.Equal(3, counts.Count);
    }

    [Fact]
    public async Task Slow_consumer_drops_oldest_frames_instead_of_blocking()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, new EcuScript())));
        var sniffer = new CanSniffer(rig.Elm, new SnifferOptions { QueueCapacity = 4 });
        using var gen = new CancellationTokenSource();
        var genTask = Task.Run(async () =>
        {
            await CanAddressingTests.WaitUntil(() => rig.Bus.IsMonitored);
            await rig.Bus.GenerateTrafficAsync(Traffic, TimeSpan.FromMilliseconds(1), gen.Token);
        });
        int n = 0;
        await foreach (var _ in sniffer.FramesAsync())
        {
            await Task.Delay(250);   // very slow UI (generous: Windows timers tick ~15 ms)
            if (++n == 2) break;
        }
        gen.Cancel(); await genTask;
        Assert.True(sniffer.DroppedFrames > 0);
        Assert.True(sniffer.ReceivedFrames > 4);
    }
}
