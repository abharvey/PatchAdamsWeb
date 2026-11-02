# PatchAdams# Windows feature set

This document defines the behavior the Windows rewrite must preserve from
the existing Patch Adams application. It is the baseline for characterization
tests of the current engines and implementation tests for the Windows app.

## Product and platform constraints

- Windows-only desktop application, implemented in C#.
- Works offline and keeps patch, base, session, and finding data on the
  machine. No hosting, accounts, telemetry, or network requests.
- Reads candidate code and patches but never runs, builds, installs, or tests
  the code under review.
- One reviewer compares two to four candidate unified-diff patches against a
  shared base and records a ranking and findings.
- The Windows application is the sole application target in this fork.

## Loading and applying patches

- Load the base from a local directory, a local `.tar`, `.tar.gz`, or `.zip`
  archive, or a local Git revision.
- Load two to four `.patch` or `.diff` files by picker, drag and drop, or
  paste. Accept unified diff and `git format-patch` forms, including rename,
  binary-stub, and mode-change metadata.
- Optionally enter the request/task text and display it in a collapsible
  panel.
- Apply each patch in memory. Report clean, fuzzed, and failed application
  per file. A failed file remains inspectable using its raw hunks.
- Build a shared synthetic base from the union of the patch hunks when the
  reviewer has no base available.
- Keep the raw patch and apply report accessible for every file and patch.

## Side-by-side viewer

- Display two, three, or four patch columns and a shared file tree.
- List every file touched by any patch. Per-patch file status distinguishes
  changed, added, deleted, unchanged, and failed-to-apply files.
- Selecting a file opens it in every column. Columns show the full
  post-patch file, additions, removals, and unchanged context. Removed lines
  can be collapsed.
- Show both base and post-patch line numbers. Added lines map to their base
  context; references for removed lines use base line numbers and a `-L`
  prefix.
- Keep columns independently scrollable; scroll lock synchronizes aligned
  positions, not merely identical raw line numbers.
- Show each column label and patch source file name together. Columns can be
  reordered or hidden; hidden columns retain their findings.
- Keep the app responsive on large files. Files over 50,000 lines use a
  reduced, hunks-only view.
- Provide syntax coloring for supported languages, configurable monospace
  font and size, and keyboard shortcuts for common actions.

## Comparison and navigation

- Selecting a line region in any column locates the corresponding region in
  every other column and shows an `exact`, `modified`, `moved`, or `absent`
  status with confidence.
- Alignment uses line maps and positional matching, token similarity, and
  identifier matching for declarations. Confidence below 0.6 is reported as
  absent rather than presenting a low-confidence match as correct.
- Absent results identify the nearest hunk and provide a navigation target.
- A comparison panel presents word-level diffs for the selected region.
- A reviewer can correct a wrong anchor manually; the override is persisted
  and takes precedence on reload.
- Search text across all columns, grouped by file and column, with line
  references. Pins preserve selections for later navigation.
- A golden column can be selected or cleared. Other columns display their
  diffs against its post-patch text; the golden column remains compared with
  the base. File status reflects the same comparison.

## Findings, ranking, and blind review

- Create findings from a selection so the patch, file, and reference are
  generated from the selected lines, never typed as a free-form reference.
- Store title, category, severity, description, request relevance, optional
  note, and a not-flagged review disposition. Findings remain anchored to
  their patch and line range.
- Provide the built-in review categories and allow categories to be edited
  per session. Severity options are trivial, minor, material, and blocking.
- Rank patches with ties and save a rationale.
- Blind mode uses a deterministic saved shuffle, shows numbered labels
  instead of patch identities during review, and reveals the mapping only at
  the end of the export.
- Export the review to Markdown and JSON. Markdown includes the request,
  ranking, findings grouped in rank order, not-flagged passes, rationale,
  and (when blind) the mapping last. JSON includes anchors and confidence.
- Copy generated references and Markdown to the clipboard.

## Sessions and persistence

- Save and reopen a `.patchadam` session package containing session data and
  copies of loaded patches, plus a pointer to the base.
- Restore request, patches, column order and visibility, golden selection,
  findings, pins, ranking, manual anchors, and blind-mode seed/state.
- If the original base is unavailable or has moved, rebuild a synthetic base
  from the saved patches and clearly report the fallback.
- Reopening the same session restores the same column ordering, mappings,
  anchors, and blind-mode assignment.

## Safety and quality gates

- No network calls or telemetry.
- No execution or installation of code being reviewed.
- Parsing is strict: hunk body counts that disagree with headers are errors
  with a useful source line, not silently repaired input.
- Core parsing, loading, applying, diffing, alignment, session, and export
  behavior must be testable without the UI.
- Port the fixture-backed behavioral tests before replacing the existing
  engines. Keep fixture coverage for all checked-in patch fixtures.
- The final Windows build must have a reproducible local build/test workflow
  and an installable distribution artifact or documented installer workflow.

## Delivery order

1. Characterize the current behavior with tests against the existing Swift
   engines and fixtures.
2. Add the C# Windows solution and implement the engine/domain behavior,
   preserving fixture coverage.
3. Implement the Windows UI in feature-sized commits: loading/viewer,
   comparison/navigation, review workflow, then persistence/settings.
4. Add Windows build, test, packaging, and installation instructions/files.
5. Verify the full feature set against fixtures and the end-to-end review
   workflow before considering the rewrite complete.
