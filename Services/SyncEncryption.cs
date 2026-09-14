using System;
using System.Security.Cryptography;
using System.Text;

namespace SideDesk.Services
{
    /// <summary>
    /// 云同步加密工具：基于用户设置的同步加密密码派生 AES-256 密钥，使用 AES-GCM 加解密敏感设置项。
    /// 派生密钥通过 Windows DPAPI 保护后缓存到本地。
    /// </summary>
    public static class SyncEncryption
    {
        private const int SaltIterations = 100_000;
        private const int KeySizeBytes = 32; // AES-256
        private const int IvSizeBytes = 12;  // GCM 推荐 96-bit IV
        private const int TagSizeBytes = 16; // GCM tag
        private const string CipherPrefix = "ENC:v1:";

        /// <summary>
        /// 从用户设置的同步加密密码 + userId 派生 AES-256 密钥（PBKDF2-SHA256）。
        /// salt 只需确定性且每用户唯一，直接用 encrypt2 密文 userId 即可，无需数字 ID。
        /// </summary>
        public static byte[] DeriveKey(string password, string userId)
        {
            byte[] salt = Encoding.UTF8.GetBytes($"sidedesk-sync:{userId}");
            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, SaltIterations, HashAlgorithmName.SHA256);
            return pbkdf2.GetBytes(KeySizeBytes);
        }

        /// <summary>
        /// AES-256-GCM 加密，返回格式 "ENC:v1:{base64(iv + ciphertext + tag)}"
        /// </summary>
        public static string Encrypt(string plaintext, byte[] key)
        {
            byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            byte[] iv = new byte[IvSizeBytes];
            RandomNumberGenerator.Fill(iv);

            byte[] ciphertext = new byte[plaintextBytes.Length];
            byte[] tag = new byte[TagSizeBytes];

            using var aes = new AesGcm(key, TagSizeBytes);
            aes.Encrypt(iv, plaintextBytes, ciphertext, tag);

            // 组合: iv(12) + ciphertext(n) + tag(16)
            byte[] combined = new byte[IvSizeBytes + ciphertext.Length + TagSizeBytes];
            Buffer.BlockCopy(iv, 0, combined, 0, IvSizeBytes);
            Buffer.BlockCopy(ciphertext, 0, combined, IvSizeBytes, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, combined, IvSizeBytes + ciphertext.Length, TagSizeBytes);

            return CipherPrefix + Convert.ToBase64String(combined);
        }

        /// <summary>
        /// AES-256-GCM 解密，输入格式 "ENC:v1:{base64(iv + ciphertext + tag)}"
        /// </summary>
        public static string Decrypt(string encryptedText, byte[] key)
        {
            if (!encryptedText.StartsWith(CipherPrefix))
                return encryptedText; // 非加密格式，原样返回

            byte[] combined = Convert.FromBase64String(encryptedText[CipherPrefix.Length..]);

            int ciphertextLength = combined.Length - IvSizeBytes - TagSizeBytes;
            if (ciphertextLength < 0)
                throw new CryptographicException("密文数据长度无效");

            byte[] iv = new byte[IvSizeBytes];
            byte[] ciphertext = new byte[ciphertextLength];
            byte[] tag = new byte[TagSizeBytes];

            Buffer.BlockCopy(combined, 0, iv, 0, IvSizeBytes);
            Buffer.BlockCopy(combined, IvSizeBytes, ciphertext, 0, ciphertextLength);
            Buffer.BlockCopy(combined, IvSizeBytes + ciphertextLength, tag, 0, TagSizeBytes);

            byte[] plaintext = new byte[ciphertextLength];
            using var aes = new AesGcm(key, TagSizeBytes);
            aes.Decrypt(iv, ciphertext, tag, plaintext);

            return Encoding.UTF8.GetString(plaintext);
        }

        /// <summary>判断值是否为加密格式</summary>
        public static bool IsEncrypted(string? value) => value?.StartsWith(CipherPrefix) == true;

        /// <summary>
        /// 使用 DPAPI 保护派生密钥（仅当前 Windows 用户可解密），返回 base64 字符串
        /// </summary>
        public static string ProtectKeyForLocal(byte[] key)
        {
            byte[] protected_ = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protected_);
        }

        /// <summary>
        /// 从 DPAPI 保护的 base64 字符串还原派生密钥
        /// </summary>
        public static byte[] UnprotectKeyFromLocal(string protectedBase64)
        {
            byte[] protectedBytes = Convert.FromBase64String(protectedBase64);
            return ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        }
    }
}
