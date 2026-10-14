namespace BatteryCharger.Core.Session;

/// <summary>Stage transition shown as a vertical marker on the chart.</summary>
public readonly record struct StageMarker(double TimeSeconds, string Label);

/// <summary>One drawable point of a decimated series.</summary>
public readonly record struct ChartPoint(double T, double V, double I);

/// <summary>
/// Compact, thread-safe store of the whole session history (3 doubles per sample, ~2 MB for 24 h at 1 s) with
/// min/max bucket decimation for drawing.
/// </summary>
public sealed class ChartBuffer
{
    private readonly object _gate = new();
    private double[] _t = new double[1024];
    private double[] _v = new double[1024];
    private double[] _i = new double[1024];
    private readonly List<StageMarker> _markers = [];

    public int Count { get; private set; }

    public void Add(double t, double v, double i)
    {
        lock (_gate)
        {
            if (Count == _t.Length)
            {
                Array.Resize(ref _t, Count * 2);
                Array.Resize(ref _v, Count * 2);
                Array.Resize(ref _i, Count * 2);
            }
            _t[Count] = t; _v[Count] = v; _i[Count] = i;
            Count++;
        }
    }

    public void AddMarker(double t, string label)
    {
        lock (_gate) _markers.Add(new StageMarker(t, label));
    }

    public void Clear()
    {
        lock (_gate) { Count = 0; _markers.Clear(); }
    }

    public IReadOnlyList<StageMarker> Markers
    {
        get { lock (_gate) return _markers.ToArray(); }
    }

    /// <summary>
    /// Returns at most about <paramref name="maxPoints"/> points. When the history is longer, each bucket contributes its
    /// voltage minimum, voltage maximum, current minimum and current maximum samples so spikes are never lost.
    /// </summary>
    public IReadOnlyList<ChartPoint> Snapshot(int maxPoints)
    {
        lock (_gate)
        {
            var n = Count;
            var result = new List<ChartPoint>(Math.Min(n, maxPoints));
            if (n <= maxPoints)
            {
                for (var k = 0; k < n; k++) result.Add(new ChartPoint(_t[k], _v[k], _i[k]));
                return result;
            }
            var buckets = Math.Max(1, maxPoints / 4);
            for (var b = 0; b < buckets; b++)
            {
                var from = (int)((long)b * n / buckets);
                var to = (int)((long)(b + 1) * n / buckets);
                if (to <= from) continue;
                int vMin = from, vMax = from, iMin = from, iMax = from;
                for (var k = from; k < to; k++)
                {
                    if (_v[k] < _v[vMin]) vMin = k;
                    if (_v[k] > _v[vMax]) vMax = k;
                    if (_i[k] < _i[iMin]) iMin = k;
                    if (_i[k] > _i[iMax]) iMax = k;
                }
                foreach (var k in new[] { vMin, vMax, iMin, iMax }.Distinct().OrderBy(x => x))
                    result.Add(new ChartPoint(_t[k], _v[k], _i[k]));
            }
            return result;
        }
    }
}
