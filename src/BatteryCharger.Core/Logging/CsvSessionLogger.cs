using System.Diagnostics;
using System.Globalization;
using System.Text;
using BatteryCharger.Core.Batteries;
using BatteryCharger.Core.Instruments;
using BatteryCharger.Core.Session;

namespace BatteryCharger.Core.Logging;

/// <summary>
/// Writes the session CSV (spec section 8.1): UTF-8 with BOM, invariant culture, '#' header and footer comments.
/// Flushes on every event row and at least every 5 s.
/// </summary>
public sealed class CsvSessionLogger : ISessionLogger
{
    private const string Columns =
        "timestamp_local,elapsed_s,state,stage,voltage_v,current_a,power_w,charge_ah,energy_wh,voltage_set_v,current_limit_a,psu_mode,event";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly string _folder;
    private readonly char _separator;
    private StreamWriter? _writer;
    private readonly Stopwatch _sinceFlush = new();

    public CsvSessionLogger(string folder, char separator = ',')
    {
        _folder = folder;
        _separator = separator;
    }

    public string? FilePath { get; private set; }

    /// <summary>Builds the log file name for a session start.</summary>
    public static string BuildFileName(DateTimeOffset start, string model, BatteryDescriptor b)
    {
        var tech = CellChemistryInfo.For(b.Technology).Technology.ToString();
        var name = string.Create(Inv, $"{start:yyyyMMdd_HHmmss}_{model}_{tech}_{b.NominalVoltage:0.##}V_{b.CapacityAh:0.###}Ah.csv");
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Replace(' ', '_');
    }

    public Task OpenAsync(SessionHeader h, CancellationToken ct)
    {
        Directory.CreateDirectory(_folder);
        var model = InstrumentIdentity.Parse(h.Instrument.Idn).Model;
        if (string.IsNullOrWhiteSpace(model)) model = "PSU";
        var path = Path.Combine(_folder, BuildFileName(h.Start, model, h.Profile.Battery));
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(true));
        FilePath = path;

        var b = h.Profile.Battery;
        var o = h.Profile.Options;
        var w = _writer;
        w.WriteLine(string.Create(Inv, $"# BatteryCharger {AppVersion.Display}"));
        w.WriteLine(string.Create(Inv, $"# start: {h.Start:yyyy-MM-dd HH:mm:ss zzz} (UTC {h.Start.UtcDateTime:yyyy-MM-dd HH:mm:ss})"));
        w.WriteLine(string.Create(Inv, $"# instrument: {h.Instrument.Idn}"));
        w.WriteLine(string.Create(Inv, $"# resource: {h.Instrument.Resource}"));
        w.WriteLine(string.Create(Inv, $"# channel: {h.Instrument.Channel}"));
        w.WriteLine(string.Create(Inv, $"# battery: technology={b.Technology} nominal_v={b.NominalVoltage:0.###} capacity_ah={b.CapacityAh:0.####} charge_rate_c={b.ChargeRateC:0.###}"));
        w.WriteLine(string.Create(Inv, $"# cells_in_series: {b.CellsInSeries}"));
        w.WriteLine(string.Create(Inv, $"# profile: target_pack_v={h.Profile.TargetPackVolts:0.###} main_current_a={h.Profile.MainCurrentAmps:0.####} termination_a={h.Profile.TerminationAmps:0.####} ovp_v={h.Profile.OvpVolts:0.###} max_charge_ah={h.Profile.MaxChargeAh:0.###}"));
        w.WriteLine(string.Create(Inv, $"# options: target_v_cell={o.TargetVoltagePerCell:0.###} precharge_below_v_cell={o.PrechargeBelowVoltagePerCell?.ToString("0.###", Inv) ?? "-"} precharge_rate_c={o.PrechargeRateC:0.###} termination_c={o.TerminationCurrentC:0.###} dv_cell_v={o.DvPerCellVolts:0.####} float_v_cell={o.FloatVoltagePerCell?.ToString("0.###", Inv) ?? "-"} max_charge_factor={o.MaxChargeFactor:0.##} ovp_margin_cell={o.OvpMarginPerCell:0.###} cc_timeout_factor={o.CcTimeoutFactor:0.##} cv_timeout_h={o.CvTimeout.TotalHours:0.##} precharge_timeout_min={o.PrechargeTimeout.TotalMinutes:0.#} refuse_below_v_cell={o.RefuseBelowVoltagePerCell:0.###}"));
        var i = 1;
        foreach (var s in h.Profile.Stages) w.WriteLine(string.Create(Inv, $"# stage{i++}: {s.Description}"));
        w.WriteLine(string.Create(Inv, $"# poll_interval_s: {h.PollInterval.TotalSeconds:0.###}"));
        w.WriteLine(Columns.Replace(',', _separator));
        w.Flush();
        _sinceFlush.Restart();
        return Task.CompletedTask;
    }

    public Task WriteSampleAsync(Sample s, CancellationToken ct)
    {
        var w = Writer();
        var sep = _separator;
        w.WriteLine(string.Create(Inv,
            $"{Ts(s.Time)}{sep}{s.Elapsed.TotalSeconds:0.000}{sep}{s.State}{sep}{Esc(s.Stage)}{sep}{s.Volts:0.0000}{sep}{s.Amps:0.00000}{sep}{s.Watts:0.0000}{sep}{s.Ah:0.00000}{sep}{s.Wh:0.0000}{sep}{s.VoltageSet:0.000}{sep}{s.CurrentLimit:0.0000}{sep}{s.Mode}{sep}{Esc(s.Event)}"));
        FlushIfDue(s.Event is not null);
        return Task.CompletedTask;
    }

    public Task WriteEventAsync(DateTimeOffset time, TimeSpan elapsed, ChargeState state, string stage, string text, CancellationToken ct)
    {
        var w = Writer();
        var sep = _separator;
        w.WriteLine(string.Create(Inv,
            $"{Ts(time)}{sep}{elapsed.TotalSeconds:0.000}{sep}{state}{sep}{Esc(stage)}{sep}{sep}{sep}{sep}{sep}{sep}{sep}{sep}{sep}{Esc(text)}"));
        FlushIfDue(true);
        return Task.CompletedTask;
    }

    public Task CloseAsync(SessionResult r, CancellationToken ct)
    {
        if (_writer is null) return Task.CompletedTask;
        var w = _writer;
        w.WriteLine(string.Create(Inv, $"# end_reason: {r.Reason}"));
        w.WriteLine(string.Create(Inv, $"# message: {r.Message.Replace('\r', ' ').Replace('\n', ' ')}"));
        w.WriteLine(string.Create(Inv, $"# duration_s: {r.Duration.TotalSeconds:0.0}"));
        w.WriteLine(string.Create(Inv, $"# charge_ah: {r.AhDelivered:0.00000}"));
        w.WriteLine(string.Create(Inv, $"# energy_wh: {r.WhDelivered:0.0000}"));
        w.WriteLine(string.Create(Inv, $"# final_voltage_v: {r.FinalVoltage:0.0000}"));
        w.Flush();
        w.Dispose();
        _writer = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _writer?.Dispose();
        _writer = null;
        return ValueTask.CompletedTask;
    }

    private StreamWriter Writer() => _writer ?? throw new InvalidOperationException("The session log is not open.");

    private void FlushIfDue(bool force)
    {
        if (force || _sinceFlush.Elapsed >= TimeSpan.FromSeconds(5))
        {
            _writer!.Flush();
            _sinceFlush.Restart();
        }
    }

    private static string Ts(DateTimeOffset t) => t.ToString("yyyy-MM-dd HH:mm:ss.fff", Inv);

    private string Esc(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        return v.IndexOfAny([_separator, '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
