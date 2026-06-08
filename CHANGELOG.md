# Changelog

This project follows [Semantic Versioning](https://semver.org/).

## Unreleased

### Changed

- Target health checks now use transient probe files written directly under each target root and delete them immediately after each check.
- In-flight copies now use a unique sibling `.tmp` file in the destination directory instead of creating `.filetransfer-staging` directories.
- Delete handling keeps existing target files in place by default. `.trash` backups now require `backupDeletedTargetsToTrash: true`.
- Public README files and example YAML documents were updated to describe the new delete and transient-file behavior.

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
