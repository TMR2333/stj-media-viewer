using System.Diagnostics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

// WinRT 解码 / 编码可行性探针：验证在普通控制台进程里
// StorageFile.GetFileFromPathAsync + BitmapDecoder/Encoder 真能跑出像素。
// 用法： StjMediaViewer.MediaSmoke raw [file-or-dir ...]

internal static class WinRtProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.WriteLine($"[raw] apartment={Thread.CurrentThread.GetApartmentState()} runtime={Environment.Version}");
        var files = Collect(args);
        if (files.Count == 0)
        {
            Console.WriteLine("[raw] no input files");
            return 2;
        }

        int ok = 0, fail = 0;
        foreach (var f in files)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(f);
                using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);

                // 1) 不带 transform 解码，拿原始尺寸
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var raw = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Ignore,
                    new BitmapTransform(),
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);
                byte[] rawPx = raw.DetachPixelData();

                // 2) 缩放解码（缩略图路径）
                uint ow = decoder.OrientedPixelWidth, oh = decoder.OrientedPixelHeight;
                double scale = Math.Min(320.0 / Math.Max(1u, ow), 320.0 / Math.Max(1u, oh));
                if (scale > 1.0) scale = 1.0;
                var t = new BitmapTransform
                {
                    ScaledWidth = (uint)Math.Max(1, Math.Round(ow * scale)),
                    ScaledHeight = (uint)Math.Max(1, Math.Round(oh * scale)),
                    InterpolationMode = BitmapInterpolationMode.Fant,
                };
                var scaled = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Ignore,
                    t,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);
                byte[] scaledPx = scaled.DetachPixelData();

                // 3) 编码回 JPEG（缩略图落盘路径）
                byte[] jpeg = await EncodeJpegAsync(scaledPx, t.ScaledWidth, t.ScaledHeight, 85);

                // 像素校验：非全 0 / 非全相同
                int distinct = DistinctSample(rawPx, out double mean);
                sw.Stop();
                Console.WriteLine(
                    $"[raw] OK  {Path.GetFileName(f),-44} {decoder.PixelWidth}x{decoder.PixelHeight} " +
                    $"oriented={ow}x{oh} rawBytes={rawPx.Length} scaled={t.ScaledWidth}x{t.ScaledHeight} " +
                    $"jpeg={jpeg.Length}B mean={mean:0.0} distinct={distinct} {sw.ElapsedMilliseconds}ms");
                ok++;
            }
            catch (Exception ex)
            {
                sw.Stop();
                fail++;
                Console.WriteLine($"[raw] FAIL {Path.GetFileName(f)} {sw.ElapsedMilliseconds}ms :: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"[raw] done ok={ok} fail={fail}");
        return fail == 0 ? 0 : 3;
    }

    private static int DistinctSample(byte[] bgra, out double mean)
    {
        long sum = 0;
        var set = new HashSet<uint>();
        int step = Math.Max(4, (bgra.Length / 4 / 4096) * 4);
        int n = 0;
        for (int i = 0; i + 3 < bgra.Length; i += step)
        {
            uint v = (uint)(bgra[i] | bgra[i + 1] << 8 | bgra[i + 2] << 16);
            set.Add(v);
            sum += bgra[i] + bgra[i + 1] + bgra[i + 2];
            n++;
        }
        mean = n == 0 ? 0 : sum / (3.0 * n);
        return set.Count;
    }

    private static async Task<byte[]> EncodeJpegAsync(byte[] bgra, uint w, uint h, int quality)
    {
        using var outStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, w, h, 96, 96, bgra);
        await encoder.FlushAsync();
        outStream.Seek(0);
        var reader = new DataReader(outStream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)outStream.Size);
        var bytes = new byte[outStream.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static List<string> Collect(string[] args)
    {
        var list = new List<string>();
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".gif", ".webp", ".heic", ".heif", ".avif" };
        foreach (var rawArg in args)
        {
            var a = Path.GetFullPath(rawArg);   // WinRT 只接受规范化后的 Windows 路径
            if (Directory.Exists(a))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories))
                        if (exts.Contains(Path.GetExtension(f))) list.Add(f);
                }
                catch { }
            }
            else if (File.Exists(a)) list.Add(a);
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list.Take(12).ToList();
    }
}