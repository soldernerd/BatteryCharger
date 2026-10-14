using BatteryCharger.Instruments.Transport;

namespace BatteryCharger.Tests.Instruments;

/// <summary>Fake SCPI instrument: records every command and answers queries from a rule table.</summary>
public sealed class ScriptedTransport : IVisaTransport
{
    public List<string> Sent { get; } = [];
    public Dictionary<string, string> Responses { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Queries that never get an answer (throw a timeout).</summary>
    public HashSet<string> Silent { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Queue<string> ErrorQueue { get; } = new();
    public int FailNextIo { get; set; }
    public bool IsOpen { get; private set; }
    public int Clears { get; private set; }
    public Action<string>? OnWrite { get; set; }

    public void Open(string resource, int timeoutMs) => IsOpen = true;
    public void Close() => IsOpen = false;
    public void Dispose() => Close();
    public void Clear() => Clears++;

    private void MaybeFail()
    {
        if (FailNextIo > 0) { FailNextIo--; throw new VisaIoException("timeout", unchecked((int)0xBFFF0015)); }
    }

    public Task WriteAsync(string command, CancellationToken ct)
    {
        MaybeFail();
        Sent.Add(command);
        OnWrite?.Invoke(command);
        return Task.CompletedTask;
    }

    public Task<string> QueryAsync(string command, CancellationToken ct)
    {
        MaybeFail();
        Sent.Add(command);
        OnWrite?.Invoke(command);
        if (Silent.Contains(command)) throw new VisaIoException("timeout", unchecked((int)0xBFFF0015));
        if (command == "SYST:ERR?") return Task.FromResult(ErrorQueue.Count > 0 ? ErrorQueue.Dequeue() : "+0,\"No error\"");
        if (command == "*OPC?") return Task.FromResult("1");
        if (Responses.TryGetValue(command, out var r)) return Task.FromResult(r);
        return Task.FromResult("0");
    }
}
