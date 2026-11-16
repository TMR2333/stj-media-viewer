using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace StjMediaViewer.App.ViewModels;

/// <summary>
/// 把一条素材变成可绑定的 ImageSource。
/// 顺序：缩略图服务（有磁盘缓存，最快）→ 原文件直读（真实素材库时保证清晰度）。
/// 两条路都失败时返回 null，由调用方显示占位。
/// </summary>
public static class ImageSourceLoader
{
    public static async Task<ImageSource?> LoadAsync(MediaAsset asset, int size, CancellationToken ct = default)
    {
        try
        {
            var fromService = await AppServices.Thumbs.LoadAsync(asset, size, ct);
            if (fromService is not null) return fromService;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"缩略图服务取图失败 {asset.FileName}: {ex.Message}");
        }

        ct.ThrowIfCancellationRequested();
        return await LoadFromFileAsync(asset.FullPath, size, ct);
    }

    public static async Task<ImageSource?> LoadFromFileAsync(string path, int size, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            ct.ThrowIfCancellationRequested();
            // 文件 URI 加载：BitmapImage 自己管理文件句柄生命周期，
            // 不会出现"流已释放但解码还没完成"导致的原生崩溃。
            return new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = Math.Clamp(size, 64, 4096),
                UriSource = new Uri(Path.GetFullPath(path)),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"直接读取图片失败 {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>视频首帧：优先走缩略图服务（内部会调用解码/ffmpeg），失败返回 null。</summary>
    public static async Task<ImageSource?> LoadVideoPosterAsync(MediaAsset asset, int size, CancellationToken ct = default)
    {
        try
        {
            var thumb = await AppServices.Thumbs.LoadAsync(asset, size, ct);
            if (thumb is not null) return thumb;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"视频首帧加载失败 {asset.FileName}: {ex.Message}");
        }
        return await LoadFromFileAsync(asset.FullPath, size, ct);
    }

    /// <summary>音频封面：没有解码服务时用系统缩略图兜底。</summary>
    public static async Task<ImageSource?> LoadAudioCoverAsync(MediaAsset asset, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(asset.FullPath) || !File.Exists(asset.FullPath)) return null;
            var file = await StorageFile.GetFileFromPathAsync(asset.FullPath);
            var thumb = await file.GetThumbnailAsync(ThumbnailMode.MusicView, 512);
            if (thumb is null || thumb.Size == 0) { thumb?.Dispose(); return null; }

            var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
            await bitmap.SetSourceAsync(thumb);
            // 缩略图流必须活到解码完成：ImageOpened / ImageFailed 之后再释放。
            void Release() { try { thumb.Dispose(); } catch { } }
            bitmap.ImageOpened += (_, _) => Release();
            bitmap.ImageFailed += (_, _) => Release();
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Warn($"音频封面加载失败 {asset.FileName}: {ex.Message}");
            return null;
        }
    }
}