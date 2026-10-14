namespace BatteryCharger.Core.Instruments;

/// <summary>An instrument operation failed (SCPI error text in <see cref="Exception.Message"/>).</summary>
public class InstrumentException : Exception
{
    public InstrumentException(string message) : base(message) { }
    public InstrumentException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>I/O to the instrument failed repeatedly; its output state must be considered unknown.</summary>
public sealed class InstrumentCommunicationLost : InstrumentException
{
    public InstrumentCommunicationLost(string message) : base(message) { }
    public InstrumentCommunicationLost(string message, Exception inner) : base(message, inner) { }
}
