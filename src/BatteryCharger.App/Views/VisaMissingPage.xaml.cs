using BatteryCharger.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace BatteryCharger.App.Views;

public sealed partial class VisaMissingPage : Page
{
    public VisaMissingPage()
    {
        InitializeComponent();
        ProblemText.Text = App.Services.GetRequiredService<VisaService>().Problem ?? "";
    }
}
