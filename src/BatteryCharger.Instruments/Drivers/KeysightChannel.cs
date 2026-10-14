using System.Globalization;
using BatteryCharger.Core.Instruments;

namespace BatteryCharger.Instruments.Drivers;

/// <summary>
/// Shared channel logic for Keysight/Agilent supplies. Dialect differences (channel addressing, voltage ranges) live in
/// the abstract members. Never sends *RST and touches only its own channel.
/// </summary>
public abstract class KeysightChannel : IPowerSupplyChannel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private ChannelLimits? _limits;

    protected KeysightChannel(ScpiLink link, int index)
    {
        Link = link;
        Index = index;
    }

    protected ScpiLink Link { get; }
    public int Index { get; }
    public string? OutputWarning { get; protected set; }
    public virtual bool HasVoltageRanges => false;

    /// <summary>Last known limits used by <see cref="DisplayName"/>; null until read.</summary>
    protected ChannelLimits? KnownLimits { get => _limits; set => _limits = value; }
    public string DisplayName => _limits is null ? $"CH{Index}" : $"CH{Index} ({_limits.MaxVolts:0.#} V / {_limits.MaxAmps:0.###} A)";

    /// <summary>Command that sets <paramref name="header"/> (e.g. "VOLT") to <paramref name="arg"/>.</summary>
    protected abstract string Set(string header, string arg);
    /// <summary>Query for <paramref name="header"/> (without '?'), optional argument such as MAX.</summary>
    protected abstract string Get(string header, string? arg = null);
    /// <summary>Command that clears a latched protection trip (OVP) so the output can be enabled again.</summary>
    protected abstract string ProtectionClearCommand { get; }
    /// <summary>Selects this channel on instruments that need it; runs at the start of every exclusive sequence.</summary>
    protected virtual Task PrepareAsync(ScpiIo io) => Task.CompletedTask;
    /// <summary>Selects the smallest voltage range covering <paramref name="requiredVolts"/>. No-op without ranges.</summary>
    protected virtual Task SelectRangeAsync(ScpiIo io, double requiredVolts) => Task.CompletedTask;
    /// <summary>Reads the current range name for snapshots; null when the model has none.</summary>
    protected virtual Task<string?> GetRangeAsync(ScpiIo io) => Task.FromResult<string?>(null);
    protected virtual Task RestoreRangeAsync(ScpiIo io, string range) => Task.CompletedTask;

    protected static double ParseDouble(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, Inv, out var d)
            ? d
            : throw new InstrumentException($"Unexpected instrument response: '{s}'");

    protected static bool ParseBool(string s)
    {
        s = s.Trim();
        return s is "1" or "+1" || s.Equals("ON", StringComparison.OrdinalIgnoreCase);
    }

    protected static string Num(double v) => v.ToString("0.#####", Inv);

    private async Task<ChannelLimits> ReadLimitsAsync(ScpiIo io)
    {
        var maxV = ParseDouble(await io.QueryAsync(Get("VOLT", "MAX")));
        var maxI = ParseDouble(await io.QueryAsync(Get("CURR", "MAX")));
        var minV = ParseDouble(await io.QueryAsync(Get("VOLT", "MIN")));
        var minI = ParseDouble(await io.QueryAsync(Get("CURR", "MIN")));
        return new ChannelLimits(maxV, maxI, minV, minI);
    }

    public Task<ChannelLimits> GetLimitsAsync(double requiredVolts, CancellationToken ct) =>
        Link.RunAsync(async io =>
        {
            await PrepareAsync(io);
            await SelectRangeAsync(io, requiredVolts);
            var l = await ReadLimitsAsync(io);
            _limits = l;
            return l;
        }, ct);

    public Task<ChannelSnapshot> ReadStateAsync(CancellationToken ct) =>
        Link.RunAsync(async io =>
        {
            await PrepareAsync(io);
            var on = ParseBool(await io.QueryAsync(Get("OUTP")));
            var volts = ParseDouble(await io.QueryAsync(Get("VOLT")));
            var amps = ParseDouble(await io.QueryAsync(Get("CURR")));
            var ovp = ParseDouble(await io.QueryAsync(Get("VOLT:PROT")));             // VERIFY
            var ovpOn = ParseBool(await io.QueryAsync(Get("VOLT:PROT:STAT")));        // VERIFY
            var ocpOn = ParseBool(await io.QueryAsync(Get("CURR:PROT:STAT")));        // VERIFY
            var range = await GetRangeAsync(io);
            return new ChannelSnapshot(on, volts, amps, ovp, ovpOn, ocpOn, null, range);
        }, ct);

    public Task ApplySafeStateAsync(CancellationToken ct) =>
        Link.RunAsync(async io =>
        {
            await PrepareAsync(io);
            await io.WriteAsync(Set("OUTP", "OFF"));
            await io.WriteAsync(Set("VOLT", "MIN"));
            await io.WriteAsync(Set("CURR", "MIN"));
        }, ct);

    public Task ConfigureAsync(ChargeSetpoint sp, CancellationToken ct) =>
        Link.RunAsync(async io =>
        {
            await PrepareAsync(io);
            await io.WriteAsync(Set("OUTP", "OFF"));
            await io.WriteAsync("*CLS");
            await io.WriteAsync(ProtectionClearCommand);                    // VERIFY
            await DrainAsync(io);                                            // an unsupported clear command must not abort the start
            await SelectRangeAsync(io, sp.Volts);
            await io.WriteAsync(Set("VOLT", Num(sp.Volts)));
            await io.WriteAsync(Set("CURR", Num(sp.AmpsLimit)));
            await io.WriteAsync(Set("VOLT:PROT", Num(sp.OvpVolts)));        // VERIFY
            await io.WriteAsync(Set("VOLT:PROT:STAT", "ON"));               // VERIFY
            // OCP would trip the output whenever the supply enters CC mode, which is normal for a charger.
            await io.WriteAsync(Set("CURR:PROT:STAT", "OFF"));              // VERIFY
            await io.QueryAsync("*OPC?");

            var errors = await DrainAsync(io);
            if (errors.Count > 0)
                throw new InstrumentException("The power supply rejected the configuration: " + string.Join("; ", errors));

            var v = ParseDouble(await io.QueryAsync(Get("VOLT")));
            var i = ParseDouble(await io.QueryAsync(Get("CURR")));
            var o = ParseDouble(await io.QueryAsync(Get("VOLT:PROT")));
            if (!Close(v, sp.Volts)) throw new InstrumentException($"Voltage readback {v:0.000} V differs from the requested {sp.Volts:0.000} V.");
            if (!Close(i, sp.AmpsLimit)) throw new InstrumentException($"Current limit readback {i:0.000} A differs from the requested {sp.AmpsLimit:0.000} A.");
            if (!Close(o, sp.OvpVolts)) throw new InstrumentException($"OVP readback {o:0.000} V differs from the requested {sp.OvpVolts:0.000} V.");
        }, ct);

    /// <summary>Within 1 % or 5 mV / 5 mA (spec 5.3).</summary>
    private static bool Close(double actual, double expected) =>
        Math.Abs(actual - expected) <= Math.Max(0.01 * Math.Abs(expected), 0.005);

    public Task SetCurrentLimitAsync(double amps, CancellationToken ct) =>
        Link.RunAsync(async io => { await PrepareAsync(io); await io.WriteAsync(Set("CURR", Num(amps))); }, ct);

    public Task SetVoltageAsync(double volts, CancellationToken ct) =>
        Link.RunAsync(async io => { await PrepareAsync(io); await io.WriteAsync(Set("VOLT", Num(volts))); }, ct);

    public Task SetOutputAsync(bool on, CancellationToken ct) =>
        Link.RunAsync(async io => { await PrepareAsync(io); await io.WriteAsync(Set("OUTP", on ? "ON" : "OFF")); }, ct);

    public Task<Measurement> MeasureAsync(CancellationToken ct) =>
        Link.RunAsync(async io =>
        {
            await PrepareAsync(io);
            var v = ParseDouble(await io.QueryAsync(Get("MEAS:VOLT")));
            var i = ParseDouble(await io.QueryAsync(Get("MEAS:CURR")));
            return new Measurement(DateTimeOffset.Now, v, i);
        }, ct);

    public Task<bool> GetOutputAsync(CancellationToken ct) =>
        Link.RunAsync(async io => { await PrepareAsync(io); return ParseBool(await io.QueryAsync(Get("OUTP"))); }, ct);

    public Task<IReadOnlyList<string>> DrainErrorsAsync(CancellationToken ct) =>
        Link.RunAsync(io => DrainAsync(io), ct);

    internal static async Task<IReadOnlyList<string>> DrainAsync(ScpiIo io)
    {
        var errors = new List<string>();
        for (var n = 0; n < 20; n++)
        {
            var r = (await io.QueryAsync("SYST:ERR?")).Trim();
            var code = r.Split(',')[0].Trim();
            if (code is "0" or "+0" or "-0" || code.TrimStart('+', '-').Trim('0').Length == 0) break;
            errors.Add(r);
        }
        return errors;
    }

    public Task RestoreAsync(ChannelSnapshot s, CancellationToken ct) =>
        Link.RunAsync(async io =>
        {
            await PrepareAsync(io);
            await io.WriteAsync(Set("OUTP", "OFF"));
            if (s.VoltageRange is not null) await RestoreRangeAsync(io, s.VoltageRange);
            await io.WriteAsync(Set("VOLT", Num(s.Volts)));
            await io.WriteAsync(Set("CURR", Num(s.AmpsLimit)));
            await io.WriteAsync(Set("VOLT:PROT", Num(s.OvpVolts)));
            await io.WriteAsync(Set("VOLT:PROT:STAT", s.OvpEnabled ? "ON" : "OFF"));
            await io.WriteAsync(Set("CURR:PROT:STAT", s.OcpEnabled ? "ON" : "OFF"));
        }, ct);
}
