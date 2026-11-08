using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AccountUsageUI.Infrastructure.Profiles;

internal sealed class CurrentUserDataProtector
{
    private static readonly byte[] Magic = "AUPR1"u8.ToArray();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
        "AccountUsageUI.ProfileCredential.CurrentUser.v1");

    private const int CryptProtectUiForbidden = 0x1;

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var protectedBytes = InvokeDataProtection(plaintext, protect: true);
        var result = new byte[Magic.Length + protectedBytes.Length];
        Magic.CopyTo(result, 0);
        protectedBytes.CopyTo(result, Magic.Length);
        CryptographicOperations.ZeroMemory(protectedBytes);
        return result;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedPayload)
    {
        if (protectedPayload.Length <= Magic.Length ||
            !protectedPayload[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException(
                "지원하지 않는 인증 사본 형식입니다.");
        }

        return InvokeDataProtection(
            protectedPayload[Magic.Length..],
            protect: false);
    }

    private static byte[] InvokeDataProtection(
        ReadOnlySpan<byte> input,
        bool protect)
    {
        var inputBytes = input.ToArray();
        var entropyBytes = Entropy.ToArray();
        var inputBlob = default(DataBlob);
        var entropyBlob = default(DataBlob);
        var outputBlob = default(DataBlob);

        try
        {
            inputBlob = CreateBlob(inputBytes);
            entropyBlob = CreateBlob(entropyBytes);

            var succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    "AccountUsageUI profile credential",
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob);

            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var result = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, result, 0, outputBlob.Size);
            return result;
        }
        finally
        {
            ZeroAndFree(ref inputBlob);
            ZeroAndFree(ref entropyBlob);
            ZeroAndLocalFree(ref outputBlob);
            CryptographicOperations.ZeroMemory(inputBytes);
            CryptographicOperations.ZeroMemory(entropyBytes);
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var allocationSize = Math.Max(bytes.Length, 1);
        var blob = new DataBlob
        {
            Size = bytes.Length,
            Data = Marshal.AllocHGlobal(allocationSize)
        };

        if (bytes.Length > 0)
        {
            Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        }

        return blob;
    }

    private static void ZeroAndFree(ref DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        ZeroMemory(blob.Data, blob.Size);
        Marshal.FreeHGlobal(blob.Data);
        blob = default;
    }

    private static void ZeroAndLocalFree(ref DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        ZeroMemory(blob.Data, blob.Size);
        _ = LocalFree(blob.Data);
        blob = default;
    }

    private static void ZeroMemory(IntPtr address, int length)
    {
        for (var index = 0; index < length; index++)
        {
            Marshal.WriteByte(address, index, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
