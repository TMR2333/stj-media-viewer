"""造一张"大图 + EXIF 旋转"的样本，用来在真实 App 里回归预览/缩略图解码。

尺寸默认 6192×4128（和用户遇到的手机原图同量级），EXIF Orientation=6，
画面按"正立"语义绘制：四角四色 + 中间斜向渐变 + 细网格，任何 stride 错位/方向错误都会一眼看出。

用法：python tools/SampleLibrary/gen_big_oriented.py [输出路径]
默认输出：tools/SampleLibrary/out/library/_regression/BIG_ORIENTED_6192x4128.jpg
"""
import os
import sys
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
default_out = os.path.join(HERE, "out", "library", "_regression", "BIG_ORIENTED_6192x4128.jpg")
out = sys.argv[1] if len(sys.argv) > 1 else default_out
os.makedirs(os.path.dirname(out), exist_ok=True)

W, H = 6192, 4128
upright = Image.new("RGB", (W, H), (30, 30, 34))
draw = ImageDraw.Draw(upright)

# 四角色块（每块 1/6 边长）
bw, bh = W // 6, H // 6
draw.rectangle([0, 0, bw, bh], fill=(220, 40, 40))                    # 左上 红
draw.rectangle([W - bw, 0, W, bh], fill=(40, 200, 60))                # 右上 绿
draw.rectangle([0, H - bh, bw, H], fill=(40, 60, 220))                # 左下 蓝
draw.rectangle([W - bw, H - bh, W, H], fill=(230, 210, 40))           # 右下 黄

# 中间斜向渐变 + 网格（错位会表现为"斜纹撕裂"）
for y in range(bh, H - bh, 8):
    shade = int(60 + 150 * (y - bh) / max(1, H - 2 * bh))
    draw.line([(bw, y), (W - bw, y)], fill=(shade, shade // 2, 255 - shade), width=1)
for x in range(bw, W - bw, 96):
    draw.line([(x, bh), (x, H - bh)], fill=(240, 240, 240), width=2)
for y in range(bh, H - bh, 96):
    draw.line([(bw, y), (W - bw, y)], fill=(240, 240, 240), width=2)

# 存储方向 = 正立图逆时针 90°（对应 EXIF Orientation=6，读取时顺时针转回）
stored = upright.rotate(90, expand=True)
exif = Image.Exif()
exif[274] = 6
stored.save(out, "JPEG", quality=88, exif=exif, optimize=True)
print(f"wrote {out}  stored={stored.size} orientation=6  bytes={os.path.getsize(out)}")