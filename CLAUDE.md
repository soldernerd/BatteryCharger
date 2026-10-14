# BatteryCharger - notes for Claude

Spec: `battery-charger-spec.md` (authoritative). Overview and deviations: `README.md`. Open hardware questions:
`docs/verification-checklist.md`.

- Build: `dotnet build BatteryCharger.sln -c Release` (WinUI app needs Windows). Test: `dotnet test tests/BatteryCharger.Tests`.
- `BatteryCharger.Core` must stay free of UI and VISA. All instrument access goes through `IPowerSupply` / `IPowerSupplyChannel`;
  tests use `FakePowerSupply` (simulated battery + clock) and `ScriptedTransport` (SCPI drivers).
- Safety rules (spec 5.3, 6, 7.4) are intentional: never send `*RST`, never enable OCP, never loosen hard limits. A new
  SCPI command that is not verified gets a `// VERIFY` comment and an entry in `docs/verification-checklist.md`.
- VISA initialisation can hang forever on broken installs: it is probed in a child process (`Program.cs --visa-probe`,
  `VisaService`). Never call VISA on the UI thread or before `VisaService.InitializeAsync` completed.
- Settings -> Developer mode enables the simulated supply (60x time scale) and the SCPI console; with it on, the Charger page
  is usable even without VISA, which is handy for UI work.
- Release: bump `src/BatteryCharger.Core/AppVersion.cs`; the CI publishes a release for each new version on `master`.
- Editing XAML is the risky part of builds here: a full `dotnet build` of the App takes minutes and its errors can be opaque.
