# Windows build and test runbook

## Gate

From the repository root, with the .NET 10 SDK installed:

```powershell
dotnet run --project Tests/PatchAdams.Windows.Tests/PatchAdams.Windows.Tests.csproj
dotnet build App/PatchAdams.Windows/PatchAdams.Windows.csproj
```

Both commands must exit successfully with no compiler warnings. The custom
test runner has no external package dependencies. `NuGet.Config` clears all
package sources.

## Publish and install

Publish a Windows x64 framework-dependent deployment:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\windows\publish.ps1
```

Output is written to `artifacts/windows/win-x64/`. The target machine needs the
.NET 10 Desktop Runtime. To install for the current user:

```powershell
.\scripts\windows\install.ps1 -SkipPublish
```

The regular install command runs publish automatically. Uninstall with
`scripts/windows/uninstall.ps1`.

## Safety boundaries

- The application makes no network requests and emits no telemetry.
- Git access is restricted to local read-only inspection commands in
  `Sources/PatchAdams.Core/BaseLoader.cs`.
- Patch contents are parsed and applied in memory. The app never executes code
  being reviewed.
- The test suite uses only checked-in fixtures and temporary local files.

## Current project layout

```text
App/PatchAdams.Windows/          WPF desktop UI
Sources/PatchAdams.Core/         UI-independent engines and session model
Tests/PatchAdams.Windows.Tests/  dependency-free fixture test runner
Fixtures/                        checked-in patch and base fixtures
scripts/windows/                 publish, install, uninstall
docs/windows-feature-set.md      behavioral contract
```
