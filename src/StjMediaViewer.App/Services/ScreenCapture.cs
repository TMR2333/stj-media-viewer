using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using StjMediaViewer.Core.Util;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace StjMediaViewer.App.Services;

/// <summary>
/// 应用内截图：把根视觉树渲染成 PNG。
///
/// 为什么不从外部抓屏：WinUI 3 的内容走 DirectComposition，PrintWindow 只能抓到黑图；
/// 屏幕截图又会被其它窗口遮挡、还会干扰用户。RenderTargetBitmap 是唯一确定性方案
/// （不含系统背板 Mica/Acrylic 的桌面部分，但应用自身的背景与卡片都在）。
///
/// 触发方式：启动前设环境变量 STJ_CAPTURE=&lt;png 路径&gt;，可选 STJ_CAPTURE_DELAY_MS（默认 2500）。
/// 截完会自动退出进程，便于脚本批量出图。
/// </summary>
public static class ScreenCapture
{
    public static string? RequestedPath => Environment.GetEnvironmentVariable("STJ_CAPTURE");

    public static int DelayMs =>
        int.TryParse(Environment.GetEnvironmentVariable("STJ_CAPTURE_DELAY_MS"), out var ms) ? Math.Clamp(ms, 200, 30000) : 2500;

    /// <summary>启动后调用：若有截图请求，延迟渲染并退出。</summary>
    public static async void ScheduleIfRequested(Window window)
    {
        var path = RequestedPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            await Task.Delay(DelayMs);
            var root = window.Content as FrameworkElement;
            if (root is null) { Log.Warn("[capture] 根元素为空"); Application.Current.Exit(); return; }

            var ok = await RenderAsync(root, path);
            Log.Info(ok ? $"[capture] 已保存 {path}" : $"[capture] 失败 {path}");
        }
        catch (Exception ex)
        {
            Log.Error("[capture] 截图异常", ex);
        }
        finally
        {
            Application.Current.Exit();
        }
    }

    public static async Task<bool> RenderAsync(UIElement element, string path)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(element);

        var width = rtb.PixelWidth;
        var height = rtb.PixelHeight;
        if (width <= 0 || height <= 0) return false;

        var pixels = await rtb.GetPixelsAsync();
        var bytes = new byte[pixels.Length];
        using (var reader = DataReader.FromBuffer(pixels))
        {
            reader.ReadBytes(bytes);
        }

        // RenderTargetBitmap 给的是预乘 alpha 的 BGRA；PNG 需要非预乘。
        // 为了截图稳定、也为了不让低透明度区域（背景光场）在查看器里被错误放大，
        // 这里直接合成到不透明底色上（可用 STJ_CAPTURE_BG=RRGGBB 覆盖）。
        var bg = ParseBackground();
        for (int i = 0; i + 3 < bytes.Length; i += 4)
        {
            int a = bytes[i + 3];
            if (a == 255) continue;
            if (a == 0)
            {
                bytes[i] = bg.B; bytes[i + 1] = bg.G; bytes[i + 2] = bg.R; bytes[i + 3] = 255;
                continue;
            }
            // 预乘 → 非预乘 → 合成到底色
            int b = Math.Min(255, bytes[i] * 255 / a);
            int g = Math.Min(255, bytes[i + 1] * 255 / a);
            int r = Math.Min(255, bytes[i + 2] * 255 / a);
            bytes[i] = (byte)((b * a + bg.B * (255 - a)) / 255);
            bytes[i + 1] = (byte)((g * a + bg.G * (255 - a)) / 255);
            bytes[i + 2] = (byte)((r * a + bg.R * (255 - a)) / 255);
            bytes[i + 3] = 255;
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var folder = await StorageFolder.GetFolderFromPathAsync(string.IsNullOrEmpty(dir) ? Path.GetTempPath() : dir);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)width, (uint)height, 96, 96, bytes);
        await encoder.FlushAsync();
        return true;
    }

    private static (byte R, byte G, byte B) ParseBackground()
    {
        var raw = Environment.GetEnvironmentVariable("STJ_CAPTURE_BG");
        if (!string.IsNullOrWhiteSpace(raw) && raw.Length >= 6 &&
            byte.TryParse(raw[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(raw.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(raw.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return (r, g, b);
        return (26, 26, 28);
    }
}