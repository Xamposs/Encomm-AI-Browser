using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Encomm.Browser.Settings;

/// <summary>
/// Windows DPAPI (CurrentUser scope) encryption. We use this as the
/// primary secret store so keys are bound to the user account and never
/// leave the machine in plaintext.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DpapiSecretCipher
{
    public static byte[] Protect(byte[] plaintext)
    {
        return ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
    }

    public static byte[] Unprotect(byte[] ciphertext)
    {
        return ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
    }

    public static string ProtectString(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = Protect(bytes);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string UnprotectString(string ciphertextBase64)
    {
        var bytes = Convert.FromBase64String(ciphertextBase64);
        var plain = Unprotect(bytes);
        return Encoding.UTF8.GetString(plain);
    }
}