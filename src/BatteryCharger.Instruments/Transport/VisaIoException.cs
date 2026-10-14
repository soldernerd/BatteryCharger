using BatteryCharger.Core.Instruments;

namespace BatteryCharger.Instruments.Transport;

/// <summary>A VISA call returned an error status.</summary>
public sealed class VisaIoException : InstrumentException
{
    public int Status { get; }
    public bool IsTimeout => unchecked((uint)Status) == 0xBFFF0015;
    public VisaIoException(string message, int status) : base(message) => Status = status;
}
