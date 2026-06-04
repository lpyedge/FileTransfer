# Changelog

This project follows [Semantic Versioning](https://semver.org/).

## 1.0.0 - 2026-06-03

### Added

- YAML-only configuration.
- Multi-rule file transfer engine.
- Multiple source roots per rule.
- `FirstAvailable` and `AllHealthyTargets` target modes.
- `StableSize`, `RenameOnly`, and `DoneFile` readiness modes.
- `LengthAndTimestamp` and `Hash` verification modes.
- Bounded copy/delete queues.
- Reconciliation scan for missed events, missing targets, and stale targets.
- Memory LRU + append-only journal dynamic path cache.
- Configurable runtime state/log directories.
- `--config`, `--validate`, `--version`, and `--help` command-line options.
- Localized runtime logs: English, Japanese, and Traditional Chinese.
- Windows Service and Linux systemd deployment documentation.
- GitHub Actions CI and release workflows.
