using BatteryCharger.Core.Instruments;
using BatteryCharger.Instruments.Drivers;

namespace BatteryCharger.Tests.Instruments;

public class DriverTests
{
    private static ScpiLink Link(ScriptedTransport t) =>
        new(t, null, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(1));

    private static async Task<IPowerSupply> Connect(ScriptedTransport t, string idn)
    {
        var link = Link(t);
        return await DriverFactory.CreateAsync("TEST", InstrumentIdentity.Parse(idn), link, default);
    }

    [Theory]
    [InlineData("E36103A", true)]
    [InlineData("E36233A", true)]
    [InlineData("E36312A", true)]
    [InlineData("E3631A", false)]
    [InlineData("E3640A", true)]
    [InlineData("E3649A", true)]
    [InlineData("E3650A", false)]
    [InlineData("34401A", false)]
    public void ModelTable(string model, bool supported) =>
        Assert.Equal(supported, SupportedModels.TryResolve(model, out _));

    [Theory]
    [InlineData("Keysight Technologies", true)]
    [InlineData("Agilent Technologies", true)]
    [InlineData("HEWLETT-PACKARD", true)]
    [InlineData("Rigol", false)]
    public void ManufacturerMatch(string m, bool ok) => Assert.Equal(ok, SupportedModels.IsKeysightManufacturer(m));

    [Fact]
    public void IdnParsing()
    {
        var id = InstrumentIdentity.Parse("Keysight Technologies,E36312A,MY12345678,1.7.3-1.0.4");
        Assert.Equal("E36312A", id.Model);
        Assert.Equal("MY12345678", id.Serial);
        Assert.Equal("1.7.3-1.0.4", id.Firmware);
    }

    [Fact]
    public async Task UnsupportedInstrumentIsRefused()
    {
        var t = new ScriptedTransport();
        await Assert.ThrowsAsync<InstrumentException>(() => Connect(t, "Rigol,DP832,1,1"));
    }

    [Fact]
    public async Task E36312A_UsesChannelListAndThreeChannels()
    {
        var t = new ScriptedTransport();
        t.Responses["VOLT? MAX,(@2)"] = "25";
        t.Responses["CURR? MAX,(@2)"] = "1";
        t.Responses["MEAS:VOLT? (@2)"] = "3.987";
        t.Responses["MEAS:CURR? (@2)"] = "0.5";
        t.Responses["OUTP? (@2)"] = "1";
        await using var psu = await Connect(t, "Keysight Technologies,E36312A,SN,1.0");
        Assert.Equal(3, psu.Channels.Count);
        Assert.Equal("CH2 (25 V / 1 A)", psu.Channels[1].DisplayName);

        var m = await psu.Channels[1].MeasureAsync(default);
        Assert.Equal(3.987, m.Volts, 6);
        Assert.Equal(0.5, m.Amps, 6);
        Assert.True(await psu.Channels[1].GetOutputAsync(default));
        await psu.Channels[1].SetOutputAsync(false, default);
        Assert.Contains("OUTP OFF,(@2)", t.Sent);
        Assert.DoesNotContain("*RST", t.Sent);
    }

    [Fact]
    public async Task E36103A_SingleChannelHasNoChannelList()
    {
        var t = new ScriptedTransport();
        t.Responses["VOLT? MAX"] = "6";
        t.Responses["CURR? MAX"] = "5";
        await using var psu = await Connect(t, "Keysight Technologies,E36103A,SN,1.0");
        Assert.Single(psu.Channels);
        await psu.Channels[0].SetVoltageAsync(4.2, default);
        Assert.Contains("VOLT 4.2", t.Sent);
    }

    [Fact]
    public async Task UnknownE363Model_ProbesChannels()
    {
        var t = new ScriptedTransport();
        t.Silent.Add("VOLT? MAX,(@3)");
        t.Silent.Add("VOLT? MAX,(@4)");
        await using var psu = await Connect(t, "Keysight Technologies,E36299A,SN,1.0");
        Assert.Equal(2, psu.Channels.Count);
    }

    [Fact]
    public async Task ConfigureFollowsSafeSequence()
    {
        var t = new ScriptedTransport();
        t.Responses["VOLT? MAX"] = "6";
        t.Responses["CURR? MAX"] = "5";
        t.Responses["VOLT?"] = "4.2";
        t.Responses["CURR?"] = "1";
        t.Responses["VOLT:PROT?"] = "4.5";
        await using var psu = await Connect(t, "Keysight Technologies,E36103A,SN,1.0");
        t.Sent.Clear();
        await psu.Channels[0].ConfigureAsync(new ChargeSetpoint(4.2, 1, 4.5), default);

        var idx = (string s) => t.Sent.IndexOf(s);
        Assert.True(idx("OUTP OFF") < idx("VOLT 4.2"));
        Assert.True(idx("VOLT 4.2") < idx("CURR 1"));
        Assert.True(idx("CURR 1") < idx("VOLT:PROT 4.5"));
        Assert.True(idx("VOLT:PROT 4.5") < idx("VOLT:PROT:STAT ON"));
        Assert.Contains("CURR:PROT:STAT OFF", t.Sent);
        Assert.True(idx("OUTP OFF") < idx("OUTP:PROT:CLE"));       // a latched trip is cleared before the new setpoints
        Assert.DoesNotContain("OUTP ON", t.Sent);          // configure never enables the output
        Assert.DoesNotContain("*RST", t.Sent);
        Assert.Contains("SYST:ERR?", t.Sent);
    }

    [Fact]
    public async Task ConfigureAbortsOnInstrumentError()
    {
        var t = new ScriptedTransport();
        await using var psu = await Connect(t, "Keysight Technologies,E36103A,SN,1.0");
        t.OnWrite = c => { if (c == "CURR 1") t.ErrorQueue.Enqueue("-222,\"Data out of range\""); };   // error raised by a setpoint, after the protection clear
        var ex = await Assert.ThrowsAsync<InstrumentException>(() => psu.Channels[0].ConfigureAsync(new ChargeSetpoint(4.2, 1, 4.5), default));
        Assert.Contains("Data out of range", ex.Message);
    }

    [Fact]
    public async Task ConfigureAbortsOnReadbackMismatch()
    {
        var t = new ScriptedTransport();
        t.Responses["VOLT?"] = "3.0";       // supply clamped the voltage
        t.Responses["CURR?"] = "1";
        t.Responses["VOLT:PROT?"] = "4.5";
        await using var psu = await Connect(t, "Keysight Technologies,E36103A,SN,1.0");
        var ex = await Assert.ThrowsAsync<InstrumentException>(() => psu.Channels[0].ConfigureAsync(new ChargeSetpoint(4.2, 1, 4.5), default));
        Assert.Contains("readback", ex.Message);
    }

    [Fact]
    public async Task DrainErrorsReturnsAllEntries()
    {
        var t = new ScriptedTransport();
        await using var psu = await Connect(t, "Keysight Technologies,E36103A,SN,1.0");
        t.ErrorQueue.Enqueue("-113,\"Undefined header\"");
        t.ErrorQueue.Enqueue("-102,\"Syntax error\"");
        var errs = await psu.Channels[0].DrainErrorsAsync(default);
        Assert.Equal(2, errs.Count);
    }

    [Fact]
    public async Task TransientErrorIsRetried()
    {
        var t = new ScriptedTransport();
        t.Responses["MEAS:VOLT?"] = "1.5";
        t.Responses["MEAS:CURR?"] = "0.1";
        await using var psu = await Connect(t, "Keysight Technologies,E36103A,SN,1.0");
        t.FailNextIo = 2;
        var m = await psu.Channels[0].MeasureAsync(default);
        Assert.Equal(1.5, m.Volts, 6);
        Assert.True(t.Clears >= 2);
    }

    [Fact]
    public async Task PersistentErrorBecomesCommunicationLost()
    {
        var t = new ScriptedTransport();
        await using var psu = await Connect(t, "Keysight Technologies,E36103A,SN,1.0");
        t.FailNextIo = 100;
        await Assert.ThrowsAsync<InstrumentCommunicationLost>(() => psu.Channels[0].MeasureAsync(default));
    }

    [Fact]
    public async Task E3646A_SelectsOutputAndRange()
    {
        var t = new ScriptedTransport();
        t.Responses["VOLT:RANG?"] = "P20V";
        t.Responses["VOLT? MAX"] = "8.24";           // read after switching to P8V
        t.Responses["CURR? MAX"] = "3";
        await using var psu = await Connect(t, "HEWLETT-PACKARD,E3646A,SN,1.0");
        Assert.Equal(2, psu.Channels.Count);
        Assert.NotNull(psu.Channels[1].OutputWarning);

        t.Sent.Clear();
        var lim = await psu.Channels[1].GetLimitsAsync(4.2, default);
        Assert.Equal(8.24, lim.MaxVolts, 6);
        Assert.Equal("INST:SEL OUT2", t.Sent[0]);
        Assert.Contains("VOLT:RANG P8V", t.Sent);
    }

    [Fact]
    public async Task ConnectingDoesNotChangeTheRange()
    {
        var t = new ScriptedTransport();
        t.Responses["VOLT:RANG?"] = "P20V";
        await using var psu = await Connect(t, "Agilent Technologies,E3640A,SN,1.0");
        Assert.DoesNotContain(t.Sent, s => s.StartsWith("VOLT:RANG P"));
    }

    [Fact]
    public async Task LargerRangeIsChosenWhenNeeded()
    {
        var t = new ScriptedTransport();
        t.Responses["VOLT:RANG?"] = "P8V";
        t.Responses["VOLT? MAX"] = "8.24";
        await using var psu = await Connect(t, "Agilent Technologies,E3640A,SN,1.0");
        t.Sent.Clear();
        // After "VOLT:RANG P20V" the instrument would report 20.6 V; emulate by switching the response when seen.
        t.OnWrite = c => { if (c == "VOLT:RANG P20V") t.Responses["VOLT? MAX"] = "20.6"; };
        var lim = await psu.Channels[0].GetLimitsAsync(12.6, default);
        Assert.Contains("VOLT:RANG P20V", t.Sent);
        Assert.Equal(20.6, lim.MaxVolts, 6);
    }
}
