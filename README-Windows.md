# PatchAdams# for Windows

PatchAdams# is the Windows rewrite of Patch Adams: a local desktop tool for
reviewing two to four candidate patches side by side. Patch files, source
trees, Git revisions, archives, and saved sessions remain on the machine.
The app makes no network requests and never executes code from a patch.

## Requirements

- Windows 10 or later, x64.
- .NET 10 Desktop Runtime to run the published app.
- .NET 10 SDK to publish or install from a source checkout.
- Git is optional; it is needed only to open a Git revision as the base.

## Build and test

From the repository root, with the .NET 10 SDK installed:

```powershell
dotnet run --project Tests/PatchAdams.Windows.Tests/PatchAdams.Windows.Tests.csproj
dotnet build App/PatchAdams.Windows/PatchAdams.Windows.csproj
```

The test runner is dependency-free and uses the checked-in patch fixtures.
`NuGet.Config` clears package feeds so restoring the C# projects does not
download dependencies.

For the Windows-only search-navigation UI regression checks:

```powershell
dotnet run --project Tests/PatchAdams.Windows.UiTests/PatchAdams.Windows.UiTests.csproj
```

This dependency-free STA runner opens its own test windows, exercises both
search-result activation paths, and checks the destination's patch, file,
line, keyboard focus, and viewport position after WPF layout. It creates
synthetic test inputs under `artifacts/ui-navigation-tests`; it does not
open user patch files, save preferences, or install the app.

## Install for the current user

Run PowerShell from the repository root:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\windows\install.ps1
```

The script publishes a framework-dependent Windows app into
`artifacts/windows/win-x64`, copies it to
`%LOCALAPPDATA%\Programs\PatchAdams#`, and adds a Start menu shortcut. It
does not require administrator rights.

To build a distributable folder without installing it:

```powershell
.\scripts\windows\publish.ps1
```

To remove the current user's installed app and shortcut:

```powershell
.\scripts\windows\uninstall.ps1
```

## Current rewrite status

The Windows app supports local directory, archive, Git revision, and
synthetic bases; unified-diff parsing and in-memory application; side-by-side
file views, a tests-only file-tree filter, golden diffs, search, generated
line references, findings, pins, manual anchors, rankings, blind-mode mapping,
and `.patchadam` session save/load and Markdown/JSON export.
