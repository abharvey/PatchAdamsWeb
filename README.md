# PatchAdams#

PatchAdams# is an offline Windows desktop app for reviewing two to four
candidate patches against one code base. It presents the patches side by side,
aligns selected regions, supports a golden reference, and records findings with
generated file-and-line references.

The app keeps patch, base, session, and finding data on the local machine. It
does not make network requests and never executes, builds, installs, or tests
the code under review.

## Install

Requires Windows x64 and the .NET 10 Desktop Runtime.

For a per-user install from a source checkout, run PowerShell at the repository
root:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\windows\install.ps1
```

The script publishes to `artifacts/windows/win-x64`, installs under
`%LOCALAPPDATA%\Programs\PatchAdams#`, and adds a Start menu shortcut. It does
not require administrator rights. Use `scripts/windows/uninstall.ps1` to remove
the current user's installed app.

## Build and test

Requires the .NET 10 SDK:

```powershell
dotnet run --project Tests/PatchAdams.Windows.Tests/PatchAdams.Windows.Tests.csproj
dotnet build App/PatchAdams.Windows/PatchAdams.Windows.csproj
.\scripts\windows\publish.ps1
```

The test runner is dependency-free and uses the checked-in patch fixtures.
`NuGet.Config` clears package feeds so project restore does not download
dependencies.

## Review workflow

1. Choose a local directory, archive, or Git revision as the base—or load
   patches first to build a synthetic base.
2. Add two to four `.patch`/`.diff` files by picker, drag and drop, or paste.
3. Select a file to inspect it in every visible patch column. Use the shared
   file tree, search, scroll lock, and optional golden comparison to navigate.
4. Select lines to compare the region across patches, pin it, correct an anchor,
   or create a finding.
5. Rank patches and save a `.patchadam` session. Export Markdown or JSON when
   the review is ready.

See [the Windows feature baseline](docs/windows-feature-set.md) for the
behavioral contract and [the Windows setup notes](README-Windows.md) for
distribution details.
