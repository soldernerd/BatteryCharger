using BatteryCharger.Core.Session;

namespace BatteryCharger.Tests.Core;

public class ChartBufferTests
{
    [Fact]
    public void SmallHistoryIsReturnedUnchanged()
    {
        var b = new ChartBuffer();
        for (var k = 0; k < 100; k++) b.Add(k, k * 0.1, 1);
        var s = b.Snapshot(20_000);
        Assert.Equal(100, s.Count);
        Assert.Equal(9.9, s[^1].V, 6);
    }

    [Fact]
    public void LongHistoryIsDecimatedButKeepsSpikes()
    {
        var b = new ChartBuffer();
        for (var k = 0; k < 100_000; k++) b.Add(k, 4.0, 1.0);
        b.Add(100_001, 9.0, 1.0);               // voltage spike
        b.Add(100_002, 4.0, 0.0);               // current dip
        var s = b.Snapshot(2000);
        Assert.InRange(s.Count, 100, 2200);
        Assert.Contains(s, p => p.V == 9.0);
        Assert.Contains(s, p => p.I == 0.0);
        Assert.Equal(s.OrderBy(p => p.T).Select(p => p.T), s.Select(p => p.T));   // still time ordered
    }

    [Fact]
    public void GrowsBeyondInitialCapacityAndTracksMarkers()
    {
        var b = new ChartBuffer();
        for (var k = 0; k < 5000; k++) b.Add(k, 1, 1);
        b.AddMarker(12, "CV");
        Assert.Equal(5000, b.Count);
        Assert.Equal("CV", b.Markers[0].Label);
        b.Clear();
        Assert.Equal(0, b.Count);
        Assert.Empty(b.Markers);
    }
}
