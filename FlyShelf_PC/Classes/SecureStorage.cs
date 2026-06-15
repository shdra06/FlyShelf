using System;
using System.Security.Cryptography;
using System.Text;

namespace FlyShelf.Classes
{
    public static class SecureStorage
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FlyShelfDataProtectionEntropy");

        /// <summary>True if the last Decrypt() call encountered legacy plaintext JSON (pre-v2.1.0).
        /// When set, the caller should re-save the file so Encrypt() re-wraps it with DPAPI.</summary>
        internal static bool LegacyMigrationNeeded = false;

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
            
            // Legacy pre-v2.1.0 migration: plaintext JSON files start with '{' or '['.
            // Accept them for this load so the caller can parse them, but flag for re-encryption.
            string trimmed = ciphertext.Trim();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                Logger.LogAction("SECURE_STORAGE", "Legacy plaintext detected — will be encrypted on next save");
                LegacyMigrationNeeded = true;
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
                // Do NOT return the original ciphertext — an attacker could write crafted content
                // that fails DPAPI and gets passed through as-is, bypassing encryption entirely.
                Logger.LogAction("SECURE_STORAGE", $"Decryption failed — treating as corrupted: {ex.Message}");
                return "";
            }
        }
    }
}
