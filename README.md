# Unity Steam Deployer

> [繁體中文](README_ZH.md)

Unity Editor plugin that runs a Unity build, generates SteamCMD VDF scripts, and uploads to Steam — all from a single EditorWindow.

*This project is a vibe-coded prototype for personal use.*

---

## Features

- Three operation modes from one window: **Build** only, **Upload** only, or **Build & Upload** (one-click).
- Configurable **Build Output Path** stored in the config asset (absolute or project-relative). Builds use an atomic temp-swap so a failed build never corrupts an existing output folder.
- Dynamically generates `app_build_{AppID}.vdf` and `depot_build_{DepotID}.vdf` into the SteamCMD `scripts/` directory.
- `SetLive` is an optional toggle. When enabled, promotes the specified branch after upload; when disabled, `SetLive` is left empty in the VDF (required for apps not yet past Valve's review queue).
- SteamCMD runs as an asynchronous child process; stdout/stderr are bridged to the Unity main thread via `ConcurrentQueue`, so the Editor is never blocked.
- Mid-operation Steam Guard input: when SteamCMD requests a code, the window enters `WaitingForSteamGuard` state; submitting the code resumes the upload without re-running the build.
- **Test Login** button: runs `+login` only to verify credentials without triggering a build or upload.
- **Unity 6+ Build Profile** support: optionally activate a Build Profile asset before building.
- Authentication and App Settings sections collapse automatically when all required fields are filled.
- Password encrypted with AES-256-CBC using a key derived from the machine's hardware ID, stored in `EditorPrefs`. Never written to any project file.
- Non-ASCII path validation (SteamCMD does not support Unicode paths).
- Upload is aborted automatically if the Unity build fails.

---

## Requirements

| | Minimum |
|--|---------|
| Unity | 2021.3 LTS |
| OS | Windows 10 / macOS 12 / Ubuntu 20.04 |
| Steam account | Steamworks partner account with publish rights for the target AppID |

---

## Installation

### UPM (Git URL)

1. **Window → Package Manager → + → Add package from git URL**
2. Enter:

```
https://github.com/qwe321qwe321qwe321/unity-steam-deployer.git
```

3. After import, **Tools → Steam Deployer → Open Window** appears in the menu.

> If Unity reports "no git executable was found": install Git, then restart Unity.

### Manual

Copy the `Editor/SteamDeployer/` folder into any `Editor/` directory in the target project.

---

## Configuration

### 1. Create a config asset

Open the deployer window (**Tools → Steam Deployer → Open Window**) and click **"Create New Config Asset"**.

Alternatively, right-click in the Project window: **Create → SteamDeployer → Deploy Config**.

The config asset contains no sensitive data and is safe to commit.

### 2. App settings

| Field | Description |
|-------|-------------|
| **App ID** | Steam Application ID from the Steamworks partner portal |
| **Depot ID** | Depot ID (for single-depot apps, typically AppID + 1) |
| **Set Live** | Whether to promote a branch after upload. Disable for apps not yet past Valve review |
| **Build Branch** | Branch to set live when Set Live is enabled (`default` for the public branch) |
| **Build Description** | Label shown in Steamworks build history. Supports `{Version}` (`Application.version`) and `{Date}` macros |
| **Ignore Files** | Comma-separated glob patterns mapped to VDF `FileExclusion` entries |
| **SteamCMD Path** | Absolute path to `steamcmd.exe` (Windows) or `steamcmd` (macOS/Linux) |
| **Build Output Path** | Absolute or project-relative path to the directory where Unity outputs the build. Also used as the depot content root for the Steam upload |

### 3. Authentication

- **Steam Username / Password**: passed to SteamCMD `+login`.
- **Save credentials (AES-256)**: encrypts the password and stores it in `EditorPrefs`; auto-loaded on next open.
- **Test Login**: verifies credentials and SteamCMD path without building or uploading.

### 4. Steam Guard

When Steam Guard is active, SteamCMD requires a code on login. The window enters `WaitingForSteamGuard`; enter the code to resume — no rebuild needed.

- **Email**: check for the code in the Steam verification email.
- **Mobile Authenticator**: Steam App → Steam Guard → current code.

After a successful login from the machine, SteamCMD caches the session; subsequent runs typically do not require a code.

---

## Deployment

The **Build & Upload** section exposes three buttons:

| Button | What it does |
|--------|-------------|
| **Build** | Runs `BuildPipeline.BuildPlayer` to the configured Build Output Path. Requires Build Output Path to be set. |
| **Upload** | Generates VDF scripts and launches SteamCMD against the existing build folder. Requires an executable to already be present in the output path. |
| **Build & Upload** | Runs both stages in sequence. Enabled only when all fields are filled and a Build Output Path is set. |

Clicking **Build & Upload** runs the following stages in order:

| Stage | Description |
|-------|-------------|
| Validate | Checks field completeness, path validity, non-ASCII characters |
| Unity Build | Calls `BuildPipeline.BuildPlayer` with the current Build Settings (or the selected Build Profile on Unity 6+). Builds to a temp folder first; the output folder is replaced only on success. |
| Generate VDF | Writes `app_build_{AppID}.vdf` and `depot_build_{DepotID}.vdf` to the SteamCMD `scripts/` directory |
| SteamCMD Upload | Launches the child process; log output streams to the window and Unity Console |
| Result | Green banner on success; red banner with error detail on failure |

---

## SteamCMD Setup

### Windows

Download and extract `steamcmd.zip`:

```
https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip
```

Run `steamcmd.exe` once to complete the self-update (quit after the `Steam>` prompt appears).

### macOS

```bash
mkdir ~/steamcmd && cd ~/steamcmd
curl -sqL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_osx.tar.gz | tar zxvf -
./steamcmd.sh +quit
```

### Linux (Ubuntu / Debian)

```bash
sudo apt-get install lib32gcc-s1 -y
mkdir ~/steamcmd && cd ~/steamcmd
curl -sqL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz | tar zxvf -
./steamcmd.sh +quit
```

> **Path restriction**: SteamCMD's C++ layer does not support Unicode paths. Both the SteamCMD directory and the Unity project path must be ASCII-only.

---

## Troubleshooting

### SteamCMD non-zero exit codes

| Exit code | Cause | Resolution |
|-----------|-------|------------|
| `5` | Non-ASCII characters in a path, or insufficient file permissions | Ensure all relevant paths are ASCII-only |
| `6` | Incorrect AppID or DepotID | Verify on the Steamworks partner portal |
| `8` | Account lacks publish rights for the target AppID | Confirm the account role is Admin or Developer |
| `63` | Network error or Steam servers unavailable | Retry later |

### Other issues

**Saved password does not work after switching machines**: the AES key is derived from the hardware ID, which differs between machines. Click "Clear Saved", re-enter the password, and save again.

**Unity build fails**: the SteamCMD upload stage does not run. Check the Console for errors. Common causes: no scenes added in Build Settings, missing Build Support Module for the target platform.

---

## Password Encryption

```
Key derivation:
  AES Key (32 bytes) = SHA-256( hardwareID + fixedSalt )
  AES IV  (16 bytes) = MD5( hardwareID + reversed(fixedSalt) )

Encrypt: plaintext password → AES-256-CBC → Base64 → EditorPrefs
Decrypt: EditorPrefs → Base64 → AES-256-CBC → plaintext (memory only)
```

The password never appears in any `.asset`, `.json`, `.txt`, or any Git-tracked file.

---

## Architecture

```
Editor/SteamDeployer/
├── SteamDeployConfig.cs        ScriptableObject storing non-sensitive config (AppID, paths, etc.)
├── CryptographyHelper.cs       AES-256 encrypt/decrypt, EditorPrefs ciphertext management
├── VDFGenerator.cs             Generates app_build.vdf and depot_build.vdf
├── SteamCmdProcessHandler.cs   Wraps Process, async I/O, ConcurrentQueue bridge
└── SteamDeployWindow.cs        Main EditorWindow UI and state machine
```

State machine: `Setup` → `Building` / `TestingLogin` → `Uploading` → `WaitingForSteamGuard` (if needed) → `Success` / `Failed`

Async log pipeline: SteamCMD stdout/stderr → OS ThreadPool → `ConcurrentQueue<LogEntry>` → `EditorApplication.update` (main thread) → `Debug.Log` / Repaint

---

## License

MIT License
