using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace StjMediaViewer.Installer;

/// <summary>
/// STJ Media Viewer 分发包（一个 exe，两种用法）：
///
///   1) 安装包（默认）：带界面，可选择安装目录、是否建桌面快捷方式
///        STJMediaViewer-Setup.exe               交互式安装
///        STJMediaViewer-Setup.exe /S            静默安装到默认目录
///        STJMediaViewer-Setup.exe /S /D=D:\App  静默安装到指定目录
///        STJMediaViewer-Setup.exe /uninstall    卸载（加 /S 静默）
///
///   2) 便携版：解压到用户目录并直接启动，不写注册表、不建快捷方式
///        STJMediaViewer-Portable.exe            解压到 %LOCALAPPDATA%\StjMediaViewerPortable 并启动
///        STJMediaViewer-Portable.exe /D=<dir>   解压到指定目录并启动
///
/// 权限策略：默认 asInvoker（装用户目录不需要管理员）；当目标目录不可写
/// （例如 C:\Program Files）时，安装器会主动请求 UAC 提升，提权后以机器级
/// 作用域写「所有用户」开始菜单快捷方式与 HKLM 卸载项。
/// </summary>
internal static class Program
{
    private const string AppName = "STJ Media Viewer";
    private const string AppId = "StjMediaViewer";
    private const string Version = "1.7.1";
    private const string Publisher = "Stjarna Perspective";
    private const string ExeName = "StjMediaViewer.exe";
    private const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId;

    /// <summary>true = 以管理员身份运行（或提权后重启的本进程），使用机器级作用域。</summary>
    private static bool _machineWide;

    private static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", AppId);

    private static string DefaultPortableDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StjMediaViewerPortable");

    private static string StartMenuDirectory => _machineWide
        ? Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        : Environment.GetFolderPath(Environment.SpecialFolder.Programs);

    private static string DesktopDirectory => _machineWide
        ? Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    private static string StartMenuLink => Path.Combine(StartMenuDirectory, $"{AppName}.lnk");

    private static readonly string[] LogPaths =
    {
        Path.Combine(AppContext.BaseDirectory, "stj-setup.log"),
        Path.Combine(Path.GetTempPath(), "stj-setup.log"),
    };

    [STAThread]
    private static int Main(string[] args)
    {
        _machineWide = IsAdministrator() || HasArg(args, "/elevated");
        Log($"start args=[{string.Join(" | ", args)}] exe={Environment.ProcessPath} " +
            $"admin={_machineWide} user={Environment.UserName}");

        var silent = HasArg(args, "/S") || HasArg(args, "/silent");
        var uninstall = HasArg(args, "/uninstall");
        var explicitDir = ArgValue(args, "/D");
        var portable = HasArg(args, "/portable") ||
                       (Environment.ProcessPath?.Contains("portable", StringComparison.OrdinalIgnoreCase) ?? false);

        try
        {
            if (portable)
                return RunPortable(silent, explicitDir ?? DefaultPortableDir);

            if (uninstall)
                return Uninstall(silent, explicitDir);

            if (silent)
                return Install(silent, explicitDir ?? DefaultInstallDir, desktopShortcut: false);

            // 交互式：WinForms 小窗口，可选择安装目录。
            // 受限环境（组策略/沙箱）里 EnableVisualStyles 可能因临时目录不可写而抛异常 —— 降级继续，
            // 界面只是没有视觉样式，功能不受影响。
            try
            {
                ApplicationConfiguration.Initialize();
            }
            catch (Exception ex)
            {
                Log($"EnableVisualStyles skipped: {ex.GetType().Name}: {ex.Message}");
                try { Application.SetCompatibleTextRenderingDefault(false); } catch { }
            }

            using var form = new InstallerForm(explicitDir ?? DefaultInstallDir, _machineWide);
            if (form.ShowDialog() != DialogResult.OK) return 0;

            var dir = form.SelectedDirectory;
            if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any() &&
                !Question($"目录已存在且不为空：\n{dir}\n\n继续会覆盖同名文件，确定吗？"))
                return 0;

            return Install(silent: false, dir, form.CreateDesktopShortcut);
        }
        catch (Exception ex)
        {
            Log($"ERROR {ex}");
            if (!silent) Error($"安装失败：{ex.Message}");
            return 1;
        }
    }

    // =====================================================================
    //  安装
    // =====================================================================

    private static int Install(bool silent, string targetDir, bool desktopShortcut)
    {
        // 自定义目录统一补上自己的子目录（`D:\Apps` → `D:\Apps\STJ Media Viewer`），
        // 不把一堆程序文件散落在用户选的目录里。默认目录（已经是自己的目录）保持不变。
        targetDir = InstallerForm.NormalizeInstallDirectory(targetDir);
        Log($"install -> {targetDir} (silent={silent}, desktop={desktopShortcut}, admin={_machineWide})");

        // 目标目录不可写（Program Files / 受策略保护的位置）时先请求提权，
        // 不能干等着写文件失败再报错。
        var elevated = EnsureWritableOrElevate(targetDir, silent, out var exitCode);
        if (!elevated) return exitCode;

        StopRunningApp();
        Directory.CreateDirectory(targetDir);
        ExtractPayload(targetDir, silent);
        Log("payload extracted");

        TryStep("start menu shortcut", () => CreateShortcut(StartMenuLink, TargetExeIn(targetDir)));
        if (desktopShortcut)
            TryStep("desktop shortcut", () =>
                CreateShortcut(Path.Combine(DesktopDirectory, $"{AppName}.lnk"), TargetExeIn(targetDir)));
        TryStep("uninstall registry entry", () => RegisterUninstallEntry(targetDir));

        if (!silent && Question($"安装完成：{targetDir}\n\n现在启动 {AppName} 吗？"))
            StartApp(targetDir);

        return 0;
    }

    /// <summary>检查目标目录是否可写；不可写则按需请求 UAC 提权后重启安装器。</summary>
    private static bool EnsureWritableOrElevate(string targetDir, bool silent, out int exitCode)
    {
        exitCode = 0;
        if (IsDirectoryWritable(targetDir)) return true;
        if (_machineWide)
        {
            // 已经是管理员还不让写：目标位置被 ACL/策略锁死，换目录吧。
            Log($"not writable even elevated: {targetDir}");
            if (!silent) Error($"没有写入权限：{targetDir}\n\n请换一个可写目录（例如“文档”或桌面）。");
            exitCode = 1;
            return false;
        }

        if (!silent &&
            !Question($"这个目录需要管理员权限才能写入：\n{targetDir}\n\n" +
                      "要以管理员身份继续安装吗？（会弹出 UAC 确认；取消则换一个目录）"))
        {
            exitCode = 0;   // 用户选择不装，正常退出
            return false;
        }

        Log("requesting elevation");
        exitCode = ElevateAndRelaunch($"/S /elevated /D=\"{targetDir}\"");
        return false;
    }

    private static int ElevateAndRelaunch(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(Environment.ProcessPath ?? "")
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            Process.Start(startInfo);
            Log("elevated instance started");
            return 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log("elevation cancelled by user");
            Info("已取消管理员授权，安装未进行。\n可以换一个不需要管理员权限的目录（例如“文档”或桌面）。");
            return 2;
        }
        catch (Exception ex)
        {
            Log($"elevation failed: {ex.Message}");
            Error($"请求管理员权限失败：{ex.Message}\n\n请换一个可写目录，或右键安装包选择「以管理员身份运行」。");
            return 1;
        }
    }

    private static bool IsDirectoryWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".stj-write-probe-{Guid.NewGuid():N}");
            using (var stream = File.Create(probe)) { stream.WriteByte(0); }
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            Log($"write probe failed: {dir}: {ex.GetType().Name}");
            return false;
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string TargetExeIn(string dir) => Path.Combine(dir, ExeName);

    // =====================================================================
    //  便携版：解压到用户目录 + 启动
    // =====================================================================

    private static int RunPortable(bool silent, string targetDir)
    {
        Log($"portable -> {targetDir}");
        var exe = TargetExeIn(targetDir);
        var marker = Path.Combine(targetDir, ".portable-version");

        var upToDate = File.Exists(exe) && File.Exists(marker) &&
                       File.ReadAllText(marker).Trim() == Version;

        if (!upToDate)
        {
            if (!EnsureWritableOrElevate(targetDir, silent, out var exitCode)) return exitCode;
            StopRunningApp();
            Directory.CreateDirectory(targetDir);
            ExtractPayload(targetDir, silent);
            try { File.WriteAllText(marker, Version); } catch { }
            Log("portable payload extracted");
        }

        if (!File.Exists(exe))
        {
            if (!silent) Error("便携版解压失败，找不到主程序。");
            return 1;
        }

        StartApp(targetDir);
        return 0;
    }

    // =====================================================================
    //  载荷
    // =====================================================================

    private static void ExtractPayload(string targetDir, bool silent)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("安装包损坏：缺少内嵌文件（payload.zip）");

        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("无法读取内嵌安装文件");

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        int total = Math.Max(archive.Entries.Count, 1), done = 0;
        var root = Path.GetFullPath(targetDir);

        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;   // 防 zip slip

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);

            done++;
            if (!silent && done % 60 == 0) Log($"释放文件… {done * 100 / total}%");
        }
    }

    // =====================================================================
    //  快捷方式 / 卸载项
    // =====================================================================

    private static void CreateShortcut(string linkPath, string targetExe)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(targetExe);
        link.SetWorkingDirectory(Path.GetDirectoryName(targetExe) ?? "");
        link.SetDescription("素材管理与 AI 分类工作台");
        link.SetIconLocation(targetExe, 0);

        var file = (IPersistFile)link;
        file.Save(linkPath, true);
    }

    private static void RegisterUninstallEntry(string installDir)
    {
        var root = _machineWide ? Registry.LocalMachine : Registry.CurrentUser;
        using var key = root.CreateSubKey(UninstallSubKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("DisplayIcon", TargetExeIn(installDir));
        key.SetValue("InstallLocation", installDir);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("UninstallString", $"\"{Environment.ProcessPath}\" /uninstall");
        key.SetValue("QuietUninstallString", $"\"{Environment.ProcessPath}\" /S /uninstall");
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

    // =====================================================================
    //  卸载
    // =====================================================================

    private static string ResolveUninstallDirectory(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir);
            // 用户可能传的是"上级目录"（安装时自动建了子目录），优先删真正的安装目录
            var child = Path.Combine(full, InstallerForm.AppDirectoryName);
            if (Directory.Exists(child) && File.Exists(Path.Combine(child, ExeName))) return child;
            var legacyChild = Path.Combine(full, AppId);
            if (Directory.Exists(legacyChild) && File.Exists(Path.Combine(legacyChild, ExeName))) return legacyChild;
            return full;
        }
        catch
        {
            return dir;
        }
    }

    private static int Uninstall(bool silent, string? explicitDir)
    {
        var installDir = ResolveUninstallDirectory(explicitDir ?? FindInstalledDirectory() ?? DefaultInstallDir);

        if (!Directory.Exists(installDir))
        {
            if (!silent) Info("没有找到已安装的 STJ Media Viewer。");
            return 0;
        }

        if (!silent && !Question($"确定卸载 {AppName} 吗？\n\n安装位置：{installDir}\n\n" +
                                 "用户数据（索引库、缩略图缓存、设置）会保留在 %LOCALAPPDATA%\\StjMediaViewer。"))
            return 0;

        StopRunningApp();

        // 用户级 / 机器级两套快捷方式与卸载项都清掉
        foreach (var dir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                 })
        {
            try { File.Delete(Path.Combine(dir, $"{AppName}.lnk")); } catch { }
        }
        foreach (var dir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                 })
        {
            try { File.Delete(Path.Combine(dir, $"{AppName}.lnk")); } catch { }
        }
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try { root.DeleteSubKeyTree(UninstallSubKey, throwOnMissingSubKey: false); } catch { }
        }

        // 目录可能因权限删不掉：能删则删，删不掉就提示（不静默失败）
        var removed = TryDeleteDirectory(installDir);
        if (!removed)
        {
            Log($"directory delete failed: {installDir}");
            if (!_machineWide && IsAdministrator() == false)
            {
                if (!silent &&
                    Question($"安装目录需要管理员权限才能删除：\n{installDir}\n\n以管理员身份重新卸载吗？"))
                    return ElevateAndRelaunch($"/S /elevated /uninstall /D=\"{installDir}\"");
            }

            if (!silent) Error($"删除安装目录失败，请手动删除：\n{installDir}");
            return 1;
        }

        if (!silent) Info("已卸载。感谢使用。");
        return 0;
    }

    private static bool TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(dir, recursive: true);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static string? FindInstalledDirectory()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(UninstallSubKey);
                if (key?.GetValue("InstallLocation") is string location && location.Length > 0)
                    return location;
            }
            catch { }
        }
        return null;
    }

    // =====================================================================
    //  进程
    // =====================================================================

    private static void StopRunningApp()
    {
        foreach (var name in new[] { AppId, "StjMediaViewer" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }
    }

    private static void StartApp(string dir)
    {
        try
        {
            Process.Start(new ProcessStartInfo(TargetExeIn(dir))
            {
                WorkingDirectory = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log($"start app failed: {ex.Message}");
            if (Environment.UserInteractive) Error($"启动失败：{ex.Message}");
        }
    }

    // =====================================================================
    //  参数与提示
    // =====================================================================

    private static bool HasArg(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? ArgValue(string[] args, string name)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return arg[(name.Length + 1)..].Trim('"');
            if (arg.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                return arg[(name.Length + 1)..].Trim('"');
        }
        return null;
    }

    private static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
        foreach (var path in LogPaths)
        {
            try { File.AppendAllText(path, line); return; }
            catch { }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static void Info(string text) => MessageBoxW(IntPtr.Zero, text, AppName, 0x40 | 0x40000);

    private static void Error(string text) => MessageBoxW(IntPtr.Zero, text, AppName, 0x10 | 0x40000);

    private static bool Question(string text) =>
        MessageBoxW(IntPtr.Zero, text, AppName, 0x04 | 0x40000) == 6;

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