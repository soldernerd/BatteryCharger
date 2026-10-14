using System.Text;

namespace BatteryCharger.Instruments.Transport;

/// <summary>VISA resource manager backed by the native VISA library.</summary>
public sealed class NativeVisaResourceManager : IVisaResourceManager, IDisposable
{
    private uint _rm;
    private readonly object _gate = new();

    internal uint Handle
    {
        get
        {
            lock (_gate)
            {
                if (_rm == 0) Check(NativeVisa.viOpenDefaultRM(out _rm), "viOpenDefaultRM");
                return _rm;
            }
        }
    }

    public bool IsAvailable(out string? problem)
    {
        try
        {
            _ = Handle;
            problem = null;
            return true;
        }
        catch (DllNotFoundException)
        {
            problem = "No VISA library (visa64.dll) was found. Install the Keysight IO Libraries Suite (or NI-VISA, 64-bit).";
            return false;
        }
        catch (BadImageFormatException)
        {
            problem = "The installed VISA library is not a 64-bit build. Install the 64-bit Keysight IO Libraries Suite or NI-VISA.";
            return false;
        }
        catch (Exception ex)
        {
            problem = "VISA could not be initialised: " + ex.Message;
            return false;
        }
    }

    public IReadOnlyList<string> FindResources(string pattern)
    {
        var result = new List<string>();
        var desc = new StringBuilder(256);
        var status = NativeVisa.viFindRsrc(Handle, pattern, out var list, out var count, desc);
        if (status < 0)
        {
            if (unchecked((uint)status) == 0xBFFF0011) return result;   // VI_ERROR_RSRC_NFOUND
            throw new VisaIoException($"viFindRsrc failed (0x{status:X8})", status);
        }
        result.Add(desc.ToString());
        for (uint i = 1; i < count; i++)
        {
            desc.Clear();
            if (NativeVisa.viFindNext(list, desc) < 0) break;
            result.Add(desc.ToString());
        }
        NativeVisa.viClose(list);
        return result;
    }

    public IVisaTransport CreateTransport() => new NativeVisaTransport(this);

    internal static void Check(int status, string call)
    {
        if (status < 0) throw new VisaIoException($"{call} failed (VISA status 0x{status:X8})", status);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_rm != 0) { NativeVisa.viClose(_rm); _rm = 0; }
        }
    }
}
