using BatteryCharger.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BatteryCharger.App.Views;

public sealed partial class ChargerPage : Page
{
    public ChargerViewModel ViewModel { get; }

    public ChargerPage()
    {
        ViewModel = App.Services.GetRequiredService<ChargerViewModel>();
        InitializeComponent();
        Chart.Buffer = ViewModel.Chart;
        Loaded += (_, _) =>
        {
            ViewModel.ChartInvalidated += OnChartInvalidated;
            Chart.Redraw();
        };
        Unloaded += (_, _) => ViewModel.ChartInvalidated -= OnChartInvalidated;
    }

    private void OnChartInvalidated(object? sender, EventArgs e) => Chart.Redraw();

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: double volts }) ViewModel.ApplyVoltagePreset(volts);
    }
}
