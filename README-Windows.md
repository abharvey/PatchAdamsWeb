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

For the Windows-only navigation and scrollbar UI regression checks:

```powershell
dotnet run --project Tests/PatchAdams.Windows.UiTests/PatchAdams.Windows.UiTests.csproj
```

This dependency-free STA runner opens its own test windows, exercises both
search-result activation paths, and checks the destination's patch, file,
line, keyboard focus, and viewport position after WPF layout. It also verifies
compact scrollbar dimensions and interactions, shared file captions, and the
copy-code buttons against the resulting file text. Selection checks cover
mouse/key release plus the 200 ms delay, the read-only caret, cached row ranges,
background alignment, cancellation, and stale-result rejection. Clipboard tests temporarily
use the clipboard and restore its previous data afterward. The runner creates
synthetic test inputs under `artifacts/ui-navigation-tests`; it does not
open user patch files, save preferences, or install the app.

## Disconnected line selections

Select the first range, then hold Ctrl and drag over another range in the same
code column. Earlier ranges stay highlighted. Release Ctrl and the mouse;
after 200 ms, the reference lists every range, for example
`main.cs L20:L21, L100:L105`. Add one finding or pin for the whole selection.
Removed lines retain their own `-L` ranges. Comparisons run separately for each
post-patch range.

A normal drag, unmodified navigation key, or Esc clears the extra ranges.
Shift+arrow keys extend the active range. Ctrl+C copies the selected source
lines, without gutters or unselected gaps.

Newly saved session packages use version 3 to preserve comparison findings as
well as multiple ranges. This build reads version 1 and 2 sessions, but older
builds cannot read version 3.
Use a separate session package while trying this local build if you need to
keep using an older build. Manual anchor corrections currently require a single
contiguous post-patch range in each column.

## Comparison finding persistence

The persistence layer can store one comparison assessment with independently
anchored evidence from two or more patches. Each location retains its file,
disconnected line ranges, removed-base anchors, and optional observation.
Ordinary findings and rankings remain independent. Session packages and JSON
preserve comparison data, including through the current app's reopen/save flow.
Creating or displaying comparisons and adding them to Markdown reports are not
implemented yet; those are separate UI/export work.

## Correcting findings after reviewer feedback

In the Review panel, open **Reviewer feedback…** to paste a Slack message or
other large text block. The editor is resizable and preserves multiline text.
Choose **Keep feedback**, then save the session with Ctrl+S to persist it.
Feedback is included in session packages and JSON exports, not Markdown reports.
Cancel leaves the previous feedback unchanged.

Select a mistaken finding in the Findings list, then choose **Remove selected
finding…**. Check its title, patch, reference, and description before confirming.
Removal cannot be undone in the current review; it clears the highlight and
excludes the finding from future saves and exports, including not-flagged passes.
Other findings, pins, and pasted feedback stay intact. Save the session to keep
the correction. Feedback does not remove findings automatically.

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
