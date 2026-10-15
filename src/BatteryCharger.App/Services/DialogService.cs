using BatteryCharger.Core.Profiles;
using BatteryCharger.Core.Session;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BatteryCharger.App.Services;

/// <summary>Modal dialogs. Only one <see cref="ContentDialog"/> can be open per window, so calls are serialised.</summary>
public sealed class DialogService(WindowContext window)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dlg)
    {
        await _gate.WaitAsync();
        try
        {
            dlg.XamlRoot = window.XamlRoot;
            return await dlg.ShowAsync();
        }
        finally { _gate.Release(); }
    }

    public Task ShowMessageAsync(string title, string message) =>
        ShowAsync(new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        });

    public async Task<bool> ConfirmAsync(string title, string message, string primary, string close)
    {
        var r = await ShowAsync(new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Close,
        });
        return r == ContentDialogResult.Primary;
    }

    /// <summary>Asks for a host name / IP address or a full VISA resource string.</summary>
    public async Task<string?> PromptLanInstrumentAsync()
    {
        var box = new TextBox
        {
            PlaceholderText = "192.168.1.50  or  TCPIP0::host::5025::SOCKET",
            Header = "Host name, IP address or full VISA resource string",
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "LAN instrument address");
        var r = await ShowAsync(new ContentDialog
        {
            Title = "Add LAN instrument",
            Content = box,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        });
        var text = box.Text.Trim();
        return r == ContentDialogResult.Primary && text.Length > 0 ? text : null;
    }

    /// <summary>Shows what is about to happen (supply, battery, measured voltage, warnings) with a single confirm button.</summary>
    public async Task<bool> ConfirmChargeAsync(ChargeProfile profile, PreflightResult pre, string supplyText, string? outputWarning)
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 560 };
        void Text(string t, bool bold = false, bool warn = false)
        {
            var tb = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap };
            if (bold) tb.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            if (warn) tb.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 90, 0));
            panel.Children.Add(tb);
        }

        var b = profile.Battery;
        Text($"{supplyText}", bold: true);
        Text($"Battery: {b.CellsInSeries} x {Batteries(b.Technology)}, {b.NominalVoltage:0.##} V nominal, {b.CapacityAh:0.###} Ah");
        Text($"Measured battery voltage: {pre.BatteryVolts:0.000} V");
        Text($"Target {profile.TargetPackVolts:0.00} V, charge current {profile.MainCurrentAmps:0.###} A, supply OVP {profile.OvpVolts:0.00} V");
        foreach (var w in pre.Warnings.Concat(profile.Warnings)) Text(w, warn: true);
        if (outputWarning is not null) Text(outputWarning, warn: true);

        var dlg = new ContentDialog
        {
            Title = "Start charging?",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
            PrimaryButtonText = "Start charging",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dlg) == ContentDialogResult.Primary;
    }

    private static string Batteries(Core.Batteries.BatteryTechnology t) => Core.Batteries.CellChemistryInfo.For(t).DisplayName;
}
