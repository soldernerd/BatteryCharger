using System.Collections.ObjectModel;
using System.Diagnostics;
using BatteryCharger.App.Services;
using BatteryCharger.Core.Batteries;
using BatteryCharger.Core.Instruments;
using BatteryCharger.Core.Instruments.Simulation;
using BatteryCharger.Core.Logging;
using BatteryCharger.Core.Profiles;
using BatteryCharger.Core.Session;
using BatteryCharger.Instruments.Discovery;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace BatteryCharger.App.ViewModels;

/// <summary>
/// State and commands of the Charger page: supply selection, battery inputs, profile summary and the running session.
/// Singleton, so a running charge survives navigation to other pages.
/// </summary>
public sealed partial class ChargerViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly VisaService _visa;
    private readonly InstrumentService _instruments;
    private readonly DialogService _dialogs;
    private readonly SleepInhibitor _sleep;
    private readonly ILogger<ChargerViewModel> _log;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _uiTimer;

    private bool _loading = true;
    private bool _updatingCells;
    private ChannelLimits? _limits;
    private ChargeSession? _session;
    private Task? _runTask;
    private volatile Sample? _latest;
    private int _lastStageIndex = -1;

    public ChargerViewModel(SettingsService settings, VisaService visa, InstrumentService instruments,
        DialogService dialogs, SleepInhibitor sleep, ILogger<ChargerViewModel> log)
    {
        _settings = settings;
        _visa = visa;
        _instruments = instruments;
        _dialogs = dialogs;
        _sleep = sleep;
        _log = log;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _uiTimer = _dispatcher.CreateTimer();
        _uiTimer.Interval = TimeSpan.FromMilliseconds(250);   // chart and readouts update at <= 4 Hz
        _uiTimer.Tick += (_, _) => RefreshReadouts();

        var b = settings.Current.Battery;
        _technologyIndex = (int)b.Technology;
        _nominalVoltage = b.NominalVoltage;
        _capacityInMah = b.CapacityInMah;
        _capacityValue = b.CapacityInMah ? b.CapacityAh * 1000 : b.CapacityAh;
        _chargeRateC = b.ChargeRateC ?? CellChemistryInfo.For(b.Technology).DefaultRateC;
        if (b.CellsOverride is { } cells && cells >= 1) { _autoCells = false; _cells = cells; }
        else { _autoCells = true; _cells = CellChemistryInfo.DeriveCells(b.Technology, b.NominalVoltage); }

        RebuildAdvancedFields(loadSaved: true);
        _loading = false;
        Recompute();
        RebuildSupplyList(null);
    }

    // ---------------------------------------------------------------- supply

    public ObservableCollection<SupplyItem> Supplies { get; } = [];
    public ObservableCollection<ChannelItem> Channels { get; } = [];

    [ObservableProperty] private SupplyItem? _selectedSupply;
    [ObservableProperty] private ChannelItem? _selectedChannel;
    [ObservableProperty] private bool _isDiscovering;
    [ObservableProperty] private string _discoveryStatus = "";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _identityText = "";
    [ObservableProperty] private string? _outputWarning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";

    public bool DeveloperMode => _settings.Current.DeveloperMode;
    public bool HasOutputWarning => !string.IsNullOrEmpty(OutputWarning);
    public bool VisaAvailable => _visa.IsAvailable;
    public bool CanChangeSupply => !IsConnected && !IsBusy;

    partial void OnSelectedSupplyChanged(SupplyItem? value) => ConnectCommand.NotifyCanExecuteChanged();

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanChangeSupply));
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanChangeSupply));
        StartCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedChannelChanged(ChannelItem? value)
    {
        OutputWarning = value?.Channel.OutputWarning;
        OnPropertyChanged(nameof(HasOutputWarning));
        _limits = null;
        Recompute();
        if (value is not null) _ = LoadLimitsAsync(value.Channel);
    }

    private async Task LoadLimitsAsync(IPowerSupplyChannel channel)
    {
        try
        {
            // requiredVolts = 0: read the ratings at the current range without changing anything on the instrument.
            var l = await channel.GetLimitsAsync(0, CancellationToken.None);
            if (SelectedChannel?.Channel == channel)
            {
                _limits = l;
                Recompute();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read channel limits");
            ShowBanner($"Could not read the channel limits: {ex.Message}", InfoBarSeverity.Warning);
        }
    }

    private void RebuildSupplyList(IEnumerable<VisaResourceInfo>? found)
    {
        var keep = SelectedSupply?.Resource ?? _settings.Current.LastResource;
        Supplies.Clear();
        if (_settings.Current.DeveloperMode)
            Supplies.Add(SimulatorItem());
        foreach (var f in found ?? []) Supplies.Add(ToItem(f));
        SelectedSupply = Supplies.FirstOrDefault(s => s.Resource == keep) ?? Supplies.FirstOrDefault(s => s.Supported);
    }

    private static SupplyItem ToItem(VisaResourceInfo f) =>
        new(f.Supported ? f.Display : $"{f.Display}  [not a supported power supply]", f.Resource, f.Supported, false);

    private bool _autoDiscovered;

    /// <summary>Runs the first instrument search when the page opens, so the user does not have to press Refresh.</summary>
    public void DiscoverOnFirstShow()
    {
        if (_autoDiscovered || !_visa.IsAvailable || IsConnected) return;
        _autoDiscovered = true;
        if (RefreshCommand.CanExecute(null)) RefreshCommand.Execute(null);
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RefreshAsync(CancellationToken ct)
    {
        if (!_visa.IsAvailable) return;
        IsDiscovering = true;
        DiscoveryStatus = "Searching for instruments...";
        var results = new List<VisaResourceInfo>();
        try
        {
            var progress = new Progress<VisaResourceInfo>(info =>
            {
                results.Add(info);
                _log.LogInformation("Discovery: {Display} (supported: {Supported})", info.Display, info.Supported);
                DiscoveryStatus = $"Found {results.Count}: {info.Display}";
                RebuildSupplyList(results);   // usable straight away, not only after the slow LAN check
            });
            await _instruments.DiscoverAsync(progress, ct);
            RebuildSupplyList(results);
            DiscoveryStatus = results.Count == 0
                ? "No instruments found. For LAN instruments use \"Add LAN instrument\"."
                : $"{results.Count(r => r.Supported)} supported of {results.Count} instrument(s) found.";
        }
        catch (OperationCanceledException)
        {
            RebuildSupplyList(results);
            DiscoveryStatus = "Search cancelled.";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Discovery failed");
            DiscoveryStatus = "Search failed: " + ex.Message;
        }
        finally { IsDiscovering = false; }
    }

    [RelayCommand]
    private async Task AddLanAsync()
    {
        var text = await _dialogs.PromptLanInstrumentAsync();
        if (text is null) return;
        var resource = InstrumentDiscovery.ToResourceString(text);
        if (!_settings.Current.LanInstruments.Contains(resource, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Current.LanInstruments.Add(resource);
            _settings.Save();
        }
        IsDiscovering = true;
        DiscoveryStatus = $"Contacting {resource}...";
        try
        {
            var info = await _instruments.IdentifyAsync(resource, CancellationToken.None);
            var existing = Supplies.FirstOrDefault(s => s.Resource.Equals(resource, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) Supplies.Remove(existing);
            var item = ToItem(info);
            Supplies.Add(item);
            SelectedSupply = item;
            DiscoveryStatus = info.Supported ? $"Added {info.Display}" : $"Added, but: {info.Reason}";
        }
        finally { IsDiscovering = false; }
    }

    private bool CanConnect() => !IsConnected && !IsBusy && SelectedSupply is { Supported: true };

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        var item = SelectedSupply!;
        IsBusy = true;
        BusyText = "Connecting...";
        try
        {
            var psu = await _instruments.ConnectAsync(item.Resource, item.IsSimulator ? BuildSimulatedBattery() : null, CancellationToken.None);
            Channels.Clear();
            foreach (var ch in psu.Channels) Channels.Add(new ChannelItem(ch.DisplayName, ch));
            IdentityText = $"{psu.Identity.Model}   S/N {psu.Identity.Serial}   firmware {psu.Identity.Firmware}";
            IsConnected = true;
            _settings.Current.LastResource = item.Resource;
            _settings.Save();
            SelectedChannel = Channels.FirstOrDefault();
            ClearBanner();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Connect failed");
            ShowBanner($"Could not connect to {item.Resource}: {ex.Message}", InfoBarSeverity.Error);
        }
        finally { IsBusy = false; }
    }

    private bool CanDisconnect() => IsConnected && !IsSessionActive;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        await _instruments.DisconnectAsync();
        Channels.Clear();
        SelectedChannel = null;
        IdentityText = "";
        IsConnected = false;
    }

    private SimulatedBattery BuildSimulatedBattery()
    {
        var chem = CellChemistryInfo.For(Technology);
        var cells = EffectiveCells;
        var cap = CapacityAh > 0 ? CapacityAh : 2.0;
        var full = chem.IsNickel ? 1.45 : chem.TargetV;
        var empty = chem.Technology switch
        {
            BatteryTechnology.LiFePO4 => 2.9,
            BatteryTechnology.LeadAcid => 1.95,
            _ when chem.IsNickel => 1.0,
            _ => 3.0,
        };
        return new SimulatedBattery(cells, cap, empty, full, 0.1 / cap, 0.3) { DvCellVolts = chem.IsNickel ? chem.DvCellV * 2 : 0 };
    }

    // ---------------------------------------------------------------- battery inputs

    public IReadOnlyList<CellChemistryInfo> Technologies => CellChemistryInfo.Table;
    public ObservableCollection<OptionField> AdvancedFields { get; } = [];

    [ObservableProperty] private int _technologyIndex;
    [ObservableProperty] private double _nominalVoltage;
    [ObservableProperty] private double _capacityValue;
    [ObservableProperty] private bool _capacityInMah;
    [ObservableProperty] private int _cells;
    [ObservableProperty] private bool _autoCells = true;
    [ObservableProperty] private double _chargeRateC;

    public BatteryTechnology Technology => (BatteryTechnology)Math.Clamp(TechnologyIndex, 0, Technologies.Count - 1);
    public CellChemistryInfo Chemistry => CellChemistryInfo.For(Technology);
    public double CapacityAh => CapacityInMah ? CapacityValue / 1000.0 : CapacityValue;
    public int EffectiveCells => AutoCells ? CellChemistryInfo.DeriveCells(Technology, NominalVoltage) : Math.Max(1, Cells);
    public IReadOnlyList<double> VoltagePresets => Chemistry.PresetVoltages;
    public string CapacityUnit => CapacityInMah ? "mAh" : "Ah";
    public string RateHint => $"Default {Chemistry.DefaultRateC:0.##} C, maximum {Chemistry.MaxRateC:0.##} C";

    partial void OnTechnologyIndexChanged(int value)
    {
        if (_loading) return;
        ChargeRateC = Chemistry.DefaultRateC;
        OnPropertyChanged(nameof(Technology));
        OnPropertyChanged(nameof(Chemistry));
        OnPropertyChanged(nameof(VoltagePresets));
        OnPropertyChanged(nameof(RateHint));
        SyncCells();
        RebuildAdvancedFields(loadSaved: true);
        Recompute();
    }

    partial void OnNominalVoltageChanged(double value)
    {
        SyncCells();
        Recompute();
    }

    partial void OnCapacityValueChanged(double value) => Recompute();
    partial void OnChargeRateCChanged(double value) => Recompute();

    partial void OnCapacityInMahChanged(bool value)
    {
        OnPropertyChanged(nameof(CapacityUnit));
        if (_loading || double.IsNaN(CapacityValue)) return;
        CapacityValue = value ? CapacityValue * 1000 : CapacityValue / 1000;
    }

    partial void OnCellsChanged(int value)
    {
        if (_loading || _updatingCells) return;
        AutoCells = false;
        Recompute();
    }

    partial void OnAutoCellsChanged(bool value)
    {
        if (value) SyncCells();
        Recompute();
    }

    private void SyncCells()
    {
        if (_loading || !AutoCells) return;
        _updatingCells = true;
        Cells = CellChemistryInfo.DeriveCells(Technology, NominalVoltage);
        _updatingCells = false;
    }

    /// <summary>Number-box friendly view of the cell count (shows the derived value while automatic).</summary>
    public double CellsValue
    {
        get => EffectiveCells;
        set
        {
            if (double.IsNaN(value)) return;
            Cells = Math.Max(1, (int)Math.Round(value));
        }
    }

    public void ApplyVoltagePreset(double volts) => NominalVoltage = volts;

    [RelayCommand]
    private void ResetCells() => AutoCells = true;

    private void RebuildAdvancedFields(bool loadSaved)
    {
        foreach (var f in AdvancedFields) f.ValueEdited -= OnAdvancedEdited;
        AdvancedFields.Clear();
        var options = ProfileOptions.Defaults(Technology);
        var saved = loadSaved ? _settings.Current.AdvancedOverrides : null;
        if (saved is not null)
        {
            // Saved overrides are re-validated against the hard limits; anything invalid falls back to the defaults.
            var candidate = options;
            foreach (var f in OptionFieldFactory.Build(Technology, options))
                if (saved.TryGetValue($"{Technology}.{f.Id}", out var v)) candidate = f.Write(candidate, v);
            if (candidate.Validate(Technology).Count == 0) options = candidate;
        }
        foreach (var f in OptionFieldFactory.Build(Technology, options))
        {
            f.ValueEdited += OnAdvancedEdited;
            AdvancedFields.Add(f);
        }
    }

    private void OnAdvancedEdited(object? sender, EventArgs e) => Recompute();

    [RelayCommand]
    private void ResetAdvanced()
    {
        foreach (var f in AdvancedFields) f.Value = f.DefaultValue;
    }

    [RelayCommand]
    private void SaveAdvanced()
    {
        var map = _settings.Current.AdvancedOverrides ?? [];
        foreach (var k in map.Keys.Where(k => k.StartsWith(Technology + ".", StringComparison.Ordinal)).ToList()) map.Remove(k);
        foreach (var f in AdvancedFields.Where(f => !f.IsDefault)) map[$"{Technology}.{f.Id}"] = f.Value;
        _settings.Current.AdvancedOverrides = map.Count == 0 ? null : map;
        _settings.Save();
        ShowBanner("Advanced values saved as defaults for " + Chemistry.DisplayName + ".", InfoBarSeverity.Success);
    }

    private ProfileOptions BuildOptions()
    {
        var o = ProfileOptions.Defaults(Technology);
        foreach (var f in AdvancedFields) o = f.Write(o, f.Value);
        return o;
    }

    private BatteryDescriptor BuildBattery() =>
        new(Technology, NominalVoltage, CapacityAh, EffectiveCells, ChargeRateC);

    // ---------------------------------------------------------------- profile summary

    public ObservableCollection<string> ProfileLines { get; } = [];
    public ObservableCollection<string> ProfileWarnings { get; } = [];
    public ObservableCollection<string> ProfileErrors { get; } = [];

    [ObservableProperty] private ChargeProfile? _profile;
    public bool HasWarnings => ProfileWarnings.Count > 0;
    public bool HasErrors => ProfileErrors.Count > 0;

    private void Recompute()
    {
        if (_loading) return;
        // Range-capable supplies only switch range at start, so voltage limits are not checked against the current range here.
        var lim = IsConnected && SelectedChannel is { Channel.HasVoltageRanges: false } ? _limits : null;
        var p = ChargeProfileFactory.Create(BuildBattery(), BuildOptions(), lim);
        Profile = p;
        OnPropertyChanged(nameof(CellsValue));
        Fill(ProfileLines, p.IsValid ? p.SummaryLines : []);
        Fill(ProfileWarnings, p.Warnings);
        Fill(ProfileErrors, p.Errors);
        if (SelectedChannel is { Channel.HasVoltageRanges: true } && IsConnected)
            ProfileWarnings.Add("This supply has voltage ranges; the smallest suitable range is selected automatically when charging starts.");
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(HasErrors));
        foreach (var f in AdvancedFields)
            f.Message = p.Errors.FirstOrDefault(e => e.StartsWith(f.Label, StringComparison.OrdinalIgnoreCase)) ?? "";
        StartCommand.NotifyCanExecuteChanged();
    }

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    // ---------------------------------------------------------------- banner

    [ObservableProperty] private bool _isBannerOpen;
    [ObservableProperty] private string _bannerMessage = "";
    [ObservableProperty] private InfoBarSeverity _bannerSeverity = InfoBarSeverity.Informational;

    private void ShowBanner(string message, InfoBarSeverity severity)
    {
        void Show()
        {
            BannerMessage = message;
            BannerSeverity = severity;
            IsBannerOpen = true;
        }
        if (_dispatcher.HasThreadAccess) Show(); else _dispatcher.TryEnqueue(Show);
    }

    private void ClearBanner() => IsBannerOpen = false;

    // ---------------------------------------------------------------- session

    public ChartBuffer Chart { get; } = new();
    public event EventHandler? ChartInvalidated;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowSetup), nameof(ShowCharging))]
    private bool _isSessionActive;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowSetup), nameof(ShowCharging))]
    private bool _hasResult;

    public bool ShowSetup => !IsSessionActive && !HasResult;
    public bool ShowCharging => IsSessionActive || HasResult;

    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _stageText = "";
    [ObservableProperty] private string _elapsedText = "0:00:00";
    [ObservableProperty] private string _voltageText = "-";
    [ObservableProperty] private string _currentText = "-";
    [ObservableProperty] private string _powerText = "-";
    [ObservableProperty] private string _chargeText = "-";
    [ObservableProperty] private string _energyText = "-";
    [ObservableProperty] private string _modeText = "-";
    [ObservableProperty] private string _setpointText = "-";
    [ObservableProperty] private string _logFileName = "";
    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultDetails = "";
    private string? _logFilePath;

    partial void OnIsSessionActiveChanged(bool value)
    {
        DisconnectCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() =>
        IsConnected && !IsBusy && !IsSessionActive && SelectedChannel is not null && Profile is { IsValid: true };

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var channel = SelectedChannel!.Channel;
        IsBusy = true;
        ClearBanner();
        try
        {
            BusyText = "Reading supply limits...";
            var battery = BuildBattery();
            var options = BuildOptions();
            var target = ChargeProfileFactory.Create(battery, options).TargetPackVolts;
            _limits = await channel.GetLimitsAsync(target, CancellationToken.None);   // selects the voltage range if the model has ranges
            var profile = ChargeProfileFactory.Create(battery, options, _limits);
            Profile = profile;
            if (!profile.IsValid)
            {
                await _dialogs.ShowMessageAsync("Cannot start charging", string.Join("\n", profile.Errors));
                Recompute();
                return;
            }

            BusyText = "Checking the power supply output...";
            var snapshot = await channel.ReadStateAsync(CancellationToken.None);
            if (snapshot.OutputOn && !await _dialogs.ConfirmAsync("Output is on",
                    "The output of this channel is currently on. It will be switched off before charging starts.", "Continue", "Cancel"))
                return;

            BusyText = "Running preflight checks...";
            var pre = await PreflightChecker.RunAsync(channel, profile, CancellationToken.None);
            if (pre.Refused)
            {
                await _dialogs.ShowMessageAsync("Charging refused", pre.Refusal + (pre.Hints.Count > 0 ? "\n\n" + string.Join("\n", pre.Hints) : ""));
                return;
            }

            var supplyText = $"{_instruments.Current?.Identity.Model} {channel.DisplayName}";
            if (!await _dialogs.ConfirmChargeAsync(profile, pre, supplyText, channel.OutputWarning)) return;

            PersistInputs();
            StartSession(channel, profile);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Start failed");
            ShowBanner($"Could not start charging: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void StartSession(IPowerSupplyChannel channel, ChargeProfile profile)
    {
        var psu = _instruments.Current!;
        var simulated = psu is FakePowerSupply;
        var s = _settings.Current;
        ISessionClock clock = simulated ? new ScaledClock(InstrumentService.SimulatorTimeScale) : new StopwatchClock();
        var logger = new CsvSessionLogger(s.EffectiveLogFolder, s.CsvSeparatorChar);
        var session = new ChargeSession(channel, profile.Battery, profile, logger, new SessionOptions
        {
            PollInterval = TimeSpan.FromSeconds(s.PollIntervalSeconds),
            RestorePreviousSetpoints = s.RestorePreviousSetpoints,
            UserConfirmed = true,
            Instrument = new InstrumentInfo(psu.Identity.ToString(), psu.Resource, channel.DisplayName),
            Clock = clock,
        });

        Chart.Clear();
        _latest = null;
        _lastStageIndex = -1;
        _logFilePath = null;
        LogFileName = "";
        ResetReadouts();
        HasResult = false;
        IsSessionActive = true;
        StateText = "Preflight";
        session.Updated += OnSessionUpdated;
        _session = session;
        _activeLogger = logger;
        _sleep.Acquire();
        _uiTimer.Start();
        _log.LogInformation("Charge started: {Tech} {Cells}S {Ah} Ah", profile.Battery.Technology, profile.Battery.CellsInSeries, profile.Battery.CapacityAh);
        _runTask = RunSessionAsync(session, logger);
    }

    private async Task RunSessionAsync(ChargeSession session, CsvSessionLogger logger)
    {
        SessionResult result;
        try
        {
            result = await Task.Run(() => session.RunAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Session crashed");
            result = new SessionResult(EndReason.Fault, ex.Message, TimeSpan.Zero, 0, 0, 0, logger.FilePath);
        }

        // Back on the UI thread (the task was started from it).
        _uiTimer.Stop();
        _sleep.Release();
        session.Updated -= OnSessionUpdated;
        RefreshReadouts();
        _logFilePath = result.LogFilePath;
        LogFileName = result.LogFilePath is null ? "" : Path.GetFileName(result.LogFilePath);
        ShowResult(result);
        IsSessionActive = false;
        HasResult = true;
        _session = null;
        _activeLogger = null;
        await session.DisposeAsync();
        await logger.DisposeAsync();
        _log.LogInformation("Charge ended: {Reason} - {Message}", result.Reason, result.Message);
    }

    private void ShowResult(SessionResult r)
    {
        ResultTitle = r.Reason switch
        {
            EndReason.Completed => "Charge complete",
            EndReason.CompletedWithWarning => "Charge ended with a warning",
            EndReason.UserStopped => "Charge stopped",
            _ => "Charge aborted by a fault",
        };
        ResultDetails =
            $"{r.Message}\nDuration {FormatElapsed(r.Duration)}   Charge {FormatCharge(r.AhDelivered)}   Energy {r.WhDelivered:0.000} Wh   Final voltage {r.FinalVoltage:0.000} V";
        StateText = r.Reason switch
        {
            EndReason.Completed or EndReason.CompletedWithWarning => "Completed",
            EndReason.UserStopped => "Stopped",
            _ => "Faulted",
        };
        if (r.Reason == EndReason.Fault)
            ShowBanner("FAULT: " + r.Message, InfoBarSeverity.Error);       // stays until dismissed
        else if (r.Reason == EndReason.CompletedWithWarning)
            ShowBanner("Warning: " + r.Message, InfoBarSeverity.Warning);
    }

    /// <summary>Runs on the session thread.</summary>
    private void OnSessionUpdated(object? sender, SessionUpdate u)
    {
        if (u.Sample is { } s)
        {
            Chart.Add(s.Elapsed.TotalSeconds, s.Volts, s.Amps);
            if (u.StageIndex != _lastStageIndex)
            {
                _lastStageIndex = u.StageIndex;
                Chart.AddMarker(s.Elapsed.TotalSeconds, u.Stage);
            }
            _latest = s;
        }
        if (u.Sample is null && u.IsWarning && u.Message is not null && !u.State.IsTerminal())
            ShowBanner(u.Message, InfoBarSeverity.Warning);
        _dispatcher.TryEnqueue(() =>
        {
            if (_session is not null)
            {
                StateText = StateName(u.State);
                StageText = u.Stage;
            }
        });
    }


    private void RefreshReadouts()
    {
        var s = _latest;
        if (s is not null)
        {
            ElapsedText = FormatElapsed(s.Elapsed);
            VoltageText = $"{s.Volts:0.000} V";
            CurrentText = $"{s.Amps:0.000} A";
            PowerText = $"{s.Watts:0.00} W";
            ChargeText = FormatCharge(s.Ah);
            EnergyText = $"{s.Wh:0.000} Wh";
            ModeText = s.Mode.ToString();
            SetpointText = $"{s.VoltageSet:0.000} V / {s.CurrentLimit:0.###} A";
            StageText = s.Stage;
        }
        if (_activeLogger?.FilePath is { } path) LogFileName = Path.GetFileName(path);   // known once the session opened the log
        ChartInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private CsvSessionLogger? _activeLogger;

    private void ResetReadouts()
    {
        ElapsedText = "0:00:00";
        VoltageText = CurrentText = PowerText = ChargeText = EnergyText = ModeText = SetpointText = "-";
        StageText = "";
    }

    private static string StateName(ChargeState s) => s switch
    {
        ChargeState.Precharge => "Pre-charge",
        ChargeState.ConstantCurrent => "Constant current",
        ChargeState.ConstantVoltage => "Constant voltage",
        ChargeState.Float => "Charged, floating",
        _ => s.ToString(),
    };

    private static string FormatElapsed(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
    private static string FormatCharge(double ah) => ah < 1 ? $"{ah * 1000:0.0} mAh" : $"{ah:0.000} Ah";

    [RelayCommand]
    private async Task StopAsync()
    {
        if (_session is { } s) await s.RequestStopAsync();
    }

    [RelayCommand]
    private void NewCharge()
    {
        HasResult = false;
        ClearBanner();
        Chart.Clear();
        ChartInvalidated?.Invoke(this, EventArgs.Empty);
        ResetReadouts();
    }

    [RelayCommand]
    private void DismissBanner() => IsBannerOpen = false;

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            if (_logFilePath is not null && File.Exists(_logFilePath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_logFilePath}\"") { UseShellExecute = true });
            else
            {
                Directory.CreateDirectory(_settings.Current.EffectiveLogFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.Current.EffectiveLogFolder}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not open the log folder"); }
    }

    [RelayCommand]
    private void OpenLog()
    {
        try
        {
            if (_logFilePath is not null && File.Exists(_logFilePath))
                Process.Start(new ProcessStartInfo(_logFilePath) { UseShellExecute = true });
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not open the log file"); }
    }

    /// <summary>Called when the window is closing. Returns true when the window may close.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!IsSessionActive) return true;
        var stop = await _dialogs.ConfirmAsync("Charging in progress",
            "A charge is running. Closing the application will stop charging and switch the power supply output off.",
            "Stop charging and exit", "Cancel");
        if (!stop) return false;
        if (_session is { } s) await s.RequestStopAsync();
        if (_runTask is { } t) await t;
        return true;
    }

    private void PersistInputs()
    {
        var b = _settings.Current.Battery;
        b.Technology = Technology;
        b.NominalVoltage = NominalVoltage;
        b.CapacityInMah = CapacityInMah;
        b.CapacityAh = CapacityAh;
        b.CellsOverride = AutoCells ? null : Cells;
        b.ChargeRateC = ChargeRateC;
        _settings.Save();
    }

    /// <summary>Re-reads settings that can change on the Settings page (developer mode shows the simulated supply).</summary>
    public void OnSettingsChanged()
    {
        OnPropertyChanged(nameof(DeveloperMode));
        var sim = Supplies.FirstOrDefault(s => s.IsSimulator);
        if (DeveloperMode && sim is null)
            Supplies.Insert(0, SimulatorItem());
        else if (!DeveloperMode && sim is not null && !(IsConnected && SelectedSupply == sim))
            Supplies.Remove(sim);
    }

    private static SupplyItem SimulatorItem() =>
        new("Simulated power supply (demo, 60x speed)", InstrumentService.SimulatorResource, true, true);
}
