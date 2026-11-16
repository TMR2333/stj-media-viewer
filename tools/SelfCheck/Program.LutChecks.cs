using StjMediaViewer.Core.Color;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    // ==================================================================================
    //  LUT（内置生成 / 解析 / 应用 / Sony 自动匹配）
    // ==================================================================================
    private static async Task CheckLuts(Services s, string tempData)
    {
        var luts = s.Luts;
        Check("LUT 服务可用", luts is not null, luts?.GetType().Name);

        var list = await luts!.ListAsync(forceRefresh: true);
        var builtIn = list.Where(e => e.Category == LutCategory.BuiltIn).ToList();
        Check("内置 LUT 已生成", builtIn.Count >= 4, $"{builtIn.Count} 条");
        Check("内置 LUT 都是 33³", builtIn.All(e => e.Size == 33 && !e.Is1D),
              string.Join(", ", builtIn.Select(e => $"{e.Name}:{e.Size}")));

        var slog3 = builtIn.FirstOrDefault(e => e.Id == "builtin:sony-slog3-cine-709");
        Check("S-Log3 内置 LUT 存在", slog3 is not null, slog3?.Name);

        var cube = slog3 is null ? null : await luts.LoadCubeAsync(slog3);
        Check("内置 LUT 可解析", cube is not null && cube.Size == 33, cube is null ? "解析失败" : $"{cube.Size}³");

        if (cube is not null)
        {
            // 18% 灰在 S-Log3 里编码为 0.4105，经 LUT 后应回到 709 的中灰附近（≈0.5）
            cube.Sample(0.4105f, 0.4105f, 0.4105f, out var mr, out var mg, out var mb);
            Check("S-Log3 18% 灰 → 709 中灰", mr is > 0.40f and < 0.62f,
                  $"({mr:0.###}, {mg:0.###}, {mb:0.###})");
            // 纯黑仍然接近黑，纯白接近白
            cube.Sample(0f, 0f, 0f, out var br, out _, out _);
            cube.Sample(1f, 1f, 1f, out var wr, out _, out _);
            Check("S-Log3 LUT 黑白端点合理", br < 0.06f && wr > 0.85f, $"黑 {br:0.###} / 白 {wr:0.###}");
        }

        // 应用 LUT 到真实图片：尺寸不变、像素确实变了
        var sample = await s.Store.QueryAsync(new AssetQuery { Limit = 50, Kinds = { MediaKind.Image } });
        var imageAsset = sample.FirstOrDefault(a => !string.IsNullOrEmpty(a.FullPath) && File.Exists(a.FullPath));
        if (imageAsset is not null && cube is not null)
        {
            var decoded = await s.Decoder.DecodeAsync(imageAsset.FullPath, 320);
            Check("解码静帧成功（用于 LUT）", decoded is not null, imageAsset.FileName);
            if (decoded is not null)
            {
                var graded = LutApplier.Apply(decoded, cube);
                Check("LUT 应用后尺寸不变",
                      graded.Width == decoded.Width && graded.Height == decoded.Height && graded.Bgra.Length == decoded.Bgra.Length,
                      $"{graded.Width}×{graded.Height}");
                int diff = 0;
                for (int i = 0; i < decoded.Bgra.Length; i += 4 * 64)
                    if (decoded.Bgra[i] != graded.Bgra[i]) diff++;
                Check("LUT 应用后像素发生变化", diff > 0, $"{diff} 个采样点不同");
            }
        }

        // 缩略图带 LUT：缓存路径必须与原图不同
        var videoAsset = (await s.Store.QueryAsync(new AssetQuery { Limit = 200, Kinds = { MediaKind.Video } })).FirstOrDefault();
        if (videoAsset is not null)
        {
            var plain = await s.Thumbnails.GetThumbnailPathAsync(videoAsset, 320);
            var gradedPath = await s.Thumbnails.GetThumbnailPathAsync(videoAsset, 320, cube, "test-lut");
            Check("LUT 缩略图走独立缓存", gradedPath is not null && !string.Equals(plain, gradedPath, StringComparison.OrdinalIgnoreCase),
                  $"{Path.GetFileName(plain)} → {Path.GetFileName(gradedPath)}");
        }

        // Sony 自动匹配：样例库里有一条 Sony S-Log3 视频
        var all = await s.Store.QueryAsync(new AssetQuery { Limit = 0 });
        var sonyLog = all.Where(a => LutCatalog.AutoMatchBuiltInKey(a) is not null).ToList();
        Check("识别出 Sony Log 素材", sonyLog.Count >= 1, $"{sonyLog.Count} 条");

        if (sonyLog.Count > 0)
        {
            var key = LutCatalog.AutoMatchBuiltInKey(sonyLog[0]);
            Check("Sony Log 命中 S-Log3 基准 LUT", key == "sony-slog3-cine-709", key);
            var resolved = s.Luts.ResolveForAsset(sonyLog[0]);
            Check("自动匹配解析到内置 LUT", resolved is { Category: LutCategory.BuiltIn },
                  resolved is null ? "未匹配" : $"{resolved.Name}（{resolved.Id}）");
        }

        // 文件名分类
        var (cat, brandName, logProfile) = LutCatalog.Classify(
            "SLog3SGamut3.CineToLC-709TypeA", @"D:\luts\Sony\SLog3SGamut3.CineToLC-709TypeA.cube");
        Check("官方 LUT 文件名分类", cat == LutCategory.Official && brandName == "Sony" && logProfile == "S-Log3",
              $"{cat} / {brandName} / {logProfile}");

        var (cat2, brand2, _) = LutCatalog.Classify(
            "Vintage Film Warm.cube", @"D:\luts\creative\Vintage Film Warm.cube");
        Check("创意 LUT 分类", cat2 == LutCategory.Creative, $"{cat2} / {brand2}");

        // 导入用户 LUT（用内置文件复制一份模拟用户文件）
        if (cube is not null)
        {
            var src = Path.Combine(tempData, "user-lut.cube");
            var identity = new System.Text.StringBuilder();
            identity.Append("TITLE \"User Test LUT\"\nLUT_3D_SIZE 2\n");
            identity.Append("0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 1\n");
            await File.WriteAllTextAsync(src, identity.ToString());
            var imported = await luts.ImportAsync(new[] { src });
            Check("导入用户 LUT", imported.Count == 1, imported.FirstOrDefault()?.Name ?? "未导入");
            if (imported.Count == 1)
            {
                var userCube = await luts.LoadCubeAsync(imported[0]);
                Check("用户 LUT 可解析（2³）", userCube is { Size: 2 }, userCube?.Size.ToString());
                var deleted = await luts.DeleteAsync(imported[0]);
                Check("用户 LUT 可删除", deleted, null);
            }
        }
    }
}