using System.Text;

namespace BatteryCharger.Instruments.Transport;

/// <summary>Message-based session over native VISA. The termination character is LF for reads and writes.</summary>
public sealed class NativeVisaTransport(NativeVisaResourceManager manager) : IVisaTransport
{
    private uint _vi;
    public bool IsOpen => _vi != 0;

    public void Open(string resource, int timeoutMs)
    {
        Close();
        NativeVisaResourceManager.Check(
            NativeVisa.viOpen(manager.Handle, resource, 0, (uint)Math.Min(timeoutMs, 10_000), out _vi), "viOpen");
        try
        {
            NativeVisaResourceManager.Check(NativeVisa.viSetAttribute(_vi, NativeVisa.AttrTmoValue, (ulong)timeoutMs), "set timeout");
            // Required for raw sockets, harmless for INSTR sessions.
            NativeVisa.viSetAttribute(_vi, NativeVisa.AttrTermChar, 10);
            NativeVisa.viSetAttribute(_vi, NativeVisa.AttrTermCharEn, 1);
            NativeVisa.viSetAttribute(_vi, NativeVisa.AttrSendEndEn, 1);
        }
        catch
        {
            Close();
            throw;
        }
    }

    public Task WriteAsync(string command, CancellationToken ct) =>
        Task.Run(() => WriteCore(command), ct);

    public Task<string> QueryAsync(string command, CancellationToken ct) =>
        Task.Run(() => { WriteCore(command); return ReadCore(); }, ct);

    private void WriteCore(string command)
    {
        if (_vi == 0) throw new VisaIoException("The VISA session is not open.", -1);
        var bytes = Encoding.ASCII.GetBytes(command + "\n");
        NativeVisaResourceManager.Check(NativeVisa.viWrite(_vi, bytes, (uint)bytes.Length, out _), "viWrite");
    }

    private string ReadCore()
    {
        var sb = new StringBuilder();
        var buf = new byte[4096];
        while (true)
        {
            var status = NativeVisa.viRead(_vi, buf, (uint)buf.Length, out var n);
            NativeVisaResourceManager.Check(status, "viRead");
            sb.Append(Encoding.ASCII.GetString(buf, 0, (int)n));
            if (status != NativeVisa.SuccessMaxCntRead) break;
        }
        return sb.ToString().TrimEnd('\r', '\n', ' ');
    }

    public void Clear()
    {
        if (_vi != 0) NativeVisa.viClear(_vi);
    }

    public void Close()
    {
        if (_vi != 0) { NativeVisa.viClose(_vi); _vi = 0; }
    }

    public void Dispose() => Close();
}
