using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sluice.Core.Profiles;

/// <summary>Windows DPAPI (CryptProtectData / CryptUnprotectData), current-user scope.</summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    public static byte[] Protect(byte[] data, string? description = null) =>
        Run(data, (ref DataBlob i, out DataBlob o) =>
            CryptProtectData(ref i, description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out o));

    public static byte[] Unprotect(byte[] data) =>
        Run(data, (ref DataBlob i, out DataBlob o) =>
            CryptUnprotectData(ref i, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out o));

    private delegate bool Call(ref DataBlob input, out DataBlob output);

    private static byte[] Run(byte[] data, Call call)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI requires Windows");
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { cbData = data.Length, pbData = handle.AddrOfPinnedObject() };
            if (!call(ref input, out var output))
                throw new CryptographicFailure(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            handle.Free();
        }
    }
}

/// <summary>A stored password could not be decrypted (wrong user, wrong master password, corrupt data).</summary>
public sealed class CryptographicFailure(string message) : Exception(message);
