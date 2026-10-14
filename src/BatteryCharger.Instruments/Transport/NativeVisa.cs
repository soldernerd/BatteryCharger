using System.Runtime.InteropServices;
using System.Text;

namespace BatteryCharger.Instruments.Transport;

/// <summary>
/// Minimal P/Invoke surface of the VISA C library (visa64.dll, installed by Keysight IO Libraries Suite or NI-VISA).
/// Chosen over Ivi.Visa because it has no managed dependency on a specific vendor assembly.
/// </summary>
internal static class NativeVisa
{
    private const string Lib = "visa64.dll";

    public const int SuccessMaxCntRead = 0x3FFF0006;

    public const uint AttrTmoValue = 0x3FFF001A;
    public const uint AttrTermChar = 0x3FFF0018;
    public const uint AttrTermCharEn = 0x3FFF0038;
    public const uint AttrSendEndEn = 0x3FFF0016;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int viOpenDefaultRM(out uint session);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int viOpen(uint rm, string name, uint accessMode, uint timeout, out uint vi);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int viClose(uint vi);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int viClear(uint vi);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int viSetAttribute(uint vi, uint attribute, ulong state);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int viWrite(uint vi, byte[] buffer, uint count, out uint retCount);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int viRead(uint vi, byte[] buffer, uint count, out uint retCount);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int viFindRsrc(uint rm, string expr, out uint findList, out uint retCount, StringBuilder description);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int viFindNext(uint findList, StringBuilder description);
}
