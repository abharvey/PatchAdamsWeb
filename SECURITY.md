# Security and privacy

PatchAdams# is a local code-review application. Its security boundary is
designed around reading untrusted patches and base files without executing
them.

## Application guarantees

- Patch content is parsed and applied in memory.
- The app never executes, builds, installs, or tests reviewed code.
- No network calls, telemetry, accounts, or remote services are used.
- Base and patch contents remain local. Session packages store the patches and
  review data needed to reopen the session.
- Git integration uses local read-only commands for commit resolution, tree
  listing, and blob reads.
- Archive entries are inspected without extraction; unsafe absolute and
  parent-traversal paths are rejected.

## Reporting a vulnerability

Do not include private patch or repository contents in a public issue. Contact
the repository owner privately with a concise description and a reproduction
using only synthetic or checked-in fixture data.
