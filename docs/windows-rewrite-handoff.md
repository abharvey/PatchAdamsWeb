# Windows rewrite checkpoint

PatchAdams# is now a Windows-only C# / WPF application. The former native
application source and Swift engine/test projects have been removed from this
fork. This note captures the continuation point and the remaining validation;
it does not claim a manual UI review has been performed.

## Product invariants

- Windows-only desktop app, offline and local-only.
- The application never runs, builds, installs, or tests reviewed patch code.
- No accounts, telemetry, hosted service, or network requests.
- Keep all base, patch, session, and review data on the machine.

## Current implementation

- `Sources/PatchAdams.Core/` contains the UI-independent C# patch parser,
  directory/archive/Git/synthetic base providers, in-memory apply engine,
  line maps and display rows, linear-space Myers/patience and token diffs,
  alignment with manual overrides, session persistence/export, and syntax
  tokenization.
- `App/PatchAdams.Windows/` contains the native WPF application: local base
  and patch loading, side-by-side columns, unchanged-file views, rename
  handling, file statuses, golden comparisons, syntax colors, scroll lock,
  search, tests-only filtering, hunks-only large-file display, selection
  alignment, references, findings, pins, manual anchors, rankings, blind mode,
  settings, save/load, and Markdown/JSON export.
- `Tests/PatchAdams.Windows.Tests/` is a dependency-free fixture test runner.
  `NuGet.Config` clears package sources.
- `scripts/windows/` contains Windows x64 publish, per-user install, and
  uninstall scripts. Publish is framework-dependent and requires the .NET 10
  Desktop Runtime on the target machine.
- `docs/windows-feature-set.md` is the behavioral contract.

## Checks most recently run

```powershell
dotnet run --project Tests/PatchAdams.Windows.Tests/PatchAdams.Windows.Tests.csproj --no-restore
dotnet build App/PatchAdams.Windows/PatchAdams.Windows.csproj --no-restore
.\scripts\windows\publish.ps1
```

The C# fixture runner and WPF build passed on Windows with .NET SDK 10.0.403.
The publish script produced `artifacts/windows/win-x64/PatchAdamsSharp.exe`.
PowerShell syntax was checked for all three distribution scripts. The install
and uninstall scripts passed an isolated per-user directory check under
`artifacts/`.
The published app also passed a process startup/close smoke check.

## Remaining validation

1. Manually exercise the published app on Windows with fixture patches:
   load 2–4 patches, compare regions, search, record findings/pins/anchors,
   rank, save/reopen a session, and verify Markdown/JSON export and blind-mode
   mapping.
2. Manually exercise the full review workflow and check user interaction
   details that compilation cannot prove, especially
   WPF selection-to-reference mapping, synchronized scrolling, menus, and
   dialog behavior.
3. Optionally check install/uninstall on a disposable Windows user profile.
   The isolated script check passed, but it did not install into the user's
   normal Start menu or application folders.
4. Inspect the final diff and commit any follow-up fixes in feature-sized
   commits.

## Continuation commands

```powershell
git status --short --branch
dotnet run --project Tests/PatchAdams.Windows.Tests/PatchAdams.Windows.Tests.csproj --no-restore
dotnet build App/PatchAdams.Windows/PatchAdams.Windows.csproj --no-restore
.\scripts\windows\publish.ps1
```

Continue on `windows-rewrite`. Do not push or merge unless explicitly asked.
