using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RobloxAccountManager.Services;

/// <summary>
/// At-rest encryption for the account store.
///
/// File layout (first byte selects the mode):
///   [0x00] DPAPI (CurrentUser)          : 0x00 | DPAPI(plaintext, entropy)
///   [0x01] Master password (AES-256-GCM): 0x01 | salt(16) | nonce(12) | tag(16) | ciphertext
///
/// No third-party native dependency — DPAPI and AES-GCM are both built into Windows/.NET.
/// </summary>
public static class Crypto
{
    private const byte ModeDpapi = 0x00;
    private const byte ModePassword = 0x01;

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RAM.Modern.v1.entropy");
    private const int Pbkdf2Iterations = 210_000;

    public static byte[] EncryptDpapi(byte[] plaintext)
    {
        byte[] protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        return Prepend(ModeDpapi, protectedBytes);
    }

    public static byte[] EncryptPassword(byte[] plaintext, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] key = DeriveKey(password, salt);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];

        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

        using var ms = new MemoryStream();
        ms.WriteByte(ModePassword);
        ms.Write(salt);
        ms.Write(nonce);
        ms.Write(tag);
        ms.Write(ciphertext);
        return ms.ToArray();
    }

    /// <summary>Returns the plaintext. Throws on wrong password / tamper.</summary>
    public static byte[] Decrypt(byte[] data, string? password)
    {
        if (data.Length == 0) return Array.Empty<byte>();
        byte mode = data[0];
        byte[] body = data[1..];

        if (mode == ModeDpapi)
            return ProtectedData.Unprotect(body, Entropy, DataProtectionScope.CurrentUser);

        if (mode == ModePassword)
        {
            if (string.IsNullOrEmpty(password))
                throw new CryptographicException("A master password is required to open this account store.");

            byte[] salt = body[0..16];
            byte[] nonce = body[16..28];
            byte[] tag = body[28..44];
            byte[] ciphertext = body[44..];
            byte[] key = DeriveKey(password, salt);
            byte[] plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext); // throws AuthenticationTagMismatch on wrong password
            return plaintext;
        }

        throw new CryptographicException("Unknown account store format.");
    }

    public static bool IsPasswordProtected(byte[] data) => data.Length > 0 && data[0] == ModePassword;

    // ---------------------------------------------------------------
    //  Per-field string protection (DPAPI) — used so cookies are never
    //  written as plain text, even inside the (already encrypted) store.
    // ---------------------------------------------------------------
    private const string StringMarker = "enc1:";

    public static string ProtectString(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        byte[] prot = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return StringMarker + Convert.ToBase64String(prot);
    }

    public static string UnprotectString(string stored)
        => TryUnprotectString(stored, out string plain) ? plain : "";

    /// <summary>
    /// Like <see cref="UnprotectString"/>, but tells a genuinely empty value apart from one that could
    /// not be decrypted (a store copied from another Windows user). Callers that re-save the data
    /// need that difference — writing "" back would destroy a value that is merely unreadable here.
    /// </summary>
    public static bool TryUnprotectString(string? stored, out string plain)
    {
        plain = "";
        if (string.IsNullOrEmpty(stored)) return true;
        if (!stored.StartsWith(StringMarker, StringComparison.Ordinal)) { plain = stored; return true; } // legacy plaintext
        try
        {
            byte[] prot = Convert.FromBase64String(stored[StringMarker.Length..]);
            plain = Encoding.UTF8.GetString(ProtectedData.Unprotect(prot, Entropy, DataProtectionScope.CurrentUser));
            return true;
        }
        catch { return false; }
    }

    /// <summary>Constant-time string comparison for secrets (passwords, tokens).</summary>
    public static bool SecretEquals(string? a, string? b)
    {
        byte[] ha = SHA256.HashData(Encoding.UTF8.GetBytes(a ?? ""));
        byte[] hb = SHA256.HashData(Encoding.UTF8.GetBytes(b ?? ""));
        return CryptographicOperations.FixedTimeEquals(ha, hb) && (a != null) == (b != null);
    }

    private static byte[] DeriveKey(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] Prepend(byte mode, byte[] body)
    {
        byte[] result = new byte[body.Length + 1];
        result[0] = mode;
        Buffer.BlockCopy(body, 0, result, 1, body.Length);
        return result;
    }
}
