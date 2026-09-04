using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Encomm.Browser.Settings;

/// <summary>
/// Optional mirror of the secret into Windows Credential Manager. Even
/// when used, the secret is never stored in plaintext on disk — the
/// Windows credential blob is opaque to the rest of the system.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialManagerSecretStore
{
    private const string TargetPrefix = "Encomm-AI-Browser:";

    public void Save(string name, string secret)
    {
        var target = TargetPrefix + name;
        var targetPtr = Marshal.StringToCoTaskMemUni(target);
        var userPtr = Marshal.StringToCoTaskMemUni(name);
        var blobPtr = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var size = (uint)(secret.Length * sizeof(char));
            var cred = new CREDENTIAL
            {
                Flags = 0,
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                CredentialBlobSize = size,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                Comment = IntPtr.Zero,
                TargetAlias = IntPtr.Zero,
                UserName = userPtr
            };
            if (!CredWriteW(ref cred, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (targetPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(targetPtr);
            if (userPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(userPtr);
            if (blobPtr != IntPtr.Zero) Marshal.ZeroFreeCoTaskMemUnicode(blobPtr);
        }
    }

    public string? Read(string name)
    {
        var target = TargetPrefix + name;
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == 1168 /* NOT_FOUND */) return null;
            throw new System.ComponentModel.Win32Exception(err);
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            var length = (int)cred.CredentialBlobSize / sizeof(char);
            var chars = new char[length];
            Marshal.Copy(cred.CredentialBlob, chars, 0, length);
            return new string(chars);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public bool Delete(string name)
    {
        var target = TargetPrefix + name;
        return CredDeleteW(target, CRED_TYPE_GENERIC, 0);
    }

    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref CREDENTIAL userCredential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr cred);
}