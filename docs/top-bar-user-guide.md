# Top bar user guide

The top bar keeps commands in **File**, **View**, **Review**, and **Export**
dropdown menus instead of wrapping buttons across several rows. Search remains
directly accessible. Press `Alt` to use the menu access keys, then arrow keys
and `Enter` to navigate. Start with
**File > Add patches…** if you do not have a base: PatchAdams# will build a synthetic
base from the patch hunks. It reads and compares code locally; it never runs
the code in a patch.

## File menu: bases, patches, and sessions

| Control | What it does |
| --- | --- |
| **Choose base…** | Select a local source directory. If patches are loaded, they are reapplied against it in memory. |
| **Choose archive…** | Select a local `.tar`, `.tar.gz`, `.tgz`, or `.zip` base archive. The app reads it without extracting files to disk. |
| **Git revision…** | Select a local Git repository, then enter a revision or commit. Git is used only to read that revision. |
| **Rebuild base from patches** | Replace the current base with one reconstructed from the loaded patch hunks. Lines not described by any patch are synthetic filler, not the original source. |
| **Add patches…** | Select **two to four** `.patch` or `.diff` files. This starts a new review and clears the current findings, pins, and manual anchors—save the current session first if you need them. You can also drop two to four patch files onto the window. |
| **Paste patch** | Add one patch from the clipboard as a third or fourth column, after two or three patches are already loaded. The new column is named `pasted patch`. |

The **Open session…** and **Save session…** commands are also in **File**.
Column controls are in **View**, and file exports are in **Export**.

## Sessions, columns, and export

| Control | What it does |
| --- | --- |
| **Open session…** | Choose a `.patchadam` **folder** containing `session.json`. It restores the copied patches and saved review state. If the original base is unavailable, the app rebuilds a synthetic base and reports that fallback. |
| **Save session…** | Choose or create a folder for the `.patchadam` package. It stores copies of the patches, a pointer to the base, and your review state. |
| **Manage columns…** | Check which patch columns stay expanded. A collapsed patch remains accessible as a narrow side tab, and at least one column must stay expanded. Drag the divider between expanded patches to resize them; use **◀ / ▶** in a patch header to choose its collapse side. Click a side tab to restore it. |
| **Balance widths** | Redistribute the available space across expanded patch panels using a rough fit to the lines currently displayed. Collapsed panels stay collapsed. Available when at least two patches are expanded; you can still drag dividers afterward. |
| **Export Markdown…** | Save the current ranking and findings as a Markdown review. |
| **Export JSON…** | Save the review record, including any recorded finding anchors and confidence values, as JSON. |

In the **Export** menu the file commands are labeled **Markdown…** and
**JSON…**. **Copy Markdown** copies the review to the clipboard without
creating a file.

## Reading and navigation

| Control | What it does |
| --- | --- |
| **Review > Golden patch** | Choose a patch as the comparison reference. A checkmark identifies the current choice. Other columns show their difference from its post-patch text. Choose **None** to return to the base comparison. |
| **Search** + **Find** | Search loaded code in every patch column, without case sensitivity. Results are ordered by file, column, and line. Select a result and choose **Open location**, or double-click the result, to open its file, expand its patch if collapsed, and focus the matching line near the middle of the code view. Near file boundaries the line stays as close to the middle as scrolling permits. A hidden target outside a large file's visible hunks is revealed with surrounding context. |
| **Review > Copy reference** | Copy the generated `filename Lstart:Lend` reference for selected code lines. A selection containing only removed lines uses `filename -Lstart:Lend` in base line numbers. Nothing is copied without a selection. |
| **View > Scroll lock** | Keep expanded columns near the same base-line position while scrolling. Turn it off to scroll independently. |
| **View > Smaller code font / Larger code font** | Decrease or increase code font size (9–24 pt). The choice is saved locally. |
| **View > Settings…** | Choose a monospace font and size, and whether to ask about blind review when starting a new review. These display preferences stay on this computer. |
| **View > Removed lines** | Show or hide removed base lines in the code views. |
| **Review > Blind review** | Replace patch identities with a repeatable numbered shuffle. The mapping is saved with the session and appears only at the end of the Markdown export. Turn blind review off to inspect the raw patch. |

The **request box directly below the bar** holds the task the patches answer.
It is saved with the session and included in exports, even while hidden.
Click **▲** beside it to hide the whole panel; use **View > Request panel**
to show it again without losing text. Visibility is saved locally, not as
part of the session. The **Files** and
**Review** side panels each have a draggable divider and a collapse button.
Click their slim side tab to reopen them at their previous width; those widths
are saved locally.

## File heading and copy code

The current file's relative path appears once above the patch panels, so it
stays visible even with the Files panel collapsed. Hover a truncated path to
see it in full.

Each expanded patch header has a small copy icon with the tooltip **copy code**.
It copies that patch's resulting file: additions plus retained source, without
line numbers, diff prefixes, or removed lines. It copies the full available
result, even when the view is scrolled, reduced to hunks, or comparing against
a golden patch. Indentation, blank lines, and line endings are preserved.

With a synthetic base, unknown placeholder lines are omitted and the status
message reports how many were skipped. This is a partial reconstruction, not
a complete source file; choose a real base to copy a complete result. Copy is
unavailable for missing, deleted, binary, or failed-to-apply files. Copying a
valid empty file clears the clipboard.

## Keyboard shortcuts

| Keys | Action |
| --- | --- |
| `Ctrl+S` | Save session |
| `Ctrl+F` | Focus search |
| `Ctrl+L` | Toggle scroll lock |
| `Ctrl+N` | Add a finding from the current selection |
| `Ctrl+P` | Pin the current selection |
| `Ctrl+Shift+C` | Copy the generated selection reference |
| `Ctrl+Alt+V` | Paste an additional patch |
| `Ctrl+E` | Export Markdown to a file |
| `Ctrl+Shift+E` | Export JSON to a file |
| `Ctrl+Alt+E` | Copy the Markdown review to the clipboard |
| `Ctrl+Shift+1` through `Ctrl+Shift+4` | Set or clear a loaded patch as golden |
