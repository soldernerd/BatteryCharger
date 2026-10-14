using BatteryCharger.Core.Batteries;
using BatteryCharger.Core.Settings;

namespace BatteryCharger.Tests.Core;

public class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bc-set-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var s = new SettingsStore(File_).Load();
        Assert.Equal(1.0, s.PollIntervalSeconds);
        Assert.True(s.RestorePreviousSetpoints);
    }

    [Fact]
    public void RoundTrip()
    {
        var store = new SettingsStore(File_);
        var s = store.Load();
        s.LanInstruments.Add("TCPIP0::10.0.0.5::inst0::INSTR");
        s.Battery.Technology = BatteryTechnology.LeadAcid;
        s.CsvSeparator = ";";
        store.Save(s);
        var r = store.Load();
        Assert.Equal(BatteryTechnology.LeadAcid, r.Battery.Technology);
        Assert.Equal(';', r.CsvSeparatorChar);
        Assert.Single(r.LanInstruments);
    }

    [Fact]
    public void UnknownAndMissingFieldsAreTolerated()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{ \"futureThing\": 5, \"pollIntervalSeconds\": 99, \"theme\": \"Neon\" }");
        var s = new SettingsStore(File_).Load();
        Assert.Equal(10, s.PollIntervalSeconds);   // clamped
        Assert.Equal("System", s.Theme);
        Assert.NotNull(s.Battery);
    }

    [Fact]
    public void CorruptFileIsBackedUpAndReplacedByDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{ not json");
        var s = new SettingsStore(File_).Load();
        Assert.Equal(1.0, s.PollIntervalSeconds);
        Assert.True(File.Exists(File_ + ".corrupt"));
    }
}
