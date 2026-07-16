# Changelog

## 1.3.0 - 2026-07-16

- HDR is now enable-only. `couch` enables HDR when needed and retains the existing v2 `activeColorMode` verification and retry behavior.
- `desk` no longer queries, disables, or verifies HDR state.
- Bumped the executable, `--version` output, and rolling log run-header version to `1.3.0`.
