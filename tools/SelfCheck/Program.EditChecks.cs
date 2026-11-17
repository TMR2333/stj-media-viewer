using StjMediaViewer.Core.Media;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>
    /// 预览编辑：旋转/翻转的像素级实现、素材字段持久化、缩略图缓存键随编辑变化。
    /// </summary>
    private static async Task CheckPreviewEdits(Services s, string tempData)
    {
        // ---------------- 像素级旋转 ----------------
        // 2x1 图：左像素红、右像素绿
        var src = MakeTwoPixelImage();
        var rotated = ImageOps.Rotate90Clockwise(src);
        Check("编辑：旋转 90° 后宽高对调", rotated.Width == 1 && rotated.Height == 2,
              $"{rotated.Width}x{rotated.Height}");
        // 顺时针：原左下 … 实际映射 (x,y)→(h-1-y, x)，2x1 时上=原(0,0)、下=原(1,0)
        Check("编辑：旋转 90° 像素位置正确",
              rotated.Bgra[0] == src.Bgra[0] && rotated.Bgra[4] == src.Bgra[4],
              $"{rotated.Bgra[0]},{rotated.Bgra[4]}");

        var flippedH = ImageOps.Flip(src, horizontal: true);
        Check("编辑：水平翻转像素交换",
              flippedH.Width == 2 && flippedH.Bgra[0] == src.Bgra[4] && flippedH.Bgra[4] == src.Bgra[0]);

        var flippedV = ImageOps.Flip(src, horizontal: false);
        Check("编辑：垂直翻转尺寸不变", flippedV.Width == 2 && flippedV.Height == 1 && flippedV.Bgra[0] == src.Bgra[0]);

        var composed = ImageOps.ApplyEdits(src, 270, true, false);
        Check("编辑：旋转 270° + 翻转后尺寸正确", composed.Width == 1 && composed.Height == 2,
              $"{composed.Width}x{composed.Height}");

        // ---------------- 持久化 ----------------
        var dir = Path.Combine(tempData, "preview-edits");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "e.jpg");
        File.WriteAllBytes(path, new byte[64]);
        var asset = NewAsset("edit-1", "edit-lib", path, "e.jpg");
        await s.Store.UpsertManyAsync(new[] { asset });
        await s.Store.UpdateImageEditsAsync("edit-1", 90, flipHorizontal: true, flipVertical: false);

        var reloaded = await s.Store.GetAsync("edit-1");
        Check("编辑：旋转/翻转已入库",
              reloaded is not null && reloaded.RotationDegrees == 90 &&
              reloaded.FlipHorizontal && !reloaded.FlipVertical,
              reloaded is null ? "(无)" : $"rot={reloaded.RotationDegrees} h={reloaded.FlipHorizontal} v={reloaded.FlipVertical}");
        Check("编辑：HasImageEdits 判定",
              reloaded?.HasImageEdits == true);

        // 整行 upsert 也要带上（扫描/保存走的是这条路）
        asset.RotationDegrees = 180;
        asset.FlipVertical = true;
        await s.Store.UpsertManyAsync(new[] { asset });
        var again = await s.Store.GetAsync("edit-1");
        Check("编辑：整行写入同样保留", again?.RotationDegrees == 180 && again.FlipVertical, again?.RotationDegrees.ToString() ?? "(无)");

        // ---------------- 缩略图缓存键 ----------------
        var keyBefore = ThumbnailService.KeyBase(asset);
        asset.FlipHorizontal = true;
        var keyAfter = ThumbnailService.KeyBase(asset);
        Check("编辑：缩略图缓存键随编辑变化", keyBefore != keyAfter, $"{keyBefore[..8]} → {keyAfter[..8]}");
    }

    private static DecodedImage MakeTwoPixelImage()
    {
        // BGRA：左红右绿
        var bgra = new byte[]
        {
            0, 0, 255, 255,   // 蓝,绿,红,alpha → 红
            0, 255, 0, 255,   // → 绿
        };
        return new DecodedImage(bgra, 2, 1, 8);
    }
}