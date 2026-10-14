namespace BatteryCharger.Core.Instruments;

/// <summary>One output of a power supply. All calls are async, cancellable and throw <see cref="InstrumentException"/>.</summary>
public interface IPowerSupplyChannel
{
    /// <summary>1-based channel index.</summary>
    int Index { get; }
    /// <summary>E.g. "CH1 (6 V / 5 A)".</summary>
    string DisplayName { get; }
    /// <summary>Non-null when switching this output also affects other outputs of the instrument.</summary>
    string? OutputWarning { get; }
    /// <summary>Returns the limits, selecting the smallest voltage range covering <paramref name="requiredVolts"/> if the model has ranges.</summary>
    Task<ChannelLimits> GetLimitsAsync(double requiredVolts, CancellationToken ct);
    Task<ChannelSnapshot> ReadStateAsync(CancellationToken ct);
    /// <summary>Output off and conservative setpoints.</summary>
    Task ApplySafeStateAsync(CancellationToken ct);
    /// <summary>Sets V, I and OVP, disables OCP, verifies. Does NOT enable the output.</summary>
    Task ConfigureAsync(ChargeSetpoint sp, CancellationToken ct);
    Task SetCurrentLimitAsync(double amps, CancellationToken ct);
    Task SetVoltageAsync(double volts, CancellationToken ct);
    Task SetOutputAsync(bool on, CancellationToken ct);
    Task<Measurement> MeasureAsync(CancellationToken ct);
    Task<bool> GetOutputAsync(CancellationToken ct);
    /// <summary>Reads <c>SYST:ERR?</c> until "No error"; returns the non-trivial entries.</summary>
    Task<IReadOnlyList<string>> DrainErrorsAsync(CancellationToken ct);
    /// <summary>Restores previous setpoints; the output stays OFF.</summary>
    Task RestoreAsync(ChannelSnapshot snapshot, CancellationToken ct);
}
