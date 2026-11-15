using System.Runtime.InteropServices;
using System.Text;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Settings;

/// <summary>
/// Windows DPAPI（当前用户域）薄封装：<c>CryptProtectData</c> / <c>CryptUnprotectData</c>。
/// 仅用于保护 API Key 之类的短密文；失败返回 null，调用方自行降级。
/// </summary>
public static class Dpapi
{
    private const int CryptProtectUiForbidden = 0x00000001;
    private const int ErrorSuccess = 0;

    /// <summary>附加熵：把密文绑定到本应用，别的程序即便拿到 base64 也解不开。</summary>
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("StjMediaViewer.Secret.v1");

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>把明文加密成 base64（DPAPI 当前用户域）。失败返回 null。</summary>
    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return null;
        if (!OperatingSystem.IsWindows()) return null;

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var inPtr = IntPtr.Zero;
        var entropyPtr = IntPtr.Zero;
        var outBlob = default(DataBlob);
        try
        {
            var inBlob = AllocInputBlob(plainBytes, out inPtr);
            var entropyBlob = AllocInputBlob(OptionalEntropy, out entropyPtr);

            if (!CryptProtectData(ref inBlob, "StjMediaViewer API Key", ref entropyBlob,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob))
            {
                Log.Warn($"DPAPI 加密失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
                return null;
            }

            var cipher = new byte[outBlob.CbData];
            if (outBlob.CbData > 0) Marshal.Copy(outBlob.PbData, cipher, 0, outBlob.CbData);
            return Convert.ToBase64String(cipher);
        }
        catch (Exception ex)
        {
            Log.Warn("DPAPI 加密异常", ex);
            return null;
        }
        finally
        {
            FreeInputBlob(ref inPtr);
            FreeInputBlob(ref entropyPtr);
            // CryptProtectData 的 out 缓冲区由 LocalAlloc 分配，必须 LocalFree。
            if (outBlob.PbData != IntPtr.Zero) LocalFree(outBlob.PbData);
        }
    }

    /// <summary>解密 base64 密文。失败（含非 DPAPI 数据）返回 null。</summary>
    public static string? Unprotect(string? cipherBase64)
    {
        if (string.IsNullOrWhiteSpace(cipherBase64)) return null;
        if (!OperatingSystem.IsWindows()) return null;

        byte[] cipher;
        try { cipher = Convert.FromBase64String(cipherBase64.Trim()); }
        catch { return null; }
        if (cipher.Length == 0) return null;

        var inPtr = IntPtr.Zero;
        var entropyPtr = IntPtr.Zero;
        var outBlob = default(DataBlob);
        try
        {
            var inBlob = AllocInputBlob(cipher, out inPtr);
            var entropyBlob = AllocInputBlob(OptionalEntropy, out entropyPtr);

            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob))
            {
                var err = Marshal.GetLastWin32Error();
                if (err != ErrorSuccess) Log.Warn($"DPAPI 解密失败，Win32 错误码 {err}");
                return null;
            }

            var plain = new byte[outBlob.CbData];
            if (outBlob.CbData > 0) Marshal.Copy(outBlob.PbData, plain, 0, outBlob.CbData);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            Log.Warn("DPAPI 解密异常", ex);
            return null;
        }
        finally
        {
            FreeInputBlob(ref inPtr);
            FreeInputBlob(ref entropyPtr);
            if (outBlob.PbData != IntPtr.Zero) LocalFree(outBlob.PbData);
        }
    }

    /// <summary>粗略判断字符串是否像 DPAPI 密文（Base64 的 0x01 0x00 0x00 0x00 开头 → "AQAAAN…"）。</summary>
    public static bool LooksLikeProtectedBlob(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith("AQAAAN", StringComparison.Ordinal) &&
        value.Length >= 40;

    private static DataBlob AllocInputBlob(byte[] data, out IntPtr rawPtr)
    {
        rawPtr = IntPtr.Zero;
        if (data.Length == 0) return new DataBlob { CbData = 0, PbData = IntPtr.Zero };
        rawPtr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, rawPtr, data.Length);
        return new DataBlob { CbData = data.Length, PbData = rawPtr };
    }

    private static void FreeInputBlob(ref IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        try { Marshal.FreeHGlobal(ptr); } catch { }
        ptr = IntPtr.Zero;
    }
}