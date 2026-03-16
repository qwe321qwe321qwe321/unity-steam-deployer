# Unity Steam Deployer

一個「保母級」Unity 編輯器外掛程式，讓你只需點一個按鈕，即可自動完成遊戲編譯並上傳至 Steam。
全程不需要手動執行任何命令列指令，不需要手動編寫任何 VDF 腳本。

---

## 功能概覽

- **一鍵部署**：點擊「Build & Upload to Steam」，系統自動完成 Unity 編譯 → 生成 VDF 腳本 → 執行 SteamCMD 上傳。
- **AES-256 密碼加密**：密碼以硬體裝置 ID 為基礎的 AES-256 金鑰加密，儲存於系統登錄檔（`EditorPrefs`），永不寫入任何專案檔案。
- **不凍結編輯器**：SteamCMD 以獨立子行程非同步執行，上傳期間編輯器完全可操作，日誌即時顯示。
- **動態 VDF 生成**：自動產生 `app_build.vdf` 與 `depot_build.vdf`，無需手動編寫。
- **完整防呆機制**：路徑 Unicode 檢查、認證失敗偵測並自動終止、建置失敗自動中止上傳。

---

## 系統需求

| 項目 | 最低版本 |
|------|----------|
| Unity | 2021.3 LTS |
| 作業系統 | Windows 10 / macOS 12 / Ubuntu 20.04 |
| Steam 帳號 | 具有目標 AppID 發布權限的 [Steamworks 合作夥伴帳號](https://partner.steamgames.com/) |

---

## 第一步：安裝外掛程式（UPM，推薦）

> 這是最簡單的安裝方式，不需要手動複製任何檔案。

1. 在 Unity 編輯器，開啟上方選單 **Window → Package Manager**。
2. 點擊左上角的 **＋** 按鈕，選擇 **「Add package from git URL...」**。
3. 貼上以下網址，按 **Add**：

```
https://github.com/qwe321qwe321qwe321/unity-steam-deployer.git
```

4. Unity 會自動下載並匯入所有腳本。完成後，上方選單會出現 **Tools → Steam Deployer → Open Window**。

> **若出現 "no git executable was found" 錯誤**：表示電腦尚未安裝 Git。
> 前往 https://git-scm.com/download 下載安裝，安裝完畢後重新啟動 Unity 再試一次。

### 替代方案：手動複製

若不想使用 UPM，直接將整個 `Assets/Editor/SteamDeployer/` 資料夾複製到你的 Unity 專案的 `Assets/Editor/` 目錄下即可。

---

## 第二步：下載並安裝 SteamCMD

SteamCMD 是 Valve 官方提供的命令列上傳工具，本外掛程式透過它將遊戲推送至 Steam。

### Windows

1. 點擊以下連結直接下載 ZIP 檔：
   **[⬇ 下載 steamcmd.zip（Windows）](https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip)**

2. 建立資料夾 `C:\steamcmd\`（名稱和位置可自訂，但**路徑必須全為英文**，不可含中文或特殊符號）。

3. 將 ZIP 內的 `steamcmd.exe` 解壓縮到 `C:\steamcmd\`。

4. **雙擊執行一次** `steamcmd.exe`，讓它完成首次自我更新（會跑一堆下載，等它自動出現 `Steam>` 提示符後輸入 `quit` 關閉即可）。

   > 首次執行後，資料夾內會多出許多檔案，這是正常現象。

### macOS

1. 點擊以下連結下載：
   **[⬇ 下載 steamcmd_osx.tar.gz（macOS）](https://steamcdn-a.akamaihd.net/client/installer/steamcmd_osx.tar.gz)**

2. 開啟終端機，執行以下指令解壓縮並完成首次更新：

   ```bash
   mkdir ~/steamcmd && cd ~/steamcmd
   tar zxvf ~/Downloads/steamcmd_osx.tar.gz
   ./steamcmd.sh +quit
   ```

3. 完成後，`steamcmd.sh`（即 `steamcmd` 執行檔）位於 `~/steamcmd/` 資料夾。

### Linux（Ubuntu / Debian）

開啟終端機，依序執行：

```bash
# 安裝 32-bit 函式庫（SteamCMD 為 32-bit 執行檔）
sudo apt-get install lib32gcc-s1 -y

# 下載並解壓縮
mkdir ~/steamcmd && cd ~/steamcmd
curl -sqL "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz" | tar zxvf -

# 首次執行完成自我更新
./steamcmd.sh +quit
```

---

## 第三步：取得你的 AppID 與 DepotID

> 若你已知道自己的 AppID 和 DepotID，可跳過此步驟。

1. 登入 [Steamworks 合作夥伴後台](https://partner.steamgames.com/apps)。
2. 點擊你的遊戲，進入 **App Admin** 頁面，網址列的數字即為你的 **AppID**（例如 `1234560`）。
3. 在 App Admin 頁面，點擊左側 **SteamPipe → Depots**，找到你的 Depot 項目，該數字即為 **DepotID**（通常為 AppID + 1，例如 `1234561`）。

---

## 第四步：開啟視窗並完成設定

在 Unity 編輯器上方選單，點擊：

**Tools → Steam Deployer → Open Window**

### 4-1. 建立設定資產

視窗頂部「Configuration Asset」區塊中，點擊 **「Create New Config Asset」**。
系統會自動在 `Assets/Editor/SteamDeployer/SteamDeployConfig.asset` 建立設定檔。
這個檔案**可以提交到 Git**，它不含任何密碼等敏感資訊。

### 4-2. 填寫 App 設定

「App Settings」區塊，依照以下說明填寫：

| 欄位 | 填什麼 | 範例 |
|------|--------|------|
| **App ID** | 你的 Steam 遊戲 AppID | `1234560` |
| **Depot ID** | 你的 Steam Depot ID | `1234561` |
| **Build Branch** | 上傳後要 SetLive 的分支。預設主分支填 `default`；若要推到測試分支，填分支名稱 | `default` |
| **Build Description** | 顯示在 Steamworks 後台的版本說明。支援 `{Version}`（自動帶入 Unity 的 `Application.version`）和 `{Date}`（自動帶入今天日期）巨集 | `v{Version} - {Date}` |
| **Ignore Files** | 不想上傳的檔案模式，逗號分隔。預設值已包含常見的 debug 垃圾檔，通常不需要修改 | `*.pdb, _BurstDebugInformation_DoNotShip` |
| **SteamCMD Path** | 點擊右側 **Browse…** 按鈕，找到並選取 `steamcmd.exe`（Windows）或 `steamcmd`（macOS/Linux） | `C:\steamcmd\steamcmd.exe` |

### 4-3. 填寫認證資訊

「Authentication」區塊：

1. **Steam Username**：填入你的 Steam 開發者帳號的使用者名稱（不是顯示名稱，是登入帳號）。
2. **Password**：填入密碼。欄位顯示為 `●●●●`，旁人無法看到。
3. **Steam Guard Code**：
   - 若帳號有開啟 **Steam Guard 電子郵件驗證**：開啟你的電子郵件，找到 Steam 寄來的驗證碼，填入此欄位。
   - 若帳號有開啟 **Steam 行動裝置驗證器（Steam Mobile Authenticator）**：開啟手機 Steam App，在右下角點擊 **Steam Guard**，取得當前的 5 位數代碼，填入此欄位。
   - 若帳號沒有開啟任何 Steam Guard：留空即可。

4. 勾選 **「Save credentials (AES-256)」** 並點擊 **「Save Now」**：密碼會以 AES-256 加密後安全儲存，下次開啟視窗時會自動帶入，不需要重新輸入。

> **Steam Guard 驗證碼的時效性**：每個驗證碼只能使用一次，且有效期約 30 秒（行動裝置）到數分鐘（電子郵件）。請確認填入後盡快點擊部署按鈕。

---

## 第五步：按下部署按鈕

確認所有欄位填寫完畢，視窗底部的按鈕會變為可點擊狀態：

```
┌─────────────────────────────────────────────────┐
│                                                 │
│         Build & Upload to Steam                 │
│                                                 │
└─────────────────────────────────────────────────┘
```

點擊後，系統自動依序執行：

| 階段 | 說明 |
|------|------|
| ① 驗證設定 | 檢查所有欄位是否填寫完整、路徑是否合法、是否含非 ASCII 字元 |
| ② Unity 建置 | 呼叫 `BuildPipeline.BuildPlayer`，使用當前的 Build Settings（場景、目標平台） |
| ③ 生成 VDF | 自動寫出 `app_build.vdf` 與 `depot_build.vdf` 至 SteamCMD 的 `scripts/` 資料夾 |
| ④ SteamCMD 上傳 | 啟動 SteamCMD 子行程，日誌即時顯示於視窗底部與 Unity Console |
| ⑤ 顯示結果 | 成功顯示綠色橫幅，失敗顯示紅色橫幅並提示錯誤原因 |

---

## 常見問題排解

### ❌ 錯誤：「Path Contains Non-ASCII Characters」

**原因**：SteamCMD 的底層 C++ 程式碼對 Unicode 路徑支援極差，中文、日文、韓文、特殊符號等字元都會導致它以神秘的 Status 6 或 Exit Code 5 錯誤崩潰。

**解決方式**：

- **SteamCMD 路徑**：將 `steamcmd.exe` 移至純英文路徑，例如：
  - ✅ `C:\steamcmd\steamcmd.exe`
  - ❌ `C:\遊戲工具\steamcmd.exe`
  - ❌ `D:\Downloads\工具\Steam\steamcmd.exe`

- **Unity 專案路徑**：確保整個 Unity 專案資料夾的路徑也不含中文或特殊字元：
  - ✅ `D:\UnityProjects\MyGame\`
  - ❌ `D:\我的專案\MyGame\`

---

### ❌ 錯誤：「Steam Guard code required」

**原因**：帳號啟用了 Steam Guard，SteamCMD 需要驗證碼才能登入。

**解決方式**：
1. **電子郵件驗證**：查看 Steam 傳送的驗證碼 Email。
2. **行動裝置驗證器**：打開手機 Steam App → 右下角 Steam Guard → 取得當前代碼。
3. 將驗證碼填入視窗的「Steam Guard Code」欄位，重新點擊部署按鈕。

> 每次 SteamCMD 從未知 IP 登入時都需要 Steam Guard 驗證碼。一旦成功登入一次，SteamCMD 會快取 session，之後相同機器通常不需要再填驗證碼。

---

### ❌ 錯誤：「Invalid Password」

**原因**：密碼錯誤，或帳號受到登入限制。

**解決方式**：
1. 確認密碼正確（可先在 [Steam 網頁](https://store.steampowered.com/login/) 測試登入）。
2. 若密碼正確但仍失敗，帳號可能被暫時鎖定，等待 30 分鐘後再試。
3. 若使用了「儲存憑證」功能，點擊「Clear Saved」清除後重新輸入密碼並儲存。

---

### ❌ Unity 建置失敗（Build Failed）

**原因**：Unity 建置本身有錯誤，與本外掛無關。SteamCMD 上傳步驟不會在建置失敗時執行。

**解決方式**：查看 Unity Console 的紅色錯誤訊息，修正後重試。常見原因：
- C# 編譯錯誤
- Build Settings 中沒有加入任何場景（前往 **File → Build Settings** 確認）
- 目標平台尚未安裝對應的 Build Support Module

---

### ❌ SteamCMD 上傳失敗（非零退出碼）

| 退出碼 | 意義 | 解決方式 |
|--------|------|----------|
| `5` | 路徑含非 ASCII 字元，或檔案存取權限不足 | 見上方 Non-ASCII 排解說明 |
| `6` | AppID 或 DepotID 填寫錯誤 | 至 [Steamworks 後台](https://partner.steamgames.com/apps) 確認正確數值 |
| `8` | 此帳號沒有目標 AppID 的發布權限 | 確認 Steam 帳號是該遊戲的 Admin 或 Developer |
| `63` | 網路問題或 Steam 伺服器暫時不可用 | 稍後重試；可至 [Steam Status 頁面](https://www.steamstatus.com/) 確認伺服器狀態 |

---

### ❌ 換了一台電腦後，已儲存的密碼無法使用

**原因**：這是預期的安全設計行為。AES-256 金鑰是從硬體裝置 ID 派生的，換機後裝置 ID 不同，無法解密在另一台機器上加密的密文。

**解決方式**：在新電腦上，清除舊的加密資料（點擊「Clear Saved」），重新輸入密碼並勾選「Save Now」即可。

---

## 密碼安全機制說明

本工具採用 **AES-256-CBC** 對稱加密，密碼永遠不會明文儲存於磁碟上的任何檔案。

```
金鑰派生：
  AES Key (32 bytes) = SHA-256( 硬體裝置ID + 固定鹽值 )
  AES IV  (16 bytes) = MD5( 硬體裝置ID + reversed(固定鹽值) )

加密流程：
  明文密碼 → AES-256-CBC 加密 → Base64 編碼 → EditorPrefs 儲存
                                                （Windows 登錄檔 HKCU\Software\Unity Technologies\...）

解密流程：
  EditorPrefs 讀取 → Base64 解碼 → AES-256-CBC 解密 → 明文密碼（僅存於記憶體）
```

- 即使有人拷貝了你電腦的登錄檔，在另一台機器上也**無法解密**，因為硬體 ID 不同。
- 密碼**不會**出現在任何 `.asset`、`.json`、`.txt` 或任何 Git 可追蹤的檔案中。

---

## 專案架構

```
Assets/Editor/SteamDeployer/
│
├── SteamDeployConfig.cs        ← ScriptableObject：儲存非敏感設定（AppID、路徑等）
├── CryptographyHelper.cs       ← AES-256 加解密、EditorPrefs 密文管理
├── VDFGenerator.cs             ← 動態生成 app_build.vdf 與 depot_build.vdf
├── SteamCmdProcessHandler.cs   ← 封裝 Process、非同步 I/O、ConcurrentQueue 橋接
└── SteamDeployWindow.cs        ← 主視窗 UI、狀態機（Setup/Building/Uploading/Success/Failed）
```

### 非同步執行架構

```
SteamCMD stdout/stderr
    │
    ▼  OS 背景 ThreadPool（DataReceivedEventHandler）
ConcurrentQueue<LogEntry>          ← 唯一跨執行緒的資料交換點（lock-free）
    │
    ▼  EditorApplication.update（Unity 主執行緒，每幀呼叫）
PumpMainThread()
    │
    ├─→ OnLogLine    → Debug.Log       + 視窗日誌緩衝區
    ├─→ OnErrorLine  → Debug.LogError  + 視窗日誌緩衝區
    └─→ OnAuthFailure → Kill Process   + 彈出引導對話框
```

> 永遠不使用 `Process.WaitForExit()`，因為它會凍結 Unity 主執行緒直到 SteamCMD 結束。

---

## 授權

MIT License
