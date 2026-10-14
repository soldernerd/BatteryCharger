using System.Text.RegularExpressions;

namespace BatteryCharger.Instruments.Drivers;

/// <summary>SCPI dialect family of a supported instrument.</summary>
public enum DriverFamily { E363xx, E364xA }

/// <summary>What the driver needs to know about a model.</summary>
/// <param name="Model">Model string from *IDN?.</param>
/// <param name="Family">Dialect.</param>
/// <param name="KnownChannels">Channel count when known from the model table; null means probe.</param>
/// <param name="Ranges">Selectable voltage ranges (E364xA), smallest first; empty when none.</param>
public sealed record ModelInfo(string Model, DriverFamily Family, int? KnownChannels, IReadOnlyList<string> Ranges);

/// <summary>The single place that maps *IDN? model strings to drivers (spec 5.2). Adding a model is a one-line change.</summary>
public static class SupportedModels
{
    private static readonly Regex Manufacturer = new("Keysight|Agilent|Hewlett-?Packard|HEWLETT", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly (Regex Pattern, DriverFamily Family)[] Table =
    [
        (new Regex(@"^E36[123]\d\d[AB]?$", RegexOptions.Compiled), DriverFamily.E363xx),   // E36100 / E36200 / E36300
        (new Regex(@"^E364\dA$", RegexOptions.Compiled), DriverFamily.E364xA),             // E3640A..E3649A
    ];

    // VERIFY: channel counts of multi-output E362xx / E363xx models. Unlisted E362xx/E363xx models are probed.
    private static readonly Dictionary<string, int> ChannelsByModel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["E36233A"] = 2, ["E36234A"] = 2,
        ["E36311A"] = 3, ["E36312A"] = 3, ["E36313A"] = 3,
    };

    public static bool IsKeysightManufacturer(string manufacturer) => Manufacturer.IsMatch(manufacturer);

    public static bool TryResolve(string model, out ModelInfo info)
    {
        model = model.Trim();
        foreach (var (pattern, family) in Table)
        {
            if (!pattern.IsMatch(model)) continue;
            info = family == DriverFamily.E363xx ? ForE363(model) : ForE364(model);
            return true;
        }
        info = null!;
        return false;
    }

    private static ModelInfo ForE363(string model)
    {
        int? channels = ChannelsByModel.TryGetValue(model, out var n) ? n
            : model.StartsWith("E361", StringComparison.OrdinalIgnoreCase) ? 1
            : null;
        return new ModelInfo(model, DriverFamily.E363xx, channels, []);
    }

    private static ModelInfo ForE364(string model)
    {
        var digit = model[4] - '0';
        // VERIFY: range enumerations. Even models P8V/P20V, odd models P35V/P60V.
        string[] ranges = digit % 2 == 0 ? ["P8V", "P20V"] : ["P35V", "P60V"];
        return new ModelInfo(model, DriverFamily.E364xA, digit >= 6 ? 2 : 1, ranges);   // E3646A-E3649A are dual output
    }
}
