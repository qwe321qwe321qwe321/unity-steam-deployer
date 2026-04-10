# Unity Steam Deployer

> [English](README.md)

Unity Editor 外掛程式，執行 Unity 建置後自動生成 VDF 腳本並透過 SteamCMD 上傳至 Steam。

***此專案為 Vibe Coding 之產物，僅為個人原型專案之速成應用。***

---

## 功能

- 依序執行：Unity BuildPipeline → 生成 VDF → SteamCMD 上傳，全程由單一視窗控制。
- 動態生成 `app_build_{AppID}.vdf` 與 `depot_build_{DepotID}.vdf`，寫入 SteamCMD 的 `scripts/` 目錄。
- `SetLive` 為可選開關；啟用時在上傳後將指定分支設為 live，停用時 `SetLive` 欄位留空（適用於尚未通過 Valve 審核的新 App）。
- SteamCMD 以獨立子行程非同步執行，stdout/stderr 經 `ConcurrentQueue` 橋接至 Unity 主執行緒，Editor 不阻塞。
- 支援 Steam Guard 中途輸入：SteamCMD 要求驗證碼時進入 `WaitingForSteamGuard` 狀態，填碼後繼續，不需重新建置。
- 提供「Test Login」按鈕，僅執行 `+login` 驗證憑證，不觸發建置或上傳。
- 密碼以 AES-256-CBC 加密，金鑰由硬體裝置 ID 派生，存於 `EditorPrefs`，不寫入任何專案檔。
- 路徑 non-ASCII 字元檢查（SteamCMD 不支援 Unicode 路徑）。
- 建置失敗時自動中止，不進入上傳流程。

---

## 系統需求

| 項目 | 版本 |
|------|------|
| Unity | 2021.3 LTS 以上 |
| 作業系統 | Windows 10 / macOS 12 / Ubuntu 20.04 |
| Steam 帳號 | 具有目標 AppID 發布權限的 Steamworks 合作夥伴帳號 |

---

## 安裝

### UPM（Git URL）

1. **Window → Package Manager → + → Add package from git URL**
2. 輸入：

```
https://github.com/qwe321qwe321qwe321/unity-steam-deployer.git
```

3. 匯入完成後，選單出現 **Tools → Steam Deployer → Open Window**。

> 若出現 "no git executable was found"：安裝 Git 後重啟 Unity。

### 手動安裝

將整個 `Editor/SteamDeployer/` 資料夾複製至目標專案的任意 `Editor/` 目錄下。

---

## 設定

### 1. 建立設定資產

開啟部署視窗（**Tools → Steam Deployer → Open Window**），點擊 **「Create New Config Asset」**。

或在 Project 視窗右鍵：**Create → SteamDeployer → Deploy Config**。

Config asset 不含任何敏感資訊，可提交至版本控制。

### 2. App 設定欄位

| 欄位 | 說明 |
|------|------|
| **App ID** | Steamworks 上的 Application ID |
| **Depot ID** | 對應的 Depot ID（單 depot 通常為 AppID + 1） |
| **Set Live** | 上傳後是否自動 SetLive。新 App 尚未通過審核時應停用 |
| **Build Branch** | SetLive 啟用時指定的分支（主分支填 `default`） |
| **Build Description** | Steamworks 建置歷史顯示的說明。支援 `{Version}`（`Application.version`）與 `{Date}` 巨集 |
| **Ignore Files** | 逗號分隔的排除 glob 模式，對應 VDF 的 `FileExclusion` |
| **SteamCMD Path** | `steamcmd.exe`（Windows）或 `steamcmd`（macOS/Linux）的絕對路徑 |

### 3. 認證欄位

- **Steam Username / Password**：用於 SteamCMD `+login`。
- **Save credentials (AES-256)**：啟用後密碼加密存於 `EditorPrefs`，下次開啟視窗自動帶入。
- **Test Login**：僅執行登入驗證，確認憑證與 SteamCMD 路徑正確。

### 4. Steam Guard

帳號開啟 Steam Guard 時，SteamCMD 登入會要求驗證碼。視窗進入 `WaitingForSteamGuard` 狀態後輸入即可，不需重新建置。

- **Email 驗證**：查收 Steam 寄出的驗證碼 email。
- **行動裝置驗證器**：Steam App → Steam Guard → 取得當前代碼。

首次從該機器登入後，SteamCMD 會快取 session，後續通常不再需要輸入驗證碼。

---

## 部署流程

點擊 **「Build & Upload to Steam」** 後依序執行：

| 階段 | 說明 |
|------|------|
| 驗證設定 | 檢查欄位完整性、路徑合法性、non-ASCII 字元 |
| Unity 建置 | 呼叫 `BuildPipeline.BuildPlayer`，使用當前 Build Settings |
| 生成 VDF | 寫出 `app_build_{AppID}.vdf` 與 `depot_build_{DepotID}.vdf` 至 SteamCMD `scripts/` 目錄 |
| SteamCMD 上傳 | 啟動子行程，log 即時顯示於視窗與 Unity Console |
| 結果 | 成功顯示綠色橫幅；失敗顯示紅色橫幅與錯誤說明 |

---

## SteamCMD 安裝

### Windows

```
https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip
```

解壓後雙擊執行一次，等待自我更新完成（出現 `Steam>` 提示後輸入 `quit`）。

### macOS

```bash
mkdir ~/steamcmd && cd ~/steamcmd
curl -sqL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_osx.tar.gz | tar zxvf -
./steamcmd.sh +quit
```

### Linux（Ubuntu / Debian）

```bash
sudo apt-get install lib32gcc-s1 -y
mkdir ~/steamcmd && cd ~/steamcmd
curl -sqL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz | tar zxvf -
./steamcmd.sh +quit
```

> **路徑限制**：SteamCMD 的 C++ 層不支援 Unicode 路徑，SteamCMD 本身與 Unity 專案路徑均須為純 ASCII。

---

## 錯誤排解

### SteamCMD 非零退出碼

| 退出碼 | 原因 | 處理方式 |
|--------|------|----------|
| `5` | 路徑含 non-ASCII 字元，或檔案存取權限不足 | 確保所有路徑為純 ASCII |
| `6` | AppID 或 DepotID 錯誤 | 至 Steamworks 後台確認 |
| `8` | 帳號對目標 AppID 無發布權限 | 確認帳號角色為 Admin 或 Developer |
| `63` | 網路問題或 Steam 伺服器不可用 | 稍後重試 |

### 其他常見問題

**換機後已儲存的密碼無法使用**：金鑰由硬體裝置 ID 派生，換機後裝置 ID 不同，無法解密。點擊「Clear Saved」後重新輸入並儲存。

**Unity 建置失敗**：SteamCMD 上傳步驟不會執行。查看 Console 錯誤訊息；常見原因為 Build Settings 未加入場景、缺少對應平台的 Build Support Module。

---

## 密碼加密機制

```
金鑰派生：
  AES Key (32 bytes) = SHA-256( 硬體裝置ID + 固定鹽值 )
  AES IV  (16 bytes) = MD5( 硬體裝置ID + reversed(固定鹽值) )

加密：明文密碼 → AES-256-CBC → Base64 → EditorPrefs
解密：EditorPrefs → Base64 → AES-256-CBC → 明文（僅存於記憶體）
```

密碼不出現在任何 `.asset`、`.json`、`.txt` 或 Git 可追蹤的檔案中。

---

## 架構

```
Editor/SteamDeployer/
├── SteamDeployConfig.cs        ScriptableObject：儲存非敏感設定（AppID、路徑等）
├── CryptographyHelper.cs       AES-256 加解密、EditorPrefs 密文管理
├── VDFGenerator.cs             生成 app_build.vdf 與 depot_build.vdf
├── SteamCmdProcessHandler.cs   封裝 Process、非同步 I/O、ConcurrentQueue 橋接
└── SteamDeployWindow.cs        主視窗 UI、狀態機
```

狀態機：`Setup` → `Building` / `TestingLogin` → `Uploading` → `WaitingForSteamGuard`（視情況）→ `Success` / `Failed`

非同步 log 管線：SteamCMD stdout/stderr → OS ThreadPool → `ConcurrentQueue<LogEntry>` → `EditorApplication.update`（主執行緒）→ `Debug.Log` / Repaint

---

## 授權

MIT License
