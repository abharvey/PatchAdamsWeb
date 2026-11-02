# Windows rewrite checkpoint

This is a continuation note for the `windows-rewrite` branch. It records the
current state and the next work to do; it is not a claim that the rewrite is
complete.

## Product direction

- Windows-only C# desktop app, displayed as **PatchAdams#**.
- Offline and local-only. No accounts, telemetry, hosted service, or network
  requests. Reviewed code is parsed and diffed but never executed.
- Keep the original macOS implementation alongside the Windows app until
  feature parity and the Windows workflow have been checked. Do not remove
  Swift/macOS files yet.

## Current implementation

- `Sources/PatchAdams.Core/` contains C# implementations for unified-diff
  parsing, directory/archive/Git/synthetic bases, in-memory patch application,
  line maps/display rows, Myers/patience and token diffs, region alignment,
  manual-anchor overrides, session persistence/export, and syntax tokenization.
- `App/PatchAdams.Windows/` is a WPF app with local base/patch loading,
  side-by-side code columns, file status tree, golden diffs, selectable
  regions and comparison results, generated references, findings, pins,
  manual anchors, ranking, blind mode, search, raw-patch viewer, settings,
  session save/load, Markdown/JSON export, keyboard shortcuts, drag/drop,
  paste, scroll lock, and font controls.
- `Tests/PatchAdams.Windows.Tests/` is a dependency-free test runner using the
  checked-in fixtures. `NuGet.Config` clears package sources.
- `scripts/windows/` has publish, per-user install, and uninstall scripts.
  Publish is framework-dependent: the installed app needs the .NET 10 Desktop
  Runtime.
- The current test runner has 30 checks, including parsing and applying every
  fixture, a merged four-patch synthetic base, Myers replay/random inputs,
  alignment, sessions, and safety-related behavior.

## Checks run most recently

```powershell
dotnet run --project Tests/PatchAdams.Windows.Tests/PatchAdams.Windows.Tests.csproj --no-restore
dotnet build App/PatchAdams.Windows/PatchAdams.Windows.csproj --no-restore
```

Both passed on Windows with .NET SDK 10.0.403. The Windows publish script was
also run successfully earlier in this rewrite; rerun it after the next
substantial app changes.

## Remaining work before calling the rewrite done

1. Re-run the full test and Windows app build after further changes; run
   `scripts/windows/publish.ps1` and verify its output.
2. Do an interactive Windows smoke review using fixture patches: load a base
   and 2–4 patches, compare and search, create findings/pins/anchors, save and
   reopen a session, and verify Markdown/JSON exports and blind-mode behavior.
3. Review gaps against [the feature baseline](windows-feature-set.md). In
   particular, test WPF behavior rather than relying on compilation for
   scrolling, selection-to-reference mapping, syntax coloring, and dialogs.
   Add missing behavioral tests and address any remaining parity gaps.
4. Update the top-level product/build documentation for the Windows-only
   fork, then remove the obsolete Swift/macOS implementation only after the
   parity checks pass.
5. Inspect the final diff and keep each remaining feature area in its own
   reviewable commit.

## Continuation point

The next agent should start with:

```powershell
git status --short --branch
dotnet run --project Tests/PatchAdams.Windows.Tests/PatchAdams.Windows.Tests.csproj --no-restore
dotnet build App/PatchAdams.Windows/PatchAdams.Windows.csproj --no-restore
```

Continue on `windows-rewrite`. Do not publish or push a branch unless asked.
