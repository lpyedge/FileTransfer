# Contributing

Thank you for considering a contribution to FileTransfer.

## Development setup

1. Install the .NET 10 SDK.
2. Clone the repository.
3. Run:

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release --no-build
```

Tests run through Microsoft.Testing.Platform via `global.json`. Keep GitHub Actions test commands compatible with the .NET 10 native test runner.


## Pull requests

Before opening a pull request:

- Keep the project YAML-only.
- Add or update tests for behavioral changes.
- Keep runtime defaults small and production-safe.
- Do not commit `.user`, local publish profiles, secrets, or internal paths.
- Update the English README first; update Japanese and Traditional Chinese documentation when the change affects users.

## Logging and localization

Runtime log message templates should use localization resources under `FileTransfer/Localization`.
Structured field names such as `RuleId`, `RuntimeId`, `SourcePath`, and `TargetPath` must remain stable English identifiers.
