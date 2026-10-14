using BatteryCharger.Core.Batteries;

namespace BatteryCharger.Core.Settings;

/// <summary>Last battery inputs, restored on start.</summary>
public sealed class BatteryInputs
{
    public BatteryTechnology Technology { get; set; } = BatteryTechnology.LiIon;
    public double NominalVoltage { get; set; } = 3.6;
    public double CapacityAh { get; set; } = 2.0;
    public bool CapacityInMah { get; set; }
    public int? CellsOverride { get; set; }
    public double? ChargeRateC { get; set; }
    /// <summary>Charge current the user chose; null means the suggested value.</summary>
    public double? ChargeCurrentAmps { get; set; }
}

/// <summary>Persisted settings (spec section 10). Unknown JSON fields are ignored, missing fields keep their defaults.</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public string? LastResource { get; set; }
    public List<string> LanInstruments { get; set; } = [];
    public BatteryInputs Battery { get; set; } = new();
    /// <summary>Last inputs per power supply (key: serial number, or the resource string when it is unknown).</summary>
    public Dictionary<string, BatteryInputs> SupplyInputs { get; set; } = [];
    /// <summary>Advanced overrides, only present when the user explicitly saved them. Keyed by option id.</summary>
    public Dictionary<string, double>? AdvancedOverrides { get; set; }
    public double PollIntervalSeconds { get; set; } = 1.0;
    /// <summary>"," or ";".</summary>
    public string CsvSeparator { get; set; } = ",";
    public string? LogFolder { get; set; }
    public bool RestorePreviousSetpoints { get; set; } = true;
    public bool LogScpiTraffic { get; set; }
    public int VisaTimeoutMs { get; set; } = 5000;
    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    /// <summary>Shows the SCPI console and the simulated supply.</summary>
    public bool DeveloperMode { get; set; }

    public static string DefaultLogFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BatteryCharger", "Logs");

    public string EffectiveLogFolder => string.IsNullOrWhiteSpace(LogFolder) ? DefaultLogFolder : LogFolder;
    public char CsvSeparatorChar => CsvSeparator == ";" ? ';' : ',';

    /// <summary>Clamps every value to its legal range (settings files may be hand-edited).</summary>
    public void Normalize()
    {
        PollIntervalSeconds = double.IsNaN(PollIntervalSeconds) ? 1.0 : Math.Clamp(PollIntervalSeconds, 0.5, 10);
        VisaTimeoutMs = Math.Clamp(VisaTimeoutMs, 500, 60_000);
        if (CsvSeparator != ";") CsvSeparator = ",";
        if (Theme is not ("Light" or "Dark")) Theme = "System";
        LanInstruments = (LanInstruments ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Battery ??= new BatteryInputs();
        SupplyInputs = SupplyInputs is null ? [] : SupplyInputs.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (!(Battery.NominalVoltage > 0)) Battery.NominalVoltage = 3.6;
        if (!(Battery.CapacityAh > 0)) Battery.CapacityAh = 2.0;
        if (!Enum.IsDefined(Battery.Technology)) Battery.Technology = BatteryTechnology.LiIon;
    }
}
