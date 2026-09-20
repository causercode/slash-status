using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TokenStatus.Core.Abstractions;

namespace TokenStatus.Infrastructure.OpenCode;

public sealed class WindowsOpenCodeGoCredentialStore : IOpenCodeGoCredentialStore
{
    internal const string CredentialTarget = "TokenStatus:OpenCodeGoApiKey";
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobBytes = 2560;

    public bool IsConfigured()
    {
        EnsureWindows();
        if (!CredRead(CredentialTarget, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return false;
            }

            throw new Win32Exception(error, "Could not read the OpenCode Go credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            return credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize > 0;
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public string? ReadApiKey()
    {
        EnsureWindows();
        if (!CredRead(CredentialTarget, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error, "Could not read the OpenCode Go credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var bytes = new byte[checked((int)credential.CredentialBlobSize)];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void SaveApiKey(string apiKey)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var normalized = apiKey.Trim();
        if (normalized.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("The OpenCode Go API key cannot contain whitespace.", nameof(apiKey));
        }

        var bytes = Encoding.UTF8.GetBytes(normalized);
        if (bytes.Length > MaximumCredentialBlobBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("The OpenCode Go API key is too long.", nameof(apiKey));
        }

        var blobPointer = IntPtr.Zero;
        var targetPointer = IntPtr.Zero;
        var userPointer = IntPtr.Zero;
        try
        {
            blobPointer = Marshal.AllocHGlobal(bytes.Length);
            targetPointer = Marshal.StringToCoTaskMemUni(CredentialTarget);
            userPointer = Marshal.StringToCoTaskMemUni("OpenCode Go");
            Marshal.Copy(bytes, 0, blobPointer, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetPointer,
                CredentialBlobSize = checked((uint)bytes.Length),
                CredentialBlob = blobPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = userPointer
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the OpenCode Go credential.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (blobPointer != IntPtr.Zero)
            {
                for (var index = 0; index < bytes.Length; index++)
                {
                    Marshal.WriteByte(blobPointer, index, 0);
                }

                Marshal.FreeHGlobal(blobPointer);
            }

            if (targetPointer != IntPtr.Zero)
            {
                Marshal.ZeroFreeCoTaskMemUnicode(targetPointer);
            }

            if (userPointer != IntPtr.Zero)
            {
                Marshal.ZeroFreeCoTaskMemUnicode(userPointer);
            }
        }
    }

    public void DeleteApiKey()
    {
        EnsureWindows();
        if (CredDelete(CredentialTarget, CredentialTypeGeneric, 0))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error, "Could not delete the OpenCode Go credential.");
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is only available on Windows.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
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

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
