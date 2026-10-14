using System.Diagnostics;
using BatteryCharger.Core.Instruments;
using BatteryCharger.Instruments.Transport;

namespace BatteryCharger.Instruments.Drivers;

/// <summary>
/// One instrument connection shared by all its channels. Serialises I/O (one in-flight command), retries transient
/// errors (up to 3 attempts within 3 s) and raises <see cref="InstrumentCommunicationLost"/> when that fails.
/// </summary>
public sealed class ScpiLink : IAsyncDisposable
{
    private readonly IVisaTransport _transport;
    private readonly Action<string>? _trace;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _retryWindow;
    private readonly TimeSpan _retryDelay;

    public ScpiLink(IVisaTransport transport, Action<string>? trace = null, TimeSpan? retryWindow = null, TimeSpan? retryDelay = null)
    {
        _transport = transport;
        _trace = trace;
        _retryWindow = retryWindow ?? TimeSpan.FromSeconds(3);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(100);
    }

    /// <summary>Runs <paramref name="body"/> with exclusive access, so multi-command sequences are not interleaved.</summary>
    public async Task<T> RunAsync<T>(Func<ScpiIo, Task<T>> body, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await body(new ScpiIo(this, ct)).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public Task RunAsync(Func<ScpiIo, Task> body, CancellationToken ct) =>
        RunAsync<int>(async io => { await body(io).ConfigureAwait(false); return 0; }, ct);

    public Task WriteAsync(string cmd, CancellationToken ct) => RunAsync(io => io.WriteAsync(cmd), ct);
    public Task<string> QueryAsync(string cmd, CancellationToken ct) => RunAsync(io => io.QueryAsync(cmd), ct);

    internal void Trace(string line) => _trace?.Invoke(line);

    internal async Task<T> WithRetryAsync<T>(string what, Func<Task<T>> op, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await op().ConfigureAwait(false); }
            catch (InstrumentException ex) when (ex is not InstrumentCommunicationLost)
            {
                last = ex;
                Trace($"ERR {what}: {ex.Message} (attempt {attempt})");
                try { _transport.Clear(); } catch (Exception) { /* best effort */ }
                if (sw.Elapsed >= _retryWindow) break;
                await Task.Delay(_retryDelay, ct).ConfigureAwait(false);
            }
        }
        throw new InstrumentCommunicationLost($"I/O to the instrument failed ({what}): {last?.Message}", last!);
    }

    internal Task WriteRawAsync(string cmd, CancellationToken ct) => _transport.WriteAsync(cmd, ct);
    internal Task<string> QueryRawAsync(string cmd, CancellationToken ct) => _transport.QueryAsync(cmd, ct);

    public ValueTask DisposeAsync()
    {
        _transport.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>I/O handle valid only inside <see cref="ScpiLink.RunAsync{T}"/>.</summary>
public sealed class ScpiIo
{
    private readonly ScpiLink _link;
    private readonly CancellationToken _ct;

    internal ScpiIo(ScpiLink link, CancellationToken ct)
    {
        _link = link;
        _ct = ct;
    }

    public async Task WriteAsync(string cmd)
    {
        _link.Trace("TX " + cmd);
        await _link.WithRetryAsync(cmd, async () => { await _link.WriteRawAsync(cmd, _ct).ConfigureAwait(false); return 0; }, _ct).ConfigureAwait(false);
    }

    public async Task<string> QueryAsync(string cmd)
    {
        _link.Trace("TX " + cmd);
        var r = await _link.WithRetryAsync(cmd, () => _link.QueryRawAsync(cmd, _ct), _ct).ConfigureAwait(false);
        _link.Trace("RX " + r);
        return r;
    }

    /// <summary>Single attempt, no retry; returns null when the instrument does not answer (used for probing).</summary>
    public async Task<string?> TryQueryAsync(string cmd)
    {
        _link.Trace("TX " + cmd);
        try
        {
            var r = await _link.QueryRawAsync(cmd, _ct).ConfigureAwait(false);
            _link.Trace("RX " + r);
            return r;
        }
        catch (InstrumentException ex)
        {
            _link.Trace("ERR " + cmd + ": " + ex.Message);
            return null;
        }
    }
}
