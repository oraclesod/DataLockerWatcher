using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace DataLockerWatcherInstall
{
    internal sealed class Installer
    {
        private const string InstallEventSource = "DataLockerWatcher-Install";
        private const string AgentEventSource = "DataLockerWatcher-Agent";
        private const string SyncEventSource = "DataLockerWatcher-Sync";

        // HKLM Run value name
        private const string HklmRunValueName = "DataLockerWatcher-Agent-Init";
        // HKCU Run value name (set by Agent --init)
        private const string HkcuRunValueName = "DataLockerWatcher-Agent";

        // Start Menu shortcut + AUMID
        private const string StartMenuFolderName = "DataLocker Watcher";
        private const string AgentShortcutName = "DataLocker Watcher - Agent";
        private const string AgentAumid = "DataLockerWatcher.Agent";

        private static readonly string InstallDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DataLockerWatcher");

        private static readonly string InstallLogPath = Path.Combine(InstallDir, "Install.log");

        // Directory where this installer was launched from (typically the publish/output folder)
        private string SourceDir => AppContext.BaseDirectory;

        private const string AgentExeName = "DataLockerWatcher-Agent.exe";
        private const string SyncExeName = "DataLockerWatcher-Sync.exe";
        private const string InstallExeName = "Install.exe";
        private const string ConfigFileName = "config.json";
        private const string IconIcoFileName = "DataLocker.ico";
        private const string IconPngFileName = "DataLocker.png";

        private static readonly TimeSpan UpgradeSyncWaitTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan UpgradeSyncPollInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan AgentStopWaitTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan AgentStopPollInterval = TimeSpan.FromSeconds(1);

        public int InstallOrRepair(bool repair)
        {
            Directory.CreateDirectory(InstallDir);
            AppendInstallLog($"{(repair ? "Repair" : "Install")} started.");

            EnsureEventLogSources();

            bool existingInstallDetected =
                File.Exists(Path.Combine(InstallDir, AgentExeName)) ||
                File.Exists(Path.Combine(InstallDir, SyncExeName));

            if (existingInstallDetected)
            {
                LogInstallEvent("Existing installation detected. Preparing upgrade flow.");

                if (!WaitForSyncToExit(UpgradeSyncWaitTimeout))
                {
                    LogInstallEvent(
                        $"Upgrade aborted because Sync did not exit within {UpgradeSyncWaitTimeout.TotalMinutes:0} minutes.",
                        EventLogEntryType.Error);

                    AppendInstallLog("Upgrade aborted: Sync still running after timeout.");
                    return 1;
                }

                StopAgentProcesses();
            }

            // Copy payloads
            CopyPayload(InstallExeName, overwrite: true); // special: copies the currently running installer
            CopyPayload(AgentExeName, overwrite: true);
            CopyPayload(SyncExeName, overwrite: true);
            CopyPayload(ConfigFileName, overwrite: true);
            CopyPayloadIfExists(IconIcoFileName, overwrite: true);
            CopyPayloadIfExists(IconPngFileName, overwrite: true);

            var agentPath = Path.Combine(InstallDir, AgentExeName);
            var iconPath = Path.Combine(InstallDir, IconIcoFileName);

            // Create Start Menu shortcut (all users) + AUMID for toast activation
            try
            {
                ShortcutHelper.CreateStartMenuShortcutWithAumid(
                    startMenuFolderName: StartMenuFolderName,
                    shortcutName: AgentShortcutName,
                    targetPath: agentPath,
                    arguments: "",
                    workingDirectory: InstallDir,
                    iconPath: File.Exists(iconPath) ? iconPath : "",
                    appUserModelId: AgentAumid
                );

                LogInstallEvent("Created Start Menu shortcut with AUMID for Agent.");
            }
            catch (Exception ex)
            {
                LogInstallEvent($"Failed creating Start Menu shortcut/AUMID: {ex}", EventLogEntryType.Warning);
            }

            // HKLM Run (not RunOnce): runs Agent --init at every user logon
            SetHklmRunInit(agentPath);

            LogInstallEvent($"{(repair ? "Repair" : "Install")} copied payloads and set HKLM Run init.");

            // If a user is already logged in, run init immediately in that session (best-effort)
            var session = SessionHelper.TryGetActiveSession();
            if (session != null)
            {
                LogInstallEvent($"Attempting to start Agent init in active session {session.SessionId}...");

                bool ok = SessionHelper.TryRunAsActiveUser(agentPath, "--init", hidden: true, out var err);
                if (ok)
                {
                    LogInstallEvent($"Started Agent init in active session {session.SessionId}.", EventLogEntryType.Information);
                }
                else
                {
                    LogInstallEvent(
                        $"Failed to start Agent init in active session {session.SessionId}. {err}",
                        EventLogEntryType.Warning);

                    // Fallback: if installer is being run interactively by an admin in the same session,
                    // a normal Process.Start may work even when session token APIs fail.
                    if (TryStartAgentFallback(agentPath, out var fallbackErr))
                    {
                        LogInstallEvent("Fallback start of Agent succeeded (Process.Start).", EventLogEntryType.Information);
                    }
                    else
                    {
                        LogInstallEvent($"Fallback start of Agent failed: {fallbackErr}", EventLogEntryType.Warning);
                        LogInstallEvent("Agent will start at next user logon via HKLM Run.", EventLogEntryType.Information);
                    }
                }
            }
            else
            {
                LogInstallEvent("No active session detected; init will run at next user logon.", EventLogEntryType.Warning);
            }

            AppendInstallLog($"{(repair ? "Repair" : "Install")} complete.");
            return 0;
        }

        public int Uninstall()
        {
            Directory.CreateDirectory(InstallDir);
            AppendInstallLog("Uninstall started.");

            EnsureEventLogSources();

            // Remove HKLM Run init
            RemoveHklmRunInit();

            // Remove Start Menu folder (all users)
            try
            {
                ShortcutHelper.RemoveStartMenuFolder(StartMenuFolderName);
                LogInstallEvent("Removed Start Menu folder: DataLocker Watcher");
            }
            catch (Exception ex)
            {
                LogInstallEvent($"Failed removing Start Menu folder: {ex}", EventLogEntryType.Warning);
            }

            // Stop agent (best effort)
            TryRunSystem("taskkill.exe", $"/IM \"{AgentExeName}\" /F");

            // Remove HKCU Run for active user (best effort)
            var session = SessionHelper.TryGetActiveSession();
            if (session != null)
            {
                SessionHelper.TryRunAsActiveUser(
                    Path.Combine(Environment.SystemDirectory, "reg.exe"),
                    $@"DELETE ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v ""{HkcuRunValueName}"" /f",
                    hidden: true,
                    out _);
            }

            // Delete files
            SafeDelete(Path.Combine(InstallDir, InstallExeName));
            SafeDelete(Path.Combine(InstallDir, AgentExeName));
            SafeDelete(Path.Combine(InstallDir, SyncExeName));
            SafeDelete(Path.Combine(InstallDir, ConfigFileName));
            SafeDelete(Path.Combine(InstallDir, IconIcoFileName));
            SafeDelete(Path.Combine(InstallDir, IconPngFileName));

            // Keep Install.log for forensics; remove if desired:
            // SafeDelete(InstallLogPath);

            TryDeleteDirIfEmpty(InstallDir);

            LogInstallEvent("Uninstall complete.");
            AppendInstallLog("Uninstall complete.");
            return 0;
        }

        private bool WaitForSyncToExit(TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            bool firstLog = true;

            while (sw.Elapsed < timeout)
            {
                Process[] syncProcesses = Array.Empty<Process>();

                try
                {
                    syncProcesses = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(SyncExeName));
                    if (syncProcesses.Length == 0)
                    {
                        LogInstallEvent("No running Sync process detected.");
                        return true;
                    }

                    if (firstLog)
                    {
                        LogInstallEvent(
                            $"Sync is currently running ({syncProcesses.Length} instance(s)); waiting up to {timeout.TotalMinutes:0} minutes for it to finish.");
                        firstLog = false;
                    }
                    else
                    {
                        LogInstallEvent(
                            $"Sync still running ({syncProcesses.Length} instance(s)); waited {Math.Floor(sw.Elapsed.TotalSeconds):0}s so far...");
                    }
                }
                catch (Exception ex)
                {
                    LogInstallEvent($"Error while checking for Sync processes: {ex}", EventLogEntryType.Warning);
                }
                finally
                {
                    foreach (var p in syncProcesses)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }

                Thread.Sleep(UpgradeSyncPollInterval);
            }

            LogInstallEvent(
                $"Timed out waiting for Sync to finish after {timeout.TotalMinutes:0} minutes.",
                EventLogEntryType.Warning);

            return false;
        }

        private void StopAgentProcesses()
        {
            try
            {
                LogInstallEvent("Stopping Agent process(es) before upgrade...");
                TryRunSystem("taskkill.exe", $"/IM \"{AgentExeName}\" /F");
            }
            catch (Exception ex)
            {
                LogInstallEvent($"taskkill for Agent threw an exception: {ex}", EventLogEntryType.Warning);
            }

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < AgentStopWaitTimeout)
            {
                Process[] agents = Array.Empty<Process>();

                try
                {
                    agents = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AgentExeName));
                    if (agents.Length == 0)
                    {
                        LogInstallEvent("Agent process(es) stopped.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    LogInstallEvent($"Error while checking for Agent processes: {ex}", EventLogEntryType.Warning);
                }
                finally
                {
                    foreach (var p in agents)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }

                Thread.Sleep(AgentStopPollInterval);
            }

            LogInstallEvent(
                $"Agent still appears to be running after waiting {AgentStopWaitTimeout.TotalSeconds:0} seconds.",
                EventLogEntryType.Warning);
        }

        private bool TryStartAgentFallback(string agentPath, out string? error)
        {
            error = null;

            try
            {
                if (!File.Exists(agentPath))
                {
                    error = $"Agent exe not found: {agentPath}";
                    return false;
                }

                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = agentPath,
                    Arguments = "--init",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(agentPath) ?? InstallDir
                });

                if (p == null)
                {
                    error = "Process.Start returned null.";
                    return false;
                }

                // We don't wait; just confirm it didn't immediately throw.
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private void SetHklmRunInit(string agentPath)
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key.SetValue(HklmRunValueName, $"\"{agentPath}\" --init", RegistryValueKind.String);
        }

        private void RemoveHklmRunInit()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                key?.DeleteValue(HklmRunValueName, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                LogInstallEvent($"Failed removing HKLM Run '{HklmRunValueName}': {ex.Message}", EventLogEntryType.Warning);
            }
        }

        private void CopyPayload(string fileName, bool overwrite)
        {
            var src = GetPayloadSourcePath(fileName);
            var dst = Path.Combine(InstallDir, fileName);

            if (!File.Exists(src))
                throw new FileNotFoundException($"Required payload not found: {src}");

            File.Copy(src, dst, overwrite);
        }

        private void CopyPayloadIfExists(string fileName, bool overwrite)
        {
            var src = GetPayloadSourcePath(fileName, allowMissing: true);
            if (!File.Exists(src))
                return;

            var dst = Path.Combine(InstallDir, fileName);
            File.Copy(src, dst, overwrite);
        }

        /// <summary>
        /// Resolve the source file on disk for a payload.
        /// Special-case: Install.exe should be the currently running installer EXE,
        /// even if the built filename is different.
        /// </summary>
        private string GetPayloadSourcePath(string fileName, bool allowMissing = false)
        {
            if (string.Equals(fileName, InstallExeName, StringComparison.OrdinalIgnoreCase))
            {
                var exePath = Environment.ProcessPath;
                exePath ??= Process.GetCurrentProcess().MainModule?.FileName;

                if (string.IsNullOrWhiteSpace(exePath))
                    throw new FileNotFoundException("Unable to resolve running installer executable path.");

                return exePath;
            }

            var candidate = Path.Combine(SourceDir, fileName);

            if (!allowMissing)
                return candidate;

            return candidate;
        }

        private void SafeDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                LogInstallEvent($"Failed to delete {path}: {ex.Message}", EventLogEntryType.Warning);
            }
        }

        private void TryDeleteDirIfEmpty(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    Directory.Delete(dir, recursive: false);
            }
            catch { }
        }

        private void EnsureEventLogSources()
        {
            EnsureEventSource(InstallEventSource);
            EnsureEventSource(AgentEventSource);
            EnsureEventSource(SyncEventSource);
        }

        private void EnsureEventSource(string source)
        {
            try
            {
                if (!EventLog.SourceExists(source))
                    EventLog.CreateEventSource(source, "Application");
            }
            catch
            {
                // ignore; apps will fallback to file logs
            }
        }

        public void LogInstallEvent(string message, EventLogEntryType type = EventLogEntryType.Information)
        {
            try
            {
                EventLog.WriteEntry(InstallEventSource, message, type);
            }
            catch
            {
                AppendInstallLog($"[{type}] {message}");
            }
        }

        private void AppendInstallLog(string message)
        {
            try
            {
                Directory.CreateDirectory(InstallDir);
                File.AppendAllText(InstallLogPath, $"{DateTime.Now:O}: {message}{Environment.NewLine}");
            }
            catch { }
        }

        private void TryRunSystem(string fileName, string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                p?.WaitForExit(10_000);
            }
            catch { }
        }
    }
}