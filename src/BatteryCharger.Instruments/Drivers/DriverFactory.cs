using BatteryCharger.Core.Instruments;
using BatteryCharger.Instruments.Transport;

namespace BatteryCharger.Instruments.Drivers;

/// <summary>Opens a resource, identifies the instrument and returns the matching driver.</summary>
public static class DriverFactory
{
    public static async Task<IPowerSupply> ConnectAsync(
        IVisaResourceManager rm, string resource, int timeoutMs, Action<string>? trace, CancellationToken ct)
    {
        var transport = rm.CreateTransport();
        var link = new ScpiLink(transport, trace);
        try
        {
            await Task.Run(() => transport.Open(resource, timeoutMs), ct);
            var idn = await link.QueryAsync("*IDN?", ct);
            var identity = InstrumentIdentity.Parse(idn);
            return await CreateAsync(resource, identity, link, ct);
        }
        catch
        {
            await link.DisposeAsync();
            throw;
        }
    }

    /// <summary>Builds the driver for an already open link (also used by tests with a scripted transport).</summary>
    public static async Task<IPowerSupply> CreateAsync(string resource, InstrumentIdentity identity, ScpiLink link, CancellationToken ct)
    {
        if (!SupportedModels.IsKeysightManufacturer(identity.Manufacturer) || !SupportedModels.TryResolve(identity.Model, out var info))
            throw new InstrumentException($"'{identity}' is not a supported power supply (supported: Keysight/Agilent E36100, E36200, E36300, E3640A-E3649A).");

        var channels = new List<IPowerSupplyChannel>();
        if (info.Family == DriverFamily.E364xA)
        {
            for (var n = 1; n <= info.KnownChannels; n++)
                channels.Add(new E364xAChannel(link, n, info.KnownChannels > 1, info.Ranges));
        }
        else
        {
            var count = info.KnownChannels ?? await ProbeChannelCountAsync(link, ct);
            for (var n = 1; n <= count; n++)
                channels.Add(new E363xxChannel(link, n, count > 1));
        }

        // Read the ratings so channel names such as "CH1 (6 V / 5 A)" are meaningful.
        foreach (var ch in channels)
        {
            try { await ch.GetLimitsAsync(0, ct); }
            catch (InstrumentException) { /* name stays "CHn"; real errors surface when the channel is used */ }
        }
        return new ScpiPowerSupply(resource, identity, link, channels);
    }

    /// <summary>
    /// Finds the number of outputs by querying <c>VOLT? MAX,(@n)</c> for n = 1..4 (VERIFY on hardware).
    /// A channel exists when the query is answered; the error queue is drained afterwards.
    /// </summary>
    private static Task<int> ProbeChannelCountAsync(ScpiLink link, CancellationToken ct) =>
        link.RunAsync(async io =>
        {
            var count = 0;
            for (var n = 1; n <= 4; n++)
            {
                var r = await io.TryQueryAsync($"VOLT? MAX,(@{n})");
                if (r is null) break;
                count = n;
            }
            await KeysightChannel.DrainAsync(io);
            return Math.Max(count, 1);
        }, ct);
}
