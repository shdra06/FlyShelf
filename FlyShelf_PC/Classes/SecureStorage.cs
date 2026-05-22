using System;
using System.Security.Cryptography;
using System.Text;

namespace FlyShelf.Classes
{
    public static class SecureStorage
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FlyShelfDataProtectionEntropy");

        public static string Encrypt(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return plaintext;
            try
            {
                byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
                byte[] ciphertextBytes = ProtectedData.Protect(plaintextBytes, Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(ciphertextBytes);
            }
            catch (Exception ex)
            {
                Logger.LogAction("SECURE_STORAGE", $"Encryption failed: {ex.Message}");
                return plaintext; // Fallback to plaintext if DPAPI is unavailable
            }
        }

        public static string Decrypt(string ciphertext)
        {
            if (string.IsNullOrEmpty(ciphertext)) return ciphertext;
            
            // Check if it's plaintext JSON first (begins with '{' or '[') to avoid unnecessary DPAPI exceptions
            string trimmed = ciphertext.Trim();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                return ciphertext;
            }

            try
            {
                byte[] ciphertextBytes = Convert.FromBase64String(ciphertext);
                byte[] plaintextBytes = ProtectedData.Unprotect(ciphertextBytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plaintextBytes);
            }
            catch (Exception ex)
            {
                Logger.LogAction("SECURE_STORAGE", $"Decryption failed or data is plaintext: {ex.Message}");
                // If it wasn't DPAPI encrypted (or is corrupt), return the original text so the JSON parser can try it (fallback to plaintext)
                return ciphertext;
            }
        }
    }
}
