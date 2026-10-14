# BatteryCharger

Windows desktop app (C# / .NET 8 / WinUI 3) that turns Keysight/Agilent lab power supplies into programmable battery
chargers. Pick a supply, describe the battery (technology, nominal voltage, capacity) and the app chooses a charging
profile, runs the charge with software supervision, shows live readouts and a voltage/current chart, and writes a CSV log.

> **Safety first.** Charging batteries is hazardous. Only charge under supervision, never leave a Li-ion/LiPo/LiFePO4
> charge unattended, and read the limitations below. The software refuses or aborts exactly where the specification
> says so; do not loosen the limits.

## Features

- Supplies: Keysight/Agilent **E36100, E36200, E36300** and **E3640A-E3649A**, over USB, LAN (VXI-11 or raw socket) or
  GPIB adapters, all through VISA.
- Chemistries: Li-ion, LiPo, LiFePO4, NiMH, NiCd, lead-acid (SLA/AGM), each with its own staged profile
  (pre-charge, CC, CV, -dV, absorption, float).
- Preflight checks with the output off (reverse polarity, missing/over-voltage battery, wrong cell count hints) and a
  mandatory confirmation dialog.
- Software safety monitor: over-voltage, over-current, tripped output, disconnected battery, charge limit, stage timeouts,
  instrument error queue, lost communication. Any fault switches the output off.
- Live readouts and chart with stage markers; CSV session log (UTF-8 with BOM, flushed at least every 5 s).
- Restores the supply's previous setpoints afterwards (output stays off). Never sends `*RST`.
- Simulated power supply and SCPI console for development (Settings -> Developer mode).

## Requirements

- Windows 10 (1809+) or 11, x64.
- A 64-bit **VISA** implementation, e.g. the [Keysight IO Libraries Suite](https://www.keysight.com/find/iosuite) (or NI-VISA).
  Without it the app shows an explanatory page and stays disabled. VISA is probed in a short-lived child process, so a
  broken VISA installation cannot freeze the UI (it is reported as unavailable after 10 s).
- The release zip is self-contained: no .NET or Windows App SDK installation needed.

## Build

```powershell
dotnet build BatteryCharger.sln -c Release
dotnet test tests/BatteryCharger.Tests
```

Building the WinUI app needs Windows with the .NET 8 SDK (a newer SDK works too). `BatteryCharger.Core` and
`BatteryCharger.Instruments` are UI-free and build and test on any OS.

```
src/
  BatteryCharger.Core/         domain, profiles, session engine, CSV logger, settings, simulated supply (no UI, no VISA)
  BatteryCharger.Instruments/  VISA transport (P/Invoke on visa64.dll), discovery, E363xx / E364xA drivers
  BatteryCharger.App/          WinUI 3 app (MVVM, CommunityToolkit.Mvvm, generic-host DI, Serilog)
tests/BatteryCharger.Tests/    xUnit tests: profiles, preflight, session engine on a simulated battery, CSV, drivers
docs/verification-checklist.md items to confirm on real hardware
```

## CI/CD

`.github/workflows/ci.yml` (GitHub Actions, `windows-latest`):

- every push and pull request: restore, build the whole solution (including the WinUI app), run the unit tests;
- pushes to `master`: publish a self-contained `win-x64` build and, if `src/BatteryCharger.Core/AppVersion.cs` holds a
  version that has no tag yet, create a GitHub release `vX.Y.Z` with `BatteryCharger-X.Y.Z-win-x64.zip`.
  **Bump `AppVersion.cs` to publish a new release.**
- optional signing: add the secrets `CODE_SIGN_CERT` (base64 PFX) and `CODE_SIGN_PASSWORD`; without them signing is skipped.

## Using it

1. Connect the supply, click **Refresh** (LAN instruments: **Add LAN instrument...** with host/IP or a full VISA string),
   select it and **Connect**. Choose the channel.
2. Enter technology, nominal voltage, capacity (Ah or mAh); cells are derived and editable. The **Charge profile** card
   shows the stages and safety limits; warnings (e.g. current clamped to the supply's rating) and blocking errors
   appear there. **Advanced** exposes every profile parameter within hard limits.
3. **Start charging** runs the preflight checks, then asks you to confirm polarity, chemistry/cell count, battery
   condition and supervision.
4. **STOP** switches the output off within one poll interval. Closing the window during a charge asks first.

Logs: `Documents\BatteryCharger\Logs` (configurable). Diagnostics: `%LOCALAPPDATA%\BatteryCharger\diagnostics`
(optionally with SCPI traffic). Settings: `%LOCALAPPDATA%\BatteryCharger\settings.json`.

## Supported chemistries (defaults per cell)

| | Li-ion / LiPo | LiFePO4 | NiMH / NiCd | Lead-acid |
|---|---|---|---|---|
| Rate (default / max) | 0.5 C / 1 C | 0.5 C / 1 C | 0.5 C / 1 C | 0.2 C / 0.3 C |
| Target | 4.20 V | 3.65 V | ceiling 1.65 V | 2.40 V, float 2.27 V |
| Termination | CV and I < 0.05 C | CV and I < 0.05 C | -dV 5 mV (NiMH) / 10 mV (NiCd) | absorption, I < 0.03 C |
| Max charge | 1.3 x C | 1.3 x C | 1.5 x C | 1.4 x C |

See [the specification](battery-charger-spec.md) for the complete rules.

## Limitations

- **If the PC or the app stops, the supply keeps its output on** with the last voltage and current limits. Hardware
  regulation stays active (CC/CV and the supply's OVP), but there is **no time cutoff**. There is no instrument-side watchdog.
- **No temperature sensing.** NiMH/NiCd -dV detection and all termination logic use voltage, current and time only;
  defaults are conservative.
- **No remote sensing.** The measured voltage is at the supply terminals; at high current the true CV point at the
  cells is slightly lower.
- The supply cannot sink current: a battery above the target voltage is refused instead of being discharged.
- v1 does not do discharging, cycling, HV-LiPo, custom chemistries or several simultaneous sessions.
- Several SCPI commands could not be checked without hardware; see [docs/verification-checklist.md](docs/verification-checklist.md).

## Deviations from the specification

- `BatteryCharger.Instruments` targets plain `net8.0` and talks to VISA via P/Invoke on `visa64.dll` (the spec's
  documented fallback to `Ivi.Visa`), so the drivers are unit-tested against a scripted transport on any OS.
- The live chart is a small custom control drawn from a decimating buffer (`ChartBuffer`) instead of LiveCharts2, to avoid
  a native SkiaSharp dependency that could not be validated here. Same features: dual axes, stage markers, hover tooltip.
- NiMH/NiCd CC timeout ends the charge with a warning (timer termination) rather than a fault.
- A sudden >10x drop of the charge current between samples is treated as "battery disconnected" (a disconnect otherwise looks
  like a finished CV stage, because the supply sits at its voltage setpoint with I = 0).
