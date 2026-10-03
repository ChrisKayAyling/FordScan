using FordDiag.Comms;
using FordDiag.Comms.Elm;
using FordDiag.Comms.Simulation;

namespace FordDiag.Comms.Tests;

public class KLineTests
{
    private static EcuScript Script() => new EcuScript()
        .On("10C0", "50C0")
        .On("2180", "6180" + string.Concat(Enumerable.Range(0, 24).Select(i => (i + 1).ToString("X2"))))
        .Pending("3101", 1, "7101", TimeSpan.FromSeconds(2));

    [Fact]
    public async Task Kwp_fast_init_and_request()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeKwpEcu("UCH", 0x7A, Script())));
        await rig.Elm.ConnectEcuAsync(EcuAddress.KLine("UCH", EcuProtocol.Kwp2000Fast, "7A"));
        Assert.Contains("ATSH 81 7A F1", rig.Device.Commands);
        Assert.Contains("ATSP 5", rig.Device.Commands);
        Assert.Contains("ATFI", rig.Device.Commands);
        Assert.Contains("81", rig.Device.Commands);        // startCommunication
        Assert.Equal(Hex.Parse("50C0"), await rig.Elm.RequestAsync(Hex.Parse("10C0")));
        var long2180 = await rig.Elm.RequestAsync(Hex.Parse("2180"));
        Assert.Equal(26, long2180.Length);
    }

    [Fact]
    public async Task Kwp_slow_init_uses_ATIIA_and_ATSI()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeKwpEcu("UCH", 0x26, Script())));
        await rig.Elm.ConnectEcuAsync(EcuAddress.KLine("UCH", EcuProtocol.Kwp2000Slow, "26"));
        Assert.Contains("ATSP 4", rig.Device.Commands);
        Assert.Contains("ATIIA 26", rig.Device.Commands);
        Assert.Contains("ATSI", rig.Device.Commands);
        Assert.Equal(Hex.Parse("50C0"), await rig.Elm.RequestAsync(Hex.Parse("10C0")));
    }

    [Fact]
    public async Task Kwp_slow_init_falls_back_to_fast_init()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeKwpEcu("UCH", 0x26, Script(), fastInit: true, slowInit: false)));
        await rig.Elm.ConnectEcuAsync(EcuAddress.KLine("UCH", EcuProtocol.Kwp2000Slow, "26"));
        Assert.Contains("ATFI", rig.Device.Commands);
        Assert.Equal(Hex.Parse("50C0"), await rig.Elm.RequestAsync(Hex.Parse("10C0")));
    }

    [Fact]
    public async Task Iso8_slow_init()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeKwpEcu("ABS", 0x28, Script(), fastInit: false, slowInit: true)));
        await rig.Elm.ConnectEcuAsync(EcuAddress.KLine("ABS", EcuProtocol.Iso8, "28"));
        Assert.Contains("ATSP 3", rig.Device.Commands);
        Assert.Contains("ATSI", rig.Device.Commands);
        Assert.Equal(Hex.Parse("50C0"), await rig.Elm.RequestAsync(Hex.Parse("10C0")));
    }

    [Fact]
    public async Task Init_failure_is_reported()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeKwpEcu("UCH", 0x7A, Script())));
        var ex = await Assert.ThrowsAsync<ElmErrorException>(async () =>
            await rig.Elm.ConnectEcuAsync(EcuAddress.KLine("Missing", EcuProtocol.Kwp2000Fast, "55")));
        Assert.Contains("BUS INIT", ex.ElmMessage);
        Assert.Null(rig.Elm.CurrentEcu);
    }

    [Fact]
    public async Task Kwp_response_pending_is_waited_out()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeKwpEcu("UCH", 0x7A, Script())));
        await rig.Elm.ConnectEcuAsync(EcuAddress.KLine("UCH", EcuProtocol.Kwp2000Fast, "7A"));
        Assert.Equal(Hex.Parse("7101"), await rig.Elm.RequestAsync(Hex.Parse("3101")));
    }

    [Fact]
    public async Task Switching_from_k_line_to_can_and_back()
    {
        await using var rig = await TestRig.CreateAsync(b =>
        {
            b.Add(new FakeKwpEcu("UCH", 0x7A, Script()));
            b.Add(new FakeCanEcu("E", 0x7E0, 0x7E8, new EcuScript().On("22F186", "62F18603")));
        });
        await rig.Elm.ConnectEcuAsync(EcuAddress.KLine("UCH", EcuProtocol.Kwp2000Fast, "7A"));
        Assert.Equal(Hex.Parse("50C0"), await rig.Elm.RequestAsync(Hex.Parse("10C0")));
        await rig.Elm.ConnectEcuAsync(EcuAddress.Can("E", "7E0", "7E8"));
        Assert.Equal(Hex.Parse("62F18603"), await rig.Elm.RequestAsync(Hex.Parse("22F186")));
        await rig.Elm.CloseProtocolAsync();
        Assert.Null(rig.Elm.CurrentEcu);
        Assert.Contains("ATPC", rig.Device.Commands);
    }
}
