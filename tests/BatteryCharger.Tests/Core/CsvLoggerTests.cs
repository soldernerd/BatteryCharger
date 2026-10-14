using BatteryCharger.Core.Batteries;
using BatteryCharger.Core.Logging;
using BatteryCharger.Core.Profiles;
using BatteryCharger.Core.Session;

namespace BatteryCharger.Tests.Core;

public class CsvLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bc-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task WritesHeaderRowsEventsAndFooter()
    {
        var b = BatteryDescriptor.CreateDefault(BatteryTechnology.LiIon, 7.2, 2.2);
        var profile = ChargeProfileFactory.Create(b, ProfileOptions.Defaults(b.Technology));
        var start = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.FromHours(1));
        var logger = new CsvSessionLogger(_dir, ';');
        await logger.OpenAsync(new SessionHeader(start,
            new InstrumentInfo("Keysight Technologies,E36312A,MY123,1.2", "USB0::X::INSTR", "CH1"), profile, TimeSpan.FromSeconds(1)), default);
        await logger.WriteSampleAsync(new Sample(start, TimeSpan.FromSeconds(1.5), ChargeState.ConstantCurrent, "CC",
            7.4123456, 1.1, 0.0004, 0.003, 7.5, 1.1, PsuMode.CC, "Stage: CC; \"quoted\""), default);
        await logger.WriteEventAsync(start, TimeSpan.FromSeconds(2), ChargeState.ConstantCurrent, "CC", "hello", default);
        await logger.CloseAsync(new SessionResult(EndReason.Completed, "done", TimeSpan.FromMinutes(1), 0.5, 3.7, 8.4, logger.FilePath), default);

        Assert.EndsWith("20250304_050607_E36312A_LiIon_7.2V_2.2Ah.csv", logger.FilePath);
        var bytes = await File.ReadAllBytesAsync(logger.FilePath!);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);   // UTF-8 BOM
        var lines = (await File.ReadAllLinesAsync(logger.FilePath!)).ToList();
        Assert.Contains(lines, l => l.StartsWith("# instrument: Keysight Technologies,E36312A"));
        Assert.Contains(lines, l => l.StartsWith("# cells_in_series: 2"));
        var hdr = lines.FindIndex(l => l.StartsWith("timestamp_local;"));
        Assert.True(hdr > 0);
        Assert.Equal("timestamp_local;elapsed_s;state;stage;voltage_v;current_a;power_w;charge_ah;energy_wh;voltage_set_v;current_limit_a;psu_mode;event", lines[hdr].TrimStart('﻿'));
        Assert.StartsWith("2025-03-04 05:06:07.000;1.500;ConstantCurrent;CC;7.4123;1.10000;", lines[hdr + 1]);
        Assert.EndsWith(";CC;\"Stage: CC; \"\"quoted\"\"\"", lines[hdr + 1]);
        Assert.Equal(13, lines[hdr + 2].Split(';').Length);
        Assert.EndsWith(";hello", lines[hdr + 2]);
        Assert.Contains("# end_reason: Completed", lines);
        Assert.Contains("# charge_ah: 0.50000", lines);
    }

    [Fact]
    public async Task FileIsReadableWhileOpenAndFlushedOnEvents()
    {
        var b = BatteryDescriptor.CreateDefault(BatteryTechnology.LiIon, 7.2, 2.2);
        var profile = ChargeProfileFactory.Create(b, ProfileOptions.Defaults(b.Technology));
        await using var logger = new CsvSessionLogger(_dir);
        await logger.OpenAsync(new SessionHeader(DateTimeOffset.Now, new InstrumentInfo("", "", ""), profile, TimeSpan.FromSeconds(1)), default);
        await logger.WriteEventAsync(DateTimeOffset.Now, TimeSpan.Zero, ChargeState.Preflight, "", "evt", default);
        using var fs = new FileStream(logger.FilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        Assert.Contains("evt", await sr.ReadToEndAsync());
    }

    [Fact]
    public async Task CannotOverwriteExistingLog()
    {
        var b = BatteryDescriptor.CreateDefault(BatteryTechnology.LiIon, 7.2, 2.2);
        var profile = ChargeProfileFactory.Create(b, ProfileOptions.Defaults(b.Technology));
        var header = new SessionHeader(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), new InstrumentInfo("", "", ""), profile, TimeSpan.FromSeconds(1));
        await using var one = new CsvSessionLogger(_dir);
        await one.OpenAsync(header, default);
        await using var two = new CsvSessionLogger(_dir);
        await Assert.ThrowsAsync<IOException>(() => two.OpenAsync(header, default));
    }
}
