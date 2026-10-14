namespace BatteryCharger.Core.Profiles;

/// <summary>What the session must do after a stage evaluated a sample.</summary>
public enum StageAction { Continue, Advance, Complete, CompleteWithWarning, Fault }

/// <summary>Result of <see cref="IChargeStage.Evaluate"/>.</summary>
public readonly record struct StageDecision(StageAction Action, string? Message = null)
{
    public static readonly StageDecision Continue = new(StageAction.Continue);
}
