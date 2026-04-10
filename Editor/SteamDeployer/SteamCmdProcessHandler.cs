using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SteamDeployer
{
	/// <summary>
	/// Encapsulates System.Diagnostics.Process for launching and monitoring steamcmd.exe.
	///
	/// THREAD SAFETY MODEL (CRITICAL):
	///   SteamCMD's stdout/stderr are read by the OS on background ThreadPool threads via
	///   DataReceivedEventHandler. Unity APIs (Debug.Log, EditorWindow.Repaint, etc.) are
	///   NOT thread-safe and MUST only be called from the Unity main thread.
	///
	///   The bridge between these two worlds is a ConcurrentQueue{LogEntry}:
	///     1. Background I/O threads push LogEntry structs into the queue (lock-free).
	///     2. The Unity main thread (via EditorApplication.update → PumpMainThread) drains
	///        the queue and calls Unity APIs safely.
	///
	///   DO NOT use Process.WaitForExit() — it blocks the calling thread, which in the Unity
	///   Editor is the main thread, causing a complete UI freeze until steamcmd.exe finishes.
	///   Instead, subscribe to the Process.Exited event and check HasExited from the pump.
	/// </summary>
	public sealed class SteamCmdProcessHandler : IDisposable
	{
		// ─── Internal log entry type ──────────────────────────────────────────────

		private readonly struct LogEntry
		{
			public readonly string   Message;
			public readonly LogLevel Level;

			public LogEntry(string message, LogLevel level)
			{
				Message = message;
				Level   = level;
			}
		}

		private enum LogLevel { Info, Error, SteamGuardRequired, AuthFailure }

		// ─── Thread-safe state ────────────────────────────────────────────────────

		/// <summary>
		/// All log lines from stdout/stderr are pushed here by background I/O threads
		/// and drained by the Unity main thread inside PumpMainThread().
		/// </summary>
		private readonly ConcurrentQueue<LogEntry> _logQueue = new ConcurrentQueue<LogEntry>();

		/// <summary>Set to true by the Process.Exited event (background thread). Read by PumpMainThread (main thread).</summary>
		private volatile bool _hasExited;

		/// <summary>Exit code captured in Process.Exited callback. Volatile int is not guaranteed atomic on all CPUs,
		/// but the _hasExited flag read before this ensures visibility via the memory model.</summary>
		private int _exitCode = -1;

		// ─── Process state ────────────────────────────────────────────────────────

		private Process _process;
		private bool    _disposed;

		// ─── Events (fired from PumpMainThread — always on the Unity main thread) ─

		/// <summary>Fired when a normal log line is received. Subscribe to forward to Debug.Log.</summary>
		public event Action<string> OnLogLine;

		/// <summary>Fired when an error-level log line is received. Subscribe to forward to Debug.LogError.</summary>
		public event Action<string> OnErrorLine;

		/// <summary>
		/// Fired when SteamCMD outputs a message indicating a Steam Guard code is required.
		/// This is NOT an authentication failure — it means steamcmd needs the code to proceed.
		/// The handler should kill the process, prompt the user for the code, and re-launch with it.
		/// </summary>
		public event Action<string> OnSteamGuardRequired;

		/// <summary>
		/// Fired when SteamCMD outputs an authentication failure message (wrong password, wrong Steam Guard code, etc.).
		/// The handler MUST call Kill() to terminate the process, as SteamCMD will hang waiting for input.
		/// </summary>
		public event Action<string> OnAuthenticationFailure;

		/// <summary>
		/// Fired when the process has fully exited and all queued log lines have been drained.
		/// exitCode == 0 means success; any non-zero value indicates failure.
		/// </summary>
		public event Action<int> OnProcessExited;

		// ─── Regex patterns for log parsing ──────────────────────────────────────

		/// <summary>
		/// Matches lines where SteamCMD signals that a Steam Guard code is required to proceed.
		/// This is distinct from an auth failure — the session can be retried once the user supplies the code.
		///
		/// Observed steamcmd output patterns (Windows, v1773426366):
		///   "This computer has not been authenticated for your account using Steam Guard."
		///   "Steam Guard code:" (the interactive prompt line, appears before ERROR on the same line)
		/// Also covers mobile authenticator (RequireTwoFactorCode) and legacy API result codes.
		/// </summary>
		private static readonly Regex _steamGuardRequiredPattern = new Regex(
			@"(not been authenticated for your account using Steam Guard|" +
			@"Steam Guard code:|" +
			@"Steam Guard code required|" +
			@"FAILED login with result code RequireTwoFactorCode|" +
			@"FAILED login with result code RequirePasswordEntry|" +
			@"Enter the current code from your Steam Guard)",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		/// <summary>
		/// Matches actual authentication failures where the credentials are wrong.
		/// These cannot be resolved by supplying a Steam Guard code.
		/// </summary>
		private static readonly Regex _authFailurePattern = new Regex(
			@"(Invalid Password|Two-factor code mismatch|" +
			@"Login Failure|Logging in user.*Failed|FAILED login with result code InvalidPassword)",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		/// <summary>Matches generic error keywords for coloring log output red in the Unity console.</summary>
		private static readonly Regex _errorKeywordPattern = new Regex(
			@"(ERROR!|error:|FAILED|Build Failed|Upload Failed|rate limit exceeded)",
			RegexOptions.Compiled);

		// ─── Public API ───────────────────────────────────────────────────────────

		/// <summary>
		/// Returns a human-readable description for a known steamcmd exit code.
		/// Exit codes are not officially documented by Valve; these descriptions are derived
		/// from community reports and observed behaviour across steamcmd versions.
		/// </summary>
		/// <param name="exitCode">The exit code returned by steamcmd.exe.</param>
		/// <returns>A short explanation string, or a generic fallback for unknown codes.</returns>
		public static string DescribeExitCode(int exitCode)
		{
			switch (exitCode)
			{
				case 0:  return "Success.";
				case 1:  return "Unknown / general error.";
				case 2:  return "Steam session error — already logged in elsewhere, or generic login failure.";
				case 3:  return "No connection to the Steam network. Check your internet connection.";
				case 4:  return "Connection timeout or invalid command-line argument.";
				case 5:  return "Steam API / SDK initialisation failed.";
				case 6:  return "Build commit failed. Content was uploaded but could not be finalised. " +
				                "Common causes: (1) the SetLive branch requires Valve review before going live " +
				                "(new apps must pass the Steam review queue before 'default' can be set); " +
				                "(2) the branch name does not exist or you lack permission to set it live; " +
				                "(3) a transient Valve server error — retry in a few minutes.";
				case 7:  return "Too many failed login attempts. Wait before retrying.";
				case 8:  return "Rate limit exceeded — too many steamcmd operations in a short period. Wait and retry.";
				case 42: return "Rate limit exceeded (Valve-side throttle). Wait several minutes before retrying.";
				default: return $"Undocumented exit code {exitCode}. Check the steamcmd log in the logs/ folder for details.";
			}
		}

		/// <summary>
		/// Constructs the argument string for a full build-and-upload steamcmd.exe run.
		/// If a Steam Guard code is provided it is injected via +set_steam_guard_code before +login.
		/// </summary>
		/// <param name="username">Steam account username.</param>
		/// <param name="password">Decrypted plaintext password (never logged).</param>
		/// <param name="steamGuardCode">Steam Guard / 2FA code obtained at runtime. Pass null or empty to omit.</param>
		/// <param name="appVdfPath">Absolute path to the app_build VDF file.</param>
		public static string BuildArguments(
			string username,
			string password,
			string steamGuardCode,
			string appVdfPath)
		{
			string quotedVdf = $"\"{appVdfPath}\"";

			if (!string.IsNullOrWhiteSpace(steamGuardCode))
			{
				// +set_steam_guard_code must appear BEFORE +login per Valve's documentation.
				return $"+set_steam_guard_code {steamGuardCode.Trim()} " +
				       $"+login {username} {password} " +
				       $"+run_app_build {quotedVdf} " +
				       $"+quit";
			}

			return $"+login {username} {password} " +
			       $"+run_app_build {quotedVdf} " +
			       $"+quit";
		}

		/// <summary>
		/// Constructs the argument string for a test-login-only steamcmd.exe run.
		/// Does not upload anything — used to verify credentials before a full deployment.
		/// </summary>
		/// <param name="username">Steam account username.</param>
		/// <param name="password">Decrypted plaintext password (never logged).</param>
		/// <param name="steamGuardCode">Steam Guard / 2FA code obtained at runtime. Pass null or empty to omit.</param>
		public static string BuildTestLoginArguments(string username, string password, string steamGuardCode = "")
		{
			if (!string.IsNullOrWhiteSpace(steamGuardCode))
			{
				return $"+set_steam_guard_code {steamGuardCode.Trim()} " +
				       $"+login {username} {password} " +
				       $"+quit";
			}

			return $"+login {username} {password} +quit";
		}

		/// <summary>
		/// Starts steamcmd.exe with the given arguments using fully redirected, asynchronous I/O.
		/// All output is captured without blocking; see PumpMainThread() for consumption.
		/// </summary>
		/// <returns>True if the process started successfully; false otherwise.</returns>
		public bool Start(string steamCmdPath, string arguments)
		{
			var psi = new ProcessStartInfo
			{
				FileName               = steamCmdPath,
				Arguments              = arguments,

				// UseShellExecute = false is REQUIRED for I/O redirection.
				UseShellExecute        = false,

				// Prevents a new console window from appearing, keeping the Unity Editor clean.
				CreateNoWindow         = true,

				// Redirect both streams so we can intercept all SteamCMD output.
				RedirectStandardOutput = true,
				RedirectStandardError  = true,

				// SteamCMD uses ANSI/ASCII output. UTF-8 is a safe superset.
				StandardOutputEncoding = System.Text.Encoding.UTF8,
				StandardErrorEncoding  = System.Text.Encoding.UTF8,
			};

			_process = new Process
			{
				StartInfo           = psi,
				EnableRaisingEvents = true,
			};

			// Subscribe to async I/O callbacks. These fire on background ThreadPool threads —
			// the ONLY safe operation here is pushing to _logQueue (ConcurrentQueue is lock-free).
			_process.OutputDataReceived += HandleOutputData;
			_process.ErrorDataReceived  += HandleErrorData;
			_process.Exited             += HandleProcessExited;

			try
			{
				bool started = _process.Start();
				if (!started)
				{
					Debug.LogError("[SteamDeployer] Process.Start() returned false for steamcmd.exe.");
					return false;
				}

				// CRITICAL: BeginOutputReadLine() / BeginErrorReadLine() must be called AFTER Start().
				_process.BeginOutputReadLine();
				_process.BeginErrorReadLine();

				return true;
			}
			catch (Exception ex)
			{
				Debug.LogError($"[SteamDeployer] Failed to start steamcmd.exe: {ex.Message}");
				return false;
			}
		}

		/// <summary>
		/// Must be called from the Unity main thread every editor frame (e.g., via EditorApplication.update).
		/// Drains all pending log entries from the ConcurrentQueue and fires the appropriate events.
		/// This is the ONLY correct place to translate background-thread I/O data into Unity API calls.
		/// </summary>
		/// <returns>True if the process has exited and the queue is fully drained; false if still running.</returns>
		public bool PumpMainThread()
		{
			while (_logQueue.TryDequeue(out LogEntry entry))
			{
				switch (entry.Level)
				{
					case LogLevel.SteamGuardRequired:
						// Steam Guard is required — surface this to the window so it can prompt the user.
						// Drain remaining log lines and signal done; the handler will kill the process.
						OnSteamGuardRequired?.Invoke(entry.Message);
						DrainRemainingQueue();
						return true;

					case LogLevel.AuthFailure:
						// Actual authentication failure — credentials are wrong.
						OnAuthenticationFailure?.Invoke(entry.Message);
						DrainRemainingQueue();
						return true;

					case LogLevel.Error:
						OnErrorLine?.Invoke(entry.Message);
						break;

					default:
						OnLogLine?.Invoke(entry.Message);
						break;
				}
			}

			// If _hasExited was set by the background Exited event AND the queue is now empty,
			// fire OnProcessExited with the captured exit code.
			if (_hasExited)
			{
				OnProcessExited?.Invoke(_exitCode);
				return true;
			}

			return false;
		}

		/// <summary>
		/// Forcefully terminates the steamcmd.exe process.
		/// Safe to call at any time, including after the process has already exited.
		/// </summary>
		public void Kill()
		{
			try
			{
				if (_process != null && !_process.HasExited)
				{
					_process.Kill();
					Debug.LogWarning("[SteamDeployer] steamcmd.exe was forcefully terminated.");
				}
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[SteamDeployer] Exception during process kill: {ex.Message}");
			}
		}

		// ─── Background I/O Thread Callbacks ─────────────────────────────────────

		/// <summary>
		/// Called by the OS on a background thread when a stdout line is available.
		/// MUST only push to the ConcurrentQueue — no Unity APIs allowed here.
		/// </summary>
		private void HandleOutputData(object sender, DataReceivedEventArgs e)
		{
			if (e.Data == null) return;

			LogLevel level = ClassifyLogLine(e.Data);
			_logQueue.Enqueue(new LogEntry(e.Data, level));
		}

		/// <summary>
		/// Called by the OS on a background thread when a stderr line is available.
		/// All stderr output is treated as error-level unless it matches a known pattern.
		/// </summary>
		private void HandleErrorData(object sender, DataReceivedEventArgs e)
		{
			if (e.Data == null) return;

			LogLevel level;
			if (_steamGuardRequiredPattern.IsMatch(e.Data))
				level = LogLevel.SteamGuardRequired;
			else if (_authFailurePattern.IsMatch(e.Data))
				level = LogLevel.AuthFailure;
			else
				level = LogLevel.Error;

			_logQueue.Enqueue(new LogEntry(e.Data, level));
		}

		/// <summary>
		/// Called by the OS on a background thread when the process exits.
		/// Captures the exit code and sets the volatile flag for main-thread detection.
		/// DO NOT call Unity APIs here.
		/// </summary>
		private void HandleProcessExited(object sender, EventArgs e)
		{
			_exitCode  = _process?.ExitCode ?? -1;
			_hasExited = true;
		}

		// ─── Helpers ──────────────────────────────────────────────────────────────

		/// <summary>
		/// Classifies a log line into the appropriate LogLevel based on content.
		/// SteamGuardRequired is checked before AuthFailure because some steamcmd versions
		/// output patterns that could match both; "needs code" takes priority over "failed".
		/// </summary>
		private static LogLevel ClassifyLogLine(string line)
		{
			if (_steamGuardRequiredPattern.IsMatch(line))
				return LogLevel.SteamGuardRequired;

			if (_authFailurePattern.IsMatch(line))
				return LogLevel.AuthFailure;

			if (_errorKeywordPattern.IsMatch(line))
				return LogLevel.Error;

			return LogLevel.Info;
		}

		/// <summary>
		/// Empties the log queue without firing events. Called after a terminal event
		/// (Steam Guard required or auth failure) to discard stale lines from a dead session.
		/// </summary>
		private void DrainRemainingQueue()
		{
			while (_logQueue.TryDequeue(out _)) { }
		}

		// ─── IDisposable ──────────────────────────────────────────────────────────

		public void Dispose()
		{
			if (_disposed) return;
			_disposed = true;

			if (_process != null)
			{
				_process.OutputDataReceived -= HandleOutputData;
				_process.ErrorDataReceived  -= HandleErrorData;
				_process.Exited             -= HandleProcessExited;
				_process.Dispose();
				_process = null;
			}
		}
	}
}
