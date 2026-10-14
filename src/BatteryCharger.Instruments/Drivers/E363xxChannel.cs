namespace BatteryCharger.Instruments.Drivers;

/// <summary>E36100 / E36200 / E36300 dialect: channel list <c>(@n)</c> appended only on multi-channel instruments.</summary>
public sealed class E363xxChannel(ScpiLink link, int index, bool multiChannel) : KeysightChannel(link, index)
{
    protected override string Set(string header, string arg) =>
        multiChannel ? $"{header} {arg},(@{Index})" : $"{header} {arg}";

    protected override string Get(string header, string? arg = null) =>
        (multiChannel, arg) switch
        {
            (true, null) => $"{header}? (@{Index})",
            (true, _) => $"{header}? {arg},(@{Index})",
            (false, null) => $"{header}?",
            (false, _) => $"{header}? {arg}",
        };
}
