using FordDiag.Comms;

namespace FordDiag.Core.Coding;

/// <summary>Reads and writes a module's configuration blocks over UDS (service 0x22 / 0x2E on DE00 + N - 1).</summary>
public static class ModuleCoding
{
    public const int MaxBlocks = 32;

    /// <summary>Reads DE00, DE01, ... until the module reports the identifier as unsupported.</summary>
    public static async Task<AsBuiltImage> ReadAsync(ModuleSession session, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var img = new AsBuiltImage { Module = session.Module.RequestId };
        for (int block = 1; block <= MaxBlocks; block++)
        {
            ct.ThrowIfCancellationRequested();
            var did = BlockDids.ForBlock(block);
            progress?.Report($"Reading block {block} (DID {did:X4})…");
            try { img.Blocks[block] = await session.ReadDidAsync(did, ct).ConfigureAwait(false); }
            catch (NegativeResponseException) when (img.Blocks.Count > 0) { break; }       // end of the block list
            catch (EcuTimeoutException) when (img.Blocks.Count > 0) { break; }
        }
        if (img.Blocks.Count == 0) throw new CommsException("The module returned no configuration blocks (DID DE00 not supported).");
        return img;
    }

    public sealed record BlockWrite(int Block, WriteResult? Result, string? Error);

    /// <summary>Writes only the blocks that differ between <paramref name="original"/> and <paramref name="edited"/>, each backed up and verified.</summary>
    public static async Task<IReadOnlyList<BlockWrite>> WriteChangedAsync(IAddressableTransport t, ModuleSession session,
        AsBuiltImage original, AsBuiltImage edited, WriteOptions options, CancellationToken ct = default)
    {
        var results = new List<BlockWrite>();
        foreach (var (block, data) in edited.Blocks)
        {
            if (!original.Blocks.TryGetValue(block, out var before) || before.AsSpan().SequenceEqual(data)) continue;
            try { results.Add(new BlockWrite(block, await DidWriter.WriteAsync(t, session, BlockDids.ForBlock(block), data, options, ct).ConfigureAwait(false), null)); }
            catch (Exception ex) when (ex is WriteRefusedException or CommsException)
            {
                results.Add(new BlockWrite(block, null, ex.Message));
                break; // do not continue with other blocks after a failure: the module may be half configured
            }
        }
        return results;
    }
}
