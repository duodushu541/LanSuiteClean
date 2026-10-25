using System.Security.Cryptography;
using System.Text;

namespace LanSuite.Shared;

/// <summary>
/// 轻量级 AES-256-CBC 加解密工具（兼容 .NET 8）
/// </summary>
public static class CryptoUtils
{
    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("LanSuiteClean-AES256-Key-2024!"));

    /// <summary>
    /// AES-CBC 加密，返回 base64（含 16 字节 IV 前缀）
    /// </summary>
    public static string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = encryptor.TransformFinalBlock(pt, 0, pt.Length);
        var result = new byte[aes.IV.Length + ct.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(ct, 0, result, aes.IV.Length, ct.Length);
        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// AES-CBC 解密
    /// </summary>
    public static string? Decrypt(string encryptedBase64)
    {
        try
        {
            var raw = Convert.FromBase64String(encryptedBase64);
            if (raw.Length < 17) return null;
            using var aes = Aes.Create();
            aes.Key = Key;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            var iv = new byte[16];
            var ct = new byte[raw.Length - 16];
            Buffer.BlockCopy(raw, 0, iv, 0, 16);
            Buffer.BlockCopy(raw, 16, ct, 0, raw.Length - 16);
            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor();
            var pt = decryptor.TransformFinalBlock(ct, 0, ct.Length);
            return Encoding.UTF8.GetString(pt);
        }
        catch { return null; }
    }

    /// <summary>
    /// 生成随机设备 ID
    /// </summary>
    public static string GenerateDeviceId()
    {
        var b = new byte[8];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(b);
        return Convert.ToHexString(b).ToLower();
    }

    /// <summary>
    /// 快速哈希
    /// </summary>
    public static string Hash(string input)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLower();
    }
}
