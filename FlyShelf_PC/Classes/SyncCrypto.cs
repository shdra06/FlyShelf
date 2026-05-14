using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AdvanceClip.Classes
{
    /// <summary>
    /// AES-256-GCM encryption for Firebase sync payloads.
    /// Uses the pairing key as the basis for key derivation via PBKDF2.
    /// All plaintext clipboard content is encrypted before leaving the device.
    /// </summary>
    public static class SyncCrypto
    {
        private const int KEY_SIZE_BYTES = 32;   // AES-256
        private const int NONCE_SIZE = 12;        // GCM standard
        private const int TAG_SIZE = 16;          // GCM authentication tag
        private const int PBKDF2_ITERATIONS = 100_000;
        private static readonly byte[] SALT = Encoding.UTF8.GetBytes("AdvanceClip_v2.6.0_SyncSalt");

        private static byte[]? _cachedKey;
        private static string? _cachedPairingKey;

        /// <summary>
        /// Derives a 256-bit AES key from the pairing key using PBKDF2-SHA256.
        /// Key is cached for the lifetime of the pairing session.
        /// </summary>
        private static byte[] GetKey()
        {
            string pairingKey = DevicePairingManager.EnsurePairingKey();
            if (string.IsNullOrEmpty(pairingKey))
                throw new InvalidOperationException("Cannot encrypt — no pairing key set");

            // Return cached key if pairing key hasn't changed
            if (_cachedKey != null && _cachedPairingKey == pairingKey)
                return _cachedKey;

            _cachedKey = Rfc2898DeriveBytes.Pbkdf2(
                pairingKey,
                SALT,
                PBKDF2_ITERATIONS,
                HashAlgorithmName.SHA256,
                KEY_SIZE_BYTES);
            _cachedPairingKey = pairingKey;
            return _cachedKey;
        }

        /// <summary>
        /// Encrypts plaintext using AES-256-GCM.
        /// Returns Base64 string in format: nonce(12B) + ciphertext + tag(16B)
        /// </summary>
        public static string Encrypt(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext))
                return plaintext;

            var key = GetKey();
            var nonce = new byte[NONCE_SIZE];
            RandomNumberGenerator.Fill(nonce);

            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[TAG_SIZE];

            using var aes = new AesGcm(key, TAG_SIZE);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

            // Pack: nonce + ciphertext + tag
            var result = new byte[NONCE_SIZE + ciphertext.Length + TAG_SIZE];
            Buffer.BlockCopy(nonce, 0, result, 0, NONCE_SIZE);
            Buffer.BlockCopy(ciphertext, 0, result, NONCE_SIZE, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, result, NONCE_SIZE + ciphertext.Length, TAG_SIZE);

            return Convert.ToBase64String(result);
        }

        /// <summary>
        /// Decrypts AES-256-GCM ciphertext (Base64 encoded).
        /// Returns null if decryption fails (wrong key, tampered data, etc.)
        /// </summary>
        public static string? Decrypt(string base64Ciphertext)
        {
            if (string.IsNullOrEmpty(base64Ciphertext))
                return base64Ciphertext;

            try
            {
                var key = GetKey();
                var packed = Convert.FromBase64String(base64Ciphertext);

                if (packed.Length < NONCE_SIZE + TAG_SIZE)
                    return null; // Too short to be valid

                var nonce = new byte[NONCE_SIZE];
                var tag = new byte[TAG_SIZE];
                int ciphertextLen = packed.Length - NONCE_SIZE - TAG_SIZE;
                var ciphertext = new byte[ciphertextLen];

                Buffer.BlockCopy(packed, 0, nonce, 0, NONCE_SIZE);
                Buffer.BlockCopy(packed, NONCE_SIZE, ciphertext, 0, ciphertextLen);
                Buffer.BlockCopy(packed, NONCE_SIZE + ciphertextLen, tag, 0, TAG_SIZE);

                var plaintext = new byte[ciphertextLen];
                using var aes = new AesGcm(key, TAG_SIZE);
                aes.Decrypt(nonce, ciphertext, tag, plaintext);

                return Encoding.UTF8.GetString(plaintext);
            }
            catch (CryptographicException)
            {
                // Wrong key or tampered data — expected for items from unencrypted versions
                return null;
            }
            catch (FormatException)
            {
                // Not valid Base64 — likely unencrypted plaintext (backward compatible)
                return null;
            }
        }

        /// <summary>
        /// Checks if a string appears to be AES-GCM encrypted (valid Base64 with sufficient length).
        /// Used to determine whether to decrypt or treat as plaintext (backward compatibility).
        /// </summary>
        public static bool IsEncrypted(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            // Encrypted values are Base64 and at least nonce + tag = 28 bytes → ~40 Base64 chars
            if (value.Length < 40) return false;
            // Quick heuristic: encrypted data starts with Base64-safe chars and no spaces/newlines
            if (value.Contains(' ') || value.Contains('\n') || value.Contains('\r')) return false;
            try
            {
                var bytes = Convert.FromBase64String(value);
                return bytes.Length >= NONCE_SIZE + TAG_SIZE;
            }
            catch { return false; }
        }

        /// <summary>
        /// Clears the cached key (call when pairing key changes or on logout).
        /// </summary>
        public static void ClearKeyCache()
        {
            if (_cachedKey != null)
            {
                Array.Clear(_cachedKey, 0, _cachedKey.Length);
                _cachedKey = null;
            }
            _cachedPairingKey = null;
        }
    }
}
