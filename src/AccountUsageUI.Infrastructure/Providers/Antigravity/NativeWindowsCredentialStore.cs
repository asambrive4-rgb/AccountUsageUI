using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AccountUsageUI.Infrastructure.Providers.Antigravity;

public interface IWindowsCredentialStore
{
    byte[]? ReadCredential(string targetName);

    void WriteCredential(
        string targetName,
        string userName,
        ReadOnlySpan<byte> secretBytes);

    void DeleteCredential(string targetName);
}

public sealed class NativeWindowsCredentialStore : IWindowsCredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public byte[]? ReadCredential(string targetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);

        if (!CredReadW(
                targetName,
                CredTypeGeneric,
                0,
                out var credentialPtr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);
            if (credential.CredentialBlob == IntPtr.Zero ||
                credential.CredentialBlobSize <= 0)
            {
                return null;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(
                credential.CredentialBlob,
                bytes,
                0,
                credential.CredentialBlobSize);
            return bytes;
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public void WriteCredential(
        string targetName,
        string userName,
        ReadOnlySpan<byte> secretBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        var copy = secretBytes.ToArray();
        var blobPtr = IntPtr.Zero;

        try
        {
            blobPtr = Marshal.AllocHGlobal(Math.Max(copy.Length, 1));
            if (copy.Length > 0)
            {
                Marshal.Copy(copy, 0, blobPtr, copy.Length);
            }

            var credential = new NativeCredential
            {
                Flags = 0,
                Type = CredTypeGeneric,
                TargetName = targetName,
                Comment = null,
                CredentialBlobSize = copy.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = null,
                UserName = userName
            };

            if (!CredWriteW(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            if (blobPtr != IntPtr.Zero)
            {
                for (var i = 0; i < copy.Length; i++)
                {
                    Marshal.WriteByte(blobPtr, i, 0);
                }

                Marshal.FreeHGlobal(blobPtr);
            }

            CryptographicOperations.ZeroMemory(copy);
        }
    }

    public void DeleteCredential(string targetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);

        if (!CredDeleteW(targetName, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return;
            }

            throw new Win32Exception(error);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(
        string target,
        uint type,
        int reservedFlag,
        out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(
        ref NativeCredential userCredential,
        uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(
        string target,
        uint type,
        int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr cred);
}
