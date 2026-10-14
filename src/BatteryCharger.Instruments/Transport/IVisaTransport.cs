namespace BatteryCharger.Instruments.Transport;

/// <summary>Raw message-based I/O to one instrument. Calls block; callers must not use it from the UI thread.</summary>
public interface IVisaTransport : IDisposable
{
    bool IsOpen { get; }
    void Open(string resource, int timeoutMs);
    /// <summary>Sends a command with the termination character appended.</summary>
    Task WriteAsync(string command, CancellationToken ct);
    /// <summary>Sends a query and returns the response without its terminator.</summary>
    Task<string> QueryAsync(string command, CancellationToken ct);
    /// <summary>Best-effort device clear after an I/O error, to drop stale responses.</summary>
    void Clear();
    void Close();
}

/// <summary>Entry point to a VISA implementation: resource enumeration and transport creation.</summary>
public interface IVisaResourceManager
{
    /// <summary>True when a VISA library could be loaded; <paramref name="problem"/> explains why not.</summary>
    bool IsAvailable(out string? problem);
    IReadOnlyList<string> FindResources(string pattern);
    IVisaTransport CreateTransport();
}
