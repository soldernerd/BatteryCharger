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
- UI structure: nav item "Charger" (overview: found instruments, Refresh, Add LAN) with one child tab per supported supply.
  `SupplyRegistry` (singleton) discovers supplies and owns one `SupplyViewModel` per supply (own connection, inputs, session,
  chart; tabs share nothing but settings). `MainWindow` keeps one `ChargerPage` per supply. Discovery never re-identifies a
  connected supply (it would disturb a running charge). The simulated supply is just another tab in Developer mode.
- XAML gotchas: a property that does not exist on the element (e.g. `IsEnabled` on a `StackPanel`; use a `ContentControl`)
  makes `XamlCompiler.exe` exit 1 with no message, and so do plain C# errors in the App project. To bisect, run
  `XamlCompiler.exe obj\...\input.json out.json` from `src\BatteryCharger.App` with a reduced `XamlPages` list (cwd matters).
  `dotnet build src/BatteryCharger.App -c Release` writes to `bin\Release\...` (no `x64` folder).
- GUI checks without a mouse: launch the exe, drive it with UI Automation (PowerShell `System.Windows.Automation`) and
  screenshot via `Graphics.CopyFromScreen`; NavigationView children added at run time need `IsExpanded` toggled to appear.
- Release: bump `src/BatteryCharger.Core/AppVersion.cs`; the CI publishes a release for each new version on `master`.
- Editing XAML is the risky part of builds here: a full `dotnet build` of the App takes minutes and its errors can be opaque.
