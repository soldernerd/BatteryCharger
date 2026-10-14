# BatteryCharger: Implementation Specification

Windows desktop application (C# / .NET 8 / WinUI 3) that turns Keysight/Agilent lab power supplies into programmable battery chargers. The user selects a power supply and describes the battery (technology, nominal voltage, capacity). The app chooses a charging profile, runs the charge, and logs everything.

---

## 0. Notes for the implementer (Claude Code)

1. **Do not invent SCPI.** Section 5 lists the intended commands per instrument family, but items marked **[VERIFY]** must be checked against the official programming guides (Keysight E36100/E36200/E36300 Programming Guide, E3640A-Series User's Guide) before being relied on. If you cannot access the docs, implement the command behind the dialect interface, mark it with a `// VERIFY` comment, and list it in `docs/verification-checklist.md`.
2. **WinUI 3 only builds on Windows.** Build environment: Windows 10/11 x64, .NET 8 SDK, Windows App SDK (latest stable), Visual Studio 2022 with the "Windows application development" workload (or equivalent MSBuild). The Core library must stay UI-free and build/test on any OS.
3. **No hardware is available during development.** Everything that touches an instrument sits behind interfaces (section 4/5). Keep `BatteryCharger.Core` free of VISA and UI references so it can be tested without hardware.
4. Work milestone by milestone (section 12). Each milestone must build cleanly and meet its acceptance criteria before the next starts.
5. Battery charging is hazardous. Where this document says **refuse**, **abort** or **fault**, implement exactly that. Do not loosen safety limits for convenience.

---

## 1. Scope

### 1.1 In scope (v1)
- Discover and connect to Keysight/Agilent DC power supplies over **USB and LAN via VISA**.
- Supported families: **E36100, E36200, E36300** series and **E3640A-E3649A** series (the latter reached via VISA, e.g. through a GPIB-USB/LAN adapter, resource strings like `GPIB0::5::INSTR`).
- Battery technologies: **Li-ion, LiPo, LiFePO4, NiMH, NiCd, Lead-acid (SLA/AGM)**.
- User inputs: power supply (+ channel/output), technology, nominal voltage, capacity, charge rate (optional, defaulted).
- Automatic profile selection with preflight safety checks, running the charge, live display, **live voltage/current chart**, and **CSV logging**.

### 1.2 Out of scope (v1)
- Discharging, cycling, impedance measurement, temperature sensing.
- Multiple simultaneous charge sessions (architecture must not prevent it later: no static/global session state).
- HV-LiPo (4.35 V/cell), Li-titanate, supercaps, or custom user-defined profiles.
- Non-Keysight instruments.

### 1.3 Not requested but strongly recommended
A simulated power supply (`FakePowerSupply : IPowerSupply`) and unit tests for the profile/state-machine logic. They were not requested and are not in the milestones, but without hardware access they are the only way to exercise the charge logic during development. The design below keeps them easy to add.

---

## 2. Technology stack

| Concern | Choice |
|---|---|
| Language / runtime | C# 12, .NET 8, `Nullable` enabled, `TreatWarningsAsErrors` in Core/Instruments |
| UI | WinUI 3 (Windows App SDK, latest stable), unpackaged, self-contained, x64 only (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`) |
| Target framework (App) | `net8.0-windows10.0.19041.0`, min `10.0.17763.0` |
| MVVM | `CommunityToolkit.Mvvm` (source generators), `x:Bind` in XAML |
| DI / config | `Microsoft.Extensions.Hosting` (DI, logging) |
| Instrument I/O | VISA via `Ivi.Visa` interfaces (`GlobalResourceManager`) with an installed vendor VISA (Keysight IO Libraries Suite or NI-VISA, 64-bit). Drivers depend only on `IVisaTransport`. **[VERIFY]** package/runtime compatibility with net8.0-windows. If `Ivi.Visa` cannot be used reliably, implement `IVisaTransport` with P/Invoke against `visa64.dll` (`viOpenDefaultRM`, `viFindRsrc`, `viOpen`, `viWrite`, `viRead`, `viClose`, `viSetAttribute`). |
| Chart | LiveCharts2 (`LiveChartsCore.SkiaSharpView.WinUI`); fallback ScottPlot 5 WinUI if LiveCharts2 has blocking WinUI 3 issues |
| Diagnostics logging | `Microsoft.Extensions.Logging` + Serilog file sink, rolling files |
| Settings | `System.Text.Json`, file in `%LOCALAPPDATA%\BatteryCharger\settings.json` |
| CSV | Hand-written writer (invariant culture); no dependency |

Prerequisite for end users: a VISA implementation (Keysight IO Libraries Suite recommended) must be installed. The app must detect a missing VISA at startup and show an actionable error page instead of crashing.

---

## 3. Solution layout

```
BatteryCharger.sln
src/
  BatteryCharger.Core/            // net8.0, no UI, no VISA
    Batteries/                    // BatteryDescriptor, BatteryTechnology, CellChemistryInfo
    Profiles/                     // ChargeProfile, IChargeStage, stage implementations, ChargeProfileFactory
    Session/                      // ChargeSession, ChargeState, SafetyMonitor, Sample, SessionResult
    Instruments/                  // IPowerSupply, IPowerSupplyChannel, ChannelLimits, Measurement (interfaces/DTOs only)
    Logging/                      // ISessionLogger, CsvSessionLogger
  BatteryCharger.Instruments/     // net8.0-windows (VISA)
    Transport/                    // IVisaTransport, VisaNetTransport, (optional) NativeVisaTransport
    Discovery/                    // InstrumentDiscovery, VisaResourceInfo
    Drivers/                      // KeysightSupplyBase, E363xxDriver, E364xADriver, DriverFactory
  BatteryCharger.App/             // WinUI 3
    Views/ ViewModels/ Services/ Controls/ Assets/
docs/
  verification-checklist.md
```

Dependencies: `App` -> `Core`, `Instruments`; `Instruments` -> `Core`; `Core` -> nothing.

---

## 4. Domain model (Core)

```csharp
public enum BatteryTechnology { LiIon, LiPo, LiFePO4, NiMH, NiCd, LeadAcid }

public sealed record BatteryDescriptor(
    BatteryTechnology Technology,
    double NominalVoltage,        // V, pack nominal as entered by the user
    double CapacityAh,            // rated capacity in Ah (UI also accepts mAh)
    int    CellsInSeries,         // derived from NominalVoltage, user-overridable
    double ChargeRateC);          // C-rate for the main charge stage, default per technology

public readonly record struct Measurement(DateTimeOffset Time, double Volts, double Amps);

public sealed record ChannelLimits(double MaxVolts, double MaxAmps, double MinVolts, double MinAmps);

public interface IPowerSupply : IAsyncDisposable
{
    string Resource { get; }
    InstrumentIdentity Identity { get; }          // Manufacturer, Model, Serial, Firmware
    IReadOnlyList<IPowerSupplyChannel> Channels { get; }
}

public interface IPowerSupplyChannel
{
    int Index { get; }                              // 1-based
    string DisplayName { get; }                     // e.g. "CH1 (6 V / 5 A)"
    Task<ChannelLimits> GetLimitsAsync(double requiredVolts, CancellationToken ct); // selects voltage range if the model has ranges
    Task<ChannelSnapshot> ReadStateAsync(CancellationToken ct);   // output on/off, setpoints, protection state
    Task ApplySafeStateAsync(CancellationToken ct);               // output off + conservative setpoints
    Task ConfigureAsync(ChargeSetpoint sp, CancellationToken ct); // sets V, I, OVP; OCP disabled; does NOT enable output
    Task SetCurrentLimitAsync(double amps, CancellationToken ct); // stage transitions
    Task SetVoltageAsync(double volts, CancellationToken ct);
    Task SetOutputAsync(bool on, CancellationToken ct);
    Task<Measurement> MeasureAsync(CancellationToken ct);
    Task<bool> GetOutputAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> DrainErrorsAsync(CancellationToken ct);  // SYST:ERR? until "No error"
    Task RestoreAsync(ChannelSnapshot snapshot, CancellationToken ct);   // restore previous setpoints, output stays OFF
}

public sealed record ChargeSetpoint(double Volts, double AmpsLimit, double OvpVolts);
```

All instrument calls are `async`, take a `CancellationToken`, and throw `InstrumentException` (with SCPI error text) on failure.

---

## 5. Instrument layer

### 5.1 Transport and discovery
- `IVisaTransport`: `Open(resource, timeoutMs)`, `WriteAsync(string)`, `QueryAsync(string)`, `Close()`. Termination `\n`, default timeout 5 s, configurable. For `...::SOCKET` resources enable termination character `\n` for read and write.
- **Serialize** all I/O per instrument (one in-flight command at a time) via a `SemaphoreSlim` or a dedicated worker thread/channel. VISA calls are blocking; never run them on the UI thread.
- **Discovery:** `GlobalResourceManager.Find("?*INSTR")` for USB/GPIB/etc. LAN instruments are often *not* found by VISA enumeration, so the UI must also let the user **add a LAN instrument by hostname/IP**, which becomes `TCPIP0::<host>::inst0::INSTR` (also accept a full pasted resource string, including `TCPIP0::<host>::5025::SOCKET`). Persist manually added instruments in settings.
- For each candidate, open it, query `*IDN?`, close. Only instruments whose manufacturer matches `Keysight|Agilent|Hewlett-Packard` **and** whose model matches a supported family are offered as chargers. Others are listed greyed out with "not a supported power supply". USB vendor IDs: `0x2A8D` (Keysight), `0x0957` (Agilent legacy). Do not rely on them exclusively.
- Discovery runs asynchronously with a cancel button and per-instrument timeout (2 s) so the UI never freezes.

### 5.2 Driver selection
Parse `*IDN?` (`manufacturer,model,serial,firmware`; model = field 2, trimmed):

| Regex on model | Driver |
|---|---|
| `^E36[123]\d\d[AB]?$` (E36100/E36200/E36300) | `E363xxDriver` |
| `^E364\dA$` (E3640A-E3649A) | `E364xADriver` |

Unknown models: refuse with a clear message. Keep the regex table in one place so adding models is a one-line change.

### 5.3 Rules for all drivers
- **Never send `*RST`.** The user may be using other channels/outputs on the same instrument. Touch only the selected channel.
- After every configuration sequence call `DrainErrorsAsync`; any non-zero error aborts the start with the error text.
- Read back setpoints (`VOLT?`, `CURR?`) after configuring and compare within tolerance (the instrument may clamp). Mismatch beyond 1 % or 5 mV/mA -> abort.
- **Over-voltage protection (OVP): enable** at the level computed by the profile (section 6.5).
- **Over-current protection (OCP): explicitly DISABLE.** On these supplies OCP trips the output when the supply enters constant-current mode, which is the *normal* state of a charger in the CC stage. Current is monitored by software instead (section 7.4).
- Output on/off is the only action allowed to be issued without a preceding configure.
- Safe sequence to start: `OUTPUT OFF` -> clear errors -> (range) -> set `VOLT` -> set `CURR` -> set OVP level + enable -> disable OCP -> verify errors/readback -> `OUTPUT ON`.
- Safe sequence to stop: `OUTPUT OFF` -> verify `OUTP?` is off (retry up to 3 times) -> optionally restore the previous setpoints (section 5.6).

### 5.4 SCPI map (intended; verify per model)

Common: `*IDN?`, `*CLS`, `SYST:ERR?` (loop until `+0,"No error"`), `*OPC?`.

**E363xx dialect** (E36100 / E36200 / E36300). Append the channel list `,(@n)` or ` (@n)` only when the instrument has more than one channel.

| Action | SCPI |
|---|---|
| Output state | `OUTP? (@n)` / `OUTP ON,(@n)` / `OUTP OFF,(@n)` |
| Set voltage / current | `VOLT <v>,(@n)` / `CURR <a>,(@n)` |
| Limits | `VOLT? MAX,(@n)`, `VOLT? MIN,(@n)`, `CURR? MAX,(@n)`, `CURR? MIN,(@n)` |
| OVP | `VOLT:PROT <v>,(@n)`, `VOLT:PROT:STAT ON,(@n)` **[VERIFY]** |
| OCP disable | `CURR:PROT:STAT OFF,(@n)` **[VERIFY]** |
| Clear protection trip | `OUTP:PROT:CLE (@n)` **[VERIFY]** |
| Measure | `MEAS:VOLT? (@n)`, `MEAS:CURR? (@n)` |
| Channel probing | for n = 1..4 send `VOLT? MAX,(@n)`, then `SYST:ERR?`; a channel exists if no error. Drain the error queue afterwards. Fall back to a model table if probing misbehaves. **[VERIFY]** |
| Voltage ranges | Some E36200 models have selectable ranges **[VERIFY]**. Handle via `GetLimitsAsync(requiredVolts)`. |

**E364xA dialect** (E3640A-E3649A):

| Action | SCPI |
|---|---|
| Select output (dual models E3646A-E3649A) | `INST:SEL OUT1` / `INST:SEL OUT2` **[VERIFY]** |
| Voltage range (models with ranges) | `VOLT:RANG P8V|P20V|P35V|P60V` (enumeration depends on model) **[VERIFY]**. Select the smallest range that covers the required voltage **before** setting `VOLT`/`CURR`, since changing the range resets setpoints. After selecting, read `VOLT? MAX` / `CURR? MAX` for the true limits at that range. |
| Output | `OUTP ON|OFF`, `OUTP?`. **[VERIFY]** whether this affects both outputs on dual models. If it does, the UI must warn that the other output will be energized too and show its present setpoints (read before start). |
| Setpoints | `VOLT <v>`, `CURR <a>` |
| OVP | `VOLT:PROT <v>`, `VOLT:PROT:STAT ON` **[VERIFY]** |
| OCP | `CURR:PROT:STAT OFF` if the model supports it **[VERIFY]** |
| Measure | `MEAS:VOLT?`, `MEAS:CURR?` |
| Remote | `SYST:REM` may be required on serial transports; send it and ignore a "command not supported" error **[VERIFY]** |

### 5.5 CV/CC mode
Do not depend on instrument status registers (bit assignments differ per family **[VERIFY]**). Infer mode from measurements: `I >= 0.98 * Iset` -> CC; otherwise CV; output off -> OFF. Status-register readout may be added later as an enhancement.

### 5.6 Snapshot and restore
Before touching a channel, `ReadStateAsync` captures output state, `VOLT?`, `CURR?`, OVP level/state, OCP state. If the output is **already ON**, show a confirmation ("Output is currently on; it will be switched off before charging"). After the session ends (any reason), restore the captured setpoints with the output left **OFF** (setting a user-configurable "restore previous setpoints" option, default on).

### 5.7 Fault tolerance
- Transient I/O errors: retry up to 3 times within 3 s, then raise `InstrumentCommunicationLost`.
- On `InstrumentCommunicationLost` during a session: try `SetOutputAsync(false)` up to 3 times, then fault the session and tell the user clearly that the output state is unknown and the supply must be checked manually.

---

## 6. Charging profiles

Profiles are **pure data + logic in Core**, built by `ChargeProfileFactory.Create(BatteryDescriptor, ProfileOptions)` into an ordered list of `IChargeStage`. They are independent of VISA.

### 6.1 Cell count derivation
`CellsInSeries = round(NominalVoltage / CellNominal)`; warn (not block) if the result differs from the entered voltage by more than 8 % of one cell, and let the user override.

| Technology | Cell nominal |
|---|---|
| Li-ion | 3.6 V |
| LiPo | 3.7 V |
| LiFePO4 | 3.2 V |
| NiMH / NiCd | 1.2 V |
| Lead-acid | 2.0 V |

### 6.2 Default parameters (per cell unless noted; C = capacity in Ah)

| | Li-ion / LiPo | LiFePO4 | NiMH | NiCd | Lead-acid (SLA/AGM) |
|---|---|---|---|---|---|
| Default charge rate | 0.5 C | 0.5 C | 0.5 C | 0.5 C | 0.2 C |
| Max allowed rate | 1.0 C | 1.0 C | 1.0 C | 1.0 C | 0.3 C |
| Target (CV/absorption) voltage | 4.20 V | 3.65 V | n/a (ceiling 1.65 V) | n/a (ceiling 1.65 V) | 2.40 V |
| Refuse below | 2.0 V | 2.0 V | 0.5 V | 0.5 V | 1.5 V |
| Pre-charge below | 3.0 V at 0.1 C | 2.8 V at 0.1 C | none | none | none |
| Termination | CV and I < 0.05 C | CV and I < 0.05 C | -dV (5 mV) | -dV (10 mV) | absorption and I < 0.03 C |
| Float | none | none | none | none | 2.27 V until stopped |
| Max charge delivered | 1.3 x C | 1.3 x C | 1.5 x C | 1.5 x C | 1.4 x C (before float) |
| OVP margin above target/ceiling | +0.10 V | +0.15 V | +0.10 V | +0.10 V | +0.20 V |

Additional rules:
- Termination current floor: `max(C-based threshold, 10 mA)` (measurement resolution).
- Pre-charge timeout 30 min; if the voltage has not reached the pre-charge exit level, fault ("cell not recovering").
- CC stage timeout: `1.5 * C / Icc` hours (Li, LFP, lead-acid bulk), `1.6 * C / Icc` (NiMH/NiCd).
- CV / absorption timeout: Li and LFP 3 h; lead-acid 8 h.
- All parameters live in a `ProfileOptions` object, exposed in an **Advanced** expander in the UI. They are validated against the hard limits in the table (max rate, max target voltage). Never accept values beyond the hard limits.
- Charge current is clamped to the supply's `MaxAmps` at the required voltage range. If clamped, inform the user (it will take longer).
- Pack target voltage must be <= supply `MaxVolts`. If not, **refuse** with an explanation.

### 6.3 Stage logic

All stages run with the output **on continuously**; the PSU's hardware regulation provides CC/CV. Software only changes setpoints at stage transitions and evaluates conditions on each sample.

**Li-ion / LiPo / LiFePO4**
1. *Pre-charge* (only if V/cell < pre-charge threshold): `V = Vtarget`, `I = 0.1 C`. Exit when V/cell >= threshold. Timeout -> fault.
2. *CC*: `V = Vtarget`, `I = Icc`. Exit to CV when `V >= Vtarget - 1 %` or `I < 0.98 * Icc`.
3. *CV*: `V = Vtarget`. Complete when `I < Iterm` holds for 10 consecutive samples. Timeout -> terminate with reason "CV timeout" (warning, not success).
4. *Completed*: output off.

**NiMH / NiCd**
1. *CC*: `I = Icc`, `V = ceiling = 1.65 V * cells`.
2. -dV detection is armed only after both >= 10 min elapsed **and** >= 10 % of rated capacity delivered. Track the running peak of a 5-sample median-filtered voltage; complete when `V_filtered < Vpeak - cells * dV_cell` for 3 consecutive samples.
3. Also terminate (reason "voltage ceiling reached", reported as completed-with-warning) if the supply sits at the ceiling (CV mode) for 60 s.
4. Charge-delivered and timeout limits apply as backstops. No trickle in v1.

**Lead-acid (SLA/AGM)**
1. *Bulk (CC)*: `V = absorption`, `I = Icc`. Exit when `V >= absorption - 1 %` or `I < 0.98 * Icc`.
2. *Absorption (CV)*: hold until `I < Iterm` for 10 samples, or timeout.
3. *Float*: `V = float`, `I = Icc` (unchanged). Continues until the user stops the session. State shows "Charged, floating". Log rate drops to once per 10 s in float. The charge-delivered limit does not apply during float.

### 6.4 Preflight checks (before output is ever enabled)
1. Output off. Measure terminal voltage `Vbat`.
2. `Vbat < -0.1 V` -> **refuse**: "Reverse polarity detected".
3. Plausibility using derived cells: `Vbat/cell < refuse threshold` -> **refuse** (battery missing, deeply discharged or damaged).
4. `Vbat > 1.05 * Vtarget` -> **refuse**: "Battery voltage exceeds target; check technology and cell count". Show a hint: "Measured voltage suggests N cells" (Li: `ceil(Vbat / 4.25)`, others analogous).
5. `Vbat >= 0.99 * Vtarget` -> warn "Battery appears already charged", allow proceeding (the profile skips to CV/absorption).
6. If `Vbat` is far below nominal but above the refuse threshold, select pre-charge per 6.2.
7. Show a required **confirmation dialog**: polarity correct, technology and cell count correct, battery undamaged (not swollen/hot/leaking), supervision acknowledged. Start stays disabled until confirmed.

### 6.5 Safety envelope (set before output on)
- Supply OVP = `Vtarget_pack + OvpMarginPerCell * cells` (or ceiling-based for NiMH/NiCd), enabled.
- Supply current limit = stage current; OCP disabled (see 5.3).

---

## 7. Charge session engine (Core)

### 7.1 States
`Idle -> Preflight -> Precharge? -> ConstantCurrent -> ConstantVoltage -> (Float) -> Completed`; terminal states: `Completed`, `Stopped` (user), `Faulted`. NiMH/NiCd use `ConstantCurrent -> Completed`.

```csharp
public sealed class ChargeSession : IAsyncDisposable
{
    public ChargeSession(IPowerSupplyChannel channel, BatteryDescriptor battery,
                         ChargeProfile profile, ISessionLogger logger, SessionOptions options);
    public ChargeState State { get; }
    public event EventHandler<SessionUpdate>? Updated;     // raised on every sample (marshalled by the UI layer)
    public Task<SessionResult> RunAsync(CancellationToken ct);   // returns when terminal
    public Task RequestStopAsync();                               // user stop
}
```

### 7.2 Control loop
- One `PeriodicTimer` loop on a background task; poll interval default 1 s (range 0.5-10 s, setting).
- Each tick: `MeasureAsync` -> build `Sample` (elapsed via `Stopwatch`, not wall-clock) -> integrate `Ah += 0.5 * (I_prev + I) * dt / 3600`, `Wh` likewise with power -> `SafetyMonitor.Check` -> `Stage.Evaluate` -> log -> raise `Updated`.
- Stage transitions apply new setpoints via the channel, then `DrainErrorsAsync`.
- A tick that takes longer than 3x the interval is logged as an event (not silently ignored).

### 7.3 Stop and shutdown
- `RequestStopAsync` turns the output off within one tick (target < 1 s), sets state `Stopped`, writes the footer, restores the snapshot.
- Any unhandled exception in the loop: best-effort output off (3 tries), state `Faulted`, footer written, error shown.
- App window close during an active session: confirmation dialog "Stop charging and exit" / "Cancel". Closing is never allowed to silently leave the output on.
- Prevent Windows sleep while a session is active (`SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)`), released at session end.

### 7.4 Safety monitor (evaluated every sample; faults abort the session and turn the output off)
| Condition | Action |
|---|---|
| `V > Vtarget + 0.5 * OvpMargin` for 3 samples | Fault: over-voltage |
| `I > 1.10 * Iset + 20 mA` for 3 samples | Fault: over-current |
| Output unexpectedly off (e.g., OVP trip), detected via `OUTP?` every 5 ticks or `V` collapsing with `I = 0` | Fault: output tripped |
| In CC/pre-charge/bulk after a 5 s grace period: `I < 0.5 * Iset` for 5 samples while `V < 0.97 * Vtarget` | Fault: battery disconnected or current not reaching battery |
| Delivered charge > max factor x C | Terminate: "charge limit reached" (warning) |
| Stage timeout | Pre-charge/CC: fault; CV/absorption: terminate with warning |
| Instrument error queue non-empty | Fault with SCPI text |
| Communication lost | Section 5.7 |

Known limitation to document in the UI/README: if the PC or app dies, the supply keeps its output state and CV/CC limits (hardware regulation remains active, but there is no time cutoff). Say so explicitly in the confirmation dialog and docs.

### 7.5 Result
`SessionResult { EndReason (Completed, CompletedWithWarning, UserStopped, Fault), Message, Duration, AhDelivered, WhDelivered, FinalVoltage, LogFilePath }`.

---

## 8. Logging

### 8.1 CSV session log
- Location: default `%USERPROFILE%\Documents\BatteryCharger\Logs\` (configurable).
- File name: `yyyyMMdd_HHmmss_<model>_<tech>_<nominalV>V_<capacity>Ah.csv`.
- Encoding UTF-8 (with BOM for Excel), invariant culture, `.` decimal, separator `,` by default (setting: `;` for Swiss/German Excel).
- Header block of `#` comment lines: app version, start time (local + UTC offset), instrument IDN, resource string, channel, battery parameters, derived cells, full profile parameters (all stage setpoints, thresholds, timeouts), poll interval.
- Then a column header row and one row per sample:

`timestamp_local, elapsed_s, state, stage, voltage_v, current_a, power_w, charge_ah, energy_wh, voltage_set_v, current_limit_a, psu_mode, event`

- `event` holds stage changes, warnings, user actions, and communication retries (empty otherwise).
- Footer comment lines: end reason, message, duration, Ah, Wh, final voltage.
- Flush to disk at least every 5 s and on every event line. A crash must lose at most a few seconds of data.
- If the log file cannot be created or written, do **not** start (or abort) the session; charging without logging is not allowed.

### 8.2 Diagnostics log
Serilog rolling file in `%LOCALAPPDATA%\BatteryCharger\diagnostics\`. Settings toggle "Log SCPI traffic" (default off) writes every command/response with timestamps.

---

## 9. User interface (WinUI 3)

Fluent design, Mica backdrop, light/dark follows system, minimum window 1000 x 700, resizable, keyboard accessible, `AutomationProperties.Name` on all controls, UI language English. Numbers shown with the user's culture; files use invariant.

`NavigationView` with: **Charger** (main), **Settings**.

### 9.1 Charger page, idle state (two-column setup, summary at the right)
**Power supply card**
- Dropdown of discovered supplies (model, serial, resource), **Refresh** button, **Add LAN instrument...** dialog (host/IP or full resource string), **Connect** / **Disconnect**.
- Once connected: identity line (model, serial, firmware), **channel/output** selector (if more than one) showing each channel's ratings, e.g. "CH1 (6 V / 5 A)".

**Battery card**
- Technology (combo), nominal voltage (number box, V, with common presets per technology, e.g., 3.7/7.4/11.1 V; 6/12/24 V), capacity (number box with unit toggle Ah/mAh), cells in series (auto, editable), charge rate (C, number box with default per technology and hard max).
- **Advanced** expander: all `ProfileOptions` (section 6.2) with per-field reset-to-default and hard-limit validation messages.

**Profile summary card** (read-only, updates live as inputs change)
- Ordered list of stages with setpoints, e.g. "CC: 4.20 V limit, 2.50 A", "CV: 4.20 V until I < 250 mA", plus safety limits (OVP, timeouts, max charge). Shows warnings (current clamped by supply, voltage exceeding supply range) and blocking errors inline.

**Start** button: enabled only when a supply is connected, inputs are valid, and no blocking error exists. Start runs preflight (section 6.4) and shows the results and the confirmation dialog.

### 9.2 Charger page, charging state
- Header: state, stage, elapsed time, session file name (link: open folder).
- Large readouts: voltage, current, power, charge (Ah / mAh), energy (Wh), PSU mode (CC/CV), setpoints.
- **Live chart:** voltage (left axis) and current (right axis) vs elapsed time, full session history, stage transitions as vertical markers, hover tooltip with exact values. Decimate when > 20 000 points (e.g., min/max bucket decimation); chart updates must not block the UI thread (batch to <= 4 updates/s).
- **Stop** button (prominent, always enabled, confirmation not required).
- Banner area for warnings/faults; faults persist until dismissed.
- On completion: result summary card (end reason, duration, Ah, Wh, final voltage, "Open log"), and a **New charge** button.

### 9.3 Settings page
Poll interval, CSV separator, log folder, restore previous supply setpoints (default on), SCPI traffic logging, VISA timeout, theme override, manage saved LAN instruments, "About" (version, VISA implementation found, log locations).

### 9.4 UI engineering rules
- ViewModels use `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]` with async commands and cancellation).
- All session updates reach the UI via `DispatcherQueue`; never touch UI objects from the session thread.
- Long operations (discovery, connect, preflight) show progress/busy state and are cancellable.
- No blocking `.Result` / `.Wait()` anywhere. No `async void` except event handlers.

---

## 10. Settings persistence
`settings.json` (versioned, tolerant of missing/unknown fields): last-used instrument resource, saved LAN instruments, last battery inputs, advanced overrides (not persisted unless explicitly saved; hard limits re-validated on load), poll interval, CSV separator, log folder, restore-setpoints flag, SCPI trace flag, VISA timeout, theme.

---

## 11. Non-functional requirements
- Poll loop jitter: sample spacing within +-20 % of the configured interval under normal conditions.
- Cold start to usable UI < 3 s (discovery is async).
- Memory: a 24 h session at 1 s sampling must not exceed ~300 MB total; keep chart data as compact arrays.
- Handle disconnect/reconnect of USB and LAN gracefully (errors surfaced, no crashes).
- Missing VISA: friendly error page with install pointer (Keysight IO Libraries Suite), the rest of the UI disabled.
- Code quality: analyzers on, nullable annotations honored, public API XML docs on Core, one class per file, no static mutable state.

---

## 12. Milestones and acceptance criteria

**M0 - Scaffold.** Solution, three projects, DI/hosting, logging, NavigationView shell with empty Charger/Settings pages, missing-VISA detection page. *Accept:* builds and launches on Windows; Core builds on any OS.

**M1 - Instrument layer.** `IVisaTransport`, discovery, `*IDN?` parsing, driver factory, `E363xxDriver` and `E364xADriver` (all methods in section 4), connection UI (supply picker, add LAN, channel selector, identity display). Include a hidden/debug "SCPI console" page (send a command, show the response, trace log) to ease hardware bring-up. *Accept:* connect, read identity, read channel limits, measure V/I with output off, on at least one real supply of each family; errors shown cleanly on unplug/timeout.

**M2 - Profiles.** Battery descriptor, cell derivation, `ProfileOptions` with hard limits, `ChargeProfileFactory`, all six technologies per section 6, profile summary UI. *Accept:* changing any input updates the summary correctly; limits enforced; clamp/refuse messages appear as specified.

**M3 - Session engine and logging.** `ChargeSession`, safety monitor, preflight, state machine, snapshot/restore, `CsvSessionLogger`, sleep prevention. *Accept:* a full Li-ion charge of a real cell completes with correct stage transitions, Ah/Wh integration plausible vs. supply display, CSV matches section 8, stop works in < 1 s, injected failures (cable pulled mid-charge, USB unplug, LAN drop) end in a safe, logged fault.

**M4 - Charging UI.** Readouts, live chart, Stop, banners, result card, close-window protection. *Accept:* UI stays responsive at 0.5 s polling for 1 h; chart shows stage markers; no cross-thread exceptions.

**M5 - Hardening.** Advanced options UX, settings page, persistence, accessibility pass, error-message polish, `docs/verification-checklist.md` completed, README (prerequisites, supported models, safety notes, limitations). *Accept:* all **[VERIFY]** items either resolved or explicitly listed with the chosen fallback.

---

## 13. Verification checklist (to be completed on real hardware)
1. Each **[VERIFY]** SCPI command in section 5.4 per model family; record the exact syntax used.
2. Does `OUTP` on dual-output E364xA units affect both outputs? Document behavior and UI warning.
3. Does `*IDN?` on the available units return the model in field 2 (older units: `HEWLETT-PACKARD,...`)?
4. Voltage-range enumerations and the max current per range for each owned E364xA model.
5. Channel probing behavior on E36300 (and E36200 if owned), including error-queue side effects.
6. OCP state after power-up and that disabling it works on every model; confirm CC mode no longer trips the output.
7. Behavior of measurement with output off (does `MEAS:VOLT?` report the battery voltage?). If not, adjust preflight (e.g., briefly enable output at minimum current limit, or require the user to confirm the voltage).
8. Accuracy/resolution of `MEAS:CURR?` at low currents (affects the 10 mA termination floor).
9. USB-TMC and LAN (VXI-11 `inst0` and raw socket 5025) both work through the chosen VISA package on a clean Windows install.
10. Behavior when the PC sleeps/loses connection mid-charge (confirm the documented limitation).

---

## 14. Risks and design decisions to keep in mind
- **Wrong cell count / chemistry** is the most dangerous user error. The preflight plausibility checks and confirmation dialog exist for this; keep them strict.
- **No temperature sensing**: NiMH/NiCd -dV detection and all termination logic rely on voltage/current/time only. State this in the README. Defaults are conservative (0.5 C).
- **Cable voltage drop**: measured voltage is at the supply terminals (no remote sensing in v1), so the true CV point at the cells is slightly lower at high current. Documented; acceptable for v1.
- **PSU cannot sink**: if the battery voltage exceeds the setpoint, the supply cannot discharge it; the preflight refuse rules prevent this.
- **Uncontrolled shutdown**: see 7.4 note; there is no instrument-side watchdog assumed.
