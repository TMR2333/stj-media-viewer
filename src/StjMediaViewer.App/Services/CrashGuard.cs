using System.Runtime.InteropServices;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.App.Services;

/// <summary>
/// 原生层崩溃兜底：WinUI 的 stowed exception（0xC000027B）不走托管 UnhandledException，
/// 这里用 SetUnhandledExceptionFilter 抢到最后一刻，把异常码 + 现场写进日志，
/// 并尝试落一个 minidump 到 %LOCALAPPDATA%\StjMediaViewer\crashes，方便定位"闪退"。
/// 全部包在 try/catch 里：崩溃路径上绝不二次抛异常。
/// </summary>
internal static class CrashGuard
{
    private const uint ExceptionExecuteHandler = 1;
    private const uint MiniDumpWithThreadInfo = 0x1000;
    private const uint MiniDumpWithIndirectlyReferencedMemory = 0x80;

    private delegate int UnhandledExceptionFilter(IntPtr exceptionInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr SetUnhandledExceptionFilter(UnhandledExceptionFilter filter);

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess, uint processId, IntPtr hFile, uint dumpType,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    private static UnhandledExceptionFilter? _filter;
    private static int _handling;

    public static void Install()
    {
        try
        {
            _filter = OnUnhandledException;
            SetUnhandledExceptionFilter(_filter);
        }
        catch (Exception ex)
        {
            try { Log.Warn("[crash] 无法安装原生异常过滤器：" + ex.Message); } catch { }
        }
    }

    private static int OnUnhandledException(IntPtr exceptionInfo)
    {
        // 只处理一次，避免递归（日志/写盘本身出异常时直接放行）
        if (Interlocked.Exchange(ref _handling, 1) == 1) return 0;

        try
        {
            uint code = 0;
            if (exceptionInfo != IntPtr.Zero)
            {
                // EXCEPTION_POINTERS { PEXCEPTION_RECORD; PCONTEXT; }
                var record = Marshal.ReadIntPtr(exceptionInfo);
                if (record != IntPtr.Zero) code = (uint)Marshal.ReadInt32(record);
            }

            var tag = code switch
            {
                0xC000027Bu => "STOWED_EXCEPTION（XAML/绑定异常，常见闪退）",
                0xC00000FDu => "STACK_OVERFLOW",
                0xC0000005u => "ACCESS_VIOLATION",
                0xE0434352u => "CLR 未处理异常",
                _ => "UNHANDLED",
            };
            Log.Error($"[crash] 原生未处理异常 0x{code:X8} · {tag}");

            var dumpPath = WriteDump();
            if (dumpPath is not null) Log.Error($"[crash] dump 已保存：{dumpPath}");
        }
        catch
        {
            // 崩溃路径上不再抛
        }

        return (int)ExceptionExecuteHandler;
    }

    private static string? WriteDump()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StjMediaViewer", "crashes");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            bool ok = MiniDumpWriteDump(
                GetCurrentProcess(), GetCurrentProcessId(), fs.SafeFileHandle.DangerousGetHandle(),
                MiniDumpWithThreadInfo | MiniDumpWithIndirectlyReferencedMemory,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return ok ? path : null;
        }
        catch
        {
            return null;
        }
    }
}