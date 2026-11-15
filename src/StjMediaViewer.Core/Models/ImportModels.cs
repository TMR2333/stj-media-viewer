namespace StjMediaViewer.Core.Models;

// =====================================================================================
//  外部存储设备
// =====================================================================================

/// <summary>一个可导入的存储设备（U 盘 / SD 卡 / 相机 / 移动硬盘，也兼容普通目录）。</summary>
public sealed class StorageDevice
{
    /// <summary>根路径，如 "E:\"。</summary>
    public string RootPath { get; set; } = "";
    /// <summary>卷标，如 "EOS_DIGITAL"。</summary>
    public string? VolumeLabel { get; set; }
    /// <summary>驱动器类型：可移动 / 固定 / 网络 / 光驱 …</summary>
    public string DriveType { get; set; } = "";
    /// <summary>是否判定为"外部设备"（可移动盘，或固定盘但总线为 USB/SD/MMC）。</summary>
    public bool IsExternal { get; set; }
    public string? FileSystem { get; set; }
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    /// <summary>总线类型（USB / SD / MMC / NVMe / SATA …），拿不到为 null。</summary>
    public string? BusType { get; set; }
    /// <summary>相机卡特征目录（DCIM 等）的绝对路径；没有则为 null。</summary>
    public string? CameraSourcePath { get; set; }
    /// <summary>相机线索，如 "DCIM/100CANON"。</summary>
    public string? CameraHint { get; set; }
    /// <summary>系统是否就绪可读（未插入介质的光驱等为 false）。</summary>
    public bool IsReady { get; set; }

    public double UsedRatio => TotalBytes > 0 ? 1.0 - (double)FreeBytes / TotalBytes : 0;
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(VolumeLabel) ? $"{VolumeLabel} ({RootPath.TrimEnd('\\')})" : RootPath;
}

// =====================================================================================
//  导入（外部设备 → 媒体库）
// =====================================================================================

/// <summary>一个导入分组：同一张照片的 JPG + RAW（+ 边车）构成一组。</summary>
public sealed class ImportGroup
{
    /// <summary>稳定分组键（目录 + 去扩展名文件名，小写）。</summary>
    public string GroupKey { get; set; } = "";
    /// <summary>预览用 JPEG（没有则为 null）。</summary>
    public string? JpegPath { get; set; }
    /// <summary>RAW 原件（没有则为 null）。</summary>
    public string? RawPath { get; set; }
    /// <summary>同组的其它文件（连拍备份、非标准命名等）。</summary>
    public List<string> OtherPaths { get; set; } = new();
    /// <summary>同组边车（.xmp/.dop/.cos…）。</summary>
    public List<string> SidecarPaths { get; set; } = new();
    public int Kind { get; set; }                 // MediaKind 的整型值，避免 Model 层依赖枚举
    public DateTimeOffset? CapturedAt { get; set; }
    public string? CameraHint { get; set; }
    public bool IsSelected { get; set; } = true;

    /// <summary>本组需要拷贝的全部文件（JPG + RAW + 其它 + 边车）。</summary>
    public IEnumerable<string> AllFiles()
    {
        if (!string.IsNullOrEmpty(JpegPath)) yield return JpegPath!;
        if (!string.IsNullOrEmpty(RawPath)) yield return RawPath!;
        foreach (var p in OtherPaths) yield return p;
        foreach (var p in SidecarPaths) yield return p;
    }

    public int FileCount => AllFiles().Count();
    public string DisplayName => Path.GetFileNameWithoutExtension(JpegPath ?? RawPath ?? GroupKey);
    public bool HasRawJpegPair => !string.IsNullOrEmpty(JpegPath) && !string.IsNullOrEmpty(RawPath);
}

public sealed class ImportOptions
{
    /// <summary>目标根目录（通常是媒体库根）。</summary>
    public string DestinationRoot { get; set; } = "";
    /// <summary>子目录模板，如 "{yyyy}/{yyyy-MM-dd}"；空 = 直接放根目录。</summary>
    public string? SubfolderTemplate { get; set; } = "{yyyy}/{yyyy-MM-dd}";
    /// <summary>true = 复制（保留源文件）；false = 移动。</summary>
    public bool CopyInsteadOfMove { get; set; } = true;
    public bool IncludeSidecars { get; set; } = true;
    /// <summary>同名且大小一致的文件跳过（不覆盖）。</summary>
    public bool SkipExisting { get; set; } = true;
    /// <summary>拷贝完成后逐文件做哈希核验。</summary>
    public bool VerifyAfterCopy { get; set; } = true;
    /// <summary>核验强度：true = SHA-256 完整核验；false = 大小 + 首尾指纹快速核验。</summary>
    public bool StrictVerify { get; set; } = true;
    /// <summary>导入后把这些文件登记进媒体库（交给 LibraryService 扫描目标目录）。</summary>
    public bool IndexAfterCopy { get; set; } = true;
    public string? LibraryId { get; set; }
    /// <summary>只导入这个时间之后拍摄的（null = 不限）。</summary>
    public DateTimeOffset? CapturedAfter { get; set; }
}

public sealed class ImportPlan
{
    public string SourceRoot { get; set; } = "";
    public ImportOptions Options { get; set; } = new();
    public List<ImportGroup> Groups { get; set; } = new();
    public int SelectedGroupCount => Groups.Count(g => g.IsSelected);
    public int TotalFiles => Groups.Where(g => g.IsSelected).Sum(g => g.FileCount);
    public long TotalBytes { get; set; }
    public List<string> Warnings { get; set; } = new();
    /// <summary>源目录里发现但因为"已有同名同大小文件"等原因会跳过的组数（预估）。</summary>
    public int EstimatedDuplicates { get; set; }
}

public sealed class ImportItemResult
{
    public string SourcePath { get; set; } = "";
    public string? DestinationPath { get; set; }
    /// <summary>Copied / Skipped / Failed</summary>
    public string Status { get; set; } = "Pending";
    public long SizeBytes { get; set; }
    /// <summary>核验结果：Verified / Mismatch / NotVerified</summary>
    public string VerifyStatus { get; set; } = "NotVerified";
    public string? SourceHash { get; set; }
    public string? DestinationHash { get; set; }
    public string? Error { get; set; }
}

public sealed class ImportReport
{
    public int GroupsTotal { get; set; }
    public int GroupsImported { get; set; }
    public int GroupsSkipped { get; set; }
    public int GroupsFailed { get; set; }
    public int FilesCopied { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesFailed { get; set; }
    public int FilesVerified { get; set; }
    public int FilesCorrupted { get; set; }
    public int FilesMissing { get; set; }
    public long BytesCopied { get; set; }
    public TimeSpan Elapsed { get; set; }
    public List<ImportItemResult> Items { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public bool Success => FilesFailed == 0 && FilesCorrupted == 0 && FilesMissing == 0;
    public bool AllVerified => FilesVerified > 0 && FilesCorrupted == 0 && FilesMissing == 0;
    public string Summary =>
        $"组 {GroupsImported}/{GroupsTotal}，文件 复制 {FilesCopied} / 跳过 {FilesSkipped} / 失败 {FilesFailed}；" +
        $"核验 {FilesVerified} 通过，损坏 {FilesCorrupted}，缺失 {FilesMissing}";
}

public sealed class ImportProgress
{
    public int GroupsProcessed { get; set; }
    public int GroupsTotal { get; set; }
    public int FilesProcessed { get; set; }
    public int FilesTotal { get; set; }
    public long BytesProcessed { get; set; }
    public long BytesTotal { get; set; }
    public string? CurrentFile { get; set; }
    public string? Message { get; set; }
    public bool Done { get; set; }
    public double Percent => BytesTotal > 0
        ? Math.Round(100.0 * BytesProcessed / BytesTotal, 1)
        : (FilesTotal > 0 ? Math.Round(100.0 * FilesProcessed / FilesTotal, 1) : 0);
}

// =====================================================================================
//  核验（导出/导入后的完整性检查）
// =====================================================================================

/// <summary>一条"源文件 → 目标文件"的核验对照。</summary>
public sealed record VerifyPair(string SourcePath, string DestinationPath);

public sealed class VerifyItemResult
{
    public string SourcePath { get; set; } = "";
    public string? DestinationPath { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>Passed / Missing / Mismatch / Error</summary>
    public string Status { get; set; } = "Passed";
    public string? SourceHash { get; set; }
    public string? DestinationHash { get; set; }
    public string? Error { get; set; }
}

public sealed class VerifyReport
{
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Missing { get; set; }
    public int Corrupted { get; set; }
    public int Errors { get; set; }
    /// <summary>源侧存在但目标侧找不到对应文件（用于"漏导"检查）。</summary>
    public int SourceOnly { get; set; }
    /// <summary>目标侧多出来的文件（模板改名等不算错，仅提示）。</summary>
    public int DestinationOnly { get; set; }
    public long BytesVerified { get; set; }
    public TimeSpan Elapsed { get; set; }
    public bool Strict { get; set; }
    public List<VerifyItemResult> Items { get; set; } = new();
    public bool Success => Missing == 0 && Corrupted == 0 && Errors == 0;
    public string Summary =>
        $"核验 {Total} 个：通过 {Passed}，缺失 {Missing}，损坏 {Corrupted}，错误 {Errors}" +
        (SourceOnly > 0 ? $"，源多出 {SourceOnly}" : "") +
        (DestinationOnly > 0 ? $"，目标多出 {DestinationOnly}" : "");
}