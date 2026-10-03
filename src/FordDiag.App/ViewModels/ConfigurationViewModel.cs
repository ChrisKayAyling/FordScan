using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FordDiag.Comms;
using FordDiag.App.Services;
using FordDiag.Core;
using FordDiag.Core.Coding;

namespace FordDiag.App.ViewModels;

public sealed record OptionItem(long Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One editable option. The block array is shared with the working image, so edits are visible there immediately.</summary>
public sealed partial class ConfigFieldViewModel : ObservableObject
{
    private readonly byte[] _block;
    private readonly Action _changed;
    private readonly long _originalRaw;
    private bool _updating;

    public ConfigFieldViewModel(CodingField field, int block, byte[] workingBlock, byte[] originalBlock, Action changed)
    {
        Field = field; Block = block; _block = workingBlock; _changed = changed;
        _originalRaw = field.ReadRaw(originalBlock);
        if (field.Kind == FieldKind.Enum)
        {
            Options = field.Options.OrderBy(o => o.Key).Select(o => new OptionItem(o.Key, o.Value)).ToList();
            // a value the definition does not name stays selectable so that opening a file never alters it
            if (!field.Options.ContainsKey(Raw)) Options = Options.Append(new OptionItem(Raw, field.Describe(Raw))).ToList();
        }
    }

    public CodingField Field { get; }
    public int Block { get; }
    public string Name => Field.Name;
    public IReadOnlyList<OptionItem> Options { get; } = Array.Empty<OptionItem>();
    public bool IsEnum => Field.Kind == FieldKind.Enum;
    public bool IsValue => Field.Kind == FieldKind.Value;
    public bool IsText => Field.Kind is FieldKind.Ascii or FieldKind.Hex;
    public string Unit => Field.Unit ?? "";
    public string? Note => string.IsNullOrWhiteSpace(Field.Note) ? null : Field.Note;
    public bool HasNote => Note is not null;
    public decimal Minimum => (decimal)(Field.Min ?? Field.ToValue(0));
    public decimal Maximum => (decimal)(Field.Max ?? Field.ToValue(Math.Min(Field.MaxRaw, 1L << 30)));
    public decimal Increment => (decimal)Math.Max(Field.Multiplier, 0.001);
    public string Location => (Field.Loc is { Length: > 0 } l ? l + " · " : "") + $"byte {Field.Byte}" +
        (Field.Size < 8 && Field.ByteSpan == 1 ? $" bit {Field.Bit}" + (Field.Size > 1 ? $"–{Field.Bit + Field.Size - 1}" : "") : Field.ByteSpan > 1 ? $"–{Field.Byte + Field.ByteSpan - 1}" : "");

    public long Raw => Field.ReadRaw(_block);
    public bool IsChanged => Raw != _originalRaw;
    public string RawText => Field.Size <= 4 ? $"{Raw:X}" : $"{Raw:X}".PadLeft(Math.Max(2, Field.ByteSpan * 2), '0');
    public string Original => Field.Describe(_originalRaw);

    public OptionItem? SelectedOption
    {
        get => Options.FirstOrDefault(o => o.Value == Raw);
        set { if (value is not null && !_updating) Set(value.Value); }
    }

    public decimal? NumberValue
    {
        get => (decimal)Field.ToValue(Raw);
        set
        {
            if (value is null || _updating) return;
            var v = Math.Clamp(value.Value, Minimum, Maximum);
            Set(Math.Clamp(Field.FromValue((double)v), 0, Field.MaxRaw));
        }
    }

    public string TextValue
    {
        get => Field.Kind == FieldKind.Ascii ? (Raw is >= 0x20 and < 0x7F ? ((char)Raw).ToString() : "") : Raw.ToString("X2", CultureInfo.InvariantCulture);
        set
        {
            if (_updating) return;
            if (Field.Kind == FieldKind.Ascii) { if (value.Length == 1 && value[0] < 0x7F) Set(value[0]); }
            else if (long.TryParse(value.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n) && n <= Field.MaxRaw) Set(n);
        }
    }

    private void Set(long raw)
    {
        if (raw == Raw) return;
        Field.WriteRaw(_block, raw);
        Refresh();
        _changed();
    }

    public void Refresh()
    {
        _updating = true;
        OnPropertyChanged(nameof(Raw)); OnPropertyChanged(nameof(RawText)); OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(SelectedOption)); OnPropertyChanged(nameof(NumberValue)); OnPropertyChanged(nameof(TextValue));
        _updating = false;
    }

    public void Revert() => Set(_originalRaw);
}

public sealed record DefinitionChoice(string Label, CodingDefinition? Definition)
{
    public override string ToString() => Label;
}

/// <summary>A row of the configuration list: a block heading or an option.</summary>
public sealed class ConfigRow
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public ConfigFieldViewModel? Field { get; init; }
    public bool IsHeader => Field is null;
    public bool IsField => Field is not null;
}

/// <summary>
/// Friendly module configuration: read the blocks from the module (or open a file), edit named options, review the
/// differences and write only the blocks that changed.
/// </summary>
public sealed partial class ConfigurationViewModel : ObservableObject
{
    private readonly AppState _s;
    private readonly Func<ModuleInfo?> _module;
    private IReadOnlyList<CodingDefinition>? _library;
    private AsBuiltImage? _original, _working;
    private readonly List<ConfigFieldViewModel> _all = new();
    private bool _suppressFileModule;
    private IReadOnlyDictionary<uint, AsBuiltImage> _fileImages = new Dictionary<uint, AsBuiltImage>();

    public ConfigurationViewModel(AppState state, Func<ModuleInfo?> selectedModule)
    {
        _s = state; _module = selectedModule;
        state.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AppState.IsExpertMode) or nameof(AppState.State)) Notify(); };
    }

    public ObservableCollection<ConfigRow> Rows { get; } = new();
    public ObservableCollection<string> Differences { get; } = new();
    public ObservableCollection<string> FileModules { get; } = new();
    public ObservableCollection<DefinitionChoice> Choices { get; } = new();
    private bool _suppressChoice;
    [ObservableProperty] private DefinitionChoice? _selectedChoice;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _sourceText = "Read the configuration from the connected module, or open an as-built file.";
    [ObservableProperty] private string _definitionText = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _selectedFileModule;
    [ObservableProperty] private int _changeCount;

    public bool HasData => _working is not null;
    public bool HasDefinition { get; private set; }
    public bool ShowRawNotice => HasData && !HasDefinition;
    public bool HasChoices => Choices.Count > 0;
    public bool ShowEmpty => !HasData;
    public bool HasChanges => ChangeCount > 0;
    public bool ExpertRequired => !_s.IsExpertMode;
    public bool ShowFileModules => FileModules.Count > 1;
    public bool HasDifferences => Differences.Count > 0;
    public string ChangeSummary => ChangeCount == 0 ? "No changes" : $"{ChangeCount} option(s) changed";
    public CodingDefinition? Definition { get; private set; }
    public string Attribution => Definition?.Attribution ?? "";
    public bool ShowAttribution => !string.IsNullOrEmpty(Attribution);
    public bool ShowApproximate => Definition is { IsExactLayout: false } && Definition.Blocks.Any(b => b.LayoutGuessed);
    public bool ShowChoices => Choices.Count > 1;

    partial void OnSelectedChoiceChanged(DefinitionChoice? value)
    {
        if (value is null || _suppressChoice || _working is null) return;
        ApplyDefinition(value.Definition);
    }

    private IReadOnlyList<CodingDefinition> Library =>
        _library ??= DefinitionLibrary.Load(Path.Combine(AppSettings.ConfigDir, "definitions"), w => _s.Error("Definition file skipped: " + w));

    partial void OnSearchChanged(string value) => BuildRows();
    partial void OnChangeCountChanged(int value) { OnPropertyChanged(nameof(HasChanges)); OnPropertyChanged(nameof(ChangeSummary)); ApplyCommand.NotifyCanExecuteChanged(); }
    partial void OnIsBusyChanged(bool value) { ReadCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); }

    partial void OnSelectedFileModuleChanged(string? value)
    {
        if (value is null || _suppressFileModule) return;
        var addr = value.Split(' ')[0];
        if (FordModules.TryParseAddress(addr, out var m) && _fileImages.TryGetValue(m, out var img)) Load(img.Clone(), $"File · module {addr}");
    }

    public void Notify()
    {
        OnPropertyChanged(nameof(ExpertRequired));
        ReadCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged();
    }

    // ---- loading ----
    private void Load(AsBuiltImage image, string source, bool fromModule = false)
    {
        _original = image.Clone(); _working = image;
        var parts = fromModule
            ? _module()?.Identification.Where(i => i.Did is 0xF188 or 0xF113 or 0xF124 or 0xF187).Select(i => i.Value).ToList()
            : null;
        var found = DefinitionLibrary.Candidates(Library, image.Module, image.BlockLengths, parts);
        _suppressChoice = true;
        Choices.Clear();
        foreach (var d in found) Choices.Add(new DefinitionChoice(d.Name + (d.IsExactLayout ? "" : "  (approximate layout)"), d));
        Choices.Add(new DefinitionChoice("None (raw bytes)", null));
        SelectedChoice = Choices[0].Definition is null ? Choices[0] : Choices[0];
        _suppressChoice = false;
        OnPropertyChanged(nameof(ShowChoices));
        SourceText = $"{source} · {image.Blocks.Count} block(s), {image.Blocks.Sum(b => b.Value.Length)} bytes";
        Differences.Clear(); OnPropertyChanged(nameof(HasDifferences));
        ApplyDefinition(Choices[0].Definition);
    }

    /// <summary>Rebuilds the option list for a definition without touching the working data or the baseline.</summary>
    private void ApplyDefinition(CodingDefinition? def)
    {
        if (_working is null || _original is null) return;
        Definition = def;
        HasDefinition = def is not null;
        _working.ApplyLineWidths(def);
        DefinitionText = def is null
            ? "No option names are known for this module and layout, so the raw bytes are shown. You can add your own definitions (see Settings)."
            : $"{def.Name} · {def.FieldCount} options · {def.License ?? "licence unknown"}" + (def.PartNumberPrefixes.Count > 0 ? $" · for {string.Join(", ", def.PartNumberPrefixes.Take(5))}" : "");
        _all.Clear();
        foreach (var (block, bytes) in _working.Blocks)
        {
            var fields = def?.Block(block)?.Fields.Where(f => f.Fits(bytes.Length)).ToList()
                         ?? (def is null ? Enumerable.Range(0, bytes.Length).Select(i => new CodingField { Name = $"Byte {i}", Byte = i, Bit = 0, Size = 8, Kind = FieldKind.Hex }).ToList() : new List<CodingField>());
            foreach (var f in fields) _all.Add(new ConfigFieldViewModel(f, block, bytes, _original.Blocks.GetValueOrDefault(block, bytes), UpdateChanges));
        }
        OnPropertyChanged(nameof(HasData)); OnPropertyChanged(nameof(HasDefinition)); OnPropertyChanged(nameof(ShowRawNotice)); OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(Attribution)); OnPropertyChanged(nameof(ShowAttribution)); OnPropertyChanged(nameof(ShowApproximate));
        BuildRows(); UpdateChanges(); Notify();
    }

    private void BuildRows()
    {
        Rows.Clear();
        if (_working is null) return;
        var q = Search.Trim();
        foreach (var grp in _all.GroupBy(f => f.Block))
        {
            var fields = grp.Where(f => q.Length == 0 || f.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                        || f.Options.Any(o => o.Label.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
            if (fields.Count == 0) continue;
            var bytes = _working.Blocks[grp.Key];
            Rows.Add(new ConfigRow { Title = $"Block {grp.Key}  ·  DID {BlockDids.ForBlock(grp.Key):X4}  ·  {bytes.Length} bytes", Subtitle = Hex.ToString(bytes, true) });
            foreach (var f in fields) Rows.Add(new ConfigRow { Field = f });
        }
    }

    private void UpdateChanges()
    {
        ChangeCount = _all.Count(f => f.IsChanged);
    }

    // ---- commands ----
    private bool CanRead() => _s.IsConnected && _module() is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task ReadAsync()
    {
        var m = _module()!;
        IsBusy = true; Message = "";
        try
        {
            var img = await _s.WithBusAsync(_ => ModuleCoding.ReadAsync(m.Session, new Progress<string>(p => Message = p)));
            FileModules.Clear(); OnPropertyChanged(nameof(ShowFileModules));
            Load(img, $"{m.Abbrev} {m.Address} · read from module", fromModule: true);
            Message = $"Read {img.Blocks.Count} blocks from {m.Abbrev}.";
            _s.Info(Message);
        }
        catch (Exception ex) when (ex is CommsException or InvalidOperationException) { Message = "Read failed: " + ex.Message; _s.Error(Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var path = await _s.Dialogs.PickOpenFileAsync("Open as-built file", "As-built files", "*.txt", "*.abt", "*.ab", "*.xml", "*.asbuilt");
        if (path is null) return;
        try { OpenText(await File.ReadAllTextAsync(path), path); }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException) { Message = "Could not open the file: " + ex.Message; }
    }

    /// <summary>Loads as-built text, Ford XML (.ab) or UCDS XML. Public for tests.</summary>
    public void OpenText(string text, string path)
    {
        var images = new Dictionary<uint, AsBuiltImage>();
        IReadOnlyList<string> warnings = Array.Empty<string>();
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('<'))
        {
            if (text.Contains("ID=\"DE", StringComparison.OrdinalIgnoreCase))
            {
                var mod = _module()?.Module.RequestId ?? 0x7D0;
                images[mod] = AsBuiltImage.FromUcdsXml(text, mod);
            }
            else
            {
                var lines = AsBuiltImage.LinesFromFordXml(text);
                foreach (var kv in AsBuiltImage.FromFile(lines, out warnings)) images[kv.Key] = kv.Value;
            }
        }
        else
        {
            var data = AsBuiltData.Parse(text, out var errs);
            foreach (var kv in AsBuiltImage.FromFile(data, out warnings)) images[kv.Key] = kv.Value;
            if (errs.Count > 0) warnings = warnings.Concat(errs).ToList();
        }
        if (images.Count == 0 || images.Values.All(i => i.Blocks.Count == 0)) { Message = "No as-built data found in " + Path.GetFileName(path); return; }

        _fileImages = images;
        FileModules.Clear();
        foreach (var (m, img) in images.OrderBy(k => k.Key)) FileModules.Add($"{m:X3} ({img.Blocks.Count} blocks)");
        OnPropertyChanged(nameof(ShowFileModules));
        var preferred = _module()?.Module.RequestId;
        var pick = images.ContainsKey(preferred ?? 0) ? preferred!.Value : images.Keys.Min();
        _suppressFileModule = true;
        SelectedFileModule = FileModules.First(x => x.StartsWith(pick.ToString("X3")));
        _suppressFileModule = false;
        Load(images[pick].Clone(), $"{Path.GetFileName(path)} · module {pick:X3}");
        Message = warnings.Count == 0 ? $"Opened {Path.GetFileName(path)}." : $"Opened {Path.GetFileName(path)} with {warnings.Count} warning(s): {warnings[0]}";
    }

    [RelayCommand]
    private async Task SaveFileAsync()
    {
        if (_working is null) return;
        var path = await _s.Dialogs.PickSaveFileAsync("Save as-built file", $"{_working.Module:X3}-asbuilt.txt", "As-built text", "*.txt");
        if (path is null) return;
        await File.WriteAllTextAsync(path, SaveText());
        Message = $"Saved to {path} (checksums recalculated).";
    }

    /// <summary>As-built text for the working image, checksums included. Public for tests.</summary>
    public string SaveText() => _working?.ToText() ?? "";

    [RelayCommand]
    private void RevertAll() { foreach (var f in _all) f.Revert(); BuildRows(); }

    [RelayCommand]
    private async Task CompareFileAsync()
    {
        if (_working is null) return;
        var path = await _s.Dialogs.PickOpenFileAsync("Compare with as-built file", "As-built files", "*.txt", "*.abt", "*.ab", "*.xml");
        if (path is null) return;
        try { Compare(await File.ReadAllTextAsync(path), path); }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException) { Message = "Could not open the file: " + ex.Message; }
    }

    private AsBuiltImage? _compareTarget;

    /// <summary>Lists the differences between the working configuration and another file (for finding what a feature changes). Public for tests.</summary>
    public void Compare(string text, string path)
    {
        if (_working is null) return;
        var data = AsBuiltData.Parse(text, out _);
        var images = AsBuiltImage.FromFile(data, out _);
        if (!images.TryGetValue(_working.Module, out var other)) { Message = $"{Path.GetFileName(path)} has no data for module {_working.Module:X3}."; return; }
        _compareTarget = other;
        Differences.Clear();
        foreach (var c in _working.Compare(other, Definition)) Differences.Add(c.Describe());
        OnPropertyChanged(nameof(HasDifferences));
        Message = Differences.Count == 0 ? "The file matches the current configuration." : $"{Differences.Count} difference(s) to {Path.GetFileName(path)}.";
    }

    [RelayCommand]
    private void TakeFileValues()
    {
        if (_working is null || _compareTarget is null) return;
        foreach (var (block, bytes) in _compareTarget.Blocks)
            if (_working.Blocks.TryGetValue(block, out var mine) && mine.Length == bytes.Length) bytes.CopyTo(mine, 0);
        foreach (var f in _all) f.Refresh();
        UpdateChanges(); BuildRows();
        Differences.Clear(); OnPropertyChanged(nameof(HasDifferences));
        Message = "Values from the file were copied into the editor. Nothing is written until you apply.";
    }

    private bool CanApply() => _s.IsConnected && _s.IsExpertMode && HasChanges && _module() is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var m = _module()!;
        if (_working is null) return;
        IsBusy = true;
        try
        {
            // always diff against what the module holds right now, so the confirmation shows the real effect
            var baseline = await _s.WithBusAsync(_ => ModuleCoding.ReadAsync(m.Session));
            var changes = baseline.Compare(_working, Definition);
            if (changes.Count == 0) { Message = "The module already has this configuration."; return; }
            var list = string.Join("\n", changes.Take(12).Select(c => "• " + c.Describe())) + (changes.Count > 12 ? $"\n… and {changes.Count - 12} more" : "");
            if (!await _s.Dialogs.ConfirmAsync($"Write {changes.Count} change(s) to {m.Abbrev}?",
                    $"{list}\n\nEach changed block is backed up first and verified afterwards. Keep the ignition on and the battery charged.", "Write to module", danger: true))
                return;
            var results = await _s.WithBusAsync(t => ModuleCoding.WriteChangedAsync(t, m.Session, baseline, _working,
                new WriteOptions { Commit = true, ReadVoltage = _s.ReadVoltageUnlockedAsync }));
            var failed = results.FirstOrDefault(r => r.Error is not null || r.Result is { Message: not "Written and verified." });
            if (failed is null)
            {
                Message = $"Wrote and verified {results.Count} block(s) on {m.Abbrev}. Backups are in the backup folder.";
                _s.Info(Message);
                _original = _working.Clone();
                var wk = _working;
                _all.Clear();
                Load(wk, $"{m.Abbrev} {m.Address} · written", fromModule: true);
            }
            else { Message = $"Block {failed.Block}: {failed.Error ?? failed.Result!.Message}"; _s.Error(Message); }
        }
        catch (Exception ex) when (ex is CommsException or WriteRefusedException or InvalidOperationException) { Message = "Failed: " + ex.Message; _s.Error(Message); }
        finally { IsBusy = false; }
    }
}
