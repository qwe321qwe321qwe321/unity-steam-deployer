# Unity Steam Deployer

一個「保母級」Unity 編輯器外掛程式，讓你只需點一個按鈕，即可自動完成遊戲編譯並上傳至 Steam。

---

## 功能概覽

- **一鍵部署**：點擊「Build & Upload to Steam」，系統自動完成 Unity 編譯 → 生成 VDF 腳本 → 執行 SteamCMD 上傳，全程無需手動介入。
- **AES-256 密碼加密**：密碼以硬體裝置 ID 為基礎派生的金鑰加密後，儲存於 `EditorPrefs`，永不寫入專案檔案。
- **非同步執行，不凍結編輯器**：SteamCMD 以子行程方式非同步執行，所有 stdout/stderr 串流即時顯示於 Unity Console 與視窗內建日誌區域，編輯器不會卡住。
- **動態 VDF 生成**：自動產生 `app_build_{AppID}.vdf` 與 `depot_build_{DepotID}.vdf`，無需手動編寫。
- **完整防呆機制**：路徑 Unicode 檢查、認證失敗即時偵測並終止程序、建置失敗自動中止上傳。

---

## 系統需求

| 項目 | 最低需求 |
|------|----------|
| Unity 版本 | 2021.3 LTS 以上 |
| 作業系統 | Windows 10 / macOS 12+ / Ubuntu 20.04+ |
| SteamCMD | 最新版（從 Valve 官方下載） |
| Steam 帳號 | 具有目標 AppID 發布權限的合作夥伴帳號 |
| .NET | Unity 內建（4.x Scripting Runtime）|

---

## 安裝方式

### 方式一：手動複製（推薦）

1. 將整個 `Assets/Editor/SteamDeployer/` 資料夾複製到你的 Unity 專案的 `Assets/Editor/` 目錄下。
2. 確認資料夾結構如下：

```
YourUnityProject/
└── Assets/
    └── Editor/
        └── SteamDeployer/
            ├── SteamDeployConfig.cs
            ├── CryptographyHelper.cs
            ├── VDFGenerator.cs
            ├── SteamCmdProcessHandler.cs
            └── SteamDeployWindow.cs
```

3. 等待 Unity 重新編譯腳本（右下角進度條消失後）。
4. 從選單開啟：**Tools → Steam Deployer → Open Window**。

### 方式二：Unity Package Manager（UPM）

將此 Repository 加入 UPM 的 Git URL 欄位：
```
https://github.com/YOUR_USERNAME/unity-steam-upload.git
```

---

## 初次設定流程

### 步驟 1：下載並安裝 SteamCMD

前往 Valve 官方說明頁面下載 SteamCMD：
- Windows：解壓縮 `steamcmd.zip`，得到 `steamcmd.exe`。
- macOS / Linux：依官方指示安裝。

> **重要**：SteamCMD 的安裝路徑以及 Unity 專案路徑**不可包含非 ASCII 字元**（中文、日文、韓文、特殊符號等）。
> 例如，路徑 `C:\遊戲工具\steamcmd\` 會導致 SteamCMD 以神秘的 Status 6 錯誤崩潰。
> 建議路徑：`C:\steamcmd\steamcmd.exe`

### 步驟 2：建立設定資產（SteamDeployConfig）

1. 開啟 **Tools → Steam Deployer → Open Window**。
2. 在「Configuration Asset」區塊中，點擊 **Create New Config Asset**。
3. 系統會在 `Assets/Editor/SteamDeployer/SteamDeployConfig.asset` 建立設定檔。
4. 這個 `.asset` 檔案可以提交到 Git，因為它**不包含任何敏感資訊**。

### 步驟 3：填寫 App 設定

在視窗的「App Settings」區塊填寫以下資訊：

| 欄位 | 說明 | 範例 |
|------|------|------|
| App ID | 你的 Steam 遊戲 AppID | `1234560` |
| Depot ID | Steam Depot ID（通常為 AppID+1） | `1234561` |
| Build Branch | 上傳後要設定為 live 的分支 | `default`、`beta` |
| Build Description | 版本說明（支援 `{Version}` 和 `{Date}` 巨集） | `v{Version} - {Date}` |
| Ignore Files | 排除上傳的檔案模式（逗號分隔） | `*.pdb, _BurstDebugInformation_DoNotShip` |
| SteamCMD Path | steamcmd.exe 的完整路徑 | `C:\steamcmd\steamcmd.exe` |

### 步驟 4：填寫認證資訊

在「Authentication」區塊：

1. 輸入 **Steam 使用者名稱**。
2. 輸入 **密碼**（欄位顯示為遮罩，旁人無法偷看）。
3. 若帳號有啟用 Steam Guard，先在 Steam App 或電子郵件取得驗證碼，填入 **Steam Guard Code** 欄位。
4. 勾選「**Save credentials (AES-256)**」並點擊「**Save Now**」，密碼將以 AES-256 加密後儲存。

### 步驟 5：按下部署按鈕

確認所有欄位填寫完畢後，點擊底部的大按鈕：

```
Build & Upload to Steam
```

系統將依序執行：
1. ✅ 驗證所有設定
2. 🔨 執行 Unity 建置（`BuildPipeline.BuildPlayer`）
3. 📝 生成 VDF 腳本（寫入 `steamcmd/scripts/`）
4. 🚀 啟動 SteamCMD 並上傳至 Steam
5. 📋 即時顯示 SteamCMD 輸出日誌
6. 🎉 顯示成功或失敗結果

---

## 密碼安全機制詳解

本工具**絕不**以明文儲存密碼。加密流程如下：

```
AES-256 金鑰（32 bytes）= SHA-256( deviceUniqueIdentifier + 固定鹽值 )
AES IV（16 bytes）       = MD5( deviceUniqueIdentifier + reversed(固定鹽值) )

密文 = AES-256-CBC( 明文密碼, Key, IV )
儲存 = EditorPrefs[ "SteamDeployer_EncryptedPassword" ] = Base64( 密文 )
```

- `SystemInfo.deviceUniqueIdentifier` 是硬體裝置的唯一識別碼，每台電腦都不同。
- 因此，即使有人複製了你的 `EditorPrefs` 登錄檔，在另一台電腦上也**無法解密**。
- 密碼**不會**寫入任何 `.asset`、`.json` 或 `.txt` 檔案，不會隨版本控制系統同步。

---

## 非同步執行機制詳解

SteamCMD 以子行程執行，若使用 `Process.WaitForExit()` 等待結束，將完全凍結 Unity 編輯器直到上傳完成（可能長達數十分鐘）。本工具採用以下非同步管線避免此問題：

```
SteamCMD stdout/stderr
    ↓ OS 背景 ThreadPool（DataReceivedEventHandler）
    ↓ ConcurrentQueue<LogEntry>（無鎖，執行緒安全）
    ↓ EditorApplication.update → PumpMainThread()（Unity 主執行緒，每幀呼叫）
    ↓ Debug.Log / Debug.LogError / Repaint（Unity API，只能在主執行緒呼叫）
```

---

## VDF 腳本範例

成功執行後，以下檔案會自動生成於 `{steamcmd目錄}/scripts/`：

**`app_build_1234560.vdf`**
```
"AppBuild"
{
    "AppID"         "1234560"
    "Desc"          "v1.0.0 - 2026-03-16"
    "Silent"        "0"
    "Preview"       "0"
    "ContentRoot"   "C:\\UnityProjects\\MyGame\\Temp\\SteamUploadOutput\\"
    "BuildOutput"   "C:\\steamcmd\\logs\\"
    "SetLive"       "default"
    "Depots"
    {
        "1234561"   "C:\\steamcmd\\scripts\\depot_build_1234561.vdf"
    }
}
```

**`depot_build_1234561.vdf`**
```
"DepotBuild"
{
    "DepotID"   "1234561"
    "FileMapping"
    {
        "LocalPath"     "*"
        "DepotPath"     "."
        "Recursive"     "1"
    }
    "FileExclusion"     "*.pdb"
    "FileExclusion"     "_BurstDebugInformation_DoNotShip"
}
```

---

## 常見問題排解

### ❌ 按下按鈕後出現「Path Contains Non-ASCII Characters」

**原因**：SteamCMD 的 C++ 核心對 Unicode 路徑支援極差。
**解決**：將 SteamCMD 移至純 ASCII 路徑（如 `C:\steamcmd\`），並確保 Unity 專案路徑也不含中文或特殊字元。

---

### ❌ 出現「Steam Guard code required」錯誤

**原因**：帳號啟用了 Steam Guard 兩步驟驗證。
**解決**：
1. 查看手機 Steam App 或電子郵件取得最新驗證碼。
2. 在「Steam Guard Code」欄位填入驗證碼。
3. 重新點擊部署按鈕。

---

### ❌ Unity Build Failed

**原因**：Unity 建置本身發生錯誤（腳本錯誤、缺少場景等）。
**解決**：查看 Unity Console 的紅色錯誤訊息，修正後重試。SteamCMD 上傳不會在建置失敗時執行。

---

### ❌ SteamCMD 退出碼非 0

| 退出碼 | 常見原因 |
|--------|---------|
| `5`    | 路徑含非 ASCII 字元，或檔案權限問題 |
| `6`    | AppID 或 Depot ID 設定錯誤 |
| `8`    | 帳號沒有此 AppID 的發布權限 |
| `63`   | 網路錯誤或 Steam 伺服器問題，稍後重試 |

---

### ❌ 換電腦後密碼無法解密

**原因**：AES 金鑰以硬體裝置 ID 派生，換機後 ID 不同，舊密文無法解密。
**解決**：這是預期行為（安全設計）。在新電腦上重新輸入密碼並勾選「Save Now」即可。

---

## 專案架構

```
Assets/Editor/SteamDeployer/
│
├── SteamDeployConfig.cs
│   └── ScriptableObject：儲存非敏感設定（AppID、路徑、分支等）
│
├── CryptographyHelper.cs
│   └── 靜態工具類：AES-256 加密/解密，管理 EditorPrefs 中的密文
│
├── VDFGenerator.cs
│   └── 靜態工具類：動態生成 app_build 與 depot_build VDF 腳本並寫入磁碟
│
├── SteamCmdProcessHandler.cs
│   └── 行程封裝類：啟動 steamcmd.exe、非同步 I/O 攔截、ConcurrentQueue 橋接
│
└── SteamDeployWindow.cs
    └── EditorWindow：主視窗 UI、狀態機、部署協調器
```

---

## 授權

MIT License — 詳見 [LICENSE](LICENSE) 檔案。

---

## 貢獻

歡迎提交 Issue 或 Pull Request。請確保所有變更在 Unity 2021.3 LTS 環境下可編譯，且不引入任何 Runtime Assembly 依賴（本工具應嚴格限於 `Editor` 環境）。
