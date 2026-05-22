using System;
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
    /// </summary>
    public static class FirebaseAuthManager
    {
        private static readonly HttpClient _authClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(15) };
        
        // Firebase Web API Key (same as in firebaseConfig.js)
        private const string FIREBASE_API_KEY = "AIzaSyA52ZXmxx1auJshsv-uuayQRHD22D7zdwk";
        
        // Cached token state
        private static string _idToken = "";
        private static string _refreshToken = "";
        private static string _uid = "";
        private static DateTime _tokenExpiry = DateTime.MinValue;
        private static readonly SemaphoreSlim _tokenLock = new(1, 1);
        
        // Pre-refresh 5 minutes before expiry to avoid 401s
        private const int TOKEN_REFRESH_BUFFER_MINUTES = 5;
        
        /// <summary>
        /// Returns a valid Firebase ID token. Signs in anonymously if needed,
        /// refreshes if expired. Thread-safe via SemaphoreSlim.
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

                // Try refresh first if we have a refresh token
                if (!string.IsNullOrEmpty(_refreshToken))
                {
                    try
                    {
                        await RefreshTokenAsync();
                        return _idToken;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("FIREBASE AUTH", $"Token refresh failed, signing in fresh: {ex.Message}");
                    }
                }

                // Sign in anonymously
                await SignInAnonymouslyAsync();
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
        /// POST https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={API_KEY}
        /// </summary>
        private static async Task SignInAnonymouslyAsync()
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
                    Logger.LogAction("FIREBASE AUTH", $"Anonymous sign-in failed: HTTP {(int)response.StatusCode} — {body}");
                    return;
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

                Logger.LogAction("FIREBASE AUTH", $"Anonymous sign-in successful — token valid for {seconds}s");
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE AUTH", $"Anonymous sign-in error: {ex.Message}");
            }
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

            Logger.LogAction("FIREBASE AUTH", $"Token refreshed — valid for {seconds}s");
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
    }
}
