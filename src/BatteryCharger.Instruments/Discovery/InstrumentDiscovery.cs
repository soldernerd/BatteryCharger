using BatteryCharger.Core.Instruments;
using BatteryCharger.Instruments.Drivers;
using BatteryCharger.Instruments.Transport;

namespace BatteryCharger.Instruments.Discovery;

/// <summary>A VISA resource found (or added manually) and what it turned out to be.</summary>
public sealed record VisaResourceInfo(string Resource, InstrumentIdentity? Identity, bool Supported, string? Reason, bool Manual)
{
    public string Display => Identity is null
        ? $"{Resource} ({Reason})"
        : $"{Identity.Model}  S/N {Identity.Serial}  {Resource}";
}

/// <summary>Finds instruments and classifies them by <c>*IDN?</c>.</summary>
public sealed class InstrumentDiscovery(IVisaResourceManager rm)
{
    /// <summary>Normalises a host name / IP / full resource string to a VISA resource string.</summary>
    public static string ToResourceString(string input)
    {
        input = input.Trim();
        if (input.Contains("::", StringComparison.Ordinal)) return input;     // full resource string pasted
        return $"TCPIP0::{input}::inst0::INSTR";
    }

    /// <summary>
    /// Enumerates VISA resources plus <paramref name="extraResources"/> (saved LAN instruments) and identifies each one
    /// with a per-instrument timeout. Reports results incrementally through <paramref name="progress"/>.
    /// </summary>
    public async Task<IReadOnlyList<VisaResourceInfo>> DiscoverAsync(
        IEnumerable<string> extraResources, TimeSpan perInstrumentTimeout, IProgress<VisaResourceInfo>? progress, CancellationToken ct)
    {
        var found = await Task.Run(() => rm.FindResources("?*INSTR"), ct);
        var manual = new HashSet<string>(extraResources, StringComparer.OrdinalIgnoreCase);
        // LAN resources last: an unreachable host blocks inside viOpen for ~10 s, and that stalls every other VISA call.
        var all = found.Concat(manual.Where(m => !found.Contains(m, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(IsLan).ToList();
        var results = new List<VisaResourceInfo>();
        foreach (var r in all)
        {
            ct.ThrowIfCancellationRequested();
            var info = await IdentifyAsync(r, manual.Contains(r), perInstrumentTimeout, ct);
            results.Add(info);
            progress?.Report(info);
        }
        return results;
    }

    private static bool IsLan(string resource) => resource.StartsWith("TCPIP", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens the resource, reads <c>*IDN?</c>, closes it again.</summary>
    public async Task<VisaResourceInfo> IdentifyAsync(string resource, bool manual, TimeSpan timeout, CancellationToken ct)
    {
        using var t = rm.CreateTransport();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // viOpen on a LAN resource can take ~10 s when the host is down and cannot be interrupted. Abandoning it
            // early would leave it holding VISA, so LAN gets enough time to finish on its own.
            var overall = timeout + TimeSpan.FromSeconds(1);
            if (IsLan(resource) && overall < TimeSpan.FromSeconds(13)) overall = TimeSpan.FromSeconds(13);
            timeoutCts.CancelAfter(overall);
            var work = Task.Run(async () =>
            {
                t.Open(resource, (int)timeout.TotalMilliseconds);
                return await t.QueryAsync("*IDN?", timeoutCts.Token);
            }, timeoutCts.Token);
            var idn = await work.WaitAsync(timeoutCts.Token);
            var id = InstrumentIdentity.Parse(idn);
            if (SupportedModels.IsKeysightManufacturer(id.Manufacturer) && SupportedModels.TryResolve(id.Model, out _))
                return new VisaResourceInfo(resource, id, true, null, manual);
            return new VisaResourceInfo(resource, id, false, "not a supported power supply", manual);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new VisaResourceInfo(resource, null, false, "no response", manual);
        }
        catch (InstrumentException ex)
        {
            return new VisaResourceInfo(resource, null, false, ex.Message, manual);
        }
        finally
        {
            try { t.Close(); } catch (Exception) { /* ignore */ }
        }
    }
}
