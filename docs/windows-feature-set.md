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
- Filter the shared tree to test-related files without dropping other files
  from the session.
- Show both base and post-patch line numbers. Added lines map to their base
  context; references for removed lines use base line numbers and a `-L`
  prefix.
- Keep columns independently scrollable; scroll lock synchronizes aligned
  positions, not merely identical raw line numbers.
- Show each column label and patch source file name together. Expanded columns
  resize using splitters or a one-click approximate best fit for the currently
  displayed code, and can be reordered. Any column can collapse into a
  narrow left or right strip, with the tabs on each side stacked vertically.
  Tabs retain their patch names (blind labels in blind mode); one to four
  columns can remain expanded. Collapsed columns retain their findings, and
  sessions restore their side and width.
- Show the selected file's relative path once above the patch columns, with
  the full path available on hover. Each expanded patch has an accessible
  copy icon with a "copy code" tooltip. Copy the complete available post-patch
  text, independent of gutters, removals, golden mode, or reduced display.
  Omit unknown synthetic-base placeholders and report the incomplete result;
  do not offer code copying for missing, deleted, binary, or failed files.
- The Files and Review side panels can be resized by dragging their dividers
  and independently collapsed to narrow side tabs. Their expanded widths and
  collapsed state are local display preferences rather than session review
  data.
- Keep the app responsive on large files. Files over 50,000 lines use a
  reduced, hunks-only view.
- Provide syntax coloring for supported languages, configurable monospace
  font and size, and keyboard shortcuts for common actions.
- Keep a visible, high-contrast caret in read-only code so keyboard navigation
  and Shift-based selection remain discoverable.
- Group top-bar commands into compact File, View, Review, and Export menus,
  with search directly accessible. The request panel can be hidden and
  restored from View without clearing its text; visibility is a local preference.
- Form inputs, selection states, and focus indicators use the dark palette
  throughout the app. Normal input text and labels meet WCAG AA 4.5:1 contrast
  against their backgrounds; control boundaries and focus indicators meet
  3:1. Every finding field has a visible label.
- Scrollbars use narrow 10-DIP dark tracks without arrow-button blocks,
  with contrast-tested thumbs, hover/drag feedback, and a minimum thumb
  length. Preserve thumb dragging, page clicks, and mouse-wheel scrolling.
  Keep code-panel padding small to reserve space for reviewing.

## Comparison and navigation

- Selecting a line region in any column locates the corresponding region in
  every other column and shows an `exact`, `modified`, `moved`, or `absent`
  status with confidence.
- Process selections only after the mouse buttons and selection keys are
  released, followed by 200 ms without further selection interaction. Run
  alignment off the UI thread, cancel superseded work, and never publish
  results for a replaced selection, file, or editor.
- Alignment uses line maps and positional matching, token similarity, and
  identifier matching for declarations. Confidence below 0.6 is reported as
  absent rather than presenting a low-confidence match as correct.
- Absent results identify the nearest hunk and provide a navigation target.
- A comparison panel presents word-level diffs for the selected region.
- A reviewer can correct a wrong anchor manually; the override is persisted
  and takes precedence on reload.
- Search text across all columns, grouped by file and column, with line
  references. Opening or double-clicking a result selects its file, expands
  its patch if needed, and focuses the matching post-patch line near the
  viewport midpoint (clamped at file boundaries). Navigation works in golden
  and reduced large-file views without discarding the comparison mode.
  Pins preserve selections for later navigation.
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
