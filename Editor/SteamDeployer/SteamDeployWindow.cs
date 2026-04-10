using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SteamDeployer
{
	/// <summary>
	/// Main EditorWindow for the Steam Deployer tool.
	///
	/// STATE MACHINE:
	///   Setup                  → user configures credentials and app settings; all fields editable.
	///   TestingLogin           → steamcmd.exe is running a login-only test; UI locked.
	///   Building               → Unity BuildPipeline is running; all fields locked.
	///   Uploading              → steamcmd.exe is running the full upload; UI locked; live log visible.
	///   WaitingForSteamGuard   → steamcmd reported that a Steam Guard code is required;
	///                            a code-entry form is shown; all other fields locked.
	///   Success                → operation completed successfully; green banner shown.
	///   Failed                 → operation failed; red banner shown; fields unlocked for retry.
	///
	/// MAIN THREAD SAFETY:
	///   EditorApplication.update is registered in OnEnable and removed in OnDisable.
	///   The update callback (OnEditorUpdate) calls SteamCmdProcessHandler.PumpMainThread()
	///   every editor frame to safely drain the ConcurrentQueue and call Unity APIs.
	/// </summary>
	public sealed class SteamDeployWindow : EditorWindow
	{
		// ─── State machine ────────────────────────────────────────────────────────

		private enum DeployState
		{
			Setup,
			TestingLogin,
			Building,
			Uploading,
			WaitingForSteamGuard,
			Success,
			Failed,
		}

		private DeployState _state         = DeployState.Setup;
		private string      _taskLabel     = "";
		private float       _progressValue = 0f;

		// ─── Config & runtime credentials ────────────────────────────────────────

		private SteamDeployConfig _config;
		private string            _password        = "";
		private bool              _saveCredentials = false;

		// ─── Steam Guard code flow ────────────────────────────────────────────────

		/// <summary>
		/// Code entered by the user in the WaitingForSteamGuard state.
		/// Submitted to steamcmd via +set_steam_guard_code on retry.
		/// </summary>
		private string _steamGuardCodeInput = "";

		/// <summary>
		/// True when the current or most-recent operation was a test-login (not a full deploy).
		/// Used to tailor log messages, the result banner, and the Steam Guard retry path.
		/// </summary>
		private bool _isTestLoginContext = false;

		/// <summary>
		/// Stores the app VDF path so the upload can be retried after a Steam Guard prompt
		/// without re-running the full Unity build.
		/// </summary>
		private string _pendingAppVdfPath = "";

		// ─── Process handler ──────────────────────────────────────────────────────

		private SteamCmdProcessHandler _processHandler;
		private bool                   _isProcessRunning;

		// ─── Embedded log buffer ──────────────────────────────────────────────────

		private string  _logBuffer = "";
		private Vector2 _logScroll;

		/// <summary>
		/// Maximum number of characters retained in the embedded log buffer.
		/// Prevents the TextArea from accumulating unbounded memory over long uploads.
		/// </summary>
		private const int MAX_LOG_BUFFER_CHARS = 60_000;

		// ─── GUI styles (lazy-initialized inside OnGUI) ───────────────────────────

		private GUIStyle _boxStyle;
		private GUIStyle _bigButtonStyle;
		private GUIStyle _logStyle;
		private GUIStyle _successBoxStyle;
		private GUIStyle _failureBoxStyle;
		private GUIStyle _warningBoxStyle;
		private bool     _stylesReady;

		// ─── Menu item ────────────────────────────────────────────────────────────

		[MenuItem("Tools/Steam Deployer/Open Window")]
		public static void OpenWindow()
		{
			var window = GetWindow<SteamDeployWindow>("Steam Deployer");
			window.minSize = new Vector2(500, 720);
			window.Show();
		}

		// ─── EditorWindow lifecycle ───────────────────────────────────────────────

		private void OnEnable()
		{
			TryLoadConfig();

			if (CryptographyHelper.HasStoredPassword())
			{
				_password        = CryptographyHelper.LoadDecryptedPassword() ?? "";
				_saveCredentials = true;
			}

			EditorApplication.update += OnEditorUpdate;
		}

		private void OnDisable()
		{
			EditorApplication.update -= OnEditorUpdate;

			if (_isProcessRunning)
			{
				_processHandler?.Kill();
				_processHandler?.Dispose();
				_processHandler   = null;
				_isProcessRunning = false;
			}
		}

		// ─── Main-thread pump ─────────────────────────────────────────────────────

		/// <summary>
		/// Called every Unity editor frame via EditorApplication.update.
		/// This is the ONLY safe place to consume data produced by the background I/O threads
		/// and forward it to Unity APIs (Debug.Log, Repaint).
		///
		/// ASYNC LOG PIPELINE:
		///   steamcmd.exe stdout/stderr
		///     → OS background ThreadPool (DataReceivedEventHandler)
		///     → ConcurrentQueue{LogEntry} inside SteamCmdProcessHandler   (thread-safe push)
		///     → PumpMainThread() called here on the main thread            (thread-safe pop)
		///     → OnLogLine / OnErrorLine / OnSteamGuardRequired / OnAuthenticationFailure events
		///     → Unity Debug.Log / Repaint
		/// </summary>
		private void OnEditorUpdate()
		{
			if (!_isProcessRunning || _processHandler == null) return;

			bool done = _processHandler.PumpMainThread();

			if (done)
				_isProcessRunning = false;

			Repaint();
		}

		// ─── GUI ──────────────────────────────────────────────────────────────────

		private void OnGUI()
		{
			EnsureStyles();

			EditorGUILayout.Space(6);
			EditorGUILayout.LabelField("  Steam Deployer", EditorStyles.largeLabel);
			EditorGUILayout.LabelField("  Automated Build & Upload via SteamCMD", EditorStyles.miniLabel);
			EditorGUILayout.Space(4);

			bool locked = _state == DeployState.Building
			           || _state == DeployState.Uploading
			           || _state == DeployState.TestingLogin
			           || _state == DeployState.WaitingForSteamGuard;

			using (new EditorGUI.DisabledScope(locked))
			{
				DrawConfigSection();
				DrawAuthSection();
				DrawAppSettingsSection();
			}

			DrawExecutionSection(locked);
			DrawLogSection();
			DrawResultBanner();
		}

		// ─── Section: Config asset ────────────────────────────────────────────────

		private void DrawConfigSection()
		{
			using (new GUILayout.VerticalScope(_boxStyle))
			{
				EditorGUILayout.LabelField("Configuration Asset", EditorStyles.boldLabel);

				_config = (SteamDeployConfig)EditorGUILayout.ObjectField(
					"Deploy Config", _config, typeof(SteamDeployConfig), false);

				if (_config == null)
				{
					EditorGUILayout.HelpBox(
						"No config assigned. Create one or drag an existing asset here.",
						MessageType.Warning);

					if (GUILayout.Button("Create New Config Asset"))
						CreateConfigAsset();
				}
			}
			EditorGUILayout.Space(3);
		}

		// ─── Section: Authentication ──────────────────────────────────────────────

		private void DrawAuthSection()
		{
			using (new GUILayout.VerticalScope(_boxStyle))
			{
				EditorGUILayout.LabelField("Authentication", EditorStyles.boldLabel);

				if (_config != null)
					_config.SteamUsername = EditorGUILayout.TextField("Steam Username", _config.SteamUsername);

				_password = EditorGUILayout.PasswordField("Password", _password);

				EditorGUILayout.Space(4);

				bool prevSave = _saveCredentials;
				_saveCredentials = EditorGUILayout.Toggle(
					new GUIContent("Save credentials (AES-256)",
						"Encrypts the password with your machine's hardware ID and stores it in EditorPrefs."),
					_saveCredentials);

				if (prevSave && !_saveCredentials)
					CryptographyHelper.ClearStoredPassword();

				if (_saveCredentials)
				{
					using (new GUILayout.HorizontalScope())
					{
						GUILayout.FlexibleSpace();
						if (GUILayout.Button("Save Now", GUILayout.Width(100)))
						{
							CryptographyHelper.SaveEncryptedPassword(_password);
							EditorUtility.DisplayDialog("Saved",
								"Password encrypted with AES-256 and stored in EditorPrefs.\n" +
								"It is only decryptable on this machine.", "OK");
						}
						if (GUILayout.Button("Clear Saved", GUILayout.Width(100)))
							CryptographyHelper.ClearStoredPassword();
					}

					if (CryptographyHelper.HasStoredPassword())
						EditorGUILayout.HelpBox("Encrypted password stored for this machine.", MessageType.Info);
				}

				// ── Test Login ────────────────────────────────────────────────────
				EditorGUILayout.Space(6);

				bool canTestLogin = _config != null
				    && !string.IsNullOrWhiteSpace(_config.SteamUsername)
				    && !string.IsNullOrWhiteSpace(_password)
				    && !string.IsNullOrWhiteSpace(_config.SteamCmdPath);

				using (new EditorGUI.DisabledScope(!canTestLogin))
				{
					if (GUILayout.Button(
						    new GUIContent("Test Login",
							    "Runs steamcmd.exe with +login only (no build or upload) to verify your credentials."),
						    GUILayout.Height(28)))
					{
						StartTestLogin();
					}
				}

				if (!canTestLogin)
				{
					EditorGUILayout.HelpBox(
						"Fill in username, password, and SteamCMD path to enable Test Login.",
						MessageType.None);
				}
			}
			EditorGUILayout.Space(3);
		}

		// ─── Section: App settings ────────────────────────────────────────────────

		private void DrawAppSettingsSection()
		{
			if (_config == null) return;

			using (new GUILayout.VerticalScope(_boxStyle))
			{
				EditorGUILayout.LabelField("App Settings", EditorStyles.boldLabel);

				using (var check = new EditorGUI.ChangeCheckScope())
				{
					_config.AppID            = EditorGUILayout.TextField("App ID",            _config.AppID);
					_config.DepotID          = EditorGUILayout.TextField("Depot ID",          _config.DepotID);
					_config.SetLiveEnabled = EditorGUILayout.Toggle(
						new GUIContent("Set Live After Upload",
							"When enabled, the build is immediately promoted to the specified branch after upload. " +
							"Disable for new apps awaiting Valve review, or to promote manually from Steamworks."),
						_config.SetLiveEnabled);

					using (new EditorGUI.DisabledScope(!_config.SetLiveEnabled))
					{
						_config.BuildBranch = EditorGUILayout.TextField(
							new GUIContent("Branch",
								"The Steam branch to promote after upload (e.g. 'default', 'beta', 'staging')."),
							_config.BuildBranch);
					}
					_config.BuildDescription = EditorGUILayout.TextField("Build Description", _config.BuildDescription);

					EditorGUILayout.HelpBox(
						"Description supports {Version}, {Date}, {DateTime} macros.",
						MessageType.None);

					_config.IgnoreFiles = EditorGUILayout.TextField(
						new GUIContent("Ignore Files",
							"Comma-separated glob patterns excluded from the depot (e.g. *.pdb, *.lib)."),
						_config.IgnoreFiles);

					EditorGUILayout.Space(6);
					EditorGUILayout.LabelField("SteamCMD Executable", EditorStyles.boldLabel);

					using (new GUILayout.HorizontalScope())
					{
						_config.SteamCmdPath = EditorGUILayout.TextField(_config.SteamCmdPath);
						if (GUILayout.Button("Browse…", GUILayout.Width(72)))
						{
							string path = EditorUtility.OpenFilePanel("Locate steamcmd.exe", "", "exe");
							if (!string.IsNullOrEmpty(path))
								_config.SteamCmdPath = NormalizeSteamCmdPath(path);
						}
					}

					if (check.changed)
					{
						EditorUtility.SetDirty(_config);
						AssetDatabase.SaveAssets();
					}
				}
			}
			EditorGUILayout.Space(3);
		}

		// ─── Section: Execution ───────────────────────────────────────────────────

		private void DrawExecutionSection(bool locked)
		{
			// ── Steam Guard prompt (overrides all other execution UI) ─────────────
			if (_state == DeployState.WaitingForSteamGuard)
			{
				using (new GUILayout.VerticalScope(_warningBoxStyle))
				{
					EditorGUILayout.LabelField("Steam Guard Code Required", EditorStyles.boldLabel);
					EditorGUILayout.HelpBox(
						"SteamCMD requires a Steam Guard code.\n" +
						"Check your email or authenticator app, then enter the code below.",
						MessageType.Warning);

					EditorGUILayout.Space(4);
					_steamGuardCodeInput = EditorGUILayout.TextField("Steam Guard Code", _steamGuardCodeInput);
					EditorGUILayout.Space(6);

					using (new GUILayout.HorizontalScope())
					{
						bool hasCode = !string.IsNullOrWhiteSpace(_steamGuardCodeInput);
						using (new EditorGUI.DisabledScope(!hasCode))
						{
							if (GUILayout.Button("Submit Code", GUILayout.Height(32)))
								SubmitSteamGuardCode();
						}

						if (GUILayout.Button("Cancel", GUILayout.Height(32)))
						{
							GUIUtility.keyboardControl = 0;
							_steamGuardCodeInput       = "";
							_state                     = DeployState.Setup;
						}
					}
				}
				EditorGUILayout.Space(3);
				return;
			}

			// ── Normal execution UI ───────────────────────────────────────────────
			using (new GUILayout.VerticalScope(_boxStyle))
			{
				if (locked)
				{
					EditorGUILayout.Space(6);
					EditorGUILayout.LabelField(_taskLabel, EditorStyles.centeredGreyMiniLabel);
					EditorGUILayout.Space(4);

					Rect bar = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(22));
					EditorGUI.ProgressBar(bar, _progressValue, _taskLabel);

					EditorGUILayout.Space(6);

					if (GUILayout.Button("Cancel", GUILayout.Height(28)))
						CancelOperation();
				}
				else
				{
					bool canDeploy = _config != null
					    && !string.IsNullOrWhiteSpace(_config.AppID)
					    && !string.IsNullOrWhiteSpace(_config.DepotID)
					    && !string.IsNullOrWhiteSpace(_config.SteamCmdPath)
					    && !string.IsNullOrWhiteSpace(_config.SteamUsername)
					    && !string.IsNullOrWhiteSpace(_password);

					using (new EditorGUI.DisabledScope(!canDeploy))
					{
						EditorGUILayout.Space(8);
						if (GUILayout.Button("Build & Upload to Steam", _bigButtonStyle, GUILayout.Height(52)))
							StartDeployment();
						EditorGUILayout.Space(8);
					}

					if (!canDeploy)
					{
						EditorGUILayout.HelpBox(
							"Please fill in: App ID, Depot ID, SteamCMD path, username, and password.",
							MessageType.Warning);
					}
				}
			}
			EditorGUILayout.Space(3);
		}

		// ─── Section: Log ─────────────────────────────────────────────────────────

		private void DrawLogSection()
		{
			if (string.IsNullOrEmpty(_logBuffer)) return;

			using (new GUILayout.VerticalScope(_boxStyle))
			{
				using (new GUILayout.HorizontalScope())
				{
					EditorGUILayout.LabelField("SteamCMD Output", EditorStyles.boldLabel);
					GUILayout.FlexibleSpace();
					if (GUILayout.Button("Clear", GUILayout.Width(56)))
						_logBuffer = "";
					if (GUILayout.Button("Open Editor.log", GUILayout.Width(110)))
						RevealEditorLog();
				}

				_logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(180));
				EditorGUILayout.TextArea(_logBuffer, _logStyle, GUILayout.ExpandHeight(true));
				EditorGUILayout.EndScrollView();
			}
			EditorGUILayout.Space(3);
		}

		// ─── Section: Result banner ───────────────────────────────────────────────

		private void DrawResultBanner()
		{
			if (_state == DeployState.Success)
			{
				string message = _isTestLoginContext
					? "  Login Test Successful!"
					: "  Upload Successful!";

				using (new GUILayout.VerticalScope(_successBoxStyle))
					EditorGUILayout.LabelField(message, EditorStyles.boldLabel);
			}
			else if (_state == DeployState.Failed)
			{
				string message = _isTestLoginContext
					? "  Login Test Failed — see Console for details."
					: "  Deployment Failed — see Console for details.";

				using (new GUILayout.VerticalScope(_failureBoxStyle))
					EditorGUILayout.LabelField(message, EditorStyles.boldLabel);
			}
		}

		// ─── Test Login ───────────────────────────────────────────────────────────

		/// <summary>
		/// Entry point for a credentials-only test: runs steamcmd.exe with +login ... +quit
		/// without triggering a build or upload. Handles Steam Guard prompts inline.
		/// </summary>
		private void StartTestLogin()
		{
			if (!ValidateTestLogin()) return;

			_logBuffer           = "";
			_isTestLoginContext  = true;
			_steamGuardCodeInput = "";

			if (_saveCredentials && !string.IsNullOrEmpty(_password))
				CryptographyHelper.SaveEncryptedPassword(_password);

			LaunchTestLogin(steamGuardCode: "");
		}

		private void LaunchTestLogin(string steamGuardCode)
		{
			string password      = GetEffectivePassword();
			string resolvedPath  = ResolveSteamCmdPath();
			string args          = SteamCmdProcessHandler.BuildTestLoginArguments(
				_config.SteamUsername, password, steamGuardCode);

			_processHandler = CreateAndWireProcessHandler();

			_state         = DeployState.TestingLogin;
			_taskLabel     = "Testing Steam login...";
			_progressValue = 0.5f;

			AppendLog($"Testing login for: {_config.SteamUsername}", isError: false);

			_isProcessRunning = true;

			if (!_processHandler.Start(resolvedPath, args))
			{
				_isProcessRunning = false;
				SetFailedState("Failed to start steamcmd.exe.");
			}
		}

		// ─── Deployment orchestration ─────────────────────────────────────────────

		/// <summary>
		/// Entry point for the build + upload pipeline. Validates all inputs, triggers
		/// the Unity build, generates VDF files, and launches steamcmd.exe.
		/// </summary>
		private void StartDeployment()
		{
			if (!ValidatePreFlight()) return;

			_isTestLoginContext  = false;
			_pendingAppVdfPath   = "";
			_steamGuardCodeInput = "";
			_state               = DeployState.Building;
			_logBuffer           = "";
			_progressValue       = 0.05f;
			_taskLabel           = "Preparing build...";
			Repaint();

			if (_saveCredentials && !string.IsNullOrEmpty(_password))
				CryptographyHelper.SaveEncryptedPassword(_password);

			// ── Phase 1: Unity Build ─────────────────────────────────────────────

			_taskLabel     = "Building Unity project...";
			_progressValue = 0.15f;
			Repaint();

			string buildOutputPath = Path.GetFullPath(
				Path.Combine(Application.dataPath, "../Temp/SteamUploadOutput/"));

			if (Directory.Exists(buildOutputPath))
				Directory.Delete(buildOutputPath, recursive: true);
			Directory.CreateDirectory(buildOutputPath);

			BuildReport report = RunUnityBuild(buildOutputPath);

			if (report == null || report.summary.result != BuildResult.Succeeded)
			{
				string detail = report != null
					? $"Result={report.summary.result}, Errors={report.summary.totalErrors}"
					: "BuildReport was null (build may have been cancelled).";

				Debug.LogError($"[SteamDeployer] Unity build FAILED — {detail}. Upload aborted.");
				AppendLog($"BUILD FAILED: {detail}", isError: true);
				SetFailedState("Build failed.");
				return;
			}

			Debug.Log($"[SteamDeployer] Unity build succeeded. Output: {buildOutputPath}");
			AppendLog($"Build succeeded → {buildOutputPath}", isError: false);
			_progressValue = 0.5f;

			// ── Phase 2: Generate VDF scripts ────────────────────────────────────

			_taskLabel     = "Generating SteamCMD VDF scripts...";
			_progressValue = 0.6f;
			Repaint();

			string appVdfPath;
			try
			{
				string desc = ResolveMacros(_config.BuildDescription);
				appVdfPath  = VDFGenerator.GenerateVdfScripts(_config, buildOutputPath, desc, ResolveSteamCmdPath());
				AppendLog($"VDF scripts written. App VDF: {appVdfPath}", isError: false);
			}
			catch (Exception ex)
			{
				Debug.LogError($"[SteamDeployer] VDF generation failed: {ex.Message}");
				AppendLog($"VDF generation failed: {ex.Message}", isError: true);
				SetFailedState("VDF generation failed.");
				return;
			}

			// ── Phase 3: Launch SteamCMD ─────────────────────────────────────────

			_taskLabel     = "Uploading to Steam via SteamCMD...";
			_progressValue = 0.70f;
			_state         = DeployState.Uploading;
			Repaint();

			LaunchSteamCmd(appVdfPath, steamGuardCode: "");
		}

		private void LaunchSteamCmd(string appVdfPath, string steamGuardCode)
		{
			_pendingAppVdfPath = appVdfPath;

			string password     = GetEffectivePassword();
			string resolvedPath = ResolveSteamCmdPath();
			string args         = SteamCmdProcessHandler.BuildArguments(
				_config.SteamUsername, password, steamGuardCode, appVdfPath);

			_processHandler = CreateAndWireProcessHandler();

			AppendLog($"Launching: {resolvedPath}", isError: false);
			AppendLog($"App VDF:   {appVdfPath}",   isError: false);

			_isProcessRunning = true;

			if (!_processHandler.Start(resolvedPath, args))
			{
				_isProcessRunning = false;
				SetFailedState("Failed to start steamcmd.exe.");
			}
		}

		/// <summary>
		/// Invokes BuildPipeline.BuildPlayer using the currently active build settings.
		/// </summary>
		private BuildReport RunUnityBuild(string outputPath)
		{
			try
			{
				BuildPlayerOptions opts = BuildPlayerWindow.DefaultBuildMethods
					.GetBuildPlayerOptions(new BuildPlayerOptions());

				string exe = Application.productName + GetExeExtension(opts.target);
				opts.locationPathName = Path.Combine(outputPath, exe);
				opts.options          = BuildOptions.None;

				return BuildPipeline.BuildPlayer(opts);
			}
			catch (BuildPlayerWindow.BuildMethodException ex)
			{
				Debug.LogWarning($"[SteamDeployer] Build was cancelled or settings are invalid: {ex.Message}");
				return null;
			}
			catch (Exception ex)
			{
				Debug.LogError($"[SteamDeployer] Unexpected error during build: {ex.Message}");
				return null;
			}
		}

		// ─── Steam Guard code submission ──────────────────────────────────────────

		/// <summary>
		/// Called when the user clicks "Submit Code" in the WaitingForSteamGuard state.
		/// Resumes the interrupted operation (test login or upload) with the provided code.
		/// </summary>
		private void SubmitSteamGuardCode()
		{
			GUIUtility.keyboardControl = 0;  // Release focus so the log TextArea updates correctly.
			string code          = _steamGuardCodeInput.Trim();
			_steamGuardCodeInput = "";

			if (_isTestLoginContext)
			{
				LaunchTestLogin(code);
			}
			else
			{
				_state         = DeployState.Uploading;
				_taskLabel     = "Uploading to Steam via SteamCMD...";
				_progressValue = 0.70f;
				LaunchSteamCmd(_pendingAppVdfPath, code);
			}
		}

		// ─── Process event handlers (called from PumpMainThread — main thread) ────

		private void HandleProcessExited(int exitCode)
		{
			_isProcessRunning = false;
			_processHandler?.Dispose();
			_processHandler = null;

			if (exitCode == 0)
			{
				_state         = DeployState.Success;
				_progressValue = 1.0f;
				_taskLabel     = _isTestLoginContext ? "Login successful!" : "Upload complete!";

				string successMsg = _isTestLoginContext
					? "=== LOGIN TEST SUCCESSFUL ==="
					: "=== UPLOAD SUCCESSFUL ===";

				Debug.Log($"[SteamDeployer] SteamCMD exited with code 0. {(_isTestLoginContext ? "Login test" : "Upload")} successful!");
				AppendLog(successMsg, isError: false);
			}
			else
			{
				string exitCodeDescription = SteamCmdProcessHandler.DescribeExitCode(exitCode);
				string failLabel = _isTestLoginContext
					? $"Login test failed (exit code {exitCode})."
					: $"SteamCMD exited with code {exitCode}.";

				SetFailedState(failLabel);
				Debug.LogError($"[SteamDeployer] SteamCMD exited with code {exitCode}. {exitCodeDescription}");
				AppendLog($"=== {(_isTestLoginContext ? "LOGIN TEST" : "UPLOAD")} FAILED (exit code {exitCode}) ===", isError: true);
				AppendLog($"Exit code {exitCode}: {exitCodeDescription}", isError: true);
			}

			Repaint();
		}

		/// <summary>
		/// Called when SteamCMD detects that a Steam Guard code is required.
		/// Kills the current process and enters WaitingForSteamGuard to prompt the user.
		/// </summary>
		private void HandleSteamGuardRequired(string message)
		{
			_processHandler?.Kill();
			_processHandler?.Dispose();
			_processHandler   = null;
			_isProcessRunning = false;

			AppendLog($"Steam Guard code required — waiting for user input.", isError: false);

			_state                     = DeployState.WaitingForSteamGuard;
			_steamGuardCodeInput       = "";
			GUIUtility.keyboardControl = 0;  // Clear any active focus so the log TextArea doesn't block input.
			Repaint();
		}

		private void HandleAuthFailure(string message)
		{
			_processHandler?.Kill();
			_processHandler?.Dispose();
			_processHandler   = null;
			_isProcessRunning = false;

			AppendLog($"AUTH FAILURE: {message}", isError: true);
			SetFailedState("Authentication failed.");
			Repaint();

			EditorApplication.delayCall += () =>
			{
				EditorUtility.DisplayDialog(
					"Steam Authentication Failed",
					$"SteamCMD reported an authentication failure:\n\n{message}\n\n" +
					"Please verify:\n" +
					"• Username and password are correct\n" +
					"• If a Steam Guard code was entered, ensure it was typed correctly\n" +
					"• Your account has publishing rights for AppID " + _config?.AppID,
					"OK");
			};
		}

		private void CancelOperation()
		{
			_processHandler?.Kill();
			_processHandler?.Dispose();
			_processHandler   = null;
			_isProcessRunning = false;

			_state     = DeployState.Setup;
			_taskLabel = "";
			Debug.LogWarning("[SteamDeployer] Operation was manually cancelled.");
			Repaint();
		}

		// ─── Process handler factory ──────────────────────────────────────────────

		/// <summary>
		/// Creates a new SteamCmdProcessHandler and wires up all event callbacks.
		/// All events fire on the Unity main thread via PumpMainThread().
		/// </summary>
		private SteamCmdProcessHandler CreateAndWireProcessHandler()
		{
			var handler = new SteamCmdProcessHandler();
			handler.OnLogLine               += line => AppendLog(line, isError: false);
			handler.OnErrorLine             += line => AppendLog(line, isError: true);
			handler.OnSteamGuardRequired    += msg  => HandleSteamGuardRequired(msg);
			handler.OnAuthenticationFailure += msg  => HandleAuthFailure(msg);
			handler.OnProcessExited         += code => HandleProcessExited(code);
			return handler;
		}

		// ─── Pre-flight validation ────────────────────────────────────────────────

		/// <summary>
		/// Validates all required fields before a full deployment begins.
		/// Critically checks for non-ASCII characters, which cause steamcmd.exe to fail
		/// with cryptic errors due to its C++ ANSI core.
		/// </summary>
		private bool ValidatePreFlight()
		{
			if (_config == null)
			{
				EditorUtility.DisplayDialog("Error", "No Deploy Config asset is assigned.", "OK");
				return false;
			}

			if (string.IsNullOrWhiteSpace(_config.AppID) || string.IsNullOrWhiteSpace(_config.DepotID))
			{
				EditorUtility.DisplayDialog("Error", "App ID and Depot ID are required.", "OK");
				return false;
			}

			if (string.IsNullOrWhiteSpace(_config.SteamCmdPath) || !File.Exists(ResolveSteamCmdPath()))
			{
				EditorUtility.DisplayDialog("SteamCMD Not Found",
					$"steamcmd.exe not found at:\n{_config.SteamCmdPath}\n\n" +
					"Use the Browse button to locate it.", "OK");
				return false;
			}

			if (string.IsNullOrWhiteSpace(_config.SteamUsername) || string.IsNullOrWhiteSpace(_password))
			{
				EditorUtility.DisplayDialog("Credentials Missing",
					"Please enter your Steam username and password.", "OK");
				return false;
			}

			// ── ASCII path check ─────────────────────────────────────────────────
			string steamCmdDir   = Path.GetDirectoryName(ResolveSteamCmdPath());
			string buildTempPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

			if (HasNonAscii(steamCmdDir))
			{
				EditorUtility.DisplayDialog(
					"Non-ASCII Path Detected",
					$"The SteamCMD directory contains non-ASCII characters:\n\n{steamCmdDir}\n\n" +
					"SteamCMD cannot handle non-ASCII paths. " +
					"Please move steamcmd.exe to a path using only English letters, numbers, " +
					"underscores, and hyphens (e.g., C:\\steamcmd\\).",
					"OK");
				return false;
			}

			if (HasNonAscii(buildTempPath))
			{
				EditorUtility.DisplayDialog(
					"Non-ASCII Project Path Detected",
					$"The Unity project path contains non-ASCII characters:\n\n{buildTempPath}\n\n" +
					"The build output will be placed in a subdirectory of this path, which " +
					"SteamCMD cannot handle. Please move the Unity project to an ASCII-only path.",
					"OK");
				return false;
			}

			return true;
		}

		private bool ValidateTestLogin()
		{
			if (_config == null)
			{
				EditorUtility.DisplayDialog("Error", "No Deploy Config asset is assigned.", "OK");
				return false;
			}

			if (string.IsNullOrWhiteSpace(_config.SteamCmdPath) || !File.Exists(ResolveSteamCmdPath()))
			{
				EditorUtility.DisplayDialog("SteamCMD Not Found",
					$"steamcmd.exe not found at:\n{_config.SteamCmdPath}\n\n" +
					"Use the Browse button to locate it.", "OK");
				return false;
			}

			if (string.IsNullOrWhiteSpace(_config.SteamUsername) || string.IsNullOrWhiteSpace(_password))
			{
				EditorUtility.DisplayDialog("Credentials Missing",
					"Please enter your Steam username and password.", "OK");
				return false;
			}

			return true;
		}

		private static bool HasNonAscii(string path)
		{
			if (string.IsNullOrEmpty(path)) return false;
			foreach (char c in path)
				if (c > 127) return true;
			return false;
		}

		// ─── Path helpers ─────────────────────────────────────────────────────────

		/// <summary>
		/// If the path selected via Browse lies inside the Unity project, stores it as a
		/// relative path from the project root (e.g., "Packages/steamcmd/steamcmd.exe").
		/// Paths outside the project are kept absolute.
		/// </summary>
		private static string NormalizeSteamCmdPath(string absolutePath)
		{
			string projectRoot = Path.GetFullPath(
				Path.Combine(Application.dataPath, "..")).Replace('\\', '/');
			string normalized = absolutePath.Replace('\\', '/');

			if (normalized.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
			{
				string relative = normalized.Substring(projectRoot.Length).TrimStart('/');
				return relative;
			}

			return absolutePath;
		}

		/// <summary>
		/// Resolves the SteamCMD path to an absolute path for filesystem operations.
		/// Relative paths are resolved from the Unity project root.
		/// </summary>
		private string ResolveSteamCmdPath()
		{
			if (string.IsNullOrEmpty(_config?.SteamCmdPath)) return "";

			string path = _config.SteamCmdPath;
			if (Path.IsPathRooted(path)) return path;

			string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
			return Path.GetFullPath(Path.Combine(projectRoot, path));
		}

		/// <summary>Returns the effective password, preferring the stored encrypted version if available.</summary>
		private string GetEffectivePassword()
		{
			return _saveCredentials
				? (CryptographyHelper.LoadDecryptedPassword() ?? _password)
				: _password;
		}

		// ─── Utilities ────────────────────────────────────────────────────────────

		private static string ResolveMacros(string template)
		{
			if (string.IsNullOrEmpty(template)) return "Unity Build";
			return template
				.Replace("{Version}", Application.version)
				.Replace("{Date}",    DateTime.Now.ToString("yyyy-MM-dd"))
				.Replace("{DateTime}",    DateTime.Now.ToString("yyyy-MM-dd-hh-mm-ss"));
		}

		private static string GetExeExtension(BuildTarget target)
		{
			switch (target)
			{
				case BuildTarget.StandaloneWindows:
				case BuildTarget.StandaloneWindows64: return ".exe";
				case BuildTarget.StandaloneOSX:       return ".app";
				default:                              return "";
			}
		}

		private static string GetEditorLogPath()
		{
#if UNITY_EDITOR_WIN
			return Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Unity", "Editor", "Editor.log");
#elif UNITY_EDITOR_OSX
			return Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.Personal),
				"Library", "Logs", "Unity", "Editor.log");
#else
			return Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				".config", "unity3d", "Editor.log");
#endif
		}

		private void RevealEditorLog()
		{
			string path = GetEditorLogPath();
			if (File.Exists(path))
				EditorUtility.RevealInFinder(path);
			else
				EditorUtility.DisplayDialog("Not Found", $"Editor log not found at:\n{path}", "OK");
		}

		private void AppendLog(string line, bool isError)
		{
			string ts    = DateTime.Now.ToString("HH:mm:ss");
			string tag   = isError ? "ERR" : "LOG";
			string entry = $"[{ts}][{tag}] {line}\n";

			_logBuffer += entry;

			if (_logBuffer.Length > MAX_LOG_BUFFER_CHARS)
			{
				int cutAt = _logBuffer.Length - MAX_LOG_BUFFER_CHARS;
				int nl    = _logBuffer.IndexOf('\n', cutAt);
				_logBuffer = nl >= 0 ? _logBuffer.Substring(nl + 1) : _logBuffer.Substring(cutAt);
			}

			_logScroll = new Vector2(0, float.MaxValue);
		}

		private void SetFailedState(string reason)
		{
			_state     = DeployState.Failed;
			_taskLabel = reason;
		}

		// ─── Config asset helpers ─────────────────────────────────────────────────

		private void TryLoadConfig()
		{
			string[] guids = AssetDatabase.FindAssets("t:SteamDeployConfig");
			if (guids.Length > 0)
			{
				_config = AssetDatabase.LoadAssetAtPath<SteamDeployConfig>(
					AssetDatabase.GUIDToAssetPath(guids[0]));
			}
		}

		private void CreateConfigAsset()
		{
			const string parentFolder = "Assets/Editor";
			const string subFolder    = "Assets/Editor/SteamDeployer";

			if (!AssetDatabase.IsValidFolder(parentFolder))
				AssetDatabase.CreateFolder("Assets", "Editor");

			if (!AssetDatabase.IsValidFolder(subFolder))
				AssetDatabase.CreateFolder(parentFolder, "SteamDeployer");

			string path = $"{subFolder}/SteamDeployConfig.asset";
			_config = CreateInstance<SteamDeployConfig>();
			AssetDatabase.CreateAsset(_config, path);
			AssetDatabase.SaveAssets();
			EditorGUIUtility.PingObject(_config);
			Debug.Log($"[SteamDeployer] Created config asset at: {path}");
		}

		// ─── Style initialization (lazy, inside OnGUI) ────────────────────────────

		private void EnsureStyles()
		{
			if (_stylesReady) return;
			_stylesReady = true;

			_boxStyle = new GUIStyle(GUI.skin.box)
			{
				padding = new RectOffset(10, 10, 8, 8),
				margin  = new RectOffset(4, 4, 2, 2),
			};

			_bigButtonStyle = new GUIStyle(GUI.skin.button)
			{
				fontSize  = 15,
				fontStyle = FontStyle.Bold,
			};

			_logStyle = new GUIStyle(EditorStyles.textArea)
			{
				wordWrap = false,
				richText = false,
				fontSize = 10,
				font     = EditorStyles.miniLabel.font,
			};

			if (EditorGUIUtility.isProSkin)
				_logStyle.normal.textColor = new Color(0.75f, 1f, 0.75f);

			_successBoxStyle = new GUIStyle(GUI.skin.box)
			{
				padding = new RectOffset(10, 10, 6, 6),
				normal  = { background = MakeColorTex(new Color(0.1f, 0.55f, 0.1f, 0.6f)) },
			};

			_failureBoxStyle = new GUIStyle(GUI.skin.box)
			{
				padding = new RectOffset(10, 10, 6, 6),
				normal  = { background = MakeColorTex(new Color(0.65f, 0.1f, 0.1f, 0.6f)) },
			};

			_warningBoxStyle = new GUIStyle(GUI.skin.box)
			{
				padding = new RectOffset(10, 10, 8, 8),
				margin  = new RectOffset(4, 4, 2, 2),
				normal  = { background = MakeColorTex(new Color(0.6f, 0.45f, 0.0f, 0.35f)) },
			};
		}

		private static Texture2D MakeColorTex(Color color)
		{
			var tex = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false);
			tex.SetPixel(0, 0, color);
			tex.Apply();
			return tex;
		}
	}
}
