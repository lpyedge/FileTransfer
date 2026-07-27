# FileTransfer

**FileTransfer** は、YAML 設定のみで動作する小規模なファイル転送 Worker サービスです。
スキャナー出力、バッチ出力、SMB/NAS 共有、業務システム間の単方向ファイル受け渡しに向いています。

言語: [English](README.md) | **日本語** | [繁體中文](README.zh.md)

## 特徴

- YAML-only。`appsettings.json` は意図的に非対応です。
- 複数 rule、複数 source root、複数 target root に対応。
- target mode: `FirstAvailable` / `AllHealthyTargets`。
- ready signal: `StableSize` / `RenameOnly` / `DoneFile`。
- 既定の検証は軽量な `LengthAndTimestamp`。必要に応じて `Hash` を使用できます。
- パス単位の Latest-Wins スケジューリングにより、最新イベントを失わず重複を統合します。
- reconciliation により、監視イベント漏れ、未転送 target、古い target を補修します。
- 動的パスは memory LRU + append-only journal/snapshot で永続化します。
- `skipInitialScan` は、起動時に既に存在していたファイルを `paths.stateDir/<ruleId>/initial-scan-skipped-files.journal` に記録します。journal を残している間はずっとスキップされ、後で削除すると過去にスキップされたファイルも再同期できます。
- state/log ディレクトリを設定可能。
- runtime ログは `auto` / `en` / `ja` / `zh-Hant` に対応。
- Windows Service / Linux systemd での運用を想定しています。

## ダウンロード

GitHub Releases から取得してください。

- `FileTransfer-win-x64.zip`
- `FileTransfer-linux-x64.tar.gz`
- `checksums.txt`

チェックサム確認例:

```powershell
Get-FileHash .\FileTransfer-win-x64.zip -Algorithm SHA256
```

```bash
sha256sum FileTransfer-linux-x64.tar.gz
```

## ライセンスと商用利用

FileTransfer は [MIT License](LICENSE) です。商用利用も可能です。
直接依存ライブラリについては [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) を参照してください。

## 対応プラットフォーム

| 用途 | 状態 |
|---|---|
| Windows x64 service | 対応 |
| Linux x64 systemd service | 対応 |
| ソースからビルド | .NET 10 SDK が必要 |
| Release binaries | self-contained。通常は .NET runtime の別途インストール不要 |

## Quick start

`appsettings.yaml` を作成します。

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

設定検証:

```bash
FileTransfer --validate --config appsettings.yaml
```

コンソール実行:

```bash
FileTransfer --config appsettings.yaml
```

## 設定リファレンス

最小構成は次の項目です。

- `rules[].id`
- `rules[].sourceRoots`
- `rules[].targetRoots`
- `rules[].pathRules`

完全な注釈付き設定は [`FileTransfer/appsettings.full.yaml`](FileTransfer/appsettings.full.yaml) を参照してください。
追加例は [`examples/`](examples/) にあります。

### runtime path

`paths.stateDir` は各 rule の journal/snapshot 状態を保存し、`paths.stateDir/<ruleId>/...` に配置されます。`paths.logDir` はファイルログを保存します。
未指定時は OS ごとの書き込み可能なアプリケーションデータ領域を使います。

- Windows: `%ProgramData%\FileTransfer\state` / `%ProgramData%\FileTransfer\logs`
- Linux/macOS: `$XDG_STATE_HOME/FileTransfer/...` または `~/.local/state/FileTransfer/...`

初回スキップ journal は `paths.stateDir/<ruleId>/initial-scan-skipped-files.journal` に保存されます。
このファイルを削除すると、過去にスキップされた起動時既存ファイルが再び同期対象になります。

### runtime ログ言語

```yaml
logging:
  language: auto # auto / en / ja / zh-Hant
```

`logging.language` は `logging` セクション配下の `language` オプションを指す dotted path です。
`auto` はサービス実行アカウントの UI culture / locale に従います。
Windows Service や systemd では、現在ログイン中のユーザーの言語と一致しないことがあります。
本番環境では `ja` など明示指定を推奨します。
`RuleId`、`RuntimeId`、`SourcePath`、`TargetPath` などの structured field 名は英語固定です。

### target mode

| Mode | 動作 |
|---|---|
| `FirstAvailable` | 最初に利用可能な target にのみコピーします。 |
| `AllHealthyTargets` | 健全なすべての target にコピーします。復旧した target は reconciliation が補完します。 |

`PrimaryAndBackup` は成功条件が曖昧だったため削除されています。

### ready signal

| Mode | 用途 |
|---|---|
| `StableSize` | 既定値。ファイルサイズと更新日時が安定するまで待ちます。 |
| `RenameOnly` | 作成側が一時ファイルを書き、完了後に rename する運用向け。 |
| `DoneFile` | `file.csv.done` のような完了マーカーを待ちます。 |

### verification mode

| Mode | コスト | 備考 |
|---|---:|---|
| `LengthAndTimestamp` | 低 | 既定値、推奨。 |
| `Hash` | 高 | より強い検証。NAS/SMB では負荷に注意。 |

### 削除イベントと target 側の一時ファイル

`watchEvents.deleted` は source 側の削除イベントを受け取るかどうかだけを制御します。
`backupDeletedTargetsToTrash` の既定値は `false` です。既定のままでは削除イベント時に runtime の削除状態だけを整理し、既存の target ファイルはその場に残します。対応する target ファイルを `.trash` へ退避したい場合だけ `true` を明示してください。

target 状態は実際のコピー結果だけで更新されます。書き込み失敗は指数バックオフし、probe ファイルや ACL による事前判定は使用しません。

コピーは固定長スナップショットを使用し、一意な同階層 `.tmp` staging を検証してから原子的に確定します。source 変更時は旧 generation を取り消します。`OverwriteExisting=false` と `DeleteSourceAfterCopy=true` の併用は無効です。

## Windows Service 登録

`.bat` インストーラーは同梱しません。パスと設定ファイルを明示して登録してください。

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

削除:

```powershell
Stop-Service FileTransfer
sc.exe delete FileTransfer
```

## Linux systemd 登録

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin filetransfer
sudo mkdir -p /opt/filetransfer /etc/filetransfer /var/lib/filetransfer /var/log/filetransfer
sudo chown -R filetransfer:filetransfer /var/lib/filetransfer /var/log/filetransfer
sudo cp -R ./FileTransfer-linux-x64/* /opt/filetransfer/
sudo cp /opt/filetransfer/appsettings.yaml /etc/filetransfer/appsettings.yaml
sudo chown -R root:root /opt/filetransfer /etc/filetransfer
sudo chmod +x /opt/filetransfer/FileTransfer
```

`/etc/systemd/system/filetransfer.service`:

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

```bash
sudo systemctl daemon-reload
sudo systemctl enable filetransfer
sudo systemctl start filetransfer
sudo systemctl status filetransfer
journalctl -u filetransfer -f
```

## ソースからビルド

```bash
dotnet restore FileTransfer.sln
dotnet build FileTransfer.sln -c Release
dotnet test FileTransfer.sln -c Release --no-build
```

このリポジトリは `global.json` で Microsoft.Testing.Platform を使用します。CI とローカルの `dotnet test` は .NET 10 ネイティブのテストランナーで実行されます。


## 非目標・制限

- 双方向同期エンジンではありません。
- target root 内の任意の orphan file を削除する機能はありません。
- 複数サービスインスタンス間の分散ロックは提供しません。
- FileSystemWatcher はヒントとして扱い、reconciliation が復旧手段です。
- `Hash` 検証は大きなファイルや NAS/SMB で負荷が高くなる可能性があります。
- YAML schema は現在 `version: 2` です。破壊的変更は [CHANGELOG.md](CHANGELOG.md) に記録します。

## 関連ファイル

- [CHANGELOG.md](CHANGELOG.md)
- [SECURITY.md](SECURITY.md)
- [CONTRIBUTING.md](CONTRIBUTING.md)
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)
