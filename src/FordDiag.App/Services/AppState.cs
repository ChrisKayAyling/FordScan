using System.Collections.ObjectModel;
using System.Net.Http;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FordDiag.Comms;
using FordDiag.Comms.Elm;
using FordDiag.Core;
using FordDiag.Core.Simulation;
using FordDiag.Core.Vehicle;

namespace FordDiag.App.Services;

public enum ConnectionState { Disconnected, Connecting, Connected }
public enum LogKind { Info, Tx, Rx, Error, Trace }

public sealed record LogLine(DateTime Time, LogKind Kind, string Text)
{
    public string TimeText => Time.ToString("HH:mm:ss.fff");
    public string Prefix => Kind switch { LogKind.Tx => ">", LogKind.Rx => "<", LogKind.Error => "!", LogKind.Trace => "·", _ => "i" };
}

public sealed record IdentLine(ushort Did, string Name, string Value) { public string DidText => Did.ToString("X4"); }

/// <summary>A module found by the scan, with what we read from it.</summary>
public sealed partial class ModuleInfo : ObservableObject
{
    public ModuleInfo(FordModule module, FordBus bus) { Module = module; Bus = bus; }
    public FordModule Module { get; }
    public FordBus Bus { get; }
    public string BusText => Bus == FordBus.MsCan ? "MS-CAN" : "HS-CAN";
    public string Address => $"{Module.RequestId:X3} / {Module.ResponseId:X3}";
    public string Abbrev => Module.Abbrev;
    public string Name => Module.Name;
    public ModuleSession Session { get; set; } = null!;
    [ObservableProperty] private IReadOnlyList<IdentLine> _identification = Array.Empty<IdentLine>();
    [ObservableProperty] private int? _dtcCount;
    public string Strategy => Identification.FirstOrDefault(i => i.Did == 0xF188)?.Value ?? "–";
    public string DtcText => DtcCount is null ? "–" : DtcCount == 0 ? "None" : $"{DtcCount}";
    public bool HasDtcs => DtcCount > 0;
    partial void OnIdentificationChanged(IReadOnlyList<IdentLine> value) => OnPropertyChanged(nameof(Strategy));
    partial void OnDtcCountChanged(int? value) { OnPropertyChanged(nameof(DtcText)); OnPropertyChanged(nameof(HasDtcs)); }
}

/// <summary>Everything shared between pages: the adapter connection, found modules, the log and global safety state.</summary>
public sealed partial class AppState : ObservableObject
{
    private readonly SemaphoreSlim _busLock = new(1, 1);
    private ElmTransport? _elm;
    private FordSimulator? _sim;

    public AppState(IDialogService dialogs, AppSettings settings) { Dialogs = dialogs; Settings = settings; }

    public IDialogService Dialogs { get; }
    public AppSettings Settings { get; }
    public ObservableCollection<ModuleInfo> Modules { get; } = new();
    public ObservableCollection<LogLine> Log { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsConnected))] private ConnectionState _state;
    [ObservableProperty] private string _statusText = "Not connected";
    [ObservableProperty] private string _adapterText = "";
    [ObservableProperty] private double? _voltage;
    [ObservableProperty] private bool _isSimulator;
    [ObservableProperty] private bool _isExpertMode;
    [ObservableProperty] private string? _vin;
    [ObservableProperty] private VinInfo? _vinInfo;
    [ObservableProperty] private VehicleDetails? _vehicleDetails;
    [ObservableProperty] private bool _isLookingUp;
    [ObservableProperty] private string? _lookupError;

    /// <summary>Replaceable in tests; the real client talks to vpic.nhtsa.dot.gov.</summary>
    public NhtsaVinClient VinClient { get; set; } = new();

    partial void OnVinChanged(string? value)
    {
        VinInfo = string.IsNullOrEmpty(value) ? null : VinDecoder.Decode(value);
        VehicleDetails = null; LookupError = null;
        OnPropertyChanged(nameof(VehicleTitle)); OnPropertyChanged(nameof(VehicleSubtitle)); OnPropertyChanged(nameof(HasVehicle));
    }
    partial void OnVehicleDetailsChanged(VehicleDetails? value)
    {
        OnPropertyChanged(nameof(VehicleTitle)); OnPropertyChanged(nameof(VehicleSubtitle)); OnPropertyChanged(nameof(VehicleModel)); OnPropertyChanged(nameof(VehicleYear));
    }

    public bool HasVehicle => VinInfo is not null;
    public string? VehicleModel => VehicleDetails?.Model;
    public int? VehicleYear => VehicleDetails?.ModelYear ?? VinInfo?.ModelYear;

    public string VehicleTitle
    {
        get
        {
            if (VehicleDetails is { Error: null } d && d.Title.Length > 0) return d.Title;
            if (VinInfo is not { } v) return "";
            return v.IsFord ? $"{v.Manufacturer}{(v.ModelYear is int y ? $" · model year {(v.ModelYears.Count > 1 ? $"{v.ModelYears[0]} or {v.ModelYears[1]}" : y.ToString())}" : "")}" : "Unrecognised manufacturer code " + v.Wmi;
        }
    }

    public string VehicleSubtitle
    {
        get
        {
            if (VinInfo is not { } v) return "";
            var parts = new List<string>();
            if (v.Country is not null) parts.Add(v.Country);
            parts.Add(!v.WellFormed ? "VIN is not 17 valid characters" : !v.CheckDigitApplies ? "check digit not used in this region" : v.CheckDigitOk ? "check digit OK" : "CHECK DIGIT WRONG (misread or altered VIN)");
            if (VehicleDetails is { Error: null } d) parts.AddRange(new[] { d.BodyClass, d.Engine, d.DriveType }.Where(x => !string.IsNullOrEmpty(x))!);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Asks NHTSA for model, trim and engine. The VIN leaves the computer, so this only runs on request.</summary>
    public async Task LookupVehicleAsync()
    {
        if (Vin is not { Length: > 0 } vin) return;
        IsLookingUp = true; LookupError = null;
        try
        {
            var d = await VinClient.LookupAsync(vin);
            if (d.Error is not null) LookupError = d.Error;
            VehicleDetails = d;
            Info($"Vehicle lookup: {d.Title}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { LookupError = "Lookup failed: " + ex.Message; Error(LookupError); }
        finally { IsLookingUp = false; }
    }
    [ObservableProperty] private ModuleInfo? _selectedModule;
    [ObservableProperty] private FordAdapter? _adapter;

    public bool IsConnected => State == ConnectionState.Connected;
    public BusSwitching Switching => Adapter?.Switching ?? BusSwitching.None;
    public IAddressableTransport? Transport => _elm;
    public string VoltageText => Voltage is double v ? $"{v:F1} V" : "– V";
    partial void OnVoltageChanged(double? value) => OnPropertyChanged(nameof(VoltageText));

    // ---- logging (thread-safe: marshals to the UI thread) ----
    public void Info(string text) => Add(LogKind.Info, text);
    public void Tx(string text) => Add(LogKind.Tx, text);
    public void Rx(string text) => Add(LogKind.Rx, text);
    public void Error(string text) => Add(LogKind.Error, text);

    private void Add(LogKind kind, string text)
    {
        var line = new LogLine(DateTime.Now, kind, text);
        void Do() { Log.Add(line); if (Log.Count > 2000) Log.RemoveAt(0); }
        if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess()) Do(); else Dispatcher.UIThread.Post(Do);
    }

    // ---- exclusive bus access: a module address is connection state, so conversations must not interleave ----
    public async Task<T> WithBusAsync<T>(Func<IAddressableTransport, Task<T>> action, CancellationToken ct = default)
    {
        if (_elm is null) throw new InvalidOperationException("Not connected to an adapter.");
        await _busLock.WaitAsync(ct);
        try { return await action(_elm); }
        finally { _busLock.Release(); }
    }

    public Task WithBusAsync(Func<IAddressableTransport, Task> action, CancellationToken ct = default) =>
        WithBusAsync<object?>(async t => { await action(t); return null; }, ct);

    // ---- connection ----
    public async Task ConnectAsync(FordAdapter adapter, string? port, bool simulator, int baud = 0)
    {
        await DisconnectAsync();
        State = ConnectionState.Connecting;
        StatusText = "Connecting…";
        Adapter = adapter;
        try
        {
            var opts = new ElmOptions { Profile = adapter.Profile, Trace = t => Add(LogKind.Trace, t) };
            if (baud > 0) opts.InitialBaud = baud;
            if (Settings.ResponseTimeoutMs > 0) opts.CanTimeoutMs = Settings.ResponseTimeoutMs;
            if (simulator)
            {
                _sim = FordSimulator.Create(stn: adapter.Profile.IsStn, msCanAvailable: adapter.Switching != BusSwitching.None);
                _elm = await ElmTransport.ConnectAsync(_sim.Host, opts);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(port)) throw new ArgumentException("Choose a serial port first.");
                _elm = await ElmTransport.OpenAsync(port, opts);
            }
            IsSimulator = simulator;
            AdapterText = $"{adapter.DisplayName} · {_elm.Info.Version}";
            Voltage = await _elm.ReadVoltageAsync();
            State = ConnectionState.Connected;
            StatusText = simulator ? "Connected (simulator)" : $"Connected · {port}";
            Info($"Connected: {AdapterText}" + (simulator ? " [simulated vehicle]" : ""));
        }
        catch (Exception ex)
        {
            Error($"Connection failed: {ex.Message}");
            await DisconnectAsync();
            State = ConnectionState.Disconnected;
            StatusText = "Connection failed";
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        var elm = _elm; var sim = _sim;
        _elm = null; _sim = null;
        if (elm is not null) { try { await elm.DisposeAsync(); } catch (Exception ex) when (ex is CommsException or IOException) { } }
        if (sim is not null) await sim.DisposeAsync();
        Modules.Clear(); SelectedModule = null; Vin = null; Voltage = null; IsSimulator = false; AdapterText = "";
        if (State != ConnectionState.Disconnected) Info("Disconnected.");
        State = ConnectionState.Disconnected;
        StatusText = "Not connected";
    }

    /// <summary>Battery voltage without taking the bus lock (call only from inside <see cref="WithBusAsync{T}"/>).</summary>
    public async ValueTask<double?> ReadVoltageUnlockedAsync(CancellationToken ct) => _elm is null ? null : await _elm.ReadVoltageAsync(ct);

    public async Task RefreshVoltageAsync()
    {
        if (_elm is null) return;
        try { Voltage = await WithBusAsync(_ => _elm.ReadVoltageAsync().AsTask()); }
        catch (CommsException) { }
    }
}
