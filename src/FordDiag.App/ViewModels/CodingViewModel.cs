using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FordDiag.Comms;
using FordDiag.App.Services;
using FordDiag.Core;

namespace FordDiag.App.ViewModels;

public sealed partial class AsBuiltLineViewModel : ObservableObject
{
    private readonly string _original;
    public AsBuiltLineViewModel(AsBuiltKey key, byte[] data)
    {
        Key = key; _original = Hex.ToString(data, true); _bytesText = _original;
    }
    public AsBuiltKey Key { get; }
    public string KeyText => Key.ToString();
    public string OriginalText => _original;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsChanged)), NotifyPropertyChangedFor(nameof(IsValid)), NotifyPropertyChangedFor(nameof(StateText)), NotifyPropertyChangedFor(nameof(IsUnchanged)), NotifyPropertyChangedFor(nameof(ChecksumText)), NotifyPropertyChangedFor(nameof(ChecksumBad)), NotifyPropertyChangedFor(nameof(ChecksumOk))] private string _bytesText;

    public string ChecksumText => !TryBytes(out var b) || b.Length < 2 ? "–" : AsBuiltChecksum.Compute(Key, b.AsSpan(0, b.Length - 1)) == b[^1] ? "OK" : "Fix";
    public bool ChecksumBad => ChecksumText == "Fix";
    public bool ChecksumOk => ChecksumText == "OK";
    /// <summary>Recomputes the last byte (FORScan does the same when you write 00).</summary>
    public void FixChecksum()
    {
        if (!TryBytes(out var b) || b.Length < 2) return;
        b[^1] = AsBuiltChecksum.Compute(Key, b.AsSpan(0, b.Length - 1));
        BytesText = Hex.ToString(b, true);
    }
    public string StateText => !IsValid ? "Invalid" : IsChanged ? "Changed" : "Unchanged";
    public bool IsUnchanged => IsValid && !IsChanged;
    public bool IsValid => TryBytes(out var b) && b.Length == _original.Replace(" ", "").Length / 2;
    public bool IsChanged => IsValid && !Normalised().Equals(_original, StringComparison.OrdinalIgnoreCase);
    private string Normalised() => TryBytes(out var b) ? Hex.ToString(b, true) : "";
    public bool TryBytes(out byte[] bytes) => Hex.TryParse(BytesText.Replace(" ", ""), out bytes) && bytes.Length > 0;
    public void Revert() => BytesText = _original;
}

public sealed record BackupRow(string Path, string Module, string Did, string Time, string Hex);

public sealed partial class CodingViewModel : PageViewModel
{
    private readonly AppState _s;
    private readonly BackupStore _backups = new();
    private AsBuiltData? _loaded;

    public CodingViewModel(AppState state)
    {
        _s = state;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.IsExpertMode) or nameof(AppState.State)) { WriteCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(ExpertRequired)); }
        };
        state.Modules.CollectionChanged += (_, _) => { if (SelectedModule is null) SelectedModule = state.Modules.FirstOrDefault(); };
        Config = new ConfigurationViewModel(state, () => SelectedModule);
        RefreshBackups();
    }

    public ConfigurationViewModel Config { get; }
    public override string Title => "Coding";
    public AppState State => _s;

    [ObservableProperty] private int _selectedTab;  // 0 Configuration, 1 As-built lines, 2 Module data

    // ---- As-built file ----
    public ObservableCollection<AsBuiltLineViewModel> Lines { get; } = new();
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _fileMessage = "Open an as-built text file (lines like 726-01-01 0840 8200 12) to inspect and edit it offline.";
    public int ChangedCount => Lines.Count(l => l.IsChanged);
    public int InvalidCount => Lines.Count(l => !l.IsValid);
    public string ChangeSummary => Lines.Count == 0 ? "" : $"{Lines.Count} lines · {ChangedCount} changed" + (InvalidCount > 0 ? $" · {InvalidCount} invalid" : "");
    public bool HasLines => Lines.Count > 0;

    public void LoadText(string text, string path)
    {
        foreach (var l in Lines) l.PropertyChanged -= LineChanged;
        Lines.Clear();
        _loaded = AsBuiltData.Parse(text, out var errors);
        foreach (var (k, v) in _loaded.Lines) { var vm = new AsBuiltLineViewModel(k, v); vm.PropertyChanged += LineChanged; Lines.Add(vm); }
        FilePath = path;
        FileMessage = errors.Count == 0 ? $"Loaded {Lines.Count} lines." : $"Loaded {Lines.Count} lines, {errors.Count} skipped: {string.Join("; ", errors.Take(3))}";
        NotifySummary();
    }

    private void LineChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) => NotifySummary();
    private void NotifySummary()
    {
        OnPropertyChanged(nameof(ChangedCount)); OnPropertyChanged(nameof(InvalidCount)); OnPropertyChanged(nameof(ChangeSummary)); OnPropertyChanged(nameof(HasLines));
        SaveFileCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var path = await _s.Dialogs.PickOpenFileAsync("Open as-built file", "As-built text", "*.txt", "*.abt", "*.asbuilt");
        if (path is null) return;
        try { LoadText(await File.ReadAllTextAsync(path), path); _s.Info($"Opened {path}"); }
        catch (IOException ex) { FileMessage = "Could not read file: " + ex.Message; }
    }

    private bool CanSave() => Lines.Count > 0 && InvalidCount == 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveFileAsync()
    {
        var path = await _s.Dialogs.PickSaveFileAsync("Save as-built file", Path.GetFileName(FilePath) is { Length: > 0 } n ? n : "asbuilt.txt", "As-built text", "*.txt");
        if (path is null) return;
        foreach (var l in Lines) if (l.ChecksumBad) l.FixChecksum();
        var sb = new System.Text.StringBuilder();
        foreach (var l in Lines) { l.TryBytes(out var b); sb.Append(l.KeyText).Append(' ').AppendLine(Hex.ToString(b, true)); }
        await File.WriteAllTextAsync(path, sb.ToString());
        FileMessage = $"Saved {Lines.Count} lines to {path}";
    }

    [RelayCommand]
    private void FixChecksums()
    {
        int n = 0;
        foreach (var l in Lines) if (l.ChecksumBad) { l.FixChecksum(); n++; }
        FileMessage = n == 0 ? "All checksums are correct." : $"Recalculated {n} checksum(s).";
    }

    [RelayCommand]
    private void RevertAll() { foreach (var l in Lines) l.Revert(); }

    // ---- raw DID access ----
    public ObservableCollection<ModuleInfo> Modules => _s.Modules;
    [ObservableProperty] private ModuleInfo? _selectedModule;
    [ObservableProperty] private string _didText = "F190";
    [ObservableProperty] private string _valueText = "";
    [ObservableProperty] private string _plan = "";
    [ObservableProperty] private string _result = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(WriteCommand)), NotifyCanExecuteChangedFor(nameof(ReadDidCommand)), NotifyCanExecuteChangedFor(nameof(PreviewCommand))] private bool _isBusy;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(WriteCommand))] private bool _planned;

    public bool ExpertRequired => !_s.IsExpertMode;
    public ObservableCollection<BackupRow> Backups { get; } = new();

    partial void OnValueTextChanged(string value) { Planned = false; Plan = ""; }
    partial void OnDidTextChanged(string value) { Planned = false; Plan = ""; }
    partial void OnSelectedModuleChanged(ModuleInfo? value) { Planned = false; Plan = ""; Config.Notify(); ReadDidCommand.NotifyCanExecuteChanged(); PreviewCommand.NotifyCanExecuteChanged(); }

    private bool TryDid(out ushort did) =>
        ushort.TryParse(DidText.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber, null, out did);

    private bool CanAccess() => _s.IsConnected && SelectedModule is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAccess))]
    private async Task ReadDidAsync()
    {
        if (!TryDid(out var did)) { Result = "DID must be 4 hex digits."; return; }
        IsBusy = true;
        try
        {
            var m = SelectedModule!;
            var data = await _s.WithBusAsync(_ => m.Session.ReadDidAsync(did).AsTask());
            ValueText = Hex.ToString(data, true);
            Result = $"{m.Abbrev} {did:X4}: {data.Length} byte(s) read.";
            _s.Tx($"22 {did:X4} -> {m.Abbrev}"); _s.Rx(Hex.ToString(data, true));
        }
        catch (Exception ex) when (ex is CommsException or InvalidOperationException) { Result = "Read failed: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private bool TryValue(out byte[] value) => Hex.TryParse(ValueText.Replace(" ", ""), out value) && value.Length > 0;

    [RelayCommand(CanExecute = nameof(CanAccess))]
    private async Task PreviewAsync()
    {
        if (!TryDid(out var did) || !TryValue(out var value)) { Result = "Enter a 4-digit DID and a hex value."; return; }
        await RunWriteAsync(did, value, commit: false);
    }

    private bool CanWrite() => CanAccess() && _s.IsExpertMode && Planned;

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        if (!TryDid(out var did) || !TryValue(out var value)) return;
        var m = SelectedModule!;
        if (!await _s.Dialogs.ConfirmAsync($"Write {m.Abbrev} DID {did:X4}?",
                $"The module's current value is backed up first, then {value.Length} byte(s) are written and verified. Keep the ignition on, do not disconnect the adapter and keep the battery charged.", "Write to module", danger: true))
            return;
        await RunWriteAsync(did, value, commit: true);
    }

    private async Task RunWriteAsync(ushort did, byte[] value, bool commit)
    {
        IsBusy = true;
        try
        {
            var m = SelectedModule!;
            var res = await _s.WithBusAsync(t => DidWriter.WriteAsync(t, m.Session, did, value,
                new WriteOptions { Commit = commit, Backups = _backups, ReadVoltage = _s.ReadVoltageUnlockedAsync }));
            Plan = $"{m.Abbrev} {did:X4}\n  current: {Hex.ToString(res.Before, true)}\n  new:     {Hex.ToString(value, true)}";
            Planned = !commit;
            Result = res.Message;
            if (commit) { _s.Tx($"2E {did:X4} -> {m.Abbrev}: {Hex.ToString(value, true)}"); _s.Info(res.Message); RefreshBackups(); Planned = false; }
        }
        catch (WriteRefusedException ex) { Result = "Refused: " + ex.Message; Planned = false; _s.Error(ex.Message); }
        catch (Exception ex) when (ex is CommsException or InvalidOperationException) { Result = "Failed: " + ex.Message; Planned = false; _s.Error(ex.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public void RefreshBackups()
    {
        Backups.Clear();
        if (!Directory.Exists(_backups.Directory)) return;
        foreach (var f in Directory.GetFiles(_backups.Directory, "*.json").OrderByDescending(x => x).Take(50))
        {
            try { var b = BackupStore.Load(f); Backups.Add(new BackupRow(f, b.Module, b.Did, b.Time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"), b.Hex)); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException) { }
        }
    }

    /// <summary>Loads a backup into the editor (it is not written until you preview and confirm).</summary>
    [RelayCommand]
    private void UseBackup(BackupRow? row)
    {
        if (row is null) return;
        DidText = row.Did; ValueText = row.Hex;
        SelectedModule = _s.Modules.FirstOrDefault(m => m.Abbrev == row.Module) ?? SelectedModule;
        Result = $"Loaded backup from {row.Time}. Preview, then write to restore.";
    }
}
