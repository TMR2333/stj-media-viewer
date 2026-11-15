using System.Runtime.InteropServices;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Devices;

/// <summary>
/// 外部存储设备枚举：U 盘 / SD 卡 / 相机 / 移动硬盘。
///
/// 实现分两层：
/// 1) 快速层（`System.IO.DriveInfo`，毫秒级）：盘符、卷标、文件系统、总容量/剩余、驱动器类型；
/// 2) 探测层（Win32 `IOCTL_STORAGE_QUERY_PROPERTY`）：总线类型（USB / SD / MMC / NVMe / SATA），
///    用来把"固定盘但从 USB 接进来"的移动硬盘也识别成外部设备；失败不影响主流程。
/// 另外扫描根目录下的 `DCIM` 相机目录结构，直接给出"相机卡"线索。
/// </summary>
public sealed class StorageDeviceService : IStorageDeviceService
{
    private static readonly HashSet<string> IgnoredTopDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN", "System Volume Information", "RECYCLER", "Windows", "Program Files",
        "Program Files (x86)", "ProgramData", "Users", "PerfLogs", "Recovery", "$WinREAgent",
    };

    public IReadOnlyList<StorageDevice> GetDevices() => Enumerate(false);

    public async Task<IReadOnlyList<StorageDevice>> ProbeDevicesAsync(CancellationToken ct = default)
    {
        var quick = Enumerate(false);
        // 总线探测会打开卷句柄，放到线程池里批量做，避免阻塞 UI
        var probed = await Task.Run(() =>
        {
            var list = new List<StorageDevice>(quick.Count);
            foreach (var dev in quick)
            {
                ct.ThrowIfCancellationRequested();
                list.Add(ProbeBusType(dev));
            }
            return list;
        }, ct).ConfigureAwait(false);

        // DCIM 线索也放到线程池里补
        return await Task.Run(() =>
        {
            foreach (var dev in probed)
            {
                ct.ThrowIfCancellationRequested();
                if (!dev.IsReady || dev.DriveType == "光驱") continue;
                DetectCamera(dev);
            }
            return (IReadOnlyList<StorageDevice>)probed
                .OrderByDescending(d => d.IsExternal)
                .ThenBy(d => d.RootPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct).ConfigureAwait(false);
    }

    public StorageDevice? GetDeviceForPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return null;
            return GetDevices().FirstOrDefault(d =>
                string.Equals(Path.GetFullPath(d.RootPath).TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Warn($"[devices] 路径设备识别失败 {path}: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ 快速枚举

    private static List<StorageDevice> Enumerate(bool includeNetwork)
    {
        var list = new List<StorageDevice>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception ex)
        {
            Log.Warn($"[devices] 枚举驱动器失败: {ex.Message}");
            return list;
        }

        foreach (var di in drives)
        {
            try
            {
                var type = di.DriveType;
                if (type is DriveType.NoRootDirectory or DriveType.Unknown) continue;
                if (!includeNetwork && type is DriveType.Network) continue;

                var isReady = di.IsReady;
                var device = new StorageDevice
                {
                    RootPath = di.Name,
                    DriveType = DriveTypeText(type),
                    IsReady = isReady,
                    IsExternal = type is DriveType.Removable,
                };

                if (isReady)
                {
                    try { device.VolumeLabel = di.VolumeLabel; } catch { /* 某些卷读不到卷标 */ }
                    try { device.FileSystem = di.DriveFormat; } catch { }
                    try { device.TotalBytes = di.TotalSize; device.FreeBytes = di.AvailableFreeSpace; } catch { }
                }

                // 非可移动盘：判断是不是"外接"（USB/SD 读卡器常被报成 Fixed）
                // 这里留到探测层，先按类型给出一个保守判断。
                if (!device.IsExternal && type == DriveType.Fixed)
                {
                    // 系统盘（Windows 所在卷）几乎肯定是内置，跳过不必要的探测标记
                    var isSystem = string.Equals(
                        di.Name.TrimEnd('\\'), Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase);
                    if (isSystem) device.BusType = "内置系统盘";
                }

                list.Add(device);
            }
            catch (Exception ex)
            {
                Log.Warn($"[devices] 读取驱动器失败 {di.Name}: {ex.Message}");
            }
        }
        return list;
    }

    private static string DriveTypeText(DriveType type) => type switch
    {
        DriveType.Removable => "可移动",
        DriveType.Fixed => "固定",
        DriveType.Network => "网络",
        DriveType.CDRom => "光驱",
        DriveType.Ram => "内存盘",
        _ => "未知",
    };

    // ------------------------------------------------------------------ 总线探测

    private static StorageDevice ProbeBusType(StorageDevice dev)
    {
        if (!dev.IsReady) return dev;
        try
        {
            var bus = QueryBusType(dev.RootPath);
            if (bus is null) return dev;
            dev.BusType = bus.Value.Text;
            if (bus.Value.IsExternalBus) dev.IsExternal = true;
        }
        catch (Exception ex)
        {
            Log.Info($"[devices] 总线探测失败 {dev.RootPath}: {ex.Message}");
        }
        return dev;
    }

    private static (string Text, bool IsExternalBus)? QueryBusType(string rootPath)
    {
        var letter = rootPath.TrimEnd('\\', '/');
        if (letter.Length < 2) return null;
        var volume = @"\\.\" + letter;   // \\.\E:

        using var handle = CreateFile(volume, 0, FileShare.ReadWrite, IntPtr.Zero,
            OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = 0,      // StorageDeviceProperty
            QueryType = 0,       // PropertyStandardQuery
        };
        int querySize = Marshal.SizeOf<STORAGE_PROPERTY_QUERY>();
        int bufferSize = 1024;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            var queryPtr = Marshal.AllocHGlobal(querySize);
            try
            {
                Marshal.StructureToPtr(query, queryPtr, false);
                if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
                        queryPtr, querySize, buffer, bufferSize, out int returned, IntPtr.Zero) || returned < 36)
                    return null;

                // STORAGE_DEVICE_DESCRIPTOR：BusType 在偏移 28（Version,Size,DeviceType,DeviceTypeModifier,
                // RemovableMedia,CommandQueueing,4×offset, BusType）
                int busTypeValue = Marshal.ReadInt32(buffer, 28);
                var text = BusTypeText(busTypeValue);
                bool external = busTypeValue is StorageBusUsb or StorageBusSd or StorageBusMmc or StorageBus1394;
                return (text, external);
            }
            finally { Marshal.FreeHGlobal(queryPtr); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private const int StorageBusUsb = 7;
    private const int StorageBusSd = 12;
    private const int StorageBusMmc = 13;
    private const int StorageBus1394 = 4;

    private static string BusTypeText(int v) => v switch
    {
        0 => "未知", 1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 5 => "SSA",
        6 => "Fibre", 7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA",
        12 => "SD", 13 => "MMC", 14 => "虚拟盘", 15 => "文件虚拟盘", 16 => "存储空间",
        17 => "NVMe", 18 => "SCM", 19 => "UFS", _ => $"总线 {v}",
    };

    // ------------------------------------------------------------------ 相机卡线索

    private static void DetectCamera(StorageDevice dev)
    {
        try
        {
            var dcim = Path.Combine(dev.RootPath, "DCIM");
            if (!Directory.Exists(dcim)) return;

            string? firstWithFiles = null;
            string? firstSubName = null;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dcim).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                {
                    if (!Directory.EnumerateFiles(sub).Any()) continue;
                    firstWithFiles ??= sub;
                    firstSubName ??= Path.GetFileName(sub);
                    break;
                }
                if (firstWithFiles is null)
                {
                    // DCIM 下直接放文件的情况
                    if (Directory.EnumerateFiles(dcim).Any()) firstWithFiles = dcim;
                }
            }
            catch (Exception ex)
            {
                Log.Info($"[devices] DCIM 枚举失败 {dcim}: {ex.Message}");
            }

            if (firstWithFiles is null) return;
            dev.CameraSourcePath = firstWithFiles;
            dev.CameraHint = firstSubName is null ? "DCIM" : $"DCIM/{firstSubName}";
        }
        catch (Exception ex)
        {
            Log.Info($"[devices] 相机目录探测失败 {dev.RootPath}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ P/Invoke

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle device, uint ioControlCode,
        IntPtr inBuffer, int inBufferSize, IntPtr outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);
}