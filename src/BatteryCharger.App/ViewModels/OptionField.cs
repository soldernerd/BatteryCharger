using BatteryCharger.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatteryCharger.App.ViewModels;

/// <summary>One editable <see cref="ProfileOptions"/> parameter in the Advanced expander.</summary>
public sealed partial class OptionField : ObservableObject
{
    public OptionField(string id, string label, string unit, double defaultValue,
        Func<ProfileOptions, double> read, Func<ProfileOptions, double, ProfileOptions> write, double current)
    {
        Id = id;
        Label = label;
        Unit = unit;
        DefaultValue = defaultValue;
        Read = read;
        Write = write;
        _value = current;
    }

    public string Id { get; }
    /// <summary>Starts with the same words as the validation messages of <see cref="ProfileOptions.Validate"/>.</summary>
    public string Label { get; }
    public string Unit { get; }
    public double DefaultValue { get; }
    public Func<ProfileOptions, double> Read { get; }
    public Func<ProfileOptions, double, ProfileOptions> Write { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDefault))]
    private double _value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = "";

    public bool IsDefault => Math.Abs(Value - DefaultValue) < 1e-9;
    public bool HasMessage => Message.Length > 0;
    public string Header => $"{Label} ({Unit})";

    public event EventHandler? ValueEdited;

    partial void OnValueChanged(double value) => ValueEdited?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Reset() => Value = DefaultValue;
}
