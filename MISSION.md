# PatchAdams# mission

PatchAdams# is a Windows-only, offline desktop application for one reviewer
comparing two to four candidate patches against a shared code base. The app
shows the patches side by side, locates corresponding regions, supports a
golden patch, and records findings with generated line references and a
Markdown or JSON export.

## Product invariants

1. **Reads code, never runs it.** Patches are parsed and applied in memory.
   Patch code is never built, installed, tested, or executed.
2. **Nothing leaves the machine.** No network requests, telemetry, accounts,
   or hosted services. Base files, patch files, sessions, and notes remain
   local.
3. **References are generated.** File and line references come from a
   selection mapped through the line map; reviewers do not type references.
4. **A wrong anchor is worse than a missing one.** Low-confidence matches are
   reported absent. Reviewers can override an anchor, and saved overrides take
   precedence.
5. **Raw patch data is always available.** A per-patch raw view and per-file
   apply status remain accessible.
6. **Patch identity stays visible except in blind mode.** Outside blind mode,
   labels appear with source patch names. Blind mode stores a deterministic
   label mapping and reveals it only in the final export section.
7. **Engine code stays independent of WPF.** Parsing, base loading, applying,
   diffing, alignment, session state, and exports live in the .NET core library
   and are tested without the UI.
8. **Sessions reopen deterministically.** Patch order, golden choice, manual
   anchors, blind mapping, findings, pins, and ranking are persisted.

## Non-goals

- Running, building, or testing reviewed code.
- Editing, merging, or committing candidate patches.
- Collaboration, hosted sessions, accounts, or shared comments.
- Language-server or semantic analysis.
