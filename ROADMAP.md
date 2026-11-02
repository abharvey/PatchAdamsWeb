# Windows rewrite roadmap

The C# Windows rewrite has replaced the former native implementation in this
fork. The behavioral baseline is
[docs/windows-feature-set.md](docs/windows-feature-set.md).

- [x] Feature set documented before the C# test baseline.
- [x] Fixture-backed parser, base loading, in-memory apply, line maps,
  synthetic bases, diffing, alignment, and session model.
- [x] Windows-native WPF viewer with file tree, visible/reorderable columns,
  golden comparison, generated references, search, findings, pins, manual
  anchors, ranking, and blind mode.
- [x] Session save/load and Markdown/JSON export.
- [x] Windows publish, per-user install, and uninstall scripts.
- [x] Dependency-free C# fixture test runner.
- [x] Remove obsolete native app, Swift engine, and Apple build code from the fork.
- [x] Replace old platform and bookkeeping instructions with Windows/local-only
  project documentation.
- [x] Audit the shipped source tree for leftover native project files.
- [ ] Interactive Windows smoke review of the full session workflow.

Build and test gates are in [FACTORY.md](FACTORY.md); installation details are
in [README-Windows.md](README-Windows.md).
