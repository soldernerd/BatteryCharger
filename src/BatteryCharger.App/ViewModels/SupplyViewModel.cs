using System.Collections.ObjectModel;
using System.Diagnostics;
using BatteryCharger.App.Services;
using BatteryCharger.Core.Batteries;
using BatteryCharger.Core.Instruments;
using BatteryCharger.Core.Instruments.Simulation;
using BatteryCharger.Core.Logging;
using BatteryCharger.Core.Profiles;
using BatteryCharger.Core.Session;
using BatteryCharger.Core.Settings;
using BatteryCharger.Instruments.Drivers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace BatteryCharger.App.ViewModels;

/// <summary>Entry of the "number of cells" drop-down.</summary>
public sealed record CellOption(int Count, string Label);

/// <summary>
/// One tab: a single power supply with its own connection, battery inputs, profile, charge session and chart. Tabs share
/// nothing but settings, so several supplies can charge different batteries at the same time.
/// </summary>
public sealed partial class SupplyViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly SleepInhibitor _sleep;
    private readonly InstrumentService _instruments;
    private readonly ILogger<SupplyViewModel> _log;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _uiTimer;

    private bool _loading = true;
    private bool _suppressTouch;
    private bool _currentTouched;
    private bool _connectAttempted;
    private ChannelLimits? _limits;
    private ChargeSession? _session;
    private Task? _runTask;
    private volatile Sample? _latest;
    private EtaEstimator? _eta;
    private object? _etaBox;                 // boxed TimeSpan?, written on the session thread
    private int _lastStageIndex = -1;
    private CsvSessionLogger? _activeLogger;
    private string? _logFilePath;

    public SupplyViewModel(string resource, string model, string serial, bool isSimulator, string firmware,
        SettingsService settings, VisaService visa, DialogService dialogs, SleepInhibitor sleep, ILoggerFactory loggers)
    {
        Resource = resource;
        Model = model;
        Serial = serial;
        Firmware = firmware;
        IsSimulator = isSimulator;
        _settings = settings;
        _dialogs = dialogs;
        _sleep = sleep;
        _instruments = new InstrumentService(visa, settings, loggers.CreateLogger<InstrumentService>());
        _log = loggers.CreateLogger<SupplyViewModel>();
        _navTitle = model;
        _uiTimer = _dispatcher.CreateTimer();
        _uiTimer.Interval = TimeSpan.FromMilliseconds(250);   // chart and readouts update at <= 4 Hz
        _uiTimer.Tick += (_, _) => RefreshReadouts();

        LoadInputs();
        _loading = false;
        UpdateCellOptions();
        UpdateCurrentRange();
        Recompute();
    }

    public string Resource { get; }
    public string Model { get; }
    public string Serial { get; }
    public string Firmware { get; }
    public bool IsSimulator { get; }
    /// <summary>Key of the saved inputs: the serial number survives a change of the USB port, the resource does not.</summary>
    public string Key => string.IsNullOrEmpty(Serial) ? Resource : Serial;
    public string IdentityText => IsSimulator ? "Simulated power supply (demo, 60x speed)" : $"{Model}   S/N {Serial}   firmware {Firmware}";

    /// <summary>Label in the navigation pane; the registry adds the serial number when two supplies share a model.</summary>
    [ObservableProperty] private string _navTitle;

    // ---------------------------------------------------------------- connection

    public ObservableCollection<ChannelItem> Channels { get; } = [];
    [ObservableProperty] private ChannelItem? _selectedChannel;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanEditInputs))] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string? _outputWarning;
    public bool HasMultipleChannels => Channels.Count > 1;
    public bool HasOutputWarning => !string.IsNullOrEmpty(OutputWarning);
    public bool CanEditInputs => IsConnected && !IsBusy;
    public bool IsDisconnectedIdle => !IsConnected && !IsBusy;

    /// <summary>Connects on first use of the tab. Later calls do nothing; use <see cref="ConnectCommand"/> to retry.</summary>
    public async Task EnsureConnectedAsync()
    {
        if (_connectAttempted) return;
        _connectAttempted = true;
        await ConnectAsync();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnected || IsBusy) return;
        IsBusy = true;
        BusyText = "Connecting...";
        OnPropertyChanged(nameof(IsDisconnectedIdle));
        ClearBanner();
        try
        {
            var psu = await _instruments.ConnectAsync(Resource, IsSimulator ? BuildSimulatedBattery() : null, CancellationToken.None);
            Channels.Clear();
            foreach (var ch in psu.Channels) Channels.Add(new ChannelItem(ch.DisplayName, ch));
            OnPropertyChanged(nameof(HasMultipleChannels));
            IsConnected = true;
            SelectedChannel = Channels.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Connect failed");
            ShowBanner($"Could not connect to {Resource}: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsDisconnectedIdle));
            OnPropertyChanged(nameof(CanEditInputs));
            ConnectCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Raw SCPI link of the connected instrument (SCPI console), null for the simulator or when not connected.</summary>
    public ScpiLink? Link => _instruments.Link;

    partial void OnSelectedChannelChanged(ChannelItem? value)
    {
        OutputWarning = value?.Channel.OutputWarning;
        OnPropertyChanged(nameof(HasOutputWarning));
        _limits = null;
        UpdateCellOptions();
        UpdateCurrentRange();
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
                UpdateCellOptions();
                UpdateCurrentRange();
                Recompute();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read channel limits");
            ShowBanner($"Could not read the channel limits: {ex.Message}", InfoBarSeverity.Warning);
        }
    }

    /// <summary>Closes the instrument session (application exit).</summary>
    public Task DisconnectAsync() => _instruments.DisconnectAsync();

    private SimulatedBattery BuildSimulatedBattery()
    {
        var chem = Chemistry;
        var cells = Cells;
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
    public ObservableCollection<CellOption> CellOptions { get; } = [];

    /// <summary>-1 until the user has picked a technology (the first step of every charge).</summary>
    [ObservableProperty] private int _technologyIndex = -1;
    [ObservableProperty] private CellOption? _selectedCell;
    [ObservableProperty] private double _capacityValue;
    [ObservableProperty] private bool _capacityInMah;
    [ObservableProperty] private double _currentAmps;
    [ObservableProperty] private double _currentMin = 0.01;
    [ObservableProperty] private double _currentMax = 1;
    [ObservableProperty] private double _suggestedAmps;

    public bool HasTechnology => TechnologyIndex >= 0;
    public BatteryTechnology Technology => (BatteryTechnology)Math.Clamp(TechnologyIndex, 0, Technologies.Count - 1);
    public CellChemistryInfo Chemistry => CellChemistryInfo.For(Technology);
    public int Cells => SelectedCell?.Count ?? 1;
    public double CapacityAh => double.IsNaN(CapacityValue) ? 0 : CapacityInMah ? CapacityValue / 1000.0 : CapacityValue;
    public bool HasCapacity => CapacityAh > 0;
    public bool CurrentIsCustom => _currentTouched && Math.Abs(CurrentAmps - SuggestedAmps) > 1e-6;
    public string CapacityUnit => CapacityInMah ? "mAh" : "Ah";

    public string CurrentHint
    {
        get
        {
            if (!HasCapacity) return "Enter the capacity to get a suggestion.";
            var rate = CurrentAmps / CapacityAh;
            return $"{rate:0.##} C. Suggested {SuggestedAmps:0.###} A ({Chemistry.DefaultRateC:0.##} C), maximum {CurrentMax:0.###} A for this battery and supply.";
        }
    }

    /// <summary>Nominal and full-charge voltage of the chosen cell count, as a check against the battery label.</summary>
    public string PackText
    {
        get
        {
            if (!HasTechnology) return "";
            var c = Chemistry;
            var n = Cells;
            var nominal = n * c.CellNominalV;
            var full = n * c.TargetV;
            return $"{n} x {c.CellNominalV:0.0#} V = {nominal:0.0#} V nominal, charged up to {full:0.00} V" +
                   (_limits is { } l && SelectedChannel is { Channel.HasVoltageRanges: false } ? $" (supply maximum {l.MaxVolts:0.#} V)" : "");
        }
    }

    partial void OnTechnologyIndexChanged(int value)
    {
        OnPropertyChanged(nameof(HasTechnology));
        OnPropertyChanged(nameof(Technology));
        OnPropertyChanged(nameof(Chemistry));
        if (_loading) return;
        _currentTouched = false;
        UpdateCellOptions(keepSelection: false);
        RebuildAdvancedFields(loadSaved: true);
        UpdateCurrentRange();
        Recompute();
    }

    partial void OnSelectedCellChanged(CellOption? value)
    {
        if (_loading) return;
        OnPropertyChanged(nameof(Cells));
        Recompute();
    }

    partial void OnCapacityValueChanged(double value)
    {
        OnPropertyChanged(nameof(HasCapacity));
        if (_loading) return;
        UpdateCurrentRange();
        Recompute();
    }

    partial void OnCapacityInMahChanged(bool value)
    {
        OnPropertyChanged(nameof(CapacityUnit));
        if (_loading || double.IsNaN(CapacityValue)) return;
        CapacityValue = value ? CapacityValue * 1000 : CapacityValue / 1000;
    }

    partial void OnCurrentAmpsChanged(double value)
    {
        if (_loading) return;
        if (!_suppressTouch && !double.IsNaN(value)) _currentTouched = true;
        OnPropertyChanged(nameof(CurrentIsCustom));
        OnPropertyChanged(nameof(CurrentHint));
        Recompute();
    }

    [RelayCommand]
    private void UseSuggestedCurrent()
    {
        _currentTouched = false;
        UpdateCurrentRange();
        Recompute();
    }

    /// <summary>Rebuilds the cell drop-down for the chemistry and the supply's voltage rating.</summary>
    private void UpdateCellOptions(bool keepSelection = true)
    {
        if (!HasTechnology) { CellOptions.Clear(); SelectedCell = null; return; }
        var c = Chemistry;
        var max = c.IsNickel ? 12 : c.Technology == BatteryTechnology.LeadAcid ? 12 : 8;
        if (_limits is { } l && SelectedChannel is { Channel.HasVoltageRanges: false })
            max = Math.Min(max, Math.Max(1, (int)Math.Floor(l.MaxVolts / c.TargetV + 1e-9)));
        var keep = keepSelection ? SelectedCell?.Count : null;
        var wanted = keep ?? _pendingCells ?? c.PresetVoltages.Select(v => CellChemistryInfo.DeriveCells(c.Technology, v)).FirstOrDefault(1);
        _pendingCells = null;
        CellOptions.Clear();
        for (var n = 1; n <= max; n++) CellOptions.Add(new CellOption(n, n == 1 ? "1 cell" : $"{n} cells"));
        SelectedCell = CellOptions.FirstOrDefault(o => o.Count == wanted) ?? CellOptions.First();
    }

    private int? _pendingCells;

    /// <summary>Recomputes the allowed current range and the suggestion; keeps a user-chosen current while it stays legal.</summary>
    private void UpdateCurrentRange()
    {
        var chem = Chemistry;
        var cap = CapacityAh;
        var max = cap > 0 ? chem.MaxRateC * cap : 1.0;
        var min = 0.01;
        if (_limits is { } l)
        {
            max = Math.Min(max, l.MaxAmps);
            min = Math.Max(min, l.MinAmps);
        }
        if (max < min) max = min;
        var suggested = cap > 0 ? Math.Clamp(Math.Round(chem.DefaultRateC * cap, 3), min, max) : min;

        _suppressTouch = true;
        try
        {
            CurrentMin = min;
            CurrentMax = max;
            SuggestedAmps = suggested;
            CurrentAmps = _currentTouched ? Math.Clamp(double.IsNaN(CurrentAmps) ? suggested : CurrentAmps, min, max) : suggested;
        }
        finally { _suppressTouch = false; }
        OnPropertyChanged(nameof(CurrentIsCustom));
        OnPropertyChanged(nameof(CurrentHint));
    }

    private void LoadInputs()
    {
        if (!_settings.Current.SupplyInputs.TryGetValue(Key, out var saved)) return;
        TechnologyIndex = (int)saved.Technology;
        CapacityInMah = saved.CapacityInMah;
        CapacityValue = saved.CapacityInMah ? saved.CapacityAh * 1000 : saved.CapacityAh;
        _pendingCells = saved.CellsOverride ?? CellChemistryInfo.DeriveCells(saved.Technology, saved.NominalVoltage);
        if (saved.ChargeCurrentAmps is { } a && a > 0)
        {
            _currentTouched = true;
            CurrentAmps = a;
        }
        RebuildAdvancedFields(loadSaved: true);
    }

    private void PersistInputs()
    {
        _settings.Current.SupplyInputs[Key] = new BatteryInputs
        {
            Technology = Technology,
            NominalVoltage = Cells * Chemistry.CellNominalV,
            CapacityAh = CapacityAh,
            CapacityInMah = CapacityInMah,
            CellsOverride = Cells,
            ChargeCurrentAmps = _currentTouched ? CurrentAmps : null,
        };
        _settings.Save();
    }

    private void RebuildAdvancedFields(bool loadSaved)
    {
        foreach (var f in AdvancedFields) f.ValueEdited -= OnAdvancedEdited;
        AdvancedFields.Clear();
        if (!HasTechnology) return;
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

    private BatteryDescriptor BuildBattery()
    {
        var cap = CapacityAh;
        var amps = double.IsNaN(CurrentAmps) ? 0 : CurrentAmps;
        return new BatteryDescriptor(Technology, Cells * Chemistry.CellNominalV, cap, Cells, cap > 0 ? amps / cap : 0);
    }

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
        OnPropertyChanged(nameof(PackText));
        if (!HasTechnology)
        {
            Profile = null;
            Fill(ProfileLines, []);
            Fill(ProfileWarnings, []);
            Fill(ProfileErrors, []);
            OnPropertyChanged(nameof(HasWarnings));
            OnPropertyChanged(nameof(HasErrors));
            StartCommand.NotifyCanExecuteChanged();
            return;
        }
        // Range-capable supplies only switch range at start, so voltage limits are not checked against the current range here.
        var lim = IsConnected && SelectedChannel is { Channel.HasVoltageRanges: false } ? _limits : null;
        var p = ChargeProfileFactory.Create(BuildBattery(), BuildOptions(), lim);
        Profile = p;
        // Without a capacity the profile errors (capacity, rate) only repeat the same missing input.
        var errors = HasCapacity ? p.Errors.ToList() : ["Enter the battery capacity."];
        Fill(ProfileLines, p.IsValid ? p.SummaryLines : []);
        Fill(ProfileWarnings, p.Warnings);
        Fill(ProfileErrors, errors);
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
    [ObservableProperty] private string _etaText = "-";
    [ObservableProperty] private string _etaDetail = "";
    [ObservableProperty] private string _logFileName = "";
    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultDetails = "";

    partial void OnIsSessionActiveChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        NavTitle = value ? $"{BaseNavTitle}  (charging)" : BaseNavTitle;
    }

    /// <summary>Navigation label without the charging marker; set by the registry.</summary>
    public string BaseNavTitle
    {
        get => _baseNavTitle ?? Model;
        set { _baseNavTitle = value; NavTitle = IsSessionActive ? $"{value}  (charging)" : value; }
    }
    private string? _baseNavTitle;

    private bool CanStart() =>
        IsConnected && !IsBusy && !IsSessionActive && HasTechnology && HasCapacity && SelectedChannel is not null && Profile is { IsValid: true };

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
            var lowVoltageStart = false;
            if (pre is { Refused: true, LowVoltageOverridable: true } && await _dialogs.ConfirmAsync("Battery voltage very low",
                    pre.Refusal + "\n\nThe battery may only be deeply discharged. You can try a gentle pre-charge at a low current; "
                    + "it is stopped automatically if the voltage does not recover. Only do this if you know the battery is intact.",
                    "Try charging anyway", "Cancel"))
            {
                profile = profile.WithRecoveryStage();
                Profile = profile;
                pre = PreflightChecker.Evaluate(profile, pre.BatteryVolts, pre.Snapshot, allowLowVoltage: true);
                lowVoltageStart = true;
            }
            if (pre.Refused)
            {
                await _dialogs.ShowMessageAsync("Charging refused", pre.Refusal + (pre.Hints.Count > 0 ? "\n\n" + string.Join("\n", pre.Hints) : ""));
                return;
            }

            var supplyText = $"{Model} {channel.DisplayName}";
            if (!await _dialogs.ConfirmChargeAsync(profile, pre, supplyText, channel.OutputWarning)) return;

            PersistInputs();
            StartSession(channel, profile, lowVoltageStart);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Start failed");
            ShowBanner($"Could not start charging: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanEditInputs));
        }
    }

    private void StartSession(IPowerSupplyChannel channel, ChargeProfile profile, bool allowLowVoltageStart)
    {
        var psu = _instruments.Current!;
        var s = _settings.Current;
        var simulated = psu is FakePowerSupply;
        ISessionClock clock = simulated ? new ScaledClock(InstrumentService.SimulatorTimeScale) : new StopwatchClock();
        var logger = new CsvSessionLogger(s.EffectiveLogFolder, s.CsvSeparatorChar);
        var session = new ChargeSession(channel, profile.Battery, profile, logger, new SessionOptions
        {
            PollInterval = TimeSpan.FromSeconds(s.PollIntervalSeconds),
            RestorePreviousSetpoints = s.RestorePreviousSetpoints,
            UserConfirmed = true,
            AllowLowVoltageStart = allowLowVoltageStart,
            Instrument = new InstrumentInfo(psu.Identity.ToString(), psu.Resource, channel.DisplayName),
            Clock = clock,
        });

        Chart.Clear();
        _latest = null;
        _etaBox = null;
        _eta = new EtaEstimator(profile);
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
        _log.LogInformation("Charge started on {Supply}: {Tech} {Cells}S {Ah} Ah", Model, profile.Battery.Technology, profile.Battery.CellsInSeries, profile.Battery.CapacityAh);
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
        _log.LogInformation("Charge ended on {Supply}: {Reason} - {Message}", Model, result.Reason, result.Message);
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
        EtaText = r.Reason is EndReason.Completed or EndReason.CompletedWithWarning ? "Done" : "-";
        EtaDetail = "";
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
            _etaBox = new EtaBox(_eta?.Update(s));
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

    private sealed record EtaBox(TimeSpan? Remaining);

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
        if (IsSessionActive && _etaBox is EtaBox box)
        {
            if (box.Remaining is { } rem)
            {
                if (rem == TimeSpan.Zero) { EtaText = "Charged"; EtaDetail = ""; }
                else
                {
                    EtaText = "in " + EtaEstimator.Format(rem);
                    EtaDetail = rem >= EtaEstimator.Cap ? "" : $"about {DateTime.Now + rem:HH:mm}, up to (assumes an empty battery)";
                }
            }
            else { EtaText = "calculating..."; EtaDetail = ""; }
        }
        if (_activeLogger?.FilePath is { } path) LogFileName = Path.GetFileName(path);   // known once the session opened the log
        ChartInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void ResetReadouts()
    {
        ElapsedText = "0:00:00";
        VoltageText = CurrentText = PowerText = ChargeText = EnergyText = ModeText = SetpointText = EtaText = "-";
        EtaDetail = "";
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

    /// <summary>Stops a running charge (application exit) and waits until the output is off.</summary>
    public async Task StopForShutdownAsync()
    {
        if (_session is { } s) await s.RequestStopAsync();
        if (_runTask is { } t) await t;
    }
}
