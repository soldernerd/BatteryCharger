using BatteryCharger.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BatteryCharger.App.Views;

/// <summary>One tab: a single power supply. The shell keeps one page instance per supply, so a running charge keeps its chart.</summary>
public sealed partial class ChargerPage : Page
{
    public SupplyViewModel ViewModel { get; }

    public ChargerPage(SupplyViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Chart.Buffer = ViewModel.Chart;
        Loaded += async (_, _) =>
        {
            ViewModel.ChartInvalidated += OnChartInvalidated;
            Chart.Redraw();
            await ViewModel.EnsureConnectedAsync();
            ViewModel.OnPageShown();
        };
        Unloaded += (_, _) =>
        {
            ViewModel.ChartInvalidated -= OnChartInvalidated;
            ViewModel.OnPageHidden();
        };
    }

    private void OnChartInvalidated(object? sender, EventArgs e) => Chart.Redraw();
}
