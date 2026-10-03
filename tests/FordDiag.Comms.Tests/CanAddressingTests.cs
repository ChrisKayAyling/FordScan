using FordDiag.Comms;
using FordDiag.Comms.Elm;
using FordDiag.Comms.Simulation;

namespace FordDiag.Comms.Tests;

public class CanAddressingTests
{
    [Fact]
    public async Task Extended_29_bit_addressing_uses_ATCP_and_8_digit_filter()
    {
        var script = new EcuScript().On("22F190", "62F1904142");
        var ecu = new FakeCanEcu("EVC", 0x18DA10F1, 0x18DAF110, script, is29Bit: true);
        await using var rig = await TestRig.CreateAsync(b => b.Add(ecu));
        await rig.Elm.ConnectEcuAsync(EcuAddress.Can("EVC", "18DA10F1", "18DAF110"));
        Assert.Equal(Hex.Parse("62F1904142"), await rig.Elm.RequestAsync(Hex.Parse("22F190")));
        Assert.Contains("ATCP 18", rig.Device.Commands);
        Assert.Contains("ATSH DA10F1", rig.Device.Commands);
        Assert.Contains("ATSP 7", rig.Device.Commands);
        Assert.Contains("ATCRA 18DAF110", rig.Device.Commands);
    }

    [Fact]
    public async Task CAN_250k_bus_selects_protocol_8()
    {
        var script = new EcuScript().On("22F190", "62F190AA");
        await using var rig = await TestRig.CreateAsync(b => { b.SpeedKbps = 250; b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, script)); });
        await rig.Elm.ConnectEcuAsync(EcuAddress.Can("E", "7E0", "7E8", CanSpeed.Kbps250));
        Assert.Equal(Hex.Parse("62F190AA"), await rig.Elm.RequestAsync(Hex.Parse("22F190")));
        Assert.Contains("ATSP 8", rig.Device.Commands);
    }

    [Fact]
    public async Task Auto_speed_falls_back_to_500k_after_CAN_ERROR()
    {
        var script = new EcuScript().On("22F190", "62F190AA");
        await using var rig = await TestRig.CreateAsync(b => { b.SpeedKbps = 500; b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, script)); });
        await rig.Elm.ConnectEcuAsync(EcuAddress.Can("E", "7E0", "7E8", CanSpeed.Auto));
        Assert.Equal(Hex.Parse("62F190AA"), await rig.Elm.RequestAsync(Hex.Parse("22F190")));
        Assert.Contains("ATSP 8", rig.Device.Commands);
        Assert.Contains("ATSP 6", rig.Device.Commands);
    }

    [Fact]
    public async Task Wrong_bus_speed_reports_can_error()
    {
        var script = new EcuScript().On("22F190", "62F190AA");
        await using var rig = await TestRig.CreateAsync(b => { b.SpeedKbps = 250; b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, script)); },
            configure: o => o.Retries = 0);
        await rig.Elm.ConnectEcuAsync(EcuAddress.Can("E", "7E0", "7E8", CanSpeed.Kbps500));
        await Assert.ThrowsAsync<ElmErrorException>(async () => await rig.Elm.RequestAsync(Hex.Parse("22F190")));
    }

    [Fact]
    public async Task Extended_isotp_address_byte_is_used_in_manual_mode_with_multi_frame()
    {
        var script = new EcuScript().On("22", _ => new[] { new EcuReply(Hex.Parse("62F190" + string.Concat(Enumerable.Repeat("AB", 12)))) });
        var ecu = new FakeCanEcu("X", 0x6A2, 0x6A3, script, extAddress: 0x4D) { BlockSize = 2 };
        await using var rig = await TestRig.CreateAsync(b => b.Add(ecu));
        var addr = EcuAddress.Can("X", "6A2", "6A3") with { ExtAddress = 0x4D };
        await rig.Elm.ConnectEcuAsync(addr);
        var rsp = await rig.Elm.RequestAsync(Hex.Parse("22F190"));
        Assert.Equal(15, rsp.Length);
        // a 14 byte request must be segmented with 6/5-byte payload frames
        var req = new byte[] { 0x2E }.Concat(Enumerable.Range(1, 13).Select(i => (byte)i)).ToArray();
        var script2 = new EcuScript().On("2E", "6E");
        var ecu2 = new FakeCanEcu("Y", 0x6B2, 0x6B3, script2, extAddress: 0x01);
        await using var rig2 = await TestRig.CreateAsync(b => b.Add(ecu2));
        await rig2.Elm.ConnectEcuAsync(EcuAddress.Can("Y", "6B2", "6B3") with { ExtAddress = 0x01 });
        Assert.Equal(new byte[] { 0x6E }, await rig2.Elm.RequestAsync(req));
        Assert.Equal(req, script2.Received.Single());
    }

    [Fact]
    public async Task Padding_option_pads_frames()
    {
        var script = new EcuScript().On("22F190", "62F190AA");
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, script)), configure: o => o.PadByte = 0xAA);
        await rig.Elm.ConnectEcuAsync(EcuAddress.Can("E", "7E0", "7E8"));
        await rig.Elm.RequestAsync(Hex.Parse("22F190"));
        Assert.Contains(rig.Device.Commands, c => c.StartsWith("0322F190AAAAAAAA"));
    }

    [Fact]
    public async Task Automatic_isotp_mode_single_and_multi_frame()
    {
        var script = new EcuScript()
            .On("22F186", "62F18603")
            .On("22F190", "62F190" + string.Concat(Enumerable.Range(0, 20).Select(i => i.ToString("X2"))));
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, script)), configure: o => o.IsoTp = IsoTpMode.ElmAutomatic);
        await rig.Elm.ConnectEcuAsync(EcuAddress.Can("E", "7E0", "7E8"));
        Assert.Equal(Hex.Parse("62F18603"), await rig.Elm.RequestAsync(Hex.Parse("22F186")));
        var rsp = await rig.Elm.RequestAsync(Hex.Parse("22F190"));
        Assert.Equal(23, rsp.Length);
        Assert.Equal(0x13, rsp[^1]);
        Assert.Contains("ATCAF1", rig.Device.Commands);
    }

    [Fact]
    public async Task Automatic_mode_rejects_long_requests()
    {
        var script = new EcuScript();
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, script)), configure: o => o.IsoTp = IsoTpMode.ElmAutomatic);
        await rig.Elm.ConnectEcuAsync(EcuAddress.Can("E", "7E0", "7E8"));
        await Assert.ThrowsAsync<ProtocolException>(async () => await rig.Elm.RequestAsync(new byte[20]));
    }

    [Fact]
    public async Task Session_request_is_sent_on_connect_and_resent_after_idle()
    {
        var script = new EcuScript().On("10C0", "50C0").On("22F186", "62F18603");
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, script)),
            configure: o => o.KeepAliveInterval = TimeSpan.FromMilliseconds(60));
        var addr = EcuAddress.Can("E", "7E0", "7E8") with { StartSession = Hex.Parse("10C0") };
        await rig.Elm.ConnectEcuAsync(addr);
        Assert.Equal(1, script.Received.Count(r => r[0] == 0x10));
        await rig.Elm.RequestAsync(Hex.Parse("22F186"));
        Assert.Equal(1, script.Received.Count(r => r[0] == 0x10));
        await Task.Delay(120);
        await rig.Elm.RequestAsync(Hex.Parse("22F186"));
        Assert.Equal(2, script.Received.Count(r => r[0] == 0x10));
    }

    [Fact]
    public async Task Failed_session_start_throws_negative_response()
    {
        var script = new EcuScript().Negative("10C0", 0x12);
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, script)));
        var addr = EcuAddress.Can("E", "7E0", "7E8") with { StartSession = Hex.Parse("10C0") };
        var ex = await Assert.ThrowsAsync<NegativeResponseException>(async () => await rig.Elm.ConnectEcuAsync(addr));
        Assert.Equal(0x12, ex.Code);
    }

    [Fact]
    public async Task Background_keep_alive_sends_tester_present_only_when_idle_and_can_be_stopped()
    {
        var script = new EcuScript().On("3E00", "7E00").On("22F186", "62F18603");
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, script)));
        await rig.Elm.ConnectEcuAsync(EcuAddress.Can("E", "7E0", "7E8"));
        rig.Elm.StartKeepAlive(TimeSpan.FromMilliseconds(40));
        await WaitUntil(() => script.Received.Count(r => r[0] == 0x3E) >= 2);
        rig.Elm.StopKeepAlive();
        int n = script.Received.Count(r => r[0] == 0x3E);
        await Task.Delay(120);
        Assert.Equal(n, script.Received.Count(r => r[0] == 0x3E));
        Assert.Equal(Hex.Parse("62F18603"), await rig.Elm.RequestAsync(Hex.Parse("22F186")));
    }

    internal static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not reached.");
            await Task.Delay(5);
        }
    }
}
