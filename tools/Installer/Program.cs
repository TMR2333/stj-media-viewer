using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace StjMediaViewer.Installer;

/// <summary>
/// STJ Media Viewer 安装包（单文件 exe，无需管理员权限）。
///
/// 用法：
///   STJMediaViewer-Setup.exe              交互式安装（确认 → 安装 → 完成）
///   STJMediaViewer-Setup.exe /S           静默安装
///   STJMediaViewer-Setup.exe /uninstall   交互式卸载
///   STJMediaViewer-Setup.exe /S /uninstall 静默卸载
///
/// 安装位置：%LOCALAPPDATA%\Programs\StjMediaViewer（用户目录，不用 UAC）
/// 附带：开始菜单快捷方式 + 系统「应用和功能」卸载项（HKCU）
/// </summary>
internal static class Program
{
    private const string AppName = "STJ Media Viewer";
    private const string AppId = "StjMediaViewer";
    private const string Version = "1.0.0";
    private const string Publisher = "Stjarna Perspective";
    private const string ExeName = "StjMediaViewer.exe";

    // 测试/绿色部署可用 STJ_SETUP_DIR 覆盖安装目录
    private static string InstallDir =>
        Environment.GetEnvironmentVariable("STJ_SETUP_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", AppId);

    private static string TargetExe => Path.Combine(InstallDir, ExeName);

    private static string StartMenuLink =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            $"{AppName}.lnk");

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId;

    private static readonly string[] LogPaths =
    {
        Path.Combine(AppContext.BaseDirectory, "stj-setup.log"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StjMediaViewerSetup.log"),
        Path.Combine(Path.GetTempPath(), "stj-setup.log"),
    };

    private static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
        foreach (var path in LogPaths)
        {
            try
            {
                File.AppendAllText(path, line);
                return;
            }
            catch { /* 换下一个位置 */ }
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Log($"start args=[{string.Join(" | ", args)}]");

        var silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase)
                                   || a.Equals("/silent", StringComparison.OrdinalIgnoreCase));
        var uninstall = args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));

        try
        {
            if (uninstall) return Uninstall(silent);
            return Install(silent);
        }
        catch (Exception ex)
        {
            Log($"ERROR {ex}");
            if (!silent) Msg($"安装失败：{ex.Message}", "STJ Media Viewer", 0x10);
            return 1;
        }
    }

    // =====================================================================
    //  安装
    // =====================================================================

    private static int Install(bool silent)
    {
        var existing = File.Exists(TargetExe);
        if (!silent)
        {
            var prompt = existing
                ? $"已检测到旧版本。\n\n将覆盖安装到：\n{InstallDir}\n\n继续吗？"
                : $"将安装 {AppName} {Version}\n\n安装位置：\n{InstallDir}\n\n" +
                  "安装到当前用户目录，不需要管理员权限。安装后可从开始菜单启动。\n\n继续吗？";
            if (Msg(prompt, $"{AppName} 安装程序", 0x04) != 6 /* IDYES */) return 0;
        }

        Log($"install -> {InstallDir} (silent={silent}, existing={existing})");

        StopRunningApp();

        if (!silent) WriteProgress("正在释放文件…");
        Directory.CreateDirectory(InstallDir);
        ExtractPayload(InstallDir, silent);
        Log("payload extracted");

        // 快捷方式与卸载项属于"锦上添花"：受限环境（组策略/沙箱）里失败不应让安装失败
        TryStep("start menu shortcut", CreateStartMenuShortcut);
        TryStep("uninstall registry entry", RegisterUninstallEntry);

        if (!silent)
        {
            if (Msg($"安装完成。\n\n现在启动 {AppName} 吗？", $"{AppName} 安装程序", 0x04) == 6)
                StartApp();
        }

        return 0;
    }

    private static void TryStep(string name, Action action)
    {
        try
        {
            action();
            Log($"{name}: ok");
        }
        catch (Exception ex)
        {
            Log($"{name}: SKIPPED ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private static void ExtractPayload(string targetDir, bool silent)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("安装包损坏：缺少内嵌文件（payload.zip）");

        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("无法读取内嵌安装文件");

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        int total = archive.Entries.Count, done = 0;
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            if (!destination.StartsWith(Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                continue;   // 防 zip slip

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);

            if (!silent && ++done % 40 == 0) WriteProgress($"正在释放文件… {done * 100 / Math.Max(total, 1)}%");
        }
    }

    private static void CreateStartMenuShortcut()
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(TargetExe);
            link.SetWorkingDirectory(InstallDir);
            link.SetDescription("素材管理与 AI 分类工作台");
            link.SetIconLocation(TargetExe, 0);

            var file = (IPersistFile)link;
            file.Save(StartMenuLink, true);
        }
        catch
        {
            // 快捷方式失败不影响安装（用户仍可从安装目录启动）
        }
    }

    private static void RegisterUninstallEntry()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("DisplayIcon", TargetExe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", GetDirectorySizeKb(InstallDir), RegistryValueKind.DWord);
        key.SetValue("UninstallString", $"\"{Environment.ProcessPath}\" /uninstall");
        key.SetValue("QuietUninstallString", $"\"{Environment.ProcessPath}\" /S /uninstall");
    }

    private static int GetDirectorySizeKb(string dir)
    {
        try
        {
            var bytes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
            return (int)Math.Min(bytes / 1024, int.MaxValue);
        }
        catch
        {
            return 0;
        }
    }

    private static void StartApp()
    {
        try
        {
            Process.Start(new ProcessStartInfo(TargetExe) { WorkingDirectory = InstallDir, UseShellExecute = true });
        }
        catch { /* 启动失败时用户可手动点快捷方式 */ }
    }

    // =====================================================================
    //  卸载
    // =====================================================================

    private static int Uninstall(bool silent)
    {
        if (!Directory.Exists(InstallDir))
        {
            if (!silent) Msg("没有找到已安装的 STJ Media Viewer。", AppName, 0x40);
            return 0;
        }

        if (!silent && Msg($"确定卸载 {AppName} 吗？\n\n用户数据（索引库、缩略图缓存、设置）会保留在：\n" +
                           $"%LOCALAPPDATA%\\{AppId}\n\n如需彻底清除请手动删除该目录。",
                $"{AppName} 卸载程序", 0x04) != 6) return 0;

        StopRunningApp();

        try { File.Delete(StartMenuLink); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }

        try
        {
            Directory.Delete(InstallDir, recursive: true);
        }
        catch (Exception ex)
        {
            if (!silent) Msg($"删除安装目录失败：{ex.Message}\n请手动删除：{InstallDir}", AppName, 0x10);
            return 1;
        }

        if (!silent) Msg("已卸载。感谢使用。", AppName, 0x40);
        return 0;
    }

    // =====================================================================
    //  公共
    // =====================================================================

    private static void StopRunningApp()
    {
        foreach (var process in Process.GetProcessesByName(AppId).Concat(Process.GetProcessesByName("StjMediaViewer")))
        {
            try
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
            }
            catch { /* 进程可能已退出 */ }
        }
    }

    private static void WriteProgress(string text) => Console.WriteLine(text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static int Msg(string text, string caption, uint type) =>
        MessageBoxW(IntPtr.Zero, text, caption, type | 0x40000 /* MB_TOPMOST */);

    // ---- 开始菜单快捷方式（COM） ----

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}