using System.Collections.ObjectModel;
using BatteryCharger.App.Services;
using BatteryCharger.Core.Instruments;
using BatteryCharger.Instruments.Drivers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace BatteryCharger.App.ViewModels;

/// <summary>Developer page: send raw SCPI to the connected supply and watch the traffic (hardware bring-up).</summary>
public sealed partial class ScpiConsoleViewModel : ObservableObject
{
    private readonly SupplyRegistry _registry;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    public ScpiConsoleViewModel(SupplyRegistry registry, VisaService visa)
    {
        _registry = registry;
        visa.Traffic += line => _dispatcher.TryEnqueue(() => Append(line));
    }

    public ObservableCollection<string> Lines { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _command = "*IDN?";

    [ObservableProperty] private string _status = "";

    private void Append(string line)
    {
        Lines.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");
        while (Lines.Count > 2000) Lines.RemoveAt(0);
    }

    [RelayCommand]
    private void Clear() => Lines.Clear();

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var link = CurrentLink();
        if (link is null)
        {
            Status = "Not connected to a real instrument. Open a power supply tab below Charger first.";
            return;
        }
        var cmd = Command.Trim();
        try
        {
            Status = "";
            if (cmd.Contains('?')) await link.QueryAsync(cmd, CancellationToken.None);
            else await link.WriteAsync(cmd, CancellationToken.None);
        }
        catch (InstrumentException ex)
        {
            Status = ex.Message;
            Append("ERR " + ex.Message);
        }
    }

    /// <summary>The link of the tab shown last, else of any connected supply.</summary>
    private ScpiLink? CurrentLink() =>
        _registry.Selected?.Link ?? _registry.Supplies.Select(s => s.Link).FirstOrDefault(l => l is not null);

    private bool CanSend() => !string.IsNullOrWhiteSpace(Command);

    [RelayCommand]
    private async Task ReadErrorsAsync()
    {
        var link = CurrentLink();
        if (link is null) return;
        try { await link.QueryAsync("SYST:ERR?", CancellationToken.None); }
        catch (InstrumentException ex) { Status = ex.Message; }
    }
}
