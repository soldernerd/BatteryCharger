using BatteryCharger.Core.Batteries;
using BatteryCharger.Core.Profiles;

namespace BatteryCharger.App.ViewModels;

/// <summary>Builds the list of Advanced fields that apply to a technology.</summary>
public static class OptionFieldFactory
{
    public static List<OptionField> Build(BatteryTechnology tech, ProfileOptions current)
    {
        var d = ProfileOptions.Defaults(tech);
        var c = CellChemistryInfo.For(tech);
        var list = new List<OptionField>();

        void Add(string id, string label, string unit, Func<ProfileOptions, double> read, Func<ProfileOptions, double, ProfileOptions> write) =>
            list.Add(new OptionField(id, label, unit, read(d), read, write, read(current)));

        Add("target", c.IsNickel ? "Voltage ceiling" : "Target voltage", "V/cell", o => o.TargetVoltagePerCell, (o, v) => o with { TargetVoltagePerCell = v });
        if (c.PrechargeBelowV is not null)
        {
            Add("preth", "Pre-charge threshold", "V/cell", o => o.PrechargeBelowVoltagePerCell ?? 0, (o, v) => o with { PrechargeBelowVoltagePerCell = v });
            Add("prerate", "Pre-charge rate", "C", o => o.PrechargeRateC, (o, v) => o with { PrechargeRateC = v });
        }
        if (c.IsNickel)
            Add("dv", "-dV", "mV/cell", o => o.DvPerCellVolts * 1000, (o, v) => o with { DvPerCellVolts = v / 1000 });
        else
            Add("term", "Termination current", "C", o => o.TerminationCurrentC, (o, v) => o with { TerminationCurrentC = v });
        if (c.FloatV is not null)
            Add("float", "Float voltage", "V/cell", o => o.FloatVoltagePerCell ?? 0, (o, v) => o with { FloatVoltagePerCell = v });
        Add("maxcharge", "Maximum charge", "x C", o => o.MaxChargeFactor, (o, v) => o with { MaxChargeFactor = v });
        Add("ovp", "OVP margin", "V/cell", o => o.OvpMarginPerCell, (o, v) => o with { OvpMarginPerCell = v });
        Add("cctimeout", "CC timeout factor", "x C/I", o => o.CcTimeoutFactor, (o, v) => o with { CcTimeoutFactor = v });
        if (!c.IsNickel)
            Add("cvtimeout", "CV timeout", "h", o => o.CvTimeout.TotalHours, (o, v) => o with { CvTimeout = TimeSpan.FromHours(v) });
        Add("refuse", "Refuse-below voltage", "V/cell", o => o.RefuseBelowVoltagePerCell, (o, v) => o with { RefuseBelowVoltagePerCell = v });
        return list;
    }
}
