using System;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SteamDeployer
{
    /// <summary>
    /// Provides AES-256-CBC symmetric encryption and decryption for securing the Steam password.
    ///
    /// KEY DERIVATION MATH:
    ///   AES Key (32 bytes) = SHA-256( deviceUniqueIdentifier + CRYPTO_SALT )
    ///     - SHA-256 produces exactly 256 bits = 32 bytes, matching AES-256 key size.
    ///     - The salt provides domain separation and defeats pre-computation attacks.
    ///
    ///   AES IV (16 bytes) = MD5( deviceUniqueIdentifier + reversed(CRYPTO_SALT) )
    ///     - MD5 produces exactly 128 bits = 16 bytes, matching the AES block / IV size.
    ///     - The reversed salt ensures IV != Key even if both hash functions were somehow equivalent.
    ///     - IV uniqueness across sessions is not required here because we use a fixed, machine-bound
    ///       IV; the security model is "only decryptable on this machine", not "IND-CPA across sessions".
    ///
    /// STORAGE: The resulting Base64-encoded ciphertext is stored in Unity's EditorPrefs,
    /// which on Windows maps to HKCU\Software\Unity Technologies\Unity Editor 5.x.
    /// It never touches any file on disk that could be committed to version control.
    /// </summary>
    public static class CryptographyHelper
    {
        // Hardcoded domain-separation salt. Changing this value will invalidate all stored passwords.
        // It is intentionally non-secret; its purpose is to prevent cross-application key reuse.
        private const string CRYPTO_SALT = "SteamDeployer_v1_#xK9mP2@qR7!Salt";

        // EditorPrefs key under which the Base64 ciphertext is stored.
        private const string EDITOR_PREFS_KEY = "SteamDeployer_EncryptedPassword";

        // ─── Key / IV Derivation ──────────────────────────────────────────────────

        /// <summary>
        /// Derives the 32-byte AES-256 key from the hardware device identifier and salt.
        /// SHA-256 is used because it produces exactly 32 bytes and is collision-resistant.
        /// </summary>
        private static byte[] DeriveKey()
        {
            // Concatenate the hardware ID and salt to form the key material.
            // SystemInfo.deviceUniqueIdentifier is platform-specific but stable per machine.
            string keyMaterial = SystemInfo.deviceUniqueIdentifier + CRYPTO_SALT;

            using (var sha256 = SHA256.Create())
            {
                // SHA-256(key_material) → 32 bytes → AES-256 key
                return sha256.ComputeHash(Encoding.UTF8.GetBytes(keyMaterial));
            }
        }

        /// <summary>
        /// Derives the 16-byte AES Initialization Vector from the hardware device identifier
        /// and a reversed version of the salt, ensuring IV ≠ Key.
        /// MD5 is used here purely for its 16-byte output size; it is not used for security.
        /// </summary>
        private static byte[] DeriveIV()
        {
            // Reverse the salt to guarantee the IV input material differs from the key input material.
            char[] saltChars = CRYPTO_SALT.ToCharArray();
            Array.Reverse(saltChars);
            string ivMaterial = SystemInfo.deviceUniqueIdentifier + new string(saltChars);

            using (var md5 = MD5.Create())
            {
                // MD5(iv_material) → 16 bytes → AES block-size IV
                return md5.ComputeHash(Encoding.UTF8.GetBytes(ivMaterial));
            }
        }

        // ─── Public API ───────────────────────────────────────────────────────────

        /// <summary>
        /// Encrypts the plaintext password with AES-256-CBC and stores the resulting
        /// Base64-encoded ciphertext in EditorPrefs. The password is only recoverable
        /// on the same machine, as the key is derived from the hardware device ID.
        /// Passing null or empty clears any stored password.
        /// </summary>
        public static void SaveEncryptedPassword(string plainTextPassword)
        {
            if (string.IsNullOrEmpty(plainTextPassword))
            {
                EditorPrefs.DeleteKey(EDITOR_PREFS_KEY);
                return;
            }

            try
            {
                using (var aes = Aes.Create())
                {
                    aes.KeySize  = 256;
                    aes.Key      = DeriveKey();   // 32 bytes — AES-256
                    aes.IV       = DeriveIV();    // 16 bytes — CBC initialization vector
                    aes.Mode     = CipherMode.CBC;
                    aes.Padding  = PaddingMode.PKCS7;

                    using (var encryptor = aes.CreateEncryptor())
                    {
                        byte[] plainBytes  = Encoding.UTF8.GetBytes(plainTextPassword);
                        // TransformFinalBlock processes the last (and only) block including padding
                        byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

                        // Base64 is safe for EditorPrefs (which stores plain strings)
                        EditorPrefs.SetString(EDITOR_PREFS_KEY, Convert.ToBase64String(cipherBytes));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SteamDeployer] Encryption failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Retrieves the stored ciphertext from EditorPrefs and decrypts it using AES-256-CBC.
        /// Returns null if no password is stored, the data is corrupted, or this is a different machine.
        /// </summary>
        public static string LoadDecryptedPassword()
        {
            string stored = EditorPrefs.GetString(EDITOR_PREFS_KEY, null);
            if (string.IsNullOrEmpty(stored))
                return null;

            try
            {
                using (var aes = Aes.Create())
                {
                    aes.KeySize  = 256;
                    aes.Key      = DeriveKey();
                    aes.IV       = DeriveIV();
                    aes.Mode     = CipherMode.CBC;
                    aes.Padding  = PaddingMode.PKCS7;

                    using (var decryptor = aes.CreateDecryptor())
                    {
                        byte[] cipherBytes = Convert.FromBase64String(stored);
                        byte[] plainBytes  = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                        return Encoding.UTF8.GetString(plainBytes);
                    }
                }
            }
            catch (Exception ex)
            {
                // Most common causes: different machine (different device ID → different key),
                // corrupted EditorPrefs, or data from an older format.
                Debug.LogWarning($"[SteamDeployer] Could not decrypt stored password " +
                                 $"(may be from a different machine): {ex.Message}");
                return null;
            }
        }

        /// <summary>Returns true if an encrypted password entry exists in EditorPrefs.</summary>
        public static bool HasStoredPassword()
        {
            return EditorPrefs.HasKey(EDITOR_PREFS_KEY) &&
                   !string.IsNullOrEmpty(EditorPrefs.GetString(EDITOR_PREFS_KEY, null));
        }

        /// <summary>Permanently removes the encrypted password from EditorPrefs.</summary>
        public static void ClearStoredPassword()
        {
            EditorPrefs.DeleteKey(EDITOR_PREFS_KEY);
        }
    }
}
