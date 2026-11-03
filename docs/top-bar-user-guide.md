# Top bar user guide

The top bar wraps onto additional rows when the window is narrow. Start with
**Add patches…** if you do not have a base: PatchAdams# will build a synthetic
base from the patch hunks. It reads and compares code locally; it never runs
the code in a patch.

## Base and patch controls

| Control | What it does |
| --- | --- |
| **Choose base…** | Select a local source directory. If patches are loaded, they are reapplied against it in memory. |
| **Choose archive…** | Select a local `.tar`, `.tar.gz`, `.tgz`, or `.zip` base archive. The app reads it without extracting files to disk. |
| **Git revision…** | Select a local Git repository, then enter a revision or commit. Git is used only to read that revision. |
| **Rebuild base from patches** | Replace the current base with one reconstructed from the loaded patch hunks. Lines not described by any patch are synthetic filler, not the original source. |
| **Add patches…** | Select **two to four** `.patch` or `.diff` files. This starts a new review and clears the current findings, pins, and manual anchors—save the current session first if you need them. You can also drop two to four patch files onto the window. |
| **Paste patch** | Add one patch from the clipboard as a third or fourth column, after two or three patches are already loaded. The new column is named `pasted patch`. |

## Sessions, columns, and export

| Control | What it does |
| --- | --- |
| **Open session…** | Choose a `.patchadam` **folder** containing `session.json`. It restores the copied patches and saved review state. If the original base is unavailable, the app rebuilds a synthetic base and reports that fallback. |
| **Save session…** | Choose or create a folder for the `.patchadam` package. It stores copies of the patches, a pointer to the base, and your review state. |
| **Manage columns…** | Check which patch columns stay expanded. A collapsed patch remains accessible as a narrow side tab, and at least one column must stay expanded. Drag the divider between expanded patches to resize them; use **◀ / ▶** in a patch header to choose its collapse side. Click a side tab to restore it. |
| **Export Markdown…** | Save the current ranking and findings as a Markdown review. |
| **Export JSON…** | Save the review record, including any recorded finding anchors and confidence values, as JSON. |

## Reading and navigation

| Control | What it does |
| --- | --- |
| **Golden** | Choose a patch as the comparison reference. Other columns show their difference from its post-patch text. Choose **None** to return to the base comparison. |
| **Search** + **Find** | Search loaded code in every patch column, without case sensitivity. Results are ordered by file, column, and line; open a result to jump there. |
| **Copy reference** | Copy the generated `filename Lstart:Lend` reference for selected code lines. A selection containing only removed lines uses `filename -Lstart:Lend` in base line numbers. Nothing is copied without a selection. |
| **Scroll lock** | Keep expanded columns near the same base-line position while scrolling. Turn it off to scroll independently. |
| **A− / A+** | Decrease or increase code font size (9–24 pt). The choice is saved locally. |
| **Settings…** | Choose a monospace font and size, and whether to ask about blind review when starting a new review. These display preferences stay on this computer. |
| **Removed** | Show or hide removed base lines in the code views. |
| **Blind review** | Replace patch identities with a repeatable numbered shuffle. The mapping is saved with the session and appears only at the end of the Markdown export. Turn blind review off to inspect the raw patch. |

The **request box directly below the bar** holds the task the patches answer.
It is saved with the session and included in exports. The **Files** and
**Review** side panels each have a collapse button; click their slim side tab
to reopen them.

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
