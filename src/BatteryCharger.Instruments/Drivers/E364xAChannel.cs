namespace BatteryCharger.Instruments.Drivers;

/// <summary>
/// E3640A-E3649A dialect: no channel list; dual models select the output with <c>INST:SEL OUTn</c>;
/// models with voltage ranges use <c>VOLT:RANG</c> (changing it resets the setpoints, so it is done before any setpoint).
/// </summary>
public sealed class E364xAChannel : KeysightChannel
{
    private readonly bool _dual;
    private readonly IReadOnlyList<string> _ranges;

    public E364xAChannel(ScpiLink link, int index, bool dual, IReadOnlyList<string> ranges) : base(link, index)
    {
        _dual = dual;
        _ranges = ranges;
        // VERIFY: whether OUTP switches both outputs on dual models. Assume it does until verified.
        if (dual) OutputWarning = "On this dual-output supply the output switch may energise both outputs. Check the settings of the other output before charging.";
    }

    public override bool HasVoltageRanges => _ranges.Count > 0;

    protected override string Set(string header, string arg) => $"{header} {arg}";

    protected override string ProtectionClearCommand => "OUTP:PROT:CLE";

    protected override string Get(string header, string? arg = null) =>
        arg is null ? $"{header}?" : $"{header}? {arg}";

    protected override Task PrepareAsync(ScpiIo io) =>
        _dual ? io.WriteAsync($"INST:SEL OUT{Index}") : Task.CompletedTask;       // VERIFY

    protected override async Task<string?> GetRangeAsync(ScpiIo io)
    {
        if (_ranges.Count == 0) return null;
        return (await io.QueryAsync("VOLT:RANG?")).Trim();                         // VERIFY
    }

    protected override Task RestoreRangeAsync(ScpiIo io, string range) => io.WriteAsync($"VOLT:RANG {range}");

    protected override async Task SelectRangeAsync(ScpiIo io, double requiredVolts)
    {
        if (_ranges.Count == 0 || requiredVolts <= 0) return;   // requiredVolts <= 0: read ratings only, never change the user range
        var current = (await io.QueryAsync("VOLT:RANG?")).Trim();
        foreach (var range in _ranges)
        {
            // Only switch when needed: a range change resets the setpoints.
            if (!string.Equals(current, range, StringComparison.OrdinalIgnoreCase))
                await io.WriteAsync($"VOLT:RANG {range}");
            current = range;
            var max = ParseDouble(await io.QueryAsync("VOLT? MAX"));
            if (max >= requiredVolts) return;
        }
        // Largest range selected; the caller refuses if its maximum is still too low.
    }
}
