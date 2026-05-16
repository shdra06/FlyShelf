using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace AdvanceClip.Classes
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
        }

        /// <summary>
        /// Downloads the new EXE and self-replaces via a helper batch script.
        /// </summary>
        public async Task<bool> DownloadAndApplyUpdateAsync()
        {
            if (string.IsNullOrEmpty(DownloadUrl))
            {
                StatusChanged?.Invoke("No download URL available.");
                return false;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "AdvanceClip_Update");
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
                    Logger.LogAction("UPDATE", "\u26a0\ufe0f No hash in version.json \u2014 skipping integrity check");
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
        /// Uses a batch script to wait for the current process to exit, then swap.
        /// </summary>
        public void ApplyUpdateAndRestart()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "AdvanceClip_Update");
            string tempExePath = Path.Combine(tempDir, "FlyShelf_new.exe");

            if (!File.Exists(tempExePath))
            {
                StatusChanged?.Invoke("Update file not found. Please re-download.");
                return;
            }

            // For single-file published apps, MainModule.FileName can be empty.
            // Use AppContext.BaseDirectory which always works.
            string currentExePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(currentExePath) || !File.Exists(currentExePath))
            {
                currentExePath = Path.Combine(AppContext.BaseDirectory, "FlyShelf.exe");
            }
            if (string.IsNullOrEmpty(currentExePath) || !File.Exists(currentExePath))
            {
                // Last resort: find ourselves by process name
                currentExePath = Environment.ProcessPath ?? "";
            }
            if (string.IsNullOrEmpty(currentExePath))
            {
                StatusChanged?.Invoke("Cannot determine current EXE path.");
                Logger.LogAction("UPDATE", "FATAL: Could not find current EXE path via any method.");
                return;
            }

            Logger.LogAction("UPDATE", $"Current EXE: {currentExePath}");
            Logger.LogAction("UPDATE", $"New EXE: {tempExePath}");

            int pid = Process.GetCurrentProcess().Id;

            // Create a robust batch script with retry logic
            string batchPath = Path.Combine(tempDir, "update.bat");
            string batchContent = $@"@echo off
echo FlyShelf Auto-Updater
echo Waiting for app to close (PID {pid})...
:waitloop
tasklist /fi ""PID eq {pid}"" 2>nul | find ""{pid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak > nul
    goto waitloop
)
echo App closed. Replacing EXE...
set RETRIES=0
:copyloop
copy /Y ""{tempExePath}"" ""{currentExePath}"" >nul 2>&1
if errorlevel 1 (
    set /a RETRIES+=1
    if %RETRIES% GEQ 10 (
        echo ERROR: Could not replace EXE after 10 attempts.
        pause
        exit /b 1
    )
    echo Retry %RETRIES%... file may be locked
    timeout /t 1 /nobreak > nul
    goto copyloop
)
echo Update applied! Starting new version...
start """" ""{currentExePath}""
timeout /t 2 /nobreak > nul
del ""{tempExePath}"" 2>nul
del ""%~f0"" 2>nul
";

            File.WriteAllText(batchPath, batchContent);

            Logger.LogAction("UPDATE", $"Launching updater batch: {batchPath}");
            StatusChanged?.Invoke("Restarting with update...");

            // Launch the batch script — must use UseShellExecute=true so it survives our process exit
            var psi = new ProcessStartInfo
            {
                FileName = batchPath,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = true
            };
            Process.Start(psi);

            // Exit current app
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                System.Windows.Application.Current.Shutdown();
            });
        }
    }
}
