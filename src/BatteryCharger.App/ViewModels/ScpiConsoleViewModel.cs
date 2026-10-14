using System.Collections.ObjectModel;
using BatteryCharger.App.Services;
using BatteryCharger.Core.Instruments;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace BatteryCharger.App.ViewModels;

/// <summary>Developer page: send raw SCPI to the connected supply and watch the traffic (hardware bring-up).</summary>
public sealed partial class ScpiConsoleViewModel : ObservableObject
{
    private readonly InstrumentService _instruments;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    public ScpiConsoleViewModel(InstrumentService instruments, VisaService visa)
    {
        _instruments = instruments;
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
        var link = _instruments.Link;
        if (link is null)
        {
            Status = "Not connected to a real instrument. Connect on the Charger page first.";
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

    private bool CanSend() => !string.IsNullOrWhiteSpace(Command);

    [RelayCommand]
    private async Task ReadErrorsAsync()
    {
        var link = _instruments.Link;
        if (link is null) return;
        try { await link.QueryAsync("SYST:ERR?", CancellationToken.None); }
        catch (InstrumentException ex) { Status = ex.Message; }
    }
}
