// ═══════════════════════════════════════════════════════════════════
// AntiTamperService — Runtime integrity protection for FlyShelf
// Extracted from LicenseManager v2.5.0 for separation of concerns.
// Handles: debugger detection, memory-patch sentinel, assembly integrity.
// ═══════════════════════════════════════════════════════════════════
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Runtime anti-tamper service providing:
    /// - Multi-layer debugger detection (managed, native, remote)
    /// - HMAC-based tier sentinel for memory-patch detection
    /// - Assembly integrity verification (SHA256 + HMAC-signed hash file)
    /// </summary>
    public static class AntiTamperService
    {
        // ═══ ANTI-DEBUG ═══
        private static int _antiDebugCounter = 0;
        private static bool _debuggerDetected = false;

        /// <summary>
        /// Returns true if a debugger was ever detected during this session.
        /// Once detected, this is permanently true until restart.
        /// </summary>
        public static bool IsDebuggerDetected => _debuggerDetected;

        /// <summary>
        /// Multi-layer debugger detection: managed debugger, native debugger, and remote debugger.
        /// Called periodically (every 5th IsPro access) to catch runtime attachments.
        /// </summary>
        public static bool DetectDebugger()
        {
            try
            {
                // Layer 1: Managed debugger (Visual Studio, dnSpy debugger mode)
                if (System.Diagnostics.Debugger.IsAttached) return true;

                // Layer 2: Native debugger (x64dbg, WinDbg, Cheat Engine debugger)
                if (NativeMethods.IsDebuggerPresent()) return true;

                // Layer 3: Remote debugger (attached from another process)
                // PL-7: Wrap in using to dispose the Process handle
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                NativeMethods.CheckRemoteDebuggerPresent(proc.Handle, out bool remote);
                if (remote) return true;
            }
            catch { /* P/Invoke may fail on non-Windows — ignore */ }
            return false;
        }

        /// <summary>
        /// Periodic debugger check — should be called from IsPro accessor.
        /// Returns true if a debugger is detected (features should be disabled).
        /// Uses sampling (every 5th call) to avoid perf overhead.
        /// </summary>
        public static bool CheckDebuggerPeriodic()
        {
#if DEBUG
            // Skip anti-debugger checks in Debug builds — running from Visual Studio
            // with debugger attached would permanently disable Pro features.
            return false;
#else
            if (_debuggerDetected) return true;
            if (Interlocked.Increment(ref _antiDebugCounter) % 5 == 0)
            {
                if (DetectDebugger())
                {
                    _debuggerDetected = true;
                    Logger.LogAction("SECURITY", "⚠️ Debugger detected — Pro features disabled");
                    return true;
                }
            }
            return false;
#endif
        }

        // ═══ TIER SENTINEL ═══

        /// <summary>
        /// Computes a sentinel value from Tier + LicenseKey + a runtime salt.
        /// Must match stored sentinel for IsPro to return true.
        /// This prevents memory-patching _data.Tier from "free" to "pro".
        /// </summary>
        public static int ComputeTierSentinel(string tier, string key, string salt, string keySecret)
        {
            try
            {
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(keySecret));
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(tier + "|" + key + "|" + salt));
                return BitConverter.ToInt32(hash, 0);
            }
            catch (Exception ex)
            {
                // PL-9: Log sentinel computation failure for diagnostics
                Logger.LogAction("ANTITAMPER", $"Sentinel computation failed: {ex.Message}");
                return 0;
            }
        }

        // ═══ ASSEMBLY INTEGRITY ═══

        private static bool _integrityChecked = false;

        /// <summary>
        /// HMAC-signs the assembly hash so the .assembly_hash file can't be tampered with.
        /// An attacker can't just delete the file and write a new hash — they'd need the HMAC secret.
        /// Format: "hash|hmac_of_hash"
        /// </summary>
        public static string SignAssemblyHash(string hash, string keySecret)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(keySecret));
            byte[] sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(hash + "|integrity"));
            string sigHex = BitConverter.ToString(sig).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            return hash + "|" + sigHex;
        }

        /// <summary>
        /// Verifies the HMAC signature on a stored assembly hash.
        /// Returns true if the hash is valid (either signed or legacy unsigned).
        /// </summary>
        public static bool VerifySignedHash(string stored, string keySecret, out string hash)
        {
            hash = null;
            if (string.IsNullOrEmpty(stored)) return false;
            var parts = stored.Split('|');
            if (parts.Length == 1)
            {
                // Legacy unsigned hash — accept but will be re-signed on next write
                hash = parts[0];
                return true;
            }
            if (parts.Length != 2) return false;
            hash = parts[0];
            string expectedSigned = SignAssemblyHash(hash, keySecret);
            return stored == expectedSigned;
        }

        /// <summary>
        /// Computes SHA256 hash of the current assembly binary.
        /// Returns null if the assembly path is unavailable.
        /// </summary>
        public static string ComputeCurrentAssemblyHash()
        {
            try
            {
                // PL-8: Wrap in using to dispose the Process handle
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                string assemblyPath = proc.MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
                {
                    Logger.LogAction("INTEGRITY", "Assembly path unavailable — skipping integrity check");
                    return null;
                }

                using var sha = SHA256.Create();
                using var stream = File.OpenRead(assemblyPath);
                byte[] hashBytes = sha.ComputeHash(stream);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception ex)
            {
                Logger.LogAction("INTEGRITY", $"Failed to compute assembly hash: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Full assembly integrity verification flow.
        /// Compares current binary hash against stored signed hash.
        /// On mismatch for Pro users: clears JWT and forces server re-activation.
        /// </summary>
        /// <param name="appDataDir">Path to %AppData%/FlyShelf</param>
        /// <param name="keySecret">XOR-decoded key secret for HMAC</param>
        /// <param name="isPro">Whether user is currently Pro tier</param>
        /// <param name="hasLicenseKey">Whether a license key is present</param>
        /// <param name="onBinaryChanged">Callback when binary has changed for Pro users (should clear JWT and trigger revalidation)</param>
        public static void VerifyAssemblyIntegrity(
            string appDataDir,
            string keySecret,
            bool isPro,
            bool hasLicenseKey,
            Action onBinaryChanged)
        {
#if DEBUG
            // Skip assembly integrity check in development builds — the binary hash
            // changes on every compilation, which would clear the JWT and force
            // server re-activation on every launch, resetting the license to Free.
            Logger.LogAction("INTEGRITY", "Skipped — Debug build");
            return;
#else
            if (_integrityChecked) return;
            _integrityChecked = true;

            try
            {
                string currentHash = ComputeCurrentAssemblyHash();
                if (currentHash == null) return;

                string hashFile = Path.Combine(appDataDir, ".assembly_hash");
                if (File.Exists(hashFile))
                {
                    string storedRaw = File.ReadAllText(hashFile).Trim();

                    // v2.4.0: Verify HMAC signature on stored hash to prevent attacker
                    // from pre-computing and writing a new hash for their patched binary
                    if (!VerifySignedHash(storedRaw, keySecret, out string storedHash))
                    {
                        Logger.LogAction("INTEGRITY", "⚠️ .assembly_hash signature invalid — file was tampered");
                        storedHash = "tampered";
                    }

                    if (!string.IsNullOrEmpty(storedHash) && storedHash != currentHash)
                    {
                        Logger.LogAction("INTEGRITY", $"Binary hash changed. Old: {storedHash.Substring(0, Math.Min(12, storedHash.Length))}..., New: {currentHash.Substring(0, 12)}...");

                        // Always update the hash file FIRST so the next launch doesn't
                        // re-trigger the binary change flow and loop forever.
                        Directory.CreateDirectory(appDataDir);
                        File.WriteAllText(hashFile, SignAssemblyHash(currentHash, keySecret));

                        if (isPro && hasLicenseKey)
                        {
                            // Notify LicenseManager to clear JWT and force re-activation
                            onBinaryChanged?.Invoke();
                        }
                        return;
                    }
                }

                // Store/update the signed hash on clean runs
                Directory.CreateDirectory(appDataDir);
                File.WriteAllText(hashFile, SignAssemblyHash(currentHash, keySecret));
                Logger.LogAction("INTEGRITY", $"✅ Assembly integrity verified: {currentHash.Substring(0, 12)}...");
            }
            catch (Exception ex)
            {
                Logger.LogAction("INTEGRITY", $"Integrity check failed (non-fatal): {ex.Message}");
            }
#endif
        }
    }
}
