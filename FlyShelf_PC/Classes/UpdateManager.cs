using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    public class UpdateManager
    {
        // ═══════════════════════════════════════════════════════════════
        // Uses GitHub Releases API (works for both public and private repos).
        // Falls back to version.json for backwards compatibility.
        // ═══════════════════════════════════════════════════════════════
        private const string RELEASES_API = "https://api.github.com/repos/shdra06/FlyShelf/releases/latest";
        private const string VERSION_URL = "https://raw.githubusercontent.com/shdra06/FlyShelf/main/version.json";

        private static readonly HttpClient _client = new HttpClient() { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly HttpClient _downloadClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        }) { Timeout = TimeSpan.FromMinutes(10) };

        public static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

        public string LatestVersion { get; private set; } = "";
        public string Changelog { get; private set; } = "";
        public string DownloadUrl { get; private set; } = "";
        public string ExpectedHash { get; private set; } = ""; // SHA-256 hash for integrity verification
        public bool IsUpdateAvailable { get; private set; }

        // Events for UI binding
        public event Action<int> DownloadProgressChanged; // 0-100
        public event Action<string> StatusChanged;
        public event Action<bool> UpdateCheckCompleted; // true = update available

        /// <summary>
        /// Checks GitHub for a newer version. Returns true if update is available.
        /// Tries the Releases API first (works for private repos), then falls back to version.json.
        /// </summary>
        public async Task<bool> CheckForUpdateAsync()
        {
#if MSIX_STORE
            StatusChanged?.Invoke("Updates are managed by the Microsoft Store.");
            UpdateCheckCompleted?.Invoke(false);
            return false;
#else
            try
            {
                StatusChanged?.Invoke("Checking for updates...");
                Logger.LogAction("UPDATE", $"Current version: {CurrentVersion}");

                // ── Strategy 1: GitHub Releases API (private-repo compatible) ──
                bool foundViaApi = false;
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, RELEASES_API);
                    request.Headers.Add("User-Agent", "FlyShelf-AutoUpdater");
                    request.Headers.Add("Accept", "application/vnd.github+json");

                    var response = await _client.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        // tag_name is "v2.9.0" — strip the 'v' prefix
                        string tagName = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "";
                        LatestVersion = tagName.TrimStart('v', 'V');

                        // Changelog from release body
                        Changelog = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";

                        // Find FlyShelf.exe in the release assets
                        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var asset in assets.EnumerateArray())
                            {
                                string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                                if (name.Equals("FlyShelf.exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    DownloadUrl = asset.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() ?? "" : "";
                                    break;
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(LatestVersion) && !string.IsNullOrEmpty(DownloadUrl))
                        {
                            foundViaApi = true;
                            Logger.LogAction("UPDATE", $"Found via Releases API: v{LatestVersion}");
                        }
                    }
                }
                catch (Exception apiEx)
                {
                    Logger.LogAction("UPDATE", $"Releases API failed: {apiEx.Message} — trying version.json fallback");
                }

                // ── Strategy 2: version.json fallback (public repo / raw content) ──
                if (!foundViaApi)
                {
                    try
                    {
                        string url = $"{VERSION_URL}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                        string json = await _client.GetStringAsync(url);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        LatestVersion = root.TryGetProperty("pc_version", out var v) ? v.GetString() ?? "" : "";
                        Changelog = root.TryGetProperty("pc_changelog", out var c) ? c.GetString() ?? "" :
                                   (root.TryGetProperty("changelog", out var c2) ? c2.GetString() ?? "" : "");
                        DownloadUrl = root.TryGetProperty("pc_download", out var d) ? d.GetString() ?? "" : "";
                        ExpectedHash = root.TryGetProperty("pc_sha256", out var h) ? h.GetString()?.ToLowerInvariant() ?? "" : "";
                    }
                    catch (Exception jsonEx)
                    {
                        Logger.LogAction("UPDATE", $"version.json also failed: {jsonEx.Message}");
                    }
                }

                if (string.IsNullOrEmpty(LatestVersion))
                {
                    StatusChanged?.Invoke("Could not read version info.");
                    UpdateCheckCompleted?.Invoke(false);
                    return false;
                }

                // ── Load ExpectedHash from version.json to guarantee security if it is not loaded yet ──
                if (string.IsNullOrEmpty(ExpectedHash))
                {
                    try
                    {
                        string url = $"{VERSION_URL}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                        string json = await _client.GetStringAsync(url);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        ExpectedHash = root.TryGetProperty("pc_sha256", out var h) ? h.GetString()?.ToLowerInvariant() ?? "" : "";
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("UPDATE", $"Could not load verification hash from version.json: {ex.Message}");
                    }
                }

                // Compare versions (semver)
                var current = new Version(CurrentVersion);
                var latest = new Version(LatestVersion);

                IsUpdateAvailable = latest > current;

                if (IsUpdateAvailable)
                {
                    StatusChanged?.Invoke($"Update v{LatestVersion} available!");
                    Logger.LogAction("UPDATE", $"New version available: {LatestVersion} (current: {CurrentVersion})");
                }
                else
                {
                    StatusChanged?.Invoke($"You're on the latest version (v{CurrentVersion}).");
                    Logger.LogAction("UPDATE", $"Already up to date: {CurrentVersion}");
                }

                UpdateCheckCompleted?.Invoke(IsUpdateAvailable);
                return IsUpdateAvailable;
            }
            catch (Exception ex)
            {
                Logger.LogAction("UPDATE_ERROR", ex.Message);
                StatusChanged?.Invoke("Update check failed — no internet?");
                UpdateCheckCompleted?.Invoke(false);
                return false;
            }
#endif
        }

        /// <summary>
        /// Downloads the new EXE and self-replaces via a helper batch script.
        /// </summary>
        public async Task<bool> DownloadAndApplyUpdateAsync()
        {
#if MSIX_STORE
            StatusChanged?.Invoke("Updates are managed by the Microsoft Store.");
            return false;
#else
            if (string.IsNullOrEmpty(DownloadUrl))
            {
                StatusChanged?.Invoke("No download URL available.");
                return false;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "FlyShelf_Update");
            Directory.CreateDirectory(tempDir);
            string tempExePath = Path.Combine(tempDir, "FlyShelf_new.exe");

            try
            {
                // Clean up any leftover file from a previous failed attempt
                try { if (File.Exists(tempExePath)) File.Delete(tempExePath); } catch { }

                StatusChanged?.Invoke("Downloading update...");
                Logger.LogAction("UPDATE", $"Downloading from {DownloadUrl}");

                // Pre-flight check — verify the download URL exists before streaming
                try
                {
                    var headRequest = new HttpRequestMessage(HttpMethod.Head, DownloadUrl);
                    var headResponse = await _downloadClient.SendAsync(headRequest);
                    if (headResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        StatusChanged?.Invoke("Release not published yet — check back soon.");
                        Logger.LogAction("UPDATE", $"Download URL returned 404: {DownloadUrl}");
                        return false;
                    }
                }
                catch { /* HEAD not supported — proceed with GET */ }

                var response = await _downloadClient.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1;
                
                // Download to file — explicit using blocks so streams are CLOSED before hash check
                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(tempExePath, FileMode.Create, FileAccess.Write, FileShare.None, 1048576))
                {
                    byte[] buffer = new byte[1048576]; // 1MB buffer
                    long totalRead = 0;
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        totalRead += bytesRead;

                        if (totalBytes > 0)
                        {
                            int pct = (int)(totalRead * 100 / totalBytes);
                            DownloadProgressChanged?.Invoke(pct);
                            
                            string sizeMB = $"{totalRead / 1048576.0:F1}/{totalBytes / 1048576.0:F1} MB";
                            StatusChanged?.Invoke($"Downloading... {pct}% ({sizeMB})");
                        }
                    }

                    await fileStream.FlushAsync();
                } // ← fileStream is now CLOSED here

                DownloadProgressChanged?.Invoke(100);
                Logger.LogAction("UPDATE", $"Download complete: {tempExePath}");

                // SAFETY: Reject suspiciously small files (< 50MB = not a real self-contained build)
                var downloadedFile = new FileInfo(tempExePath);
                long minSizeBytes = 50 * 1024 * 1024; // 50 MB
                if (downloadedFile.Length < minSizeBytes)
                {
                    Logger.LogAction("UPDATE", $"❌ REJECTED: Downloaded file is only {downloadedFile.Length / 1048576.0:F1} MB — expected ≥50 MB. This is not a valid self-contained build.");
                    StatusChanged?.Invoke($"❌ Update rejected — file too small ({downloadedFile.Length / 1048576.0:F1} MB). Must be ≥50 MB.");
                    try { File.Delete(tempExePath); } catch { }
                    return false;
                }
                Logger.LogAction("UPDATE", $"✅ Size check passed: {downloadedFile.Length / 1048576.0:F1} MB");

                // SECURITY: SHA-256 hash verification (file is now fully closed and unlocked)
                if (!string.IsNullOrEmpty(ExpectedHash))
                {
                    StatusChanged?.Invoke("Verifying integrity...");
                    string actualHash;
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    using (var verifyStream = File.OpenRead(tempExePath))
                    {
                        actualHash = BitConverter.ToString(sha.ComputeHash(verifyStream)).Replace("-", "").ToLowerInvariant();
                    }

                    if (actualHash != ExpectedHash)
                    {
                        Logger.LogAction("UPDATE", $"\u274c HASH MISMATCH! Expected: {ExpectedHash}, Got: {actualHash}");
                        StatusChanged?.Invoke("\u274c Download corrupted \u2014 hash mismatch. Please retry.");
                        try { File.Delete(tempExePath); } catch { }
                        return false;
                    }
                    Logger.LogAction("UPDATE", $"\u2705 Hash verified: {actualHash}");
                }
                else
                {
                    Logger.LogAction("UPDATE", "❌ REJECTED: No SHA-256 hash provided in version.json. Refusing to install unverified update.");
                    StatusChanged?.Invoke("❌ Update rejected — missing security signature.");
                    try { File.Delete(tempExePath); } catch { }
                    return false;
                }

                StatusChanged?.Invoke("Download complete! Verified and ready to install.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogAction("UPDATE_ERROR", $"Download failed: {ex.Message}");
                StatusChanged?.Invoke($"Download failed: {ex.Message}");
                return false;
            }
#endif
        }

        /// <summary>
        /// Finds the download URL for the currently running version (or latest release)
        /// from GitHub, downloads it, and prepares it for apply+restart.
        /// Used for repair/reinstall when files may be corrupted.
        /// </summary>
        public async Task<bool> RedownloadCurrentVersionAsync()
        {
            try
            {
                StatusChanged?.Invoke($"Finding v{CurrentVersion} on GitHub...");
                Logger.LogAction("REDOWNLOAD", $"Looking for current version {CurrentVersion} release asset");

                string foundUrl = "";

                // Try to find the specific release by tag (v5.0.0, etc.)
                try
                {
                    string tagUrl = $"https://api.github.com/repos/shdra06/FlyShelf/releases/tags/v{CurrentVersion}";
                    var request = new HttpRequestMessage(HttpMethod.Get, tagUrl);
                    request.Headers.Add("User-Agent", "FlyShelf-Redownloader");
                    request.Headers.Add("Accept", "application/vnd.github+json");

                    var response = await _client.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var asset in assets.EnumerateArray())
                            {
                                string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                                if (name.Equals("FlyShelf.exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    foundUrl = asset.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() ?? "" : "";
                                    break;
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(foundUrl))
                            Logger.LogAction("REDOWNLOAD", $"Found exact tag release: v{CurrentVersion}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("REDOWNLOAD", $"Tag lookup failed: {ex.Message}");
                }

                // Fallback: use latest release if exact tag not found
                if (string.IsNullOrEmpty(foundUrl))
                {
                    try
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, RELEASES_API);
                        request.Headers.Add("User-Agent", "FlyShelf-Redownloader");
                        request.Headers.Add("Accept", "application/vnd.github+json");

                        var response = await _client.SendAsync(request);
                        if (response.IsSuccessStatusCode)
                        {
                            string json = await response.Content.ReadAsStringAsync();
                            using var doc = JsonDocument.Parse(json);
                            var root = doc.RootElement;

                            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var asset in assets.EnumerateArray())
                                {
                                    string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                                    if (name.Equals("FlyShelf.exe", StringComparison.OrdinalIgnoreCase))
                                    {
                                        foundUrl = asset.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() ?? "" : "";
                                        break;
                                    }
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(foundUrl))
                            Logger.LogAction("REDOWNLOAD", $"Using latest release as fallback");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("REDOWNLOAD", $"Latest release lookup also failed: {ex.Message}");
                    }
                }

                if (string.IsNullOrEmpty(foundUrl))
                {
                    StatusChanged?.Invoke("Could not find download URL on GitHub.");
                    return false;
                }

                // Set the URL and download using the existing download logic
                DownloadUrl = foundUrl;
                LatestVersion = CurrentVersion; // Same version — repair
                return await DownloadAndApplyUpdateAsync();
            }
            catch (Exception ex)
            {
                Logger.LogAction("REDOWNLOAD_ERROR", $"Redownload failed: {ex.Message}");
                StatusChanged?.Invoke($"Redownload failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Replaces the running EXE with the downloaded update and restarts.
        /// 
        /// STRATEGY: Instead of a batch script (which triggers every antivirus),
        /// we launch the DOWNLOADED EXE itself with --apply-update flags.
        /// The new EXE waits for the old process to die, copies itself to the
        /// target path, then launches the target. This is the standard approach
        /// used by modern self-updating desktop apps (Squirrel, Velopack, etc.)
        /// </summary>
        public void ApplyUpdateAndRestart()
        {
#if MSIX_STORE
            StatusChanged?.Invoke("Updates are managed by the Microsoft Store.");
            return;
#else
            string tempDir = Path.Combine(Path.GetTempPath(), "FlyShelf_Update");
            string tempExePath = Path.Combine(tempDir, "FlyShelf_new.exe");

            if (!File.Exists(tempExePath))
            {
                StatusChanged?.Invoke("Update file not found. Please re-download.");
                return;
            }

            // Determine current EXE path (multiple fallback strategies for single-file deployment)
            string currentExePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(currentExePath) || !File.Exists(currentExePath))
            {
                currentExePath = Path.Combine(AppContext.BaseDirectory, "FlyShelf.exe");
            }
            if (string.IsNullOrEmpty(currentExePath) || !File.Exists(currentExePath))
            {
                currentExePath = Environment.ProcessPath ?? "";
            }
            if (string.IsNullOrEmpty(currentExePath))
            {
                StatusChanged?.Invoke("Cannot determine current EXE path.");
                Logger.LogAction("UPDATE", "FATAL: Could not find current EXE path via any method.");
                return;
            }

            int pid = Process.GetCurrentProcess().Id;

            Logger.LogAction("UPDATE", $"Current EXE: {currentExePath}");
            Logger.LogAction("UPDATE", $"New EXE: {tempExePath}");
            Logger.LogAction("UPDATE", $"Launching new EXE as self-updater (PID to wait for: {pid})");
            StatusChanged?.Invoke("Restarting with update...");

            // Launch the NEW EXE with self-update arguments.
            // The new EXE will: wait for us to die → copy itself to our path → launch from that path.
            // This is a .NET EXE, not a batch script — AV doesn't flag .NET apps as droppers.
            var psi = new ProcessStartInfo
            {
                FileName = tempExePath,
                Arguments = $"--apply-update --target \"{currentExePath}\" --pid {pid}",
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = true // Required so it survives our process exit
            };
            Process.Start(psi);

            // Exit current app — the new EXE is watching our PID
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                System.Windows.Application.Current.Shutdown();
            });
#endif
        }

        /// <summary>
        /// Handles the --apply-update startup path. Called from App.OnStartup
        /// when the EXE was launched as a self-updater by a previous version.
        /// 
        /// Flow:
        /// 1. Wait for the old process (--pid) to die
        /// 2. Copy ourselves to the target path (--target)
        /// 3. Launch the target (which is now the new version)
        /// 4. Clean up temp files
        /// 5. Exit without showing UI
        /// 
        /// Returns true if this was an --apply-update invocation (caller should exit).
        /// Returns false if this is a normal app launch.
        /// </summary>
        public static bool HandleUpdateIfRequested(string[] args)
        {
            // Check if --apply-update flag is present
            bool isUpdate = false;
            string targetPath = "";
            int waitPid = -1;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--apply-update", StringComparison.OrdinalIgnoreCase))
                    isUpdate = true;
                else if (args[i].Equals("--target", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    targetPath = args[++i].Trim('"');
                else if (args[i].Equals("--pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    int.TryParse(args[++i], out waitPid);
            }

            if (!isUpdate) return false;

            // We ARE the updater — run the update logic and exit
            try
            {
                string logPath = Path.Combine(Path.GetTempPath(), "FlyShelf_Update", "update_log.txt");
                void Log(string msg)
                {
                    try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
                }

                Log($"Self-updater started. Target: {targetPath}, WaitPID: {waitPid}");

                // Step 1: Wait for old process to exit (up to 30 seconds)
                if (waitPid > 0)
                {
                    Log($"Waiting for PID {waitPid} to exit...");
                    try
                    {
                        var oldProcess = Process.GetProcessById(waitPid);
                        if (!oldProcess.WaitForExit(30_000))
                        {
                            Log("Old process didn't exit in 30s — attempting kill...");
                            try { oldProcess.Kill(); } catch { }
                            oldProcess.WaitForExit(5_000);
                        }
                        Log("Old process exited.");
                    }
                    catch (ArgumentException)
                    {
                        Log("Old process already exited.");
                    }
                    catch (Exception ex)
                    {
                        Log($"Wait error (non-fatal): {ex.Message}");
                    }
                }

                // Step 2: Small delay for file handles to release
                System.Threading.Thread.Sleep(1000);

                // Step 3: Copy ourselves to the target path (retry up to 10 times)
                string selfPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(selfPath))
                {
                    Log("FATAL: Cannot determine own EXE path.");
                    return true;
                }

                Log($"Copying {selfPath} → {targetPath}");
                bool copied = false;
                for (int attempt = 1; attempt <= 10; attempt++)
                {
                    try
                    {
                        File.Copy(selfPath, targetPath, overwrite: true);
                        copied = true;
                        Log($"Copy succeeded on attempt {attempt}.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"Copy attempt {attempt}/10 failed: {ex.Message}");
                        System.Threading.Thread.Sleep(1000);
                    }
                }

                if (!copied)
                {
                    Log("FATAL: Could not copy new EXE after 10 attempts.");
                    return true;
                }

                // Step 4: Launch the updated EXE from the target path
                Log($"Launching updated app: {targetPath}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetPath,
                    UseShellExecute = true
                });

                // Step 5: Clean up temp files (best-effort)
                System.Threading.Thread.Sleep(2000);
                try { File.Delete(selfPath); } catch { }
                Log("Update complete. Self-updater exiting.");
            }
            catch (Exception ex)
            {
                try
                {
                    string logPath = Path.Combine(Path.GetTempPath(), "FlyShelf_Update", "update_error.txt");
                    File.WriteAllText(logPath, $"[{DateTime.Now}] Update failed:\n{ex}");
                }
                catch { }
            }

            return true; // Signal caller to exit without UI
        }
    }
}
