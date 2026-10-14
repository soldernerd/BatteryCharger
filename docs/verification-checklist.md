# Hardware verification checklist

No instrument was available during development. Everything below is implemented from the specification and
the public programming guides' general structure, covered by unit tests against a scripted SCPI transport, but **not yet
confirmed on real hardware**. Each item names the code, the assumption, and the fallback if the assumption is wrong.
Use **Settings -> Developer mode -> SCPI console** to try commands and watch the traffic.

Record the outcome next to each item (model, firmware, exact syntax that worked).

## SCPI syntax (spec 5.4 and 13.1)

| # | Item | Where | Assumption / fallback | Result |
|---|---|---|---|---|
| 1 | `VOLT:PROT <v>` / `VOLT:PROT:STAT ON` (OVP level and enable) on E363xx and E364xA | `KeysightChannel.ConfigureAsync` | Same syntax on both families. A rejected command shows up in the error queue and aborts the start with the SCPI text. | |
| 2 | `CURR:PROT:STAT OFF` (OCP disable) accepted by every model; OCP state after power-up | `KeysightChannel.ConfigureAsync` | If a model has no OCP command the start is aborted by the error check; then special-case that model in its channel class. | |
| 3 | `OUTP:PROT:CLE` (clear a latched trip) on E363xx (`(@n)`) and E364xA | `ProtectionClearCommand` | Errors from this command are drained and ignored so an unsupported clear never blocks charging. | |
| 4 | `VOLT:PROT?`, `VOLT:PROT:STAT?`, `CURR:PROT:STAT?` for the snapshot | `KeysightChannel.ReadStateAsync` | If a model rejects one, `ReadStateAsync` throws; make that read optional. | |
| 5 | Setting `VOLT` above the *old* OVP level: does the instrument accept it (spec order VOLT, CURR, OVP)? | `ConfigureAsync` | If it raises "settings conflict", set OVP first or lower VOLT to MIN before. | |
| 6 | Channel probing: `VOLT? MAX,(@n)` for n = 1..4 on E36200/E36300 models not in the table; error queue side effects | `DriverFactory.ProbeChannelCountAsync` | Known models (E36233A/E36234A = 2, E36311A/E36312A/E36313A = 3) use the table; extend `SupportedModels.ChannelsByModel`. A probe that times out costs one VISA timeout per missing channel. | |
| 7 | Voltage ranges of selected E36200 models | not implemented | Treated as fixed range. If a model has selectable ranges, add `SelectRangeAsync` like E364xA. | |
| 8 | `INST:SEL OUT1/OUT2` on E3646A-E3649A | `E364xAChannel.PrepareAsync` | Sent before every exclusive command sequence. | |
| 9 | `VOLT:RANG P8V|P20V|P35V|P60V` enumeration per model; `VOLT? MAX` / `CURR? MAX` after selecting | `SupportedModels.ForE364`, `E364xAChannel.SelectRangeAsync` | Even model digit = P8V/P20V, odd = P35V/P60V. The range is only changed at charge start (never when connecting or while editing inputs) because it resets setpoints. | |
| 10 | Does `OUTP` on dual E364xA affect both outputs? | `E364xAChannel` (`OutputWarning`) | Assumed yes: the UI shows a warning and the confirmation dialog repeats it. Remove or refine once known. | |
| 11 | `SYST:REM` on serial/GPIB transports | not sent | Add to `E364xAChannel.PrepareAsync` (ignoring "not supported") if a unit stays in local mode. | |

## Behaviour on real units (spec 13.2 - 13.10)

| # | Item | Notes | Result |
|---|---|---|---|
| 12 | `*IDN?` returns the model in field 2; older units answer `HEWLETT-PACKARD,...` | `InstrumentIdentity.Parse`, `SupportedModels` | |
| 13 | Does `MEAS:VOLT?` report the battery voltage with the output **off**? | Preflight relies on it. If it reads 0 V the preflight refuses ("battery missing"). Fallback: briefly enable the output at minimum current limit, or let the user confirm the voltage. E36103A reads 0 V with the output off (seen in the first hardware test), so the preflight now probes with the output briefly on at 80 % of the refuse threshold, 50 mA, OVP armed (`PreflightChecker.ProbeAsync`). Check the probe leaves the battery undisturbed (no discharge through the supply) and that a deeply discharged cell can be started via "Try charging anyway". | |
| 14 | Resolution/accuracy of `MEAS:CURR?` at low currents | Decides the 10 mA termination floor (`ProfileOptions.TerminationCurrentFloorAmps`). | |
| 15 | CC mode no longer trips the output after OCP is disabled | Run a CC stage into a real cell/load and watch for an unexpected output-off fault. | |
| 16 | USB-TMC, LAN VXI-11 (`inst0`) and raw socket (5025) through the installed VISA on a clean Windows install | `NativeVisaTransport` sets `\n` termination for reads and writes; check sockets in particular. | |
| 17 | PC sleep / network loss mid-charge | Confirms the documented limitation (output stays on, regulation continues, no time cutoff). The app inhibits system sleep while a charge runs. | |
| 18 | Full charge of a real cell | Stage transitions, Ah/Wh vs. the supply display, CSV content, stop in < 1 s, cable pulled, USB unplug, LAN drop end in a logged fault with the output off. | |

## Already verified without hardware

- VISA probe: a VISA installation whose `viOpenDefaultRM` never returns (found on the development machine: only the IVI
  shared components, no vendor VISA) leaves the app responsive and shows the "VISA is not available" page after 10 s.
- Full simulated charge through the WinUI app (connect, preflight dialog, confirmation, CC -> CV -> completed, chart,
  result card, CSV log) using the simulated supply.
