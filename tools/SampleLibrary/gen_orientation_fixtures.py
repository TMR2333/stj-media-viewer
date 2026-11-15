"""生成"EXIF 方向"解码测试样本：4 个 orientation（1 正常 / 3 旋转180 / 6 顺时针90 / 8 逆时针90）。

每张图在"成像后正立"的语义下：左上角 = 红块、右上角 = 绿块、左下角 = 蓝块、右下角 = 黄块。
写文件时先按 EXIF 存储方向把画面反着转，再写 Orientation 标签，
这样读取端如果正确应用 EXIF，就应该看到红块在左上。

输出目录：tools/SampleLibrary/fixtures/orientation/
用法：python tools/SampleLibrary/gen_orientation_fixtures.py
"""
import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "fixtures", "orientation")
os.makedirs(OUT, exist_ok=True)

W, H = 240, 160
BLOCK = 80

# "正立"语义下的四角
upright = Image.new("RGB", (W, H), (40, 40, 40))
px = upright.load()
for y in range(H):
    for x in range(W):
        if x < BLOCK and y < BLOCK:
            px[x, y] = (220, 30, 30)      # 左上 红
        elif x >= W - BLOCK and y < BLOCK:
            px[x, y] = (30, 200, 30)      # 右上 绿
        elif x < BLOCK and y >= H - BLOCK:
            px[x, y] = (30, 30, 220)      # 左下 蓝
        elif x >= W - BLOCK and y >= H - BLOCK:
            px[x, y] = (230, 220, 30)     # 右下 黄

# PIL 的 rotate(正角) = 逆时针。存储方向必须是"正立图的反向变换"：
#   O=6 语义是"显示时顺时针转 90°" → 存储时先逆时针转 90°（rotate(90)）
#   O=8 语义是"显示时逆时针转 90°" → 存储时先顺时针转 90°（rotate(-90)）
stored = {
    1: upright,
    3: upright.rotate(180),                                  # 存储 180°（自反）
    6: upright.rotate(90, expand=True),                      # 存储 90° 逆时针
    8: upright.rotate(-90, expand=True),                     # 存储 90° 顺时针
}

for orientation, img in stored.items():
    exif = Image.Exif()
    exif[274] = orientation          # 0x0112 Orientation
    path = os.path.join(OUT, f"orientation-{orientation}.jpg")
    img.save(path, "JPEG", quality=92, exif=exif)
    print(f"wrote {path}  stored={img.size}  orientation={orientation}")

print("done")