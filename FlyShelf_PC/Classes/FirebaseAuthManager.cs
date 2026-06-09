using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Manages Firebase Anonymous Authentication for the PC client.
    /// Obtains and caches an ID token that must be appended to all Firebase REST API calls.
    /// Token is auto-refreshed before expiry (tokens last 1 hour).
    /// 
    /// IMPORTANT: The refresh token and UID are persisted to disk (DPAPI-encrypted)
    /// so the same anonymous identity is reused across app restarts. This prevents
    /// creating a new Firebase Auth user on every launch, which would:
    /// - Hit Firebase's 100-accounts-per-IP-per-hour rate limit
    /// - Balloon Firebase Auth user count with orphaned accounts
    /// - Incur Firebase charges past the free tier
    /// </summary>
    public static class FirebaseAuthManager
    {
        private static readonly HttpClient _authClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(15) };
        
        // Firebase Web API Key (obfuscated — see FirebaseSecrets.cs)
        private static string FIREBASE_API_KEY => FirebaseSecrets.ApiKey;
        
        public static string FirebaseDatabaseUrl => FirebaseSecrets.DatabaseUrl;
        
        // Cached token state
        private static string _idToken = "";
        private static string _refreshToken = "";
        private static string _uid = "";
        private static DateTime _tokenExpiry = DateTime.MinValue;
        private static readonly SemaphoreSlim _tokenLock = new(1, 1);
        private static bool _diskLoaded = false;
        
        // Pre-refresh 5 minutes before expiry to avoid 401s
        private const int TOKEN_REFRESH_BUFFER_MINUTES = 5;

        // Persistence path for refresh token (DPAPI-encrypted)
        private static readonly string _tokenPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlyShelf", "firebase_auth.dat");

        /// <summary>
        /// Returns a valid Firebase ID token. Signs in anonymously if needed,
        /// refreshes if expired. Thread-safe via SemaphoreSlim.
        /// 
        /// Flow: Load persisted token → Refresh → Sign up (last resort)
        /// </summary>
        public static async Task<string> GetIdTokenAsync()
        {
            // Fast path: token is still valid
            if (!string.IsNullOrEmpty(_idToken) && DateTime.UtcNow < _tokenExpiry.AddMinutes(-TOKEN_REFRESH_BUFFER_MINUTES))
            {
                return _idToken;
            }

            await _tokenLock.WaitAsync();
            try
            {
                // Double-check after acquiring lock
                if (!string.IsNullOrEmpty(_idToken) && DateTime.UtcNow < _tokenExpiry.AddMinutes(-TOKEN_REFRESH_BUFFER_MINUTES))
                {
                    return _idToken;
                }

                // Step 1: Load persisted refresh token from disk (first call only)
                if (!_diskLoaded)
                {
                    LoadPersistedToken();
                    _diskLoaded = true;
                }

                // Step 2: Try refresh if we have a refresh token (either from memory or disk)
                if (!string.IsNullOrEmpty(_refreshToken))
                {
                    try
                    {
                        await RefreshTokenAsync();
                        PersistToken(); // Save updated tokens to disk
                        return _idToken;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("FIREBASE AUTH", $"Token refresh failed: {ex.Message}");
                        // If refresh fails with 400 (invalid grant), the refresh token is dead.
                        // Clear it so we don't keep retrying a dead token.
                        if (ex.Message.Contains("400"))
                        {
                            Logger.LogAction("FIREBASE AUTH", "Refresh token expired/revoked — will create new anonymous identity");
                            _refreshToken = "";
                            DeletePersistedToken();
                        }
                    }
                }

                // Step 3: Last resort — create a new anonymous identity
                // This only happens on truly fresh installs or when the refresh token is revoked
                await SignInAnonymouslyAsync();
                PersistToken(); // Save the new identity to disk for future sessions
                return _idToken;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        /// <summary>
        /// Gets the cached anonymous user ID (UID), authenticating if necessary.
        /// </summary>
        public static async Task<string> GetUidAsync()
        {
            if (string.IsNullOrEmpty(_uid))
            {
                await GetIdTokenAsync();
            }
            return _uid;
        }

        /// <summary>
        /// Appends the auth token as a query parameter to a Firebase REST URL.
        /// Usage: string secureUrl = await FirebaseAuthManager.AuthenticateUrl(baseUrl);
        /// </summary>
        public static async Task<string> AuthenticateUrl(string firebaseUrl)
        {
            string token = await GetIdTokenAsync();
            if (string.IsNullOrEmpty(token)) return firebaseUrl;
            
            // Append auth parameter
            char separator = firebaseUrl.Contains("?") ? '&' : '?';
            return $"{firebaseUrl}{separator}auth={token}";
        }

        /// <summary>
        /// Signs in anonymously via Firebase REST API.
        /// Retries up to 3 times with exponential backoff (2s, 4s, 8s).
        /// POST https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={API_KEY}
        /// </summary>
        private static async Task SignInAnonymouslyAsync()
        {
            const int MAX_ATTEMPTS = 3;
            Exception lastException = null;

            for (int attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)
            {
                try
                {
                    string url = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={FIREBASE_API_KEY}";
                    var payload = new { returnSecureToken = true };
                    string json = JsonSerializer.Serialize(payload);
                    var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                    var response = await _authClient.PostAsync(url, content);
                    string body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        Logger.LogAction("FIREBASE AUTH", $"Anonymous sign-in attempt {attempt}/{MAX_ATTEMPTS} failed: HTTP {(int)response.StatusCode} — {body}");
                        lastException = new Exception($"HTTP {(int)response.StatusCode}: {body}");
                        if (attempt < MAX_ATTEMPTS)
                        {
                            int delayMs = (int)Math.Pow(2, attempt) * 1000; // 2s, 4s, 8s
                            await Task.Delay(delayMs);
                        }
                        continue;
                    }

                    using var doc = JsonDocument.Parse(body);
                    _idToken = doc.RootElement.GetProperty("idToken").GetString() ?? "";
                    _refreshToken = doc.RootElement.GetProperty("refreshToken").GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("localId", out var uidProp))
                    {
                        _uid = uidProp.GetString() ?? "";
                    }

                    // expiresIn is in seconds (usually 3600 = 1 hour)
                    string expiresIn = doc.RootElement.GetProperty("expiresIn").GetString() ?? "3600";
                    int seconds = int.TryParse(expiresIn, out var s) ? s : 3600;
                    _tokenExpiry = DateTime.UtcNow.AddSeconds(seconds);

                    Logger.LogAction("FIREBASE AUTH", $"Anonymous sign-in successful (new identity) — UID: {_uid.Substring(0, Math.Min(8, _uid.Length))}... token valid for {seconds}s");
                    return; // Success — exit retry loop
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    Logger.LogAction("FIREBASE AUTH", $"Anonymous sign-in attempt {attempt}/{MAX_ATTEMPTS} error: {ex.Message}");
                    if (attempt < MAX_ATTEMPTS)
                    {
                        int delayMs = (int)Math.Pow(2, attempt) * 1000;
                        await Task.Delay(delayMs);
                    }
                }
            }

            // All attempts exhausted — notify user
            Logger.LogAction("FIREBASE AUTH", $"⚠️ Anonymous sign-in failed after {MAX_ATTEMPTS} attempts — cloud sync unavailable");
            try
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    Windows.ToastWindow.ShowToast("☁️ Cloud sync unavailable — check your internet connection"));
            }
            catch { }
        }

        /// <summary>
        /// Refreshes an expired token using the refresh token.
        /// POST https://securetoken.googleapis.com/v1/token?key={API_KEY}
        /// </summary>
        private static async Task RefreshTokenAsync()
        {
            string url = $"https://securetoken.googleapis.com/v1/token?key={FIREBASE_API_KEY}";
            var formContent = new FormUrlEncodedContent(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("grant_type", "refresh_token"),
                new System.Collections.Generic.KeyValuePair<string, string>("refresh_token", _refreshToken)
            });

            var response = await _authClient.PostAsync(url, formContent);
            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"HTTP {(int)response.StatusCode}: {body}");
            }

            using var doc = JsonDocument.Parse(body);
            _idToken = doc.RootElement.GetProperty("id_token").GetString() ?? "";
            _refreshToken = doc.RootElement.GetProperty("refresh_token").GetString() ?? "";
            if (doc.RootElement.TryGetProperty("user_id", out var uidProp))
            {
                _uid = uidProp.GetString() ?? "";
            }
            
            string expiresIn = doc.RootElement.GetProperty("expires_in").GetString() ?? "3600";
            int seconds = int.TryParse(expiresIn, out var s) ? s : 3600;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(seconds);

            Logger.LogAction("FIREBASE AUTH", $"Token refreshed (existing identity) — UID: {_uid.Substring(0, Math.Min(8, _uid.Length))}... valid for {seconds}s");
        }

        // ═══════════════════════════════════════════════════════════════
        // Token Persistence — DPAPI encrypted to %AppData%\FlyShelf\
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Persists the refresh token and UID to disk using DPAPI encryption.
        /// Called after successful sign-in or token refresh.
        /// </summary>
        private static void PersistToken()
        {
            try
            {
                if (string.IsNullOrEmpty(_refreshToken)) return;

                var data = new { refreshToken = _refreshToken, uid = _uid };
                string json = JsonSerializer.Serialize(data);
                string encrypted = SecureStorage.Encrypt(json);

                Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);
                string tempPath = _tokenPath + ".tmp";
                File.WriteAllText(tempPath, encrypted);
                File.Move(tempPath, _tokenPath, true);

                Logger.LogAction("FIREBASE AUTH", $"Persisted auth identity to disk (UID: {_uid.Substring(0, Math.Min(8, _uid.Length))}...)");
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE AUTH", $"Failed to persist token: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads persisted refresh token and UID from disk.
        /// Called once on first GetIdTokenAsync() invocation.
        /// </summary>
        private static void LoadPersistedToken()
        {
            try
            {
                if (!File.Exists(_tokenPath)) return;

                string encrypted = File.ReadAllText(_tokenPath);
                string json = SecureStorage.Decrypt(encrypted);

                using var doc = JsonDocument.Parse(json);
                string refreshToken = doc.RootElement.TryGetProperty("refreshToken", out var rt) ? rt.GetString() ?? "" : "";
                string uid = doc.RootElement.TryGetProperty("uid", out var u) ? u.GetString() ?? "" : "";

                if (!string.IsNullOrEmpty(refreshToken))
                {
                    _refreshToken = refreshToken;
                    _uid = uid;
                    Logger.LogAction("FIREBASE AUTH", $"Loaded persisted identity from disk (UID: {_uid.Substring(0, Math.Min(8, _uid.Length))}...)");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE AUTH", $"Failed to load persisted token (will create new identity): {ex.Message}");
                // Corrupted file — delete it so next launch creates a fresh identity
                DeletePersistedToken();
            }
        }

        /// <summary>
        /// Deletes the persisted token file (used when refresh token is revoked).
        /// </summary>
        private static void DeletePersistedToken()
        {
            try
            {
                if (File.Exists(_tokenPath)) File.Delete(_tokenPath);
            }
            catch { }
        }

        /// <summary>
        /// Forces a fresh sign-in on next call (useful after auth errors).
        /// </summary>
        public static void InvalidateToken()
        {
            _idToken = "";
            _tokenExpiry = DateTime.MinValue;
            Logger.LogAction("FIREBASE AUTH", "Token invalidated — will re-authenticate on next call");
        }

        /// <summary>
        /// Completely resets the Firebase identity — deletes the persisted token
        /// and forces a new anonymous sign-up on next call. Use when the user
        /// wants to fully reset their sync identity.
        /// </summary>
        public static void ResetIdentity()
        {
            _idToken = "";
            _refreshToken = "";
            _uid = "";
            _tokenExpiry = DateTime.MinValue;
            _diskLoaded = false;
            DeletePersistedToken();
            Logger.LogAction("FIREBASE AUTH", "Identity fully reset — will create new anonymous user on next call");
        }
    }
}
