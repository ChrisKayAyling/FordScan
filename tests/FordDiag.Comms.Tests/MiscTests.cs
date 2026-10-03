using System.Net;
using System.Net.Sockets;
using FordDiag.Comms;
using FordDiag.Comms.Diagnostics;
using FordDiag.Comms.Serial;
using FordDiag.Comms.Simulation;

namespace FordDiag.Comms.Tests;

public class MiscTests
{
    [Fact]
    public void Hex_helpers()
    {
        Assert.Equal(new byte[] { 0x10, 0xC0 }, Hex.Parse("10 c0"));
        Assert.Equal("10 C0", Hex.ToString(new byte[] { 0x10, 0xC0 }, true));
        Assert.Equal("10C0", Hex.ToString(new byte[] { 0x10, 0xC0 }));
        Assert.False(Hex.TryParse("1", out _));
        Assert.False(Hex.TryParse("ZZ", out _));
        Assert.True(Hex.IsHexLine("7E8 03"));
        Assert.False(Hex.IsHexLine("NO DATA"));
        Assert.Throws<FormatException>(() => Hex.Parse("xyz"));
    }

    [Theory]
    [InlineData("192.168.0.10:35000", PortKind.Tcp)]
    [InlineData("COM3", PortKind.Serial)]
    [InlineData("/dev/ttyUSB0", PortKind.Usb)]
    [InlineData("/dev/rfcomm0", PortKind.Bluetooth)]
    [InlineData("/dev/cu.usbserial-1410", PortKind.Usb)]
    public void Port_classification(string name, PortKind kind) => Assert.Equal(kind, PortEnumerator.Classify(name));

    [Fact]
    public void Endpoint_parsing()
    {
        Assert.True(PortEnumerator.TryParseEndpoint("192.168.0.10:35000", out var h, out var p));
        Assert.Equal(("192.168.0.10", 35000), (h, p));
        Assert.False(PortEnumerator.TryParseEndpoint("COM3", out _, out _));
        Assert.False(PortEnumerator.TryParseEndpoint("1.2.3.4:99999", out _, out _));
    }

    [Fact]
    public async Task Port_listing_does_not_throw_and_adds_network_defaults()
    {
        var ports = PortEnumerator.GetPorts(includeNetworkDefaults: true);
        Assert.Contains(ports, p => p.Name == PortEnumerator.DefaultWifiEndpoint && p.Kind == PortKind.Tcp);
        var probed = await PortEnumerator.GetPortsAsync(probe: true, includeNetworkDefaults: false, probeTimeout: TimeSpan.FromMilliseconds(100));
        Assert.All(probed, p => Assert.NotEqual(PortStatus.Unknown, p.Status));
    }

    [Fact]
    public async Task Tcp_link_speaks_to_a_wifi_adapter_and_elm_driver_connects_over_it()
    {
        // A tiny fake WiFi dongle: answers the init chatter like an ELM327.
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream(); var sb = new System.Text.StringBuilder(); var buf = new byte[64];
            while (true)
            {
                int n; try { n = await s.ReadAsync(buf); } catch { break; }
                if (n == 0) break;
                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (b != '\r') { sb.Append((char)b); continue; }
                    var cmd = sb.ToString(); sb.Clear();
                    string reply = cmd == "ATZ" || cmd == "ATI" ? "ELM327 v2.1\r\r>" : cmd == "" ? ">" : "OK\r\r>";
                    await s.WriteAsync(System.Text.Encoding.ASCII.GetBytes(reply));
                }
            }
        });
        await using var t = await Elm.ElmTransport.OpenAsync($"127.0.0.1:{port}", new Elm.ElmOptions { FirstProbeTimeout = TimeSpan.FromSeconds(2) });
        Assert.Equal(0, t.Connection.Link.BaudRate);
        Assert.Contains("ELM327", t.Info.Version);
        await t.DisposeAsync();
        listener.Stop();
        await server.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Tcp_connect_to_closed_port_fails_with_comms_exception() =>
        await Assert.ThrowsAnyAsync<CommsException>(async () => await TcpSerialLink.ConnectAsync("127.0.0.1", 1, TimeSpan.FromSeconds(1)));

    [Fact]
    public async Task In_memory_link_is_duplex_and_signals_eof()
    {
        var (a, b) = InMemorySerialLink.CreatePair();
        await a.WriteAsync(new byte[] { 1, 2, 3 });
        var buf = new byte[8];
        Assert.Equal(3, await b.ReadAsync(buf));
        await a.DisposeAsync();
        Assert.Equal(0, await b.ReadAsync(buf));
        await b.DisposeAsync();
    }

    [Fact]
    public async Task Simulated_transport()
    {
        var sim = new SimulatedTransport().AddCan("A", 0x7E0, new EcuScript().Pending("31", 1, "7101"));
        await sim.ConnectEcuAsync(EcuAddress.Can("A", "7E0", "7E8"));
        Assert.Equal(Hex.Parse("7101"), await sim.RequestAsync(Hex.Parse("3101")));
        await Assert.ThrowsAsync<EcuTimeoutException>(async () => await sim.ConnectEcuAsync(EcuAddress.Can("Z", "7FF", "7FE")));
    }

    [Fact]
    public async Task Diagnostic_helpers()
    {
        var script = new EcuScript()
            .On("1003", "5003").On("3E00", "7E00")
            .On("1902AF", "5902FF" + "0123" + "4F" + "2A" + "C0" + "00" + "08" + "04" + "01" + "00" + "09")
            .On("1800FF00", "58" + "02" + "012300" + "C0E108")
            .On("14FFFFFF", "54").On("14FF00", "54").On("1101", "5101")
            .On("2112", "6112AABB");
        var t = new SimulatedTransport().AddCan("A", 0x7E0, script);
        await t.ConnectEcuAsync(EcuAddress.Can("A", "7E0", "7E8"));
        Assert.True(await t.StartSessionAsync(0x03));
        Assert.True(await t.TesterPresentAsync());
        var uds = await t.ReadDtcsUdsAsync();
        Assert.Equal(2, uds.Count);
        Assert.Equal("P0123-4F", uds[0].Text);
        Assert.Equal((byte)0x2A, uds[0].Status);
        Assert.Equal("U0000-08", uds[1].Text.Substring(0, 8));
        var kwp = await t.ReadDtcsKwpAsync();
        Assert.Equal(new[] { "P0123", "U0108" }.Length, kwp.Count);
        Assert.Equal("P0123", kwp[0].Text);
        Assert.Equal("U00E1", kwp[1].Text);
        await t.ClearDtcsUdsAsync(); await t.ClearDtcsKwpAsync(); await t.EcuResetAsync();
        Assert.Equal(new byte[] { 0xAA, 0xBB }, await t.ReadDataByLocalIdentifierAsync(0x12));
        Assert.Equal("61 12 AA BB", await t.RequestHexAsync("2112"));
        Assert.True(DiagnosticExtensions.TryGetNegativeResponse(new byte[] { 0x7F, 0x22, 0x31 }, out var s, out var n));
        Assert.Equal((0x22, 0x31), (s, n));
        await Assert.ThrowsAsync<NegativeResponseException>(async () => await t.ReadDataByIdentifierAsync(0xABCD));
    }

    [Fact]
    public async Task Many_requests_are_fast()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, new EcuScript().On("22F190", "62F190" + new string('A', 40)))));
        await rig.Elm.ConnectEcuAsync(EcuAddress.Can("E", "7E0", "7E8"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 300; i++) await rig.Elm.RequestAsync(Hex.Parse("22F190"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"300 multi-frame requests took {sw.Elapsed}");
    }
}
