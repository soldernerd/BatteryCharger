namespace BatteryCharger.Core.Instruments;

/// <summary>Programmable limits of a channel (at the selected range).</summary>
public sealed record ChannelLimits(double MaxVolts, double MaxAmps, double MinVolts, double MinAmps);
