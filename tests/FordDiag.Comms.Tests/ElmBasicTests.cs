using FordDiag.Comms;
using FordDiag.Comms.Diagnostics;
using FordDiag.Comms.Elm;
using FordDiag.Comms.Simulation;

namespace FordDiag.Comms.Tests;

public class ElmBasicTests
{
    private static EcuScript Script() => new EcuScript()
        .On("22F190", "62F190564631323334353637383930313233343536") // 21 bytes -> multi-frame
        .On("22F186", "62F18603")
        .On("1003", "5003003201F4");

    private static readonly EcuAddress Edc = EcuAddress.Can("EDC", "7E0", "7E8");

    [Fact]
    public async Task Connect_identifies_adapter_and_runs_single_frame_request()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, Script())));
        Assert.Equal("elm327", rig.Elm.Profile.Key);
        Assert.Contains("ELM327", rig.Elm.Info.Version);
        await rig.Elm.ConnectEcuAsync(Edc);
        var rsp = await rig.Elm.RequestAsync(Hex.Parse("22F186"));
        Assert.Equal(Hex.Parse("62F18603"), rsp);
        // addressing commands were issued
        Assert.Contains("ATSH 7E0", rig.Device.Commands);
        Assert.Contains("ATCRA 7E8", rig.Device.Commands);
        Assert.Contains("ATSP 6", rig.Device.Commands);
    }

    [Fact]
    public async Task Multi_frame_response_is_reassembled()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, Script())));
        await rig.Elm.ConnectEcuAsync(Edc);
        var rsp = await rig.Elm.RequestAsync(Hex.Parse("22F190"));
        Assert.Equal(Hex.Parse("62F190564631323334353637383930313233343536"), rsp);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(100)]
    [InlineData(300)]
    [InlineData(4000)]
    public async Task Multi_frame_request_and_response_roundtrip_with_flow_control(int size)
    {
        var script = new EcuScript().On("36", req => new[] { new EcuReply(new byte[] { 0x76, req[1] }.Concat(req.Skip(2).Take(size)).ToArray()) });
        var ecu = new FakeCanEcu("TD", 0x7E0, 0x7E8, script) { BlockSize = 3, StMin = 0 };
        await using var rig = await TestRig.CreateAsync(b => b.Add(ecu));
        await rig.Elm.ConnectEcuAsync(Edc);
        var payload = Enumerable.Range(0, size + 2).Select(i => (byte)(i * 7)).ToArray();
        payload[0] = 0x36; payload[1] = 0x05;
        var rsp = await rig.Elm.RequestAsync(payload);
        Assert.Equal(payload, script.Received[0]);            // ECU received the complete segmented request
        Assert.Equal(size + 2, rsp.Length);                   // and we reassembled its (possibly long) answer
        Assert.Equal(0x76, rsp[0]);
        Assert.Equal(payload.Skip(2).ToArray(), rsp.Skip(2).ToArray());
    }

    [Fact]
    public async Task Request_with_unlimited_block_size()
    {
        var script = new EcuScript().On("3D", "7D00");
        var ecu = new FakeCanEcu("TD", 0x7E0, 0x7E8, script) { BlockSize = 0 };
        await using var rig = await TestRig.CreateAsync(b => b.Add(ecu));
        await rig.Elm.ConnectEcuAsync(Edc);
        var payload = new byte[] { 0x3D }.Concat(Enumerable.Range(0, 40).Select(i => (byte)i)).ToArray();
        var rsp = await rig.Elm.RequestAsync(payload);
        Assert.Equal(new byte[] { 0x7D, 0x00 }, rsp);
        Assert.Equal(payload, script.Received.Single());
    }

    [Fact]
    public async Task Negative_response_is_returned_and_helper_throws()
    {
        var script = new EcuScript().Negative("2E", 0x31);
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, script)));
        await rig.Elm.ConnectEcuAsync(Edc);
        var rsp = await rig.Elm.RequestAsync(Hex.Parse("2EF19000"));
        Assert.Equal(new byte[] { 0x7F, 0x2E, 0x31 }, rsp);
        var ex = await Assert.ThrowsAsync<NegativeResponseException>(async () => await rig.Elm.RequestPositiveAsync("2EF19000"));
        Assert.Equal(0x31, ex.Code);
        Assert.Contains("Out Of Range", ex.Message);
    }

    [Fact]
    public async Task Response_pending_is_waited_out_single_frame_final()
    {
        var script = new EcuScript().Pending("31", 2, "7101AA", TimeSpan.FromMilliseconds(1500));
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, script)));
        await rig.Elm.ConnectEcuAsync(Edc);
        var rsp = await rig.Elm.RequestAsync(Hex.Parse("3101AA"));
        Assert.Equal(Hex.Parse("7101AA"), rsp);
        Assert.Contains("ATMA", rig.Device.Commands);       // the final answer arrived after the adapter timeout: monitor mode was used
        // adapter is still usable afterwards
        Assert.Equal(new byte[] { 0x7F, 0x22, 0x11 }, await rig.Elm.RequestAsync(Hex.Parse("22F100")));
    }

    [Fact]
    public async Task Response_pending_with_multi_frame_final_response()
    {
        var big = "62F190" + string.Concat(Enumerable.Range(0, 30).Select(i => i.ToString("X2")));
        var script = new EcuScript().Pending("22F190", 1, big, TimeSpan.FromSeconds(3));
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, script)));
        await rig.Elm.ConnectEcuAsync(Edc);
        var rsp = await rig.Elm.RequestAsync(Hex.Parse("22F190"));
        Assert.Equal(Hex.Parse(big), rsp);
    }

    [Fact]
    public async Task Response_pending_with_fast_final_needs_no_monitor()
    {
        var script = new EcuScript().Pending("31", 1, "7101", TimeSpan.FromMilliseconds(10));
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, script)));
        await rig.Elm.ConnectEcuAsync(Edc);
        // count suffix "1" returns only the pending frame, so the monitor path is still exercised and must work
        Assert.Equal(Hex.Parse("7101"), await rig.Elm.RequestAsync(Hex.Parse("3101")));
    }

    [Fact]
    public async Task Pending_forever_times_out()
    {
        var script = new EcuScript().On("31", _ => new[] { new EcuReply(new byte[] { 0x7F, 0x31, 0x78 }) });
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, script)),
            configure: o => o.PendingTimeout = TimeSpan.FromMilliseconds(200));
        await rig.Elm.ConnectEcuAsync(Edc);
        await Assert.ThrowsAsync<EcuTimeoutException>(async () => await rig.Elm.RequestAsync(Hex.Parse("3101")));
        // and the adapter recovered: an unknown service gets the default NRC
        Assert.Equal(new byte[] { 0x7F, 0x22, 0x11 }, await rig.Elm.RequestAsync(Hex.Parse("22F100")));
    }

    [Fact]
    public async Task Broadcast_frames_from_the_ecu_are_ignored()
    {
        // The ECU spams an unrelated single frame before the real answer.
        var script = new EcuScript().On("22F186", _ => new[] { new EcuReply(Hex.Parse("6200AABB")), new EcuReply(Hex.Parse("62F18603")) });
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, script)));
        await rig.Elm.ConnectEcuAsync(Edc);
        // both are "62 xx" so the first matches the positive-sid rule; use sid mismatch for the junk instead
        var junk = new EcuScript().On("22F186", _ => new[] { new EcuReply(Hex.Parse("AA0102")), new EcuReply(Hex.Parse("62F18603")) });
        await using var rig2 = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, junk)));
        await rig2.Elm.ConnectEcuAsync(Edc);
        Assert.Equal(Hex.Parse("62F18603"), await rig2.Elm.RequestAsync(Hex.Parse("22F186")));
    }

    [Fact]
    public async Task Ecu_not_answering_gives_timeout_exception()
    {
        var script = new EcuScript().Silent("22F100");
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, script)));
        await rig.Elm.ConnectEcuAsync(Edc);
        var ex = await Assert.ThrowsAsync<EcuTimeoutException>(async () => await rig.Elm.RequestAsync(Hex.Parse("22F100")));
        Assert.Contains("No response", ex.Message);
        Assert.Equal(1, rig.Elm.Counters.NoData);
    }

    [Fact]
    public async Task Unresponsive_adapter_times_out_and_recovers()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, Script())),
            configure: o => { o.RequestTimeout = TimeSpan.FromMilliseconds(150); });
        await rig.Elm.ConnectEcuAsync(Edc);
        rig.Device.Mute = true;
        await Assert.ThrowsAsync<EcuTimeoutException>(async () => await rig.Elm.RequestAsync(Hex.Parse("22F186")));
        rig.Device.Mute = false;
        Assert.Equal(Hex.Parse("62F18603"), await rig.Elm.RequestAsync(Hex.Parse("22F186")));
    }

    [Fact]
    public async Task Cancellation_aborts_request_and_adapter_recovers()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, Script())),
            configure: o => o.RequestTimeout = TimeSpan.FromSeconds(30));
        await rig.Elm.ConnectEcuAsync(Edc);
        rig.Device.Mute = true;
        using var cts = new CancellationTokenSource(80);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await rig.Elm.RequestAsync(Hex.Parse("22F186"), cts.Token));
        rig.Device.Mute = false;
        Assert.Equal(Hex.Parse("62F18603"), await rig.Elm.RequestAsync(Hex.Parse("22F186")));
    }

    [Fact]
    public async Task Pre_cancelled_token_throws_immediately()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, Script())));
        await rig.Elm.ConnectEcuAsync(Edc);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await rig.Elm.RequestAsync(Hex.Parse("22F186"), cts.Token));
    }

    [Theory]
    [InlineData("CAN ERROR")]
    [InlineData("BUFFER FULL")]
    [InlineData("RX ERROR")]
    public async Task Transient_adapter_errors_are_retried(string error)
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, Script())));
        await rig.Elm.ConnectEcuAsync(Edc);
        rig.Device.InjectErrorOnNextData(error);
        Assert.Equal(Hex.Parse("62F18603"), await rig.Elm.RequestAsync(Hex.Parse("22F186")));
    }

    [Fact]
    public async Task Persistent_adapter_error_is_reported()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, Script())),
            configure: o => o.Retries = 0);
        await rig.Elm.ConnectEcuAsync(Edc);
        rig.Device.InjectErrorOnNextData("CAN ERROR");
        var ex = await Assert.ThrowsAsync<ElmErrorException>(async () => await rig.Elm.RequestAsync(Hex.Parse("22F186")));
        Assert.Equal("CAN ERROR", ex.ElmMessage);
    }

    [Fact]
    public async Task Concurrent_requests_are_serialised()
    {
        var script = new EcuScript().On("22", req => new[] { new EcuReply(new byte[] { 0x62, req[1], req[2], req[1] }) });
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, script)));
        await rig.Elm.ConnectEcuAsync(Edc);
        var tasks = Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            var rsp = await rig.Elm.RequestAsync(new byte[] { 0x22, (byte)i, 0x01 });
            Assert.Equal(new byte[] { 0x62, (byte)i, 0x01, (byte)i }, rsp);
        }));
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Request_without_ecu_selected_throws()
    {
        await using var rig = await TestRig.CreateAsync(b => b.Add(new FakeCanEcu("EDC", 0x7E0, 0x7E8, Script())));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await rig.Elm.RequestAsync(Hex.Parse("22F186")));
    }

    [Fact]
    public async Task Switching_between_ecus_reprograms_addressing()
    {
        var a = new EcuScript().On("22F186", "62F186AA");
        var b2 = new EcuScript().On("22F186", "62F186BB");
        await using var rig = await TestRig.CreateAsync(b => { b.Add(new FakeCanEcu("A", 0x7E0, 0x7E8, a)); b.Add(new FakeCanEcu("B", 0x7E1, 0x7E9, b2)); });
        await rig.Elm.ConnectEcuAsync(EcuAddress.Can("A", "7E0", "7E8"));
        Assert.Equal(Hex.Parse("62F186AA"), await rig.Elm.RequestAsync(Hex.Parse("22F186")));
        await rig.Elm.ConnectEcuAsync(EcuAddress.Can("B", "7E1", "7E9"));
        Assert.Equal(Hex.Parse("62F186BB"), await rig.Elm.RequestAsync(Hex.Parse("22F186")));
        // re-selecting the same ECU is free
        int before = rig.Device.Commands.Count;
        await rig.Elm.ConnectEcuAsync(EcuAddress.Can("B", "7E1", "7E9"));
        Assert.Equal(before, rig.Device.Commands.Count);
    }
}
