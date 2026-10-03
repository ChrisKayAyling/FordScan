using FordDiag.Comms;
using FordDiag.Comms.Elm;
using FordDiag.Core;
using FordDiag.Core.Coding;
using FordDiag.Core.Simulation;

namespace FordDiag.Core.Tests;

public class ChecksumTests
{
    // Real lines from a Ford service-site "Module Build Data (As-Built)" printout, plus the FORScan forum worked example.
    [Theory]
    [InlineData("720-01-01 044A 3464 202F")]
    [InlineData("720-01-01 044B 3464 2030")]
    [InlineData("724-01-01 1892 0799 77")]
    [InlineData("724-02-01 C821 4800 5F")]
    [InlineData("724-03-01 0506 0006 40")]
    [InlineData("724-04-01 0032 0320 85")]
    [InlineData("724-06-01 003C 003C AA")]
    [InlineData("724-07-01 003C FFFF 6D")]
    [InlineData("724-08-01 FFFF FFFF 30")]
    [InlineData("7D0-01-01 0800 4000 0021")]
    [InlineData("7D0-02-01 0355 5300 0085")]
    [InlineData("7D0-03-01 0000 0000 00DB")]
    public void RealLinesVerify(string line)
    {
        var d = AsBuiltData.Parse(line, out var errors);
        Assert.Empty(errors);
        Assert.Empty(d.VerifyChecksums());
    }

    [Fact]
    public void WrongChecksumIsReportedAndFixed()
    {
        var d = AsBuiltData.Parse("720-01-01 044B 3464 202F", out _);   // data changed, checksum stale
        var bad = Assert.Single(d.VerifyChecksums());
        Assert.Equal((byte)0x30, bad.Expected);
        Assert.Equal(1, d.FixChecksums());
        Assert.Equal("720-01-01 04 4B 34 64 20 30", d.Format().Trim());
        Assert.Empty(d.VerifyChecksums());
    }

    [Fact]
    public void ParsesForscanGFormat()
    {
        var d = AsBuiltData.Parse("; comment\n7D0G1G10800400000" + "21\n", out var errors);
        Assert.Empty(errors);
        Assert.Contains(d.Lines, l => l.Key.ToString() == "7D0-01-01");
        Assert.Empty(d.VerifyChecksums());
    }
}

public class FieldCodecTests
{
    [Fact]
    public void ReadsAndWritesBitFieldsWithoutTouchingNeighbours()
    {
        var f = new CodingField { Name = "Rear Camera", Byte = 0, Bit = 5, Size = 2, Kind = FieldKind.Enum };
        var block = new byte[] { 0b1001_0101, 0xFF };
        Assert.Equal(0b00, f.ReadRaw(block));
        f.WriteRaw(block, 0b11);
        Assert.Equal(0b1111_0101, block[0]);
        Assert.Equal(3, f.ReadRaw(block));
        f.WriteRaw(block, 0);
        Assert.Equal(0b1001_0101, block[0]);
        Assert.Equal(0xFF, block[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => f.WriteRaw(block, 4));
    }

    [Fact]
    public void SixteenBitValuesAreBigEndianAndScaled()
    {
        var f = new CodingField { Name = "Wheel Base", Byte = 2, Bit = 0, Size = 16, Kind = FieldKind.Value, Multiplier = 0.5, Unit = "mm" };
        var block = new byte[] { 0, 0, 0x0B, 0xB8, 0 };
        Assert.Equal(3000, f.ReadRaw(block));
        Assert.Equal(1500, f.ToValue(3000));
        f.WriteRaw(block, f.FromValue(1600));
        Assert.Equal(new byte[] { 0, 0, 0x0C, 0x80, 0 }, block);
        Assert.Equal("1600 mm", f.Describe(3200));
    }

    [Fact]
    public void FitsChecksBounds()
    {
        Assert.False(new CodingField { Name = "x", Byte = 9, Bit = 0, Size = 16, Kind = FieldKind.Value }.Fits(10));
        Assert.True(new CodingField { Name = "x", Byte = 8, Bit = 0, Size = 16, Kind = FieldKind.Value }.Fits(10));
    }
}

public class DefinitionTests
{
    [Fact]
    public void BuiltInApimDefinitionsLoad()
    {
        var lib = DefinitionLibrary.Load();
        var s3 = lib.Single(d => d.Id == "apim-sync3");
        Assert.Equal(0x7D0u, s3.Module);
        Assert.True(s3.FieldCount > 250);
        Assert.Equal("GPL-3.0", s3.License);
        var cam = s3.Block(1)!.Fields.Single(f => f.Name == "Rear Camera");
        Assert.Equal((0, 1, 2), (cam.Byte, cam.Bit, cam.Size));   // upstream counts bits from the MSB: bit 5 of 0x80..0x01 -> LSB offset 1
        Assert.Equal("RVC Present", cam.Options[1]);
        Assert.Equal(0xDE00, s3.Block(1)!.Did);
        Assert.All(s3.Blocks, b => Assert.All(b.Fields, f => Assert.True(f.Fits(s3.BlockSizes![b.Block - 1]), $"{b.Block} {f.Name}")));
    }

    [Fact]
    public void FindMatchesOnBlockLengths()
    {
        var lib = DefinitionLibrary.Load();
        var sync3 = new Dictionary<int, int> { [1] = 10, [2] = 12, [3] = 5 };
        var sync4 = new Dictionary<int, int> { [1] = 20, [2] = 15 };
        Assert.Equal("apim-sync3", DefinitionLibrary.Find(lib, 0x7D0, sync3)!.Id);
        Assert.Equal("apim-sync4", DefinitionLibrary.Find(lib, 0x7D0, sync4)!.Id);
        Assert.DoesNotContain(DefinitionLibrary.Candidates(lib, 0x7D0, new Dictionary<int, int> { [1] = 7 }), d => d.Id is "apim-sync3" or "apim-sync4");
        Assert.Null(DefinitionLibrary.Find(lib, 0x7FF, sync3));
    }

    [Fact]
    public void UserDefinitionsOverrideAndBadFilesAreReported()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fd-defs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bcm.json"), """
            { "id": "my-bcm", "name": "My BCM", "module": "726", "blockSizes": [5],
              "blocks": [ { "block": 1, "fields": [ { "name": "DRL", "byte": 0, "bit": 0, "size": 1, "options": { "0": "Off", "1": "On" } } ] } ] }
            """);
        File.WriteAllText(Path.Combine(dir, "broken.json"), "{ nope");
        var warnings = new List<string>();
        var lib = DefinitionLibrary.Load(dir, warnings.Add);
        Assert.Contains(lib, d => d.Id == "my-bcm" && d.FieldCount == 1);
        Assert.Single(warnings);
    }
}

public class ImageTests
{
    private static byte[] Bytes(string hex) => Hex.Parse(hex);

    [Fact]
    public void LinesRoundTripThroughBlocks()
    {
        var img = new AsBuiltImage { Module = 0x7D0 };
        img.Blocks[1] = Bytes("30080000000000000000");   // 10 bytes -> 2 lines
        img.Blocks[2] = Bytes("0102030405060708090A0B0C"); // 12 bytes -> 3 lines (5+5+2)
        var text = img.ToText();
        var lines = AsBuiltData.Parse(text, out var errors);
        Assert.Empty(errors);
        Assert.Empty(lines.VerifyChecksums());
        Assert.Equal(5, lines.Lines.Count);
        var back = AsBuiltImage.FromLines(lines, 0x7D0, out var warnings);
        Assert.Empty(warnings);
        Assert.Equal(img.Blocks[1], back.Blocks[1]);
        Assert.Equal(img.Blocks[2], back.Blocks[2]);
    }

    [Fact]
    public void ReportsStaleChecksumsAndMissingLines()
    {
        var data = AsBuiltData.Parse("726-01-01 0840 8200 12\n726-01-03 0000 0000 00\n", out _);
        AsBuiltImage.FromLines(data, 0x726, out var warnings);
        Assert.Contains(warnings, w => w.Contains("726-01-01") && w.Contains("checksum"));
        Assert.Contains(warnings, w => w.Contains("726-01-03") && w.Contains("missing"));
    }

    [Fact]
    public void ReadsUcdsAndFordXml()
    {
        var ucds = AsBuiltImage.FromUcdsXml("<UCDS><VEHICLE><DATA ID=\"DE00\">0840820012</DATA><DATA ID=\"DE01\">AA BB</DATA></VEHICLE></UCDS>", 0x726);
        Assert.Equal(Bytes("0840820012"), ucds.Blocks[1]);
        Assert.Equal(Bytes("AABB"), ucds.Blocks[2]);

        var ab = AsBuiltImage.LinesFromFordXml("<AB><BCE_MODULE><BCM LABEL=\"726-01-01\"><CODE>0840</CODE><CODE>8200</CODE><CODE>0012</CODE></BCM></BCE_MODULE></AB>");
        Assert.Equal(Bytes("084082000012"), ab.Lines.Single().Value);
    }

    [Fact]
    public void CompareNamesChangedFieldsAndFlagsUnknownBytes()
    {
        var def = DefinitionLibrary.Load().Single(d => d.Id == "apim-sync3");
        var a = new AsBuiltImage { Module = 0x7D0 }; a.Blocks[1] = new byte[10];
        var b = a.Clone(); b.Blocks[1][0] = 0b0000_0010;     // Rear Camera = 1
        b.Blocks[1][9] = 0x01;                               // byte with no field
        var changes = a.Compare(b, def);
        Assert.Contains(changes, c => c.Field?.Name == "Rear Camera" && c.NewRaw == 1 && c.Describe().Contains("RVC Present"));
        Assert.True(changes.Count >= 1);
        Assert.Empty(a.Compare(a.Clone(), def));
    }
}

public class LiveCodingTests : IAsyncLifetime
{
    private FordSimulator _sim = null!;
    private ElmTransport _elm = null!;
    public async Task InitializeAsync()
    {
        _sim = FordSimulator.Create();
        _elm = await ElmTransport.ConnectAsync(_sim.Host, new ElmOptions { Profile = ElmProfiles.Elm327Clone });
    }
    public async Task DisposeAsync() { await _elm.DisposeAsync(); await _sim.DisposeAsync(); }

    private ModuleSession Apim => new(_elm, FordModules.Find("APIM")!, FordBus.MsCan);

    [Fact]
    public async Task ReadsAllBlocksAndMatchesSync3Definition()
    {
        var img = await ModuleCoding.ReadAsync(Apim);
        Assert.Equal(9, img.Blocks.Count);
        Assert.Equal(10, img.Blocks[1].Length);
        var def = DefinitionLibrary.Find(DefinitionLibrary.Load(), 0x7D0, img.BlockLengths);
        Assert.Equal("apim-sync3", def!.Id);
        var field = def.Block(1)!.Fields.Single(f => f.Name == "PDC HMI");
        Assert.Equal(1, field.ReadRaw(img.Blocks[1]));       // 0x0A: PDC HMI (0x08) and Rear Camera = 1 (0x02)
    }

    [Fact]
    public async Task WritesOnlyChangedBlocksWithBackupAndVerify()
    {
        var original = await ModuleCoding.ReadAsync(Apim);
        var edited = original.Clone();
        var def = DefinitionLibrary.Load().Single(d => d.Id == "apim-sync3");
        def.Block(1)!.Fields.Single(f => f.Name == "Rear Camera").WriteRaw(edited.Blocks[1], 0);
        var dir = Path.Combine(Path.GetTempPath(), "fd-code-" + Guid.NewGuid().ToString("N"));
        var results = await ModuleCoding.WriteChangedAsync(_elm, Apim, original, edited, new WriteOptions { Commit = true, Backups = new BackupStore(dir) });
        var r = Assert.Single(results);
        Assert.Equal(1, r.Block);
        Assert.Equal("Written and verified.", r.Result!.Message);
        Assert.Equal(edited.Blocks[1], _sim.Modules["APIM"].Dids[0xDE00]);
        Assert.Equal(0x08, edited.Blocks[1][0]);
        Assert.Single(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task StopsAfterFirstFailedBlock()
    {
        var original = await ModuleCoding.ReadAsync(Apim);
        var edited = original.Clone();
        edited.Blocks[1][0] ^= 1; edited.Blocks[2][0] ^= 1;
        edited.Blocks[2] = edited.Blocks[2][..^1];            // wrong length: refused before anything is sent for block 2
        var results = await ModuleCoding.WriteChangedAsync(_elm, Apim, original, edited, new WriteOptions { Commit = true, Backups = new BackupStore(Path.GetTempPath() + "/fd-x" + Guid.NewGuid().ToString("N")) });
        Assert.Equal(2, results.Count);
        Assert.Null(results[0].Error);
        Assert.NotNull(results[1].Error);
    }
}


public class ImportedDefinitionTests
{
    private static readonly IReadOnlyList<CodingDefinition> Lib = DefinitionLibrary.Load();

    [Fact]
    public void ImportedLibraryIsLarge()
    {
        var cyan = Lib.Where(d => d.Id.StartsWith("cyan-")).ToList();
        Assert.True(cyan.Count >= 50);
        Assert.True(cyan.Sum(d => d.FieldCount) > 4000);
        Assert.All(cyan, d => Assert.Contains("cyanlabs.net", d.Source));
        Assert.All(cyan, d => Assert.Contains("CyanLabs", d.Attribution));
        Assert.Equal(Lib.Count, Lib.Select(d => d.Id).Distinct().Count());
    }

    [Fact]
    public void EveryFieldFitsInsideItsBlock()
    {
        foreach (var d in Lib)
            foreach (var b in d.Blocks)
            {
                int size = d.BlockSizes is { } s && b.Block <= s.Count ? s[b.Block - 1] : b.MinSize;
                foreach (var f in b.Fields) Assert.True(f.Fits(size), $"{d.Id} block {b.Block} {f.Name} byte {f.Byte} bit {f.Bit} size {f.Size} > {size}");
            }
    }

    [Fact]
    public void BcmFuelPrimeIsTheLowNibbleOfFirstByte()
    {
        var bcm = Lib.Single(d => d.Id == "cyan-bcm-database-2009-16-cgea-1-2");
        var f = bcm.Block(1)!.Fields.Single(x => x.Name.StartsWith("Fuel Prime"));
        Assert.Equal((0, 0, 4), (f.Byte, f.Bit, f.Size));   // 726-01-01 x*xx-xxxx: second nibble of the first byte
        Assert.Equal("No Prime", f.Options[0]);
        Assert.Equal("Prime", f.Options[1]);
        Assert.Equal("726-01-01", f.Loc);
        var wake = bcm.Block(1)!.Fields.Single(x => x.Name.StartsWith("PCM Wake"));
        Assert.Equal((1, 0, 4), (wake.Byte, wake.Bit, wake.Size)); // xxx*-xxxx: low nibble of byte 1
        var block = new byte[] { 0xA0, 0x50, 0x00, 0x00 };
        f.WriteRaw(block, 1); wake.WriteRaw(block, 1);
        Assert.Equal(new byte[] { 0xA1, 0x51, 0, 0 }, block);
    }

    [Fact]
    public void LineWidthsFollowTheMasks()
    {
        // BCM 726-01-01 is a 3 byte line (mask xxxx-xxxx = 3 data bytes + checksum), so saved files must not use 5 byte lines
        var bcm = Lib.Single(d => d.Id == "cyan-bcm-database-2009-16-cgea-1-2");
        Assert.Equal(3, bcm.Block(1)!.LineBytes![0]);
        var img = new AsBuiltImage { Module = 0x726 };
        img.Blocks[1] = new byte[] { 1, 2, 3, 4, 5, 6 };
        img.ApplyLineWidths(bcm);
        var lines = img.ToLines().Lines;
        Assert.True(lines.Count >= 2);
        Assert.Equal(4, lines[new AsBuiltKey(0x726, 1, 1)].Length);   // 3 data + checksum
        Assert.Empty(img.ToLines().VerifyChecksums());
        // round trip through text keeps the bytes
        var back = AsBuiltImage.FromLines(AsBuiltData.Parse(img.ToText(), out _), 0x726, out _);
        Assert.Equal(img.Blocks[1], back.Blocks[1]);
    }

    [Fact]
    public void PartNumberChoosesTheBcmGeneration()
    {
        var lengths = Enumerable.Range(1, 8).ToDictionary(i => i, _ => 64);
        var c1 = DefinitionLibrary.Candidates(Lib, 0x726, lengths, new[] { "JU5T-14B476-DA" });
        Assert.Equal("cyan-bcm-database-2018-2019-cgea-1-3", c1[0].Id);
        var c2 = DefinitionLibrary.Candidates(Lib, 0x726, lengths, new[] { "BC3T-14B476-AB" });
        Assert.Equal("cyan-bcm-database-2009-16-cgea-1-2", c2[0].Id);
        Assert.True(c1.Count > 1);
        Assert.Equal("BC3T", DefinitionLibrary.PartPrefix("BC3T-14B476-DA"));
    }

    [Fact]
    public void FieldsMayStraddleBytes()
    {
        // two nibbles that cross a byte boundary: low nibble of byte 0 + high nibble of byte 1
        var f = new CodingField { Name = "x", Byte = 0, Bit = 4, Size = 8, Kind = FieldKind.Value };
        var block = new byte[] { 0xA5, 0xC3 };
        Assert.Equal(0x5C, f.ReadRaw(block));
        f.WriteRaw(block, 0x12);
        Assert.Equal(new byte[] { 0xA1, 0x23 }, block);
    }

    [Fact]
    public void ValueFieldsCarryUnitsAndScale()
    {
        var all = Lib.Where(d => d.Id.StartsWith("cyan-")).SelectMany(d => d.Blocks).SelectMany(b => b.Fields).ToList();
        Assert.Contains(all, f => f.Kind == FieldKind.Value && !string.IsNullOrEmpty(f.Unit));
        Assert.Contains(all, f => f.Kind == FieldKind.Enum && f.Options.Count > 2);
    }
}
