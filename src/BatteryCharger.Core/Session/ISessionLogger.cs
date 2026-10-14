using BatteryCharger.Core.Profiles;

namespace BatteryCharger.Core.Session;

/// <summary>Everything written to the header block of a session log.</summary>
public sealed record SessionHeader(
    DateTimeOffset Start,
    InstrumentInfo Instrument,
    ChargeProfile Profile,
    TimeSpan PollInterval);

/// <summary>Session log sink. A failure to open or write must abort the session.</summary>
public interface ISessionLogger : IAsyncDisposable
{
    string? FilePath { get; }
    Task OpenAsync(SessionHeader header, CancellationToken ct);
    Task WriteSampleAsync(Sample sample, CancellationToken ct);
    /// <summary>Writes a row carrying only an event (no measurement).</summary>
    Task WriteEventAsync(DateTimeOffset time, TimeSpan elapsed, ChargeState state, string stage, string text, CancellationToken ct);
    Task CloseAsync(SessionResult result, CancellationToken ct);
}
