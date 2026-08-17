using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RvcStudio.App;

/// <summary>
/// Encrypts and authenticates local state with Windows DPAPI.  The ciphertext
/// can only be opened by the same Windows user and application entropy.
/// </summary>
internal static class ProtectedLocalStorage
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("RVC Studio local protected state v1"));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static byte[] ProtectJson<T>(T value) =>
        Protect(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    public static T? UnprotectJson<T>(byte[] ciphertext) =>
        JsonSerializer.Deserialize<T>(Unprotect(ciphertext), JsonOptions);

    public static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        var ciphertext = await File.ReadAllBytesAsync(path, cancellationToken);
        return UnprotectJson<T>(ciphertext);
    }

    public static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("受保护存储路径无效。");
        Directory.CreateDirectory(directory);

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, ProtectJson(value), cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A failed best-effort cleanup must not hide the storage result.
            }
        }
    }

    private static byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);

    private static byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("RVC Studio 的受保护存储需要 Windows DPAPI。");
        }
        if (input.Length == 0)
        {
            throw new CryptographicException("受保护存储数据为空。");
        }

        var inputBlob = AllocateBlob(input);
        var entropyBlob = AllocateBlob(Entropy);
        DataBlob outputBlob = default;
        IntPtr description = IntPtr.Zero;
        try
        {
            var succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    null,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    out description,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob);
            if (!succeeded)
            {
                throw new CryptographicException(
                    "无法读取受保护的本地数据。",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            var result = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, result, 0, outputBlob.Size);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inputBlob.Data);
            Marshal.FreeHGlobal(entropyBlob.Data);
            if (outputBlob.Data != IntPtr.Zero) LocalFree(outputBlob.Data);
            if (description != IntPtr.Zero) LocalFree(description);
        }
    }

    private static DataBlob AllocateBlob(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DataBlob { Size = data.Length, Data = pointer };
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
        string? dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
