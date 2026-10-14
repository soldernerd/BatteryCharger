using BatteryCharger.Core.Session;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace BatteryCharger.App.Controls;

/// <summary>
/// Voltage (left axis) and current (right axis) over elapsed time with stage markers and a hover tooltip.
/// Drawn directly with shapes from a <see cref="ChartBuffer"/>; the buffer decimates long histories, so redraw cost
/// stays bounded. <see cref="Redraw"/> is called by the page at most four times per second.
/// </summary>
public sealed partial class LiveChart : UserControl
{
    private const double Left = 62, Right = 62, Top = 14, Bottom = 30;
    private static readonly Windows.UI.Color VoltColor = Windows.UI.Color.FromArgb(255, 58, 150, 221);
    private static readonly Windows.UI.Color CurrentColor = Windows.UI.Color.FromArgb(255, 247, 99, 12);

    private IReadOnlyList<ChartPoint> _points = [];
    private Rect _plot;
    private double _xMax = 60, _vMin, _vMax = 1, _iMax = 1;
    private readonly Line _hoverLine = new() { StrokeThickness = 1, Visibility = Visibility.Collapsed };
    private readonly Border _tip = new() { Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(4), Visibility = Visibility.Collapsed };
    private readonly TextBlock _tipText = new() { FontSize = 12 };

    public ChartBuffer? Buffer { get; set; }

    public LiveChart()
    {
        InitializeComponent();
        _tip.Child = _tipText;
        SizeChanged += (_, _) => Redraw();
        Root.PointerMoved += OnPointerMoved;
        Root.PointerExited += (_, _) => { _hoverLine.Visibility = Visibility.Collapsed; _tip.Visibility = Visibility.Collapsed; };
    }

    private static Brush ThemeBrush(string key, Windows.UI.Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var b) && b is Brush br ? br : new SolidColorBrush(fallback);

    public void Redraw()
    {
        var w = Root.ActualWidth;
        var h = Root.ActualHeight;
        Plot.Children.Clear();
        if (w < 200 || h < 120) return;

        _plot = new Rect(Left, Top, w - Left - Right, h - Top - Bottom);
        var textBrush = ThemeBrush("TextFillColorSecondaryBrush", Colors.Gray);
        var gridBrush = ThemeBrush("DividerStrokeColorDefaultBrush", Colors.LightGray);

        _points = Buffer?.Snapshot(8000) ?? [];
        var markers = Buffer?.Markers ?? [];
        var last = _points.Count > 0 ? _points[^1] : default;
        _xMax = Math.Max(60, last.T);
        if (_points.Count > 0)
        {
            _vMin = _points.Min(p => p.V);
            _vMax = _points.Max(p => p.V);
            _iMax = Math.Max(0.1, _points.Max(p => p.I));
        }
        else { _vMin = 0; _vMax = 1; _iMax = 1; }
        if (_vMax - _vMin < 0.2) { var mid = (_vMax + _vMin) / 2; _vMin = mid - 0.1; _vMax = mid + 0.1; }
        // Round both axes to "nice" values with the same number of intervals so the grid lines serve both scales.
        var vStep = NiceNum((_vMax - _vMin) * 1.1 / 5);
        var vLo = Math.Floor(_vMin / vStep) * vStep;
        while (vLo + 5 * vStep < _vMax - 1e-9) { vStep = NiceNum(vStep * 1.05); vLo = Math.Floor(_vMin / vStep) * vStep; }
        _vMin = vLo;
        _vMax = vLo + 5 * vStep;
        _iMax = 5 * NiceNum(_iMax * 1.05 / 5);

        // Grid and axes
        const int yTicks = 5;
        for (var k = 0; k <= yTicks; k++)
        {
            var frac = (double)k / yTicks;
            var y = _plot.Bottom - frac * _plot.Height;
            Plot.Children.Add(new Line { X1 = _plot.Left, X2 = _plot.Right, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
            AddText($"{_vMin + frac * (_vMax - _vMin):0.0##} V", 2, y - 8, new SolidColorBrush(VoltColor), 56, TextAlignment.Right);
            AddText($"{frac * _iMax:0.0##} A", _plot.Right + 4, y - 8, new SolidColorBrush(CurrentColor), 56, TextAlignment.Left);
        }
        var xStep = NiceStep(_xMax / 6);
        for (var t = 0.0; t <= _xMax + 1e-9; t += xStep)
        {
            var x = MapX(t);
            Plot.Children.Add(new Line { X1 = x, X2 = x, Y1 = _plot.Top, Y2 = _plot.Bottom, Stroke = gridBrush, StrokeThickness = 1 });
            AddText(FormatTime(t), x - 30, _plot.Bottom + 6, textBrush, 60, TextAlignment.Center);
        }

        // Stage markers
        foreach (var m in markers)
        {
            var x = MapX(m.TimeSeconds);
            Plot.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = _plot.Top, Y2 = _plot.Bottom, StrokeThickness = 1,
                Stroke = textBrush, StrokeDashArray = new DoubleCollection { 4, 3 },
            });
            AddText(m.Label, x + 3, _plot.Top, textBrush, 120, TextAlignment.Left);
        }

        // Series
        if (_points.Count > 1)
        {
            var vPoly = new Polyline { Stroke = new SolidColorBrush(VoltColor), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            var iPoly = new Polyline { Stroke = new SolidColorBrush(CurrentColor), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            foreach (var p in _points)
            {
                var x = MapX(p.T);
                vPoly.Points.Add(new Point(x, MapV(p.V)));
                iPoly.Points.Add(new Point(x, MapI(p.I)));
            }
            Plot.Children.Add(vPoly);
            Plot.Children.Add(iPoly);
        }

        _hoverLine.Stroke = textBrush;
        _tip.Background = ThemeBrush("SolidBackgroundFillColorSecondaryBrush", Colors.White);
        _tip.BorderBrush = gridBrush;
        _tip.BorderThickness = new Thickness(1);
        _tipText.Foreground = ThemeBrush("TextFillColorPrimaryBrush", Colors.Black);
        Plot.Children.Add(_hoverLine);
        Plot.Children.Add(_tip);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_points.Count == 0 || _plot.Width <= 0) return;
        var pos = e.GetCurrentPoint(Root).Position;
        if (pos.X < _plot.Left || pos.X > _plot.Right) { _hoverLine.Visibility = Visibility.Collapsed; _tip.Visibility = Visibility.Collapsed; return; }

        var t = (pos.X - _plot.Left) / _plot.Width * _xMax;
        int lo = 0, hi = _points.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_points[mid].T < t) lo = mid + 1; else hi = mid;
        }
        var best = lo;
        if (lo > 0 && Math.Abs(_points[lo - 1].T - t) < Math.Abs(_points[lo].T - t)) best = lo - 1;
        var p = _points[best];

        var x = MapX(p.T);
        _hoverLine.X1 = _hoverLine.X2 = x;
        _hoverLine.Y1 = _plot.Top;
        _hoverLine.Y2 = _plot.Bottom;
        _hoverLine.Visibility = Visibility.Visible;
        _tipText.Text = $"{FormatTime(p.T)}\n{p.V:0.000} V\n{p.I:0.000} A";
        _tip.Visibility = Visibility.Visible;
        Canvas.SetLeft(_tip, x > _plot.Left + _plot.Width / 2 ? x - 100 : x + 10);
        Canvas.SetTop(_tip, _plot.Top + 8);
    }

    private double MapX(double t) => _plot.Left + t / _xMax * _plot.Width;
    private double MapV(double v) => _plot.Bottom - (v - _vMin) / (_vMax - _vMin) * _plot.Height;
    private double MapI(double i) => _plot.Bottom - i / _iMax * _plot.Height;

    private void AddText(string text, double x, double y, Brush brush, double width, TextAlignment align)
    {
        var tb = new TextBlock { Text = text, Foreground = brush, FontSize = 11, Width = width, TextAlignment = align, IsHitTestVisible = false };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        Plot.Children.Add(tb);
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <summary>Smallest "nice" number (1, 2, 2.5, 5 times a power of ten) that is at least <paramref name="x"/>.</summary>
    private static double NiceNum(double x)
    {
        if (!(x > 0)) return 1;
        var exp = Math.Floor(Math.Log10(x));
        var f = x / Math.Pow(10, exp);
        var nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
        return nice * Math.Pow(10, exp);
    }

    /// <summary>Rounds a step up to 1, 2, 5 or 10 times a power of ten, or to whole minutes/hours for long runs.</summary>
    private static double NiceStep(double raw)
    {
        double[] steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 21600, 43200];
        foreach (var s in steps) if (s >= raw) return s;
        return 86400;
    }
}
