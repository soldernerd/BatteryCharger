using BatteryCharger.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace BatteryCharger.App.Views;

public sealed partial class ScpiConsolePage : Page
{
    public ScpiConsoleViewModel ViewModel { get; }

    public ScpiConsolePage()
    {
        ViewModel = App.Services.GetRequiredService<ScpiConsoleViewModel>();
        InitializeComponent();
    }

    private void OnCommandKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.SendCommand.CanExecute(null))
        {
            ViewModel.SendCommand.Execute(null);
            e.Handled = true;
        }
    }
}
