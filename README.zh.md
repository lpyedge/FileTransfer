# FileTransfer

**FileTransfer** 是一個 YAML-only、單向目錄到目錄的檔案轉送 Worker 服務。
適合掃描器輸出、批次輸出、SMB/NAS 共用資料夾，以及簡單的生產檔案交接場景。

語言: [English](README.md) | [日本語](README.ja.md) | **繁體中文**

## 特色

- YAML-only，刻意不支援 `appsettings.json`。
- 支援多 rule、多 source root、多 target root。
- target mode: `FirstAvailable` / `AllHealthyTargets`。
- ready signal: `StableSize` / `RenameOnly` / `DoneFile`。
- 預設驗證為輕量的 `LengthAndTimestamp`，必要時可用 `Hash`。
- bounded copy/delete queue，避免檔案風暴造成無限制記憶體成長。
- reconciliation 可修復漏掉的 watcher 事件、缺失 target、過期 target。
- 動態路徑快取採用 memory LRU + append-only journal/snapshot。
- `skipInitialScan` 會把啟動時已存在的檔案寫入 `paths.stateDir/<ruleId>/initial-scan-skipped-files.journal`。保留這個檔案就會一直跳過；日後刪掉它，先前被跳過的舊檔才會重新同步。
- 可設定 state/log 目錄。
- runtime log 支援 `auto` / `en` / `ja` / `zh-Hant`。
- 適合 Windows Service 與 Linux systemd 部署。

## 下載

請從 GitHub Releases 下載：

- `FileTransfer-win-x64.zip`
- `FileTransfer-linux-x64.tar.gz`
- `checksums.txt`

檢查 SHA256：

```powershell
Get-FileHash .\FileTransfer-win-x64.zip -Algorithm SHA256
```

```bash
sha256sum FileTransfer-linux-x64.tar.gz
```

## 授權與商用

FileTransfer 採用 [MIT License](LICENSE)，允許商用。
直接依賴套件請參考 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 支援平台

| 場景 | 狀態 |
|---|---|
| Windows x64 service | 支援 |
| Linux x64 systemd service | 支援 |
| 從原始碼建置 | 需要 .NET 10 SDK |
| Release binaries | self-contained，通常不需要另外安裝 .NET runtime |

## 快速開始

建立 `appsettings.yaml`：

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

驗證設定：

```bash
FileTransfer --validate --config appsettings.yaml
```

以 console 模式執行：

```bash
FileTransfer --config appsettings.yaml
```

## 設定參考

最小必要欄位：

- `rules[].id`
- `rules[].sourceRoots`
- `rules[].targetRoots`
- `rules[].pathRules`

完整註解版設定請看 [`FileTransfer/appsettings.full.yaml`](FileTransfer/appsettings.full.yaml)。
更多範例在 [`examples/`](examples/)。

### runtime paths

`paths.stateDir` 保存各 rule 的 journal/snapshot 狀態，路徑會落在 `paths.stateDir/<ruleId>/...`。`paths.logDir` 保存檔案日誌。
未設定時會使用 OS 的可寫應用程式資料目錄。

- Windows: `%ProgramData%\FileTransfer\state` / `%ProgramData%\FileTransfer\logs`
- Linux/macOS: `$XDG_STATE_HOME/FileTransfer/...` 或 `~/.local/state/FileTransfer/...`

初始跳過 journal 會保存在 `paths.stateDir/<ruleId>/initial-scan-skipped-files.journal`。
如果刪掉這個檔案，之前被跳過的啟動既有檔案就會再次變成可同步。

### runtime 日誌語言

```yaml
logging:
  language: auto # auto / en / ja / zh-Hant
```

`logging.language` 是 `logging` 區段底下 `language` 選項的 dotted path。
`auto` 會跟隨服務帳號的 UI culture / locale。
Windows Service 或 systemd 下可能與目前登入使用者語言不同；生產環境建議明確設定。
`RuleId`、`RuntimeId`、`SourcePath`、`TargetPath` 等 structured field 名稱固定為英文，方便查詢與問題回報。

### target mode

| Mode | 行為 |
|---|---|
| `FirstAvailable` | 只複製到第一個健康 target。 |
| `AllHealthyTargets` | 複製到所有健康 target；恢復後的 target 由 reconciliation 補齊。 |

`PrimaryAndBackup` 因成功語義不清楚已移除。

### ready signal

| Mode | 用途 |
|---|---|
| `StableSize` | 預設。等待檔案大小與更新時間穩定。 |
| `RenameOnly` | 生產者先寫暫存檔，完成後 rename。 |
| `DoneFile` | 等待 `file.csv.done` 之類的完成標記。 |

### verification mode

| Mode | 成本 | 備註 |
|---|---:|---|
| `LengthAndTimestamp` | 低 | 預設、建議值。 |
| `Hash` | 高 | 較強驗證，但在 NAS/SMB 上成本較高。 |

### 刪除事件與 target 暫存檔

`watchEvents.deleted` 只控制是否接收 source 端的刪除事件。
`backupDeletedTargetsToTrash` 預設為 `false`。維持預設時，刪除事件只會清除 runtime 的刪除狀態，既有 target 檔會保留原位；只有明確設成 `true` 時，對應 target 檔才會搬到 `.trash`。

target 健康檢查會直接在每個 target root 下寫入隱藏的瞬時 `.filetransfer-health.*.probe` 檔，檢查後立即刪除。若仍殘留 probe 檔，通常代表 target 權限或清理失敗，應搭配日誌一併排查。

檔案複製時會先在目標目錄同層建立唯一的 `.tmp` 暫存檔，成功後再 rename 成正式檔名。FileTransfer 不會建立 `.filetransfer-staging` 目錄。

## Windows Service 註冊

FileTransfer 不再提供 `.bat` 安裝檔。請明確指定安裝路徑與設定檔路徑。

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

移除：

```powershell
Stop-Service FileTransfer
sc.exe delete FileTransfer
```

## Linux systemd 註冊

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin filetransfer
sudo mkdir -p /opt/filetransfer /etc/filetransfer /var/lib/filetransfer /var/log/filetransfer
sudo chown -R filetransfer:filetransfer /var/lib/filetransfer /var/log/filetransfer
sudo cp -R ./FileTransfer-linux-x64/* /opt/filetransfer/
sudo cp /opt/filetransfer/appsettings.yaml /etc/filetransfer/appsettings.yaml
sudo chown -R root:root /opt/filetransfer /etc/filetransfer
sudo chmod +x /opt/filetransfer/FileTransfer
```

建立 `/etc/systemd/system/filetransfer.service`：

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

## 從原始碼建置

```bash
dotnet restore FileTransfer.sln
dotnet build FileTransfer.sln -c Release
dotnet test FileTransfer.sln -c Release --no-build
```

本倉庫透過 `global.json` 使用 Microsoft.Testing.Platform，因此 CI 與本機 `dotnet test` 會使用 .NET 10 原生測試執行器。


## 非目標與限制

- 不是雙向同步引擎。
- 不會任意刪除 target root 中的孤兒檔案。
- 不提供多個服務實例之間的分散式鎖定。
- FileSystemWatcher 事件只是提示；reconciliation 是恢復機制。
- `Hash` 驗證在大檔案與 NAS/SMB 上可能很耗資源。
- YAML schema 目前是 `version: 2`。破壞性變更會記錄在 [CHANGELOG.md](CHANGELOG.md)。

## 相關文件

- [CHANGELOG.md](CHANGELOG.md)
- [SECURITY.md](SECURITY.md)
- [CONTRIBUTING.md](CONTRIBUTING.md)
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)
