using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace FlyShelf.Classes
{
    public static class StartupHelper
    {
        private const string StartupTaskId = "FlyShelfStartupTask"; // Matches the MSIX manifest ID
        private const string RegistryValueName = "FlyShelf";

        /// <summary>
        /// Robust check for MSIX package identity.
        /// </summary>
        public static bool IsPackaged()
        {
            try
            {
                // Accessing Package.Current will throw an InvalidOperationException if the app runs unpackaged
                return global::Windows.ApplicationModel.Package.Current != null;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Checks whether the EXE (unpackaged) edition has a Run key in the registry.
        /// Used by the MSIX build to detect a conflicting auto-start registration.
        /// </summary>
        public static bool IsExeStartupRegistered()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    return key?.GetValue(RegistryValueName) != null;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Checks whether the MSIX (Store) edition's StartupTask is enabled.
        /// Used by the EXE build to detect a conflicting auto-start registration.
        /// </summary>
        public static async Task<bool> IsMsixStartupRegisteredAsync()
        {
            try
            {
                var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
                return task.State == global::Windows.ApplicationModel.StartupTaskState.Enabled ||
                       task.State == global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
            }
            catch
            {
                // StartupTask API not available (unpackaged runtime) — no MSIX conflict
                return false;
            }
        }

        /// <summary>
        /// Registers or unregisters FlyShelf for auto-start.
        /// Uses Windows StartupTask API for MSIX, and Registry Run key for unpackaged.
        /// Prevents both editions from being registered simultaneously:
        ///   - MSIX skips enable if the EXE registry key exists.
        ///   - EXE skips enable if the MSIX StartupTask is already enabled.
        /// </summary>
        public static async Task<bool> SetRunAtStartupAsync(bool enable)
        {
            if (IsPackaged())
            {
                try
                {
                    // ── Conflict guard: If the standalone EXE is already registered
                    // in the registry Run key, don't enable the MSIX startup task.
                    // This prevents two FlyShelf instances from launching at boot.
                    if (enable && IsExeStartupRegistered())
                    {
                        Logger.LogAction("STARTUP", "Skipped MSIX startup — standalone EXE is already registered in registry Run key.");
                        return false;
                    }

                    var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
                    if (enable)
                    {
                        if (task.State == global::Windows.ApplicationModel.StartupTaskState.Disabled)
                        {
                            var state = await task.RequestEnableAsync();
                            Logger.LogAction("STARTUP", $"Startup task enable requested. Result: {state}");
                            return state == global::Windows.ApplicationModel.StartupTaskState.Enabled;
                        }
                        return task.State == global::Windows.ApplicationModel.StartupTaskState.Enabled;
                    }
                    else
                    {
                        if (task.State == global::Windows.ApplicationModel.StartupTaskState.Enabled || 
                            task.State == global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy)
                        {
                            task.Disable();
                            Logger.LogAction("STARTUP", "Startup task disabled.");
                        }
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("STARTUP_ERROR", $"Failed to set packaged startup: {ex.Message}");
                    return false;
                }
            }
            else
            {
                try
                {
                    // ── Conflict guard: If the MSIX edition's StartupTask is already
                    // enabled, don't add a registry Run key for the standalone EXE.
                    if (enable && await IsMsixStartupRegisteredAsync())
                    {
                        Logger.LogAction("STARTUP", "Skipped EXE startup — MSIX StartupTask is already enabled.");
                        return false;
                    }

                    using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        if (key != null)
                        {
                            if (enable)
                            {
                                string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FlyShelf.exe");
                                key.SetValue(RegistryValueName, exePath);
                                Logger.LogAction("STARTUP", $"Registry run value set: {exePath}");
                            }
                            else
                            {
                                key.DeleteValue(RegistryValueName, false);
                                Logger.LogAction("STARTUP", "Registry run value deleted.");
                            }
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("STARTUP_ERROR", $"Failed to set registry startup: {ex.Message}");
                }
                return false;
            }
        }

        /// <summary>
        /// Checks if FlyShelf is currently configured to run at startup.
        /// </summary>
        public static async Task<bool> IsRunAtStartupEnabledAsync()
        {
            if (IsPackaged())
            {
                try
                {
                    var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
                    return task.State == global::Windows.ApplicationModel.StartupTaskState.Enabled || 
                           task.State == global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
                }
                catch
                {
                    return false;
                }
            }
            else
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                    {
                        if (key != null)
                        {
                            return key.GetValue(RegistryValueName) != null;
                        }
                    }
                }
                catch { } // Best-effort: failure is acceptable
                return false;
            }
        }
    }
}

