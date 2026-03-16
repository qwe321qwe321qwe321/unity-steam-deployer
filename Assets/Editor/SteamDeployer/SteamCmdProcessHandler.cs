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

        private enum LogLevel { Info, Error, AuthFailure }

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
        /// Fired when SteamCMD outputs an authentication failure message (wrong password, Steam Guard required, etc.).
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
        /// Matches authentication failure messages from SteamCMD's stdout.
        /// These require immediate process termination and user notification.
        /// </summary>
        private static readonly Regex _authFailurePattern = new Regex(
            @"(Invalid Password|Steam Guard code required|Two-factor code mismatch|" +
            @"Login Failure|Logging in user.*Failed|FAILED login with result code InvalidPassword)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Matches generic error keywords for coloring log output red in the Unity console.</summary>
        private static readonly Regex _errorKeywordPattern = new Regex(
            @"(ERROR!|error:|FAILED|Build Failed|Upload Failed|rate limit exceeded)",
            RegexOptions.Compiled);

        // ─── Public API ───────────────────────────────────────────────────────────

        /// <summary>
        /// Constructs the argument string for steamcmd.exe based on whether a Steam Guard code
        /// was provided. The SteamGuard code (if any) must precede the +login command.
        /// </summary>
        /// <param name="username">Steam account username.</param>
        /// <param name="password">Decrypted plaintext password (never logged).</param>
        /// <param name="steamGuardCode">Optional Steam Guard / 2FA code. Pass null or empty to omit.</param>
        /// <param name="appVdfPath">Absolute path to the app_build VDF file.</param>
        public static string BuildArguments(
            string username,
            string password,
            string steamGuardCode,
            string appVdfPath)
        {
            // Quote the VDF path to handle spaces in the filesystem path.
            string quotedVdf = $"\"{appVdfPath}\"";

            if (!string.IsNullOrWhiteSpace(steamGuardCode))
            {
                // Steam Guard / Mobile Authenticator flow:
                // +set_steam_guard_code must appear BEFORE +login per Valve's documentation.
                return $"+set_steam_guard_code {steamGuardCode.Trim()} " +
                       $"+login {username} {password} " +
                       $"+run_app_build {quotedVdf} " +
                       $"+quit";
            }
            else
            {
                // Standard login — relies on cached credentials from a prior interactive login,
                // or the account has no 2FA configured.
                return $"+login {username} {password} " +
                       $"+run_app_build {quotedVdf} " +
                       $"+quit";
            }
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
                // It also prevents UAC prompts and shell path resolution side effects.
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
                StartInfo            = psi,
                // EnableRaisingEvents is required for the Exited event to fire automatically.
                EnableRaisingEvents  = true,
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

                // CRITICAL: BeginOutputReadLine() / BeginErrorReadLine() must be called
                // AFTER Process.Start(). Calling them before Start() throws InvalidOperationException.
                // Omitting them would cause the output buffers to fill and deadlock steamcmd.exe.
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
            // Drain every queued log entry and dispatch via the appropriate event.
            while (_logQueue.TryDequeue(out LogEntry entry))
            {
                switch (entry.Level)
                {
                    case LogLevel.AuthFailure:
                        // Auth failures are dispatched first as they require immediate process kill.
                        OnAuthenticationFailure?.Invoke(entry.Message);
                        // After firing auth failure, flush remaining queue but stop further dispatch.
                        // The process should be killed by the handler.
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
            if (e.Data == null) return;  // null signals end-of-stream; ignore

            LogLevel level = ClassifyLogLine(e.Data);
            _logQueue.Enqueue(new LogEntry(e.Data, level));
        }

        /// <summary>
        /// Called by the OS on a background thread when a stderr line is available.
        /// All stderr output is treated as error-level.
        /// </summary>
        private void HandleErrorData(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;

            // stderr might also contain auth failure messages in some SteamCMD versions.
            LogLevel level = _authFailurePattern.IsMatch(e.Data) ? LogLevel.AuthFailure : LogLevel.Error;
            _logQueue.Enqueue(new LogEntry(e.Data, level));
        }

        /// <summary>
        /// Called by the OS on a background thread when the process exits.
        /// Captures the exit code and sets the volatile flag for main-thread detection.
        /// DO NOT call Unity APIs here.
        /// </summary>
        private void HandleProcessExited(object sender, EventArgs e)
        {
            // Capture exit code before the process handle potentially gets recycled.
            _exitCode  = _process?.ExitCode ?? -1;
            _hasExited = true;
            // Main thread will detect _hasExited on next PumpMainThread() call.
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Classifies a log line string into the appropriate LogLevel based on content.
        /// </summary>
        private static LogLevel ClassifyLogLine(string line)
        {
            if (_authFailurePattern.IsMatch(line))
                return LogLevel.AuthFailure;

            if (_errorKeywordPattern.IsMatch(line))
                return LogLevel.Error;

            return LogLevel.Info;
        }

        /// <summary>
        /// Empties the log queue without firing events. Called after an auth failure
        /// to prevent stale log lines from firing events on a dead session.
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
