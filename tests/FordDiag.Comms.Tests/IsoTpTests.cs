using FordDiag.Comms;

namespace FordDiag.Comms.Tests;

public class IsoTpTests
{
    [Theory]
    [InlineData(1, null)]
    [InlineData(7, null)]
    [InlineData(8, null)]
    [InlineData(6, (byte)0x4D)]
    [InlineData(7, (byte)0x4D)]
    [InlineData(100, null)]
    [InlineData(4095, null)]
    [InlineData(500, (byte)0x01)]
    public void Segment_and_reassemble_roundtrip(int length, byte? ext)
    {
        var payload = Enumerable.Range(0, length).Select(i => (byte)(i + 1)).ToArray();
        var frames = IsoTp.Segment(payload, ext);
        Assert.All(frames, f => Assert.True(f.Length <= 8));
        var rx = new IsoTpReassembler(ext.HasValue ? 1 : 0);
        foreach (var f in frames) rx.Feed(f);
        Assert.True(rx.IsComplete);
        Assert.Equal(payload, rx.Payload);
    }

    [Fact]
    public void Single_frame_layout()
    {
        var f = IsoTp.Segment(new byte[] { 0x22, 0xF1, 0x90 }).Single();
        Assert.Equal(new byte[] { 0x03, 0x22, 0xF1, 0x90 }, f);
        Assert.Equal(new byte[] { 0x03, 0x22, 0xF1, 0x90, 0, 0, 0, 0 }, IsoTp.Segment(new byte[] { 0x22, 0xF1, 0x90 }, null, 0x00).Single());
    }

    [Fact]
    public void First_and_consecutive_frame_layout()
    {
        var payload = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
        var frames = IsoTp.Segment(payload);
        Assert.Equal(new byte[] { 0x10, 0x14, 1, 2, 3, 4, 5, 6 }, frames[0]);
        Assert.Equal(new byte[] { 0x21, 7, 8, 9, 10, 11, 12, 13 }, frames[1]);
        Assert.Equal(new byte[] { 0x22, 14, 15, 16, 17, 18, 19, 20 }, frames[2]);
        Assert.Equal(3, frames.Count);
    }

    [Fact]
    public void Sequence_numbers_wrap_after_15()
    {
        var payload = new byte[6 + 7 * 20];
        var frames = IsoTp.Segment(payload);
        Assert.Equal(0x20, frames[16][0]);   // SN 16 -> 0
        Assert.Equal(0x2F, frames[15][0]);
    }

    [Fact]
    public void Reassembler_detects_sequence_errors()
    {
        var frames = IsoTp.Segment(new byte[30]);
        var rx = new IsoTpReassembler();
        rx.Feed(frames[0]);
        Assert.Throws<ProtocolException>(() => rx.Feed(frames[2]));
    }

    [Fact]
    public void Reassembler_rejects_stray_consecutive_and_flow_control()
    {
        var rx = new IsoTpReassembler();
        Assert.Throws<ProtocolException>(() => rx.Feed(new byte[] { 0x21, 1, 2 }));
        Assert.Throws<ProtocolException>(() => rx.Feed(new byte[] { 0x30, 0, 0 }));
        Assert.Throws<ProtocolException>(() => rx.Feed(new byte[] { 0x00 }));       // SF with length 0
        Assert.Throws<ProtocolException>(() => rx.Feed(new byte[] { 0x07, 1, 2 })); // SF longer than the frame
        Assert.Throws<ProtocolException>(() => IsoTp.TypeOf(new byte[] { 0x45 }));
    }

    [Fact]
    public void Reassembler_reports_remaining_frames()
    {
        var frames = IsoTp.Segment(new byte[50]);
        var rx = new IsoTpReassembler();
        rx.Feed(frames[0]);
        Assert.True(rx.InProgress);
        Assert.Equal(frames.Count - 1, rx.FramesRemaining);
        rx.Feed(frames[1]);
        Assert.Equal(frames.Count - 2, rx.FramesRemaining);
    }

    [Fact]
    public void Flow_control_parse_and_stmin_encoding()
    {
        var fc = IsoTp.ParseFlowControl(new byte[] { 0x30, 0x08, 0x14 });
        Assert.True(fc.ClearToSend);
        Assert.Equal(8, fc.BlockSize);
        Assert.Equal(TimeSpan.FromMilliseconds(20), fc.SeparationTime);
        Assert.Equal(TimeSpan.FromTicks(300 * 10), FlowControl.DecodeStMin(0xF3));
        Assert.Equal(TimeSpan.FromMilliseconds(127), FlowControl.DecodeStMin(0x80));
        Assert.Equal((byte)5, FlowControl.EncodeStMin(TimeSpan.FromMilliseconds(5)));
        Assert.Equal((byte)0xF2, FlowControl.EncodeStMin(TimeSpan.FromMicroseconds(200)));
        Assert.True(IsoTp.ParseFlowControl(new byte[] { 0x31, 0, 0 }).Wait);
        Assert.True(IsoTp.ParseFlowControl(new byte[] { 0x32, 0, 0 }).Overflow);
        Assert.Equal(new byte[] { 0x4D, 0x30, 0x02, 0x00 }, IsoTp.BuildFlowControl(0, 2, 0, 0x4D));
    }

    [Fact]
    public void Pending_detection()
    {
        Assert.True(IsoTp.IsPending(new byte[] { 0x7F, 0x31, 0x78 }));
        Assert.True(IsoTp.IsPending(new byte[] { 0x7F, 0x31, 0x78 }, 0x31));
        Assert.False(IsoTp.IsPending(new byte[] { 0x7F, 0x31, 0x78 }, 0x22));
        Assert.False(IsoTp.IsPending(new byte[] { 0x7F, 0x31, 0x33 }));
    }

    [Fact]
    public void Oversized_payload_is_rejected() => Assert.Throws<ProtocolException>(() => IsoTp.Segment(new byte[5000]));
}
