using BatteryCharger.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BatteryCharger.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        UpdateEmptyText();
        ViewModel.LanInstruments.CollectionChanged += (_, _) => UpdateEmptyText();
    }

    private void UpdateEmptyText() =>
        NoLanText.Visibility = ViewModel.LanInstruments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string resource }) ViewModel.RemoveLanCommand.Execute(resource);
    }
}
