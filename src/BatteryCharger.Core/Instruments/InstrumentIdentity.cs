namespace BatteryCharger.Core.Instruments;

/// <summary>Parsed <c>*IDN?</c> response.</summary>
public sealed record InstrumentIdentity(string Manufacturer, string Model, string Serial, string Firmware)
{
    /// <summary>Parses <c>manufacturer,model,serial,firmware</c>; missing fields become empty strings.</summary>
    public static InstrumentIdentity Parse(string idn)
    {
        var p = (idn ?? "").Trim().Split(',');
        string F(int i) => i < p.Length ? p[i].Trim() : "";
        return new InstrumentIdentity(F(0), F(1), F(2), F(3));
    }

    public override string ToString() => $"{Manufacturer},{Model},{Serial},{Firmware}";
}
