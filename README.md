# FileTransfer

**FileTransfer** is a small, YAML-only file transfer worker service for one-way directory-to-directory file delivery.
It is designed for scanner output folders, batch output folders, SMB/NAS shares, and simple production file handoff jobs.

Languages: **English** | [日本語](README.ja.md) | [繁體中文](README.zh.md)

## Highlights

- YAML-only configuration. `appsettings.json` is intentionally not supported.
- Multiple rules, multiple source roots, and multiple target roots.
- Target modes: `FirstAvailable` and `AllHealthyTargets`.
- File readiness modes: `StableSize`, `RenameOnly`, and `DoneFile`.
- Default verification: `LengthAndTimestamp`; optional stronger `Hash` mode.
- Bounded copy/delete queues to avoid unlimited memory growth.
- Reconciliation scan to recover missed watcher events, missing targets, and stale targets.
- Dynamic path cache using memory LRU + append-only journal/snapshot.
- Configurable state and log directories.
- Localized runtime logs: `auto`, `en`, `ja`, `zh-Hant`.
- Windows Service and Linux systemd friendly.

## Download

Download release packages from GitHub Releases:

- `FileTransfer-win-x64.zip`
- `FileTransfer-linux-x64.tar.gz`
- `checksums.txt`

Verify checksums before deployment:

```powershell
Get-FileHash .\FileTransfer-win-x64.zip -Algorithm SHA256
```

```bash
sha256sum FileTransfer-linux-x64.tar.gz
```

## License and commercial use

FileTransfer is licensed under the [MIT License](LICENSE). Commercial use is allowed.
See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for direct dependency notices.

## Supported platforms

| Scenario | Status |
|---|---|
| Windows x64 service | Supported |
| Linux x64 systemd service | Supported |
| Build from source | .NET 10 SDK required |
| Release binaries | Self-contained; .NET runtime installation is not required |

## Quick start

Create `appsettings.yaml`:

```yaml
version: 2

paths:
  stateDir: ./state
  logDir: ./logs

rules:
  - id: default
    sourceRoots:
      - C:\Inbox
    targetRoots:
      - \\NAS01\Shared
    targetMode: FirstAvailable
    backupDeletedTargetsToTrash: false
    pathRules:
      - matchPattern: ^(?<date>\d{8})[\\/](?<file>.+)$
        targetTemplate: '{date}/{file}'

logging:
  language: auto
  logLevel:
    default: Information
    system: Warning
    microsoft: Warning
  file:
    path: app_{0:yyyyMMdd}.log
    fileSizeLimitBytes: 10485760
    minLevel: Information
```

Validate the configuration:

```bash
FileTransfer --validate --config appsettings.yaml
```

Run in console mode:

```bash
FileTransfer --config appsettings.yaml
```

## Configuration reference

The minimal fields are:

- `rules[].id`
- `rules[].sourceRoots`
- `rules[].targetRoots`
- `rules[].pathRules`

See [`FileTransfer/appsettings.full.yaml`](FileTransfer/appsettings.full.yaml) for a complete annotated configuration file.
Additional examples are available under [`examples/`](examples/).

### Runtime paths

`paths.stateDir` stores journal/snapshot state. `paths.logDir` stores file logs.
When omitted, FileTransfer uses writable application data locations:

- Windows: `%ProgramData%\FileTransfer\state` and `%ProgramData%\FileTransfer\logs`
- Linux/macOS: `$XDG_STATE_HOME/FileTransfer/...` or `~/.local/state/FileTransfer/...`

### Runtime log language

```yaml
logging:
  language: auto # auto / en / ja / zh-Hant
```

`logging.language` is the dotted path for the `language` option under the `logging` section.
`auto` follows the service account's UI culture / locale.
For production, set an explicit value when deterministic log language is required.
Structured fields such as `RuleId`, `RuntimeId`, `SourcePath`, and `TargetPath` remain stable English identifiers.

### Target modes

| Mode | Behavior |
|---|---|
| `FirstAvailable` | Copy to the first healthy target only. |
| `AllHealthyTargets` | Copy to every healthy target. Reconciliation fills recovered targets later. |

`PrimaryAndBackup` was intentionally removed because its success semantics were ambiguous.

### Readiness modes

| Mode | Use case |
|---|---|
| `StableSize` | Default. Waits until file length and last-write time stay stable. |
| `RenameOnly` | Producer writes a temp file and renames it only after completion. |
| `DoneFile` | Producer creates a marker file such as `file.csv.done`. |

### Verification modes

| Mode | Cost | Notes |
|---|---:|---|
| `LengthAndTimestamp` | Low | Default and recommended. |
| `Hash` | High | Stronger but expensive on NAS/SMB shares. |

### Delete handling and transient target files

`watchEvents.deleted` only controls whether source-side delete events are observed.
`backupDeletedTargetsToTrash` defaults to `false`. With the default, delete events clear runtime delete state and keep existing target files in place. Set it to `true` only when matched target files should be moved under `.trash`.

Target health checks write a hidden transient `.filetransfer-health.*.probe` file directly under each target root and delete it immediately after the check. A leftover probe file usually indicates a target permission or cleanup failure.

Copies stream into a unique sibling `.tmp` file in the destination directory and rename it into the final path only after a successful write. FileTransfer does not create `.filetransfer-staging` directories.

## Windows Service registration

FileTransfer does not ship `.bat` installers. Register the service explicitly so deployment paths are visible and auditable.

PowerShell:

```powershell
New-Item -ItemType Directory -Force "C:\Program Files\FileTransfer"
New-Item -ItemType Directory -Force "C:\ProgramData\FileTransfer"
Copy-Item ".\*" "C:\Program Files\FileTransfer" -Recurse -Force
Copy-Item ".\appsettings.yaml" "C:\ProgramData\FileTransfer\appsettings.yaml" -Force

New-Service `
  -Name "FileTransfer" `
  -DisplayName "FileTransfer" `
  -BinaryPathName '"C:\Program Files\FileTransfer\FileTransfer.exe" --config "C:\ProgramData\FileTransfer\appsettings.yaml"' `
  -StartupType Automatic

Start-Service FileTransfer
Get-Service FileTransfer
```

Alternative with `sc.exe`:

```cmd
sc.exe create FileTransfer binPath= "\"C:\Program Files\FileTransfer\FileTransfer.exe\" --config \"C:\ProgramData\FileTransfer\appsettings.yaml\"" start= delayed-auto
sc.exe description FileTransfer "YAML-only file transfer worker service"
sc.exe start FileTransfer
```

Unregister:

```powershell
Stop-Service FileTransfer
sc.exe delete FileTransfer
```

## Linux systemd registration

Create a service user and directories:

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin filetransfer
sudo mkdir -p /opt/filetransfer /etc/filetransfer /var/lib/filetransfer /var/log/filetransfer
sudo chown -R filetransfer:filetransfer /var/lib/filetransfer /var/log/filetransfer
sudo cp -R ./FileTransfer-linux-x64/* /opt/filetransfer/
sudo cp /opt/filetransfer/appsettings.yaml /etc/filetransfer/appsettings.yaml
sudo chown -R root:root /opt/filetransfer /etc/filetransfer
sudo chmod +x /opt/filetransfer/FileTransfer
```

Create `/etc/systemd/system/filetransfer.service`:

```ini
[Unit]
Description=FileTransfer
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
WorkingDirectory=/opt/filetransfer
ExecStart=/opt/filetransfer/FileTransfer --config /etc/filetransfer/appsettings.yaml
Restart=always
RestartSec=10
User=filetransfer
Group=filetransfer
Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
Environment=LANG=ja_JP.UTF-8

[Install]
WantedBy=multi-user.target
```

Enable and start:

```bash
sudo systemctl daemon-reload
sudo systemctl enable filetransfer
sudo systemctl start filetransfer
sudo systemctl status filetransfer
journalctl -u filetransfer -f
```

## Build from source

```bash
dotnet restore FileTransfer.sln
dotnet build FileTransfer.sln -c Release
dotnet test FileTransfer.sln -c Release --no-build
```

The repository uses Microsoft.Testing.Platform through `global.json`, so CI and local `dotnet test` use the .NET 10 native test runner.


Publish release-style binaries:

```bash
dotnet publish FileTransfer/FileTransfer.csproj -c Release -r win-x64 -o publish/win-x64
dotnet publish FileTransfer/FileTransfer.csproj -c Release -r linux-x64 -o publish/linux-x64
```

## Non-goals and limitations

- Not a bidirectional synchronization engine.
- Does not delete arbitrary orphan files from target roots.
- Does not provide distributed locking across multiple service instances.
- FileSystemWatcher events are treated as hints; reconciliation is the recovery mechanism.
- Hash verification can be expensive on large files and network shares.
- YAML schema version is currently `version: 2`. Breaking changes are documented in [CHANGELOG.md](CHANGELOG.md).

## Project files

- [CHANGELOG.md](CHANGELOG.md)
- [SECURITY.md](SECURITY.md)
- [CONTRIBUTING.md](CONTRIBUTING.md)
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)
