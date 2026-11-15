#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""stj-media-viewer 测试素材库生成器。

用法（在仓库任意 cwd 下都可以，路径全部基于本文件位置推导）:

    <repo>/tools/SampleLibrary/generate.py            # 生成完整素材库
    <repo>/tools/SampleLibrary/generate.py --only images   # 开发用：只跑某一阶段

产物:
    tools/SampleLibrary/out/library/            素材库（80-120 个文件，< 60MB）
    tools/SampleLibrary/out/library/catalog-manifest.json
        逐文件期望值清单，SelfCheck 用它做断言。

设计要点:
  * 幂等：每次运行先整个删掉 out/ 再重建；内容由"路径 + 固定种子"决定，重跑字节一致。
  * 失败降级：单个文件生成失败只打 WARNING 继续；ffmpeg 缺失时视频/音频走纯 Python 兜底骨架，
    并在 manifest 的 generator 段与 README 里标注。
  * EXIF / XMP / RAW / ISOBMFF 都按真实结构写，方便 MetadataExtractor / ffprobe 解析。
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import math
import os
import random
import re
import shutil
import struct
import subprocess
import sys
import time
import wave
import zipfile
import zlib
from pathlib import Path
from xml.sax.saxutils import escape as xml_escape

try:
    from PIL import Image, ImageDraw, ImageFilter, ExifTags
except Exception as exc:  # pragma: no cover
    print(f"FATAL: 需要 Pillow（pip install pillow）: {exc}")
    raise

try:
    import numpy as np
except Exception:  # pragma: no cover
    np = None

# --------------------------------------------------------------------------------------
# 路径 / 常量
# --------------------------------------------------------------------------------------
SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parents[1]          # tools/SampleLibrary -> tools -> <repo>
OUT_DIR = SCRIPT_DIR / "out"
LIB_DIR = OUT_DIR / "library"
TOOLING_DIR = OUT_DIR / "_tooling"         # ffmpeg 解包等临时产物（每次清空）

FFMPEG_ZIP = REPO_ROOT / "tools" / "ffmpeg" / "ff.zip"
FFMPEG_BIN = REPO_ROOT / "tools" / "ffmpeg" / "bin"

# 生成时的时间/随机源全部固定，保证可重复
SAMPLE_QUALITY = 70
JPEG_QUALITY = 70
CREATOR = "陈墨"
COPYRIGHT = "© 2023-2026 陈墨 / StjMediaViewer Sample Library"
SOFTWARE = "Adobe Lightroom 13.4 (Windows)"
SAMPLE_SEED = 20261115

# --------------------------------------------------------------------------------------
# 设备 / 城市 / 场景表
# --------------------------------------------------------------------------------------
DEVICES = {
    "sony": dict(
        make="SONY", model="ILCE-7M4", device="Sony A7 IV", family="Sony",
        lenses=["FE 24-70mm F2.8 GM II", "FE 70-200mm F2.8 GM OSS II",
                "FE 35mm F1.4 GM", "FE 16-35mm F2.8 GM"],
        apertures=[2.8, 4.0, 5.6, 8.0, 1.4, 11.0],
        isos=[100, 200, 400, 800, 1600, 3200, 6400],
        shutter=[(1, 60), (1, 125), (1, 250), (1, 500), (1, 1000), (1, 30)],
        focal=[16.0, 24.0, 35.0, 50.0, 70.0, 135.0, 200.0],
        prefix="DSC", ext="JPG",
    ),
    "canon": dict(
        make="Canon", model="Canon EOS R5", device="Canon EOS R5", family="Canon",
        lenses=["RF24-70mm F2.8 L IS USM", "RF85mm F1.2 L USM",
                "RF15-35mm F2.8 L IS USM", "RF100-500mm F4.5-7.1 L IS USM"],
        apertures=[1.2, 2.8, 4.0, 5.6, 7.1, 8.0],
        isos=[100, 200, 400, 800, 1600, 3200],
        shutter=[(1, 80), (1, 125), (1, 200), (1, 400), (1, 800), (1, 2000)],
        focal=[15.0, 24.0, 35.0, 50.0, 85.0, 100.0, 500.0],
        prefix="R5A", ext="JPG",
    ),
    "nikon": dict(
        make="NIKON CORPORATION", model="NIKON Z 8", device="Nikon Z 8", family="Nikon",
        lenses=["NIKKOR Z 24-70mm f/2.8 S", "NIKKOR Z 85mm f/1.2 S",
                "NIKKOR Z 14-24mm f/2.8 S"],
        apertures=[2.8, 4.0, 5.6, 8.0, 1.2],
        isos=[64, 100, 200, 400, 800, 1600],
        shutter=[(1, 100), (1, 160), (1, 250), (1, 500), (1, 1000)],
        focal=[14.0, 24.0, 35.0, 50.0, 70.0, 85.0],
        prefix="_DSC", ext="JPG",
    ),
    "fuji": dict(
        make="FUJIFILM", model="X-T5", device="Fujifilm X-T5", family="Fujifilm",
        lenses=["XF23mmF1.4 R LM WR", "XF56mmF1.2 R WR", "XF16-55mmF2.8 R LM WR"],
        apertures=[1.4, 1.2, 2.8, 4.0, 5.6],
        isos=[125, 200, 400, 800, 1600],
        shutter=[(1, 60), (1, 125), (1, 250), (1, 500)],
        focal=[16.0, 23.0, 35.0, 56.0, 55.0],
        prefix="DSCF", ext="JPG",
    ),
    "iphone": dict(
        make="Apple", model="iPhone 15 Pro", device="Apple iPhone 15 Pro", family="Apple",
        lenses=["iPhone 15 Pro back triple camera 6.765mm f/1.78",
                "iPhone 15 Pro back camera 2.22mm f/2.2"],
        apertures=[1.78, 2.2],
        isos=[32, 50, 64, 100, 200, 640],
        shutter=[(1, 60), (1, 120), (1, 250), (1, 1000)],
        focal=[6.765, 2.22, 24.0],
        prefix="IMG", ext="JPG",
    ),
    "dji": dict(
        make="DJI", model="FC3582", device="DJI FC3582", family="DJI",
        lenses=["DJI Mavic 3 Pro 24mm F2.8", "DJI Mavic 3 Pro 70mm F2.8"],
        apertures=[2.8, 3.4, 4.0],
        isos=[100, 100, 200, 400],
        shutter=[(1, 120), (1, 200), (1, 400), (1, 800)],
        focal=[24.0, 70.0],
        prefix="DJI", ext="JPG",
    ),
}

# 城市：目录名、GPS（真实地标附近）、时区偏移、国家/省份
CITIES = {
    "上海": dict(lat=31.2304, lon=121.4737, offset="+08:00", country="中国", province="上海"),
    "北京": dict(lat=39.9042, lon=116.4074, offset="+08:00", country="中国", province="北京"),
    "成都": dict(lat=30.5728, lon=104.0668, offset="+08:00", country="中国", province="四川"),
    "杭州": dict(lat=30.2741, lon=120.1551, offset="+08:00", country="中国", province="浙江"),
    "东京": dict(lat=35.6762, lon=139.6503, offset="+09:00", country="日本", province="东京都"),
    "巴黎": dict(lat=48.8566, lon=2.3522, offset="+02:00", country="法国", province="法兰西岛"),
    "纽约": dict(lat=40.7128, lon=-74.0060, offset="-04:00", country="美国", province="纽约州"),
    # 冰岛按国家标记：离线地理库很可能只到国家/地区级，manifest 不猜具体城市
    "冰岛": dict(lat=64.1466, lon=-21.9426, offset="+00:00", country="冰岛", province=None),
}

# 场景 -> 主题/影调提示（advisory，供规则分类/主题测试参考；不硬断言）
SCENE_THEME = {
    "landscape": ("landscape", "natural"),
    "city": ("city", "cinematic"),
    "night": ("night", "lowkey"),
    "street": ("street", "cinematic"),
    "architecture": ("architecture", "natural"),
    "travel": ("travel", "warm"),
    "aerial": ("landscape", "cool"),
    "portrait": ("portrait", "soft"),
    "food": ("food", "warm"),
    "abstract": ("art", "hisat"),
}

# --------------------------------------------------------------------------------------
# 图片清单（JPEG 32 张 + PNG 2 + TIFF 1）
# 字段: path, device, city, captured(本地时间), scene, w, h, embed_xmp, xmp_sidecar, dop/cos
# --------------------------------------------------------------------------------------
JPEG_SPECS = [
    # ---- 2023 上海 ----
    dict(path="2023/上海/DSC01234.JPG", dev="sony", city="上海", captured="2023-04-08T17:32:14", scene="city", w=1600, h=1067, embed_xmp=True),
    dict(path="2023/上海/DSC01235.JPG", dev="sony", city="上海", captured="2023-04-08T17:41:02", scene="city", w=1600, h=1067),
    dict(path="2023/上海/DSC01236.JPG", dev="sony", city="上海", captured="2023-04-09T07:15:44", scene="street", w=1200, h=800),
    dict(path="2023/上海/DSC01237.JPG", dev="sony", city="上海", captured="2023-06-18T20:05:55", scene="night", w=800, h=1200),
    dict(path="2023/上海/DSC01238.JPG", dev="sony", city="上海", captured="2023-06-18T20:12:31", scene="city", w=1600, h=1067, embed_xmp=True),
    # ---- 2023 成都 ----
    dict(path="2023/成都/DSC03001.JPG", dev="sony", city="成都", captured="2023-07-22T10:02:11", scene="food", w=1200, h=800),
    dict(path="2023/成都/DSC03002.JPG", dev="sony", city="成都", captured="2023-07-22T11:20:03", scene="street", w=1200, h=800, xmp_sidecar="lightroom"),
    dict(path="2023/成都/DSC03003.JPG", dev="sony", city="成都", captured="2023-07-23T18:45:59", scene="travel", w=1600, h=1067),
    # ---- 2023 杭州 ----
    dict(path="2023/杭州/R5A0010.JPG", dev="canon", city="杭州", captured="2023-11-05T06:48:20", scene="landscape", w=1600, h=1067, embed_xmp=True),
    dict(path="2023/杭州/R5A0011.JPG", dev="canon", city="杭州", captured="2023-11-05T07:12:09", scene="landscape", w=1600, h=1067),
    dict(path="2023/杭州/R5A0012.JPG", dev="canon", city="杭州", captured="2023-11-06T16:30:00", scene="architecture", w=1200, h=800),
    # ---- 2024 东京 ----
    dict(path="2024/东京/DSC02001.JPG", dev="sony", city="东京", captured="2024-11-03T16:42:10", scene="street", w=1600, h=1067, embed_xmp=True),
    dict(path="2024/东京/DSC02002.JPG", dev="sony", city="东京", captured="2024-11-03T19:05:47", scene="night", w=1200, h=800, xmp_sidecar="lightroom"),
    dict(path="2024/东京/DSCF1001.JPG", dev="fuji", city="东京", captured="2024-11-04T08:30:12", scene="street", w=1200, h=800),
    dict(path="2024/东京/DSCF1002.JPG", dev="fuji", city="东京", captured="2024-11-04T12:15:33", scene="architecture", w=800, h=1200),
    dict(path="2024/东京/IMG_4001.JPG", dev="iphone", city="东京", captured="2024-11-05T18:22:05", scene="travel", w=1200, h=900, embed_xmp=True),
    dict(path="2024/东京/IMG_4002.JPG", dev="iphone", city="东京", captured="2024-11-05T21:10:44", scene="night", w=900, h=1200),
    # ---- 2024 冰岛 ----
    dict(path="2024/冰岛/R5A0100.JPG", dev="canon", city="冰岛", captured="2024-03-15T13:20:41", scene="landscape", w=1600, h=1067, embed_xmp=True),
    dict(path="2024/冰岛/R5A0101.JPG", dev="canon", city="冰岛", captured="2024-03-15T17:55:02", scene="landscape", w=1600, h=1067, xmp_sidecar="lightroom"),
    dict(path="2024/冰岛/DJI_0001.JPG", dev="dji", city="冰岛", captured="2024-03-16T10:05:18", scene="aerial", w=1200, h=900),
    dict(path="2024/冰岛/DJI_0002.JPG", dev="dji", city="冰岛", captured="2024-03-16T10:22:47", scene="aerial", w=1200, h=900, embed_xmp=True),
    # ---- 2024 纽约 ----
    dict(path="2024/纽约/IMG_5001.JPG", dev="iphone", city="纽约", captured="2024-09-21T20:15:33", scene="night", w=1200, h=900),
    dict(path="2024/纽约/IMG_5002.JPG", dev="iphone", city="纽约", captured="2024-09-21T20:42:10", scene="city", w=900, h=1200, xmp_sidecar="lightroom"),
    dict(path="2024/纽约/R5A0200.JPG", dev="canon", city="纽约", captured="2024-09-22T08:10:00", scene="architecture", w=1600, h=1067),
    # ---- 2025 巴黎 ----
    dict(path="2025/巴黎/R5A0002.JPG", dev="canon", city="巴黎", captured="2025-10-05T15:22:40", scene="architecture", w=1600, h=1067),
    dict(path="2025/巴黎/_DSC5001.JPG", dev="nikon", city="巴黎", captured="2025-10-05T17:48:12", scene="city", w=1600, h=1067, embed_xmp=True),
    dict(path="2025/巴黎/_DSC5002.JPG", dev="nikon", city="巴黎", captured="2025-10-06T09:30:55", scene="street", w=1200, h=800, xmp_sidecar="lightroom"),
    dict(path="2025/巴黎/IMG_6001.JPG", dev="iphone", city="巴黎", captured="2025-10-06T20:01:18", scene="night", w=1200, h=900, sidecar_kind="dop"),
    dict(path="2025/巴黎/DSCF2001.JPG", dev="fuji", city="巴黎", captured="2025-10-07T14:05:05", scene="travel", w=1200, h=800),
    # ---- 2026 北京 ----
    dict(path="2026/北京/DSCF3001.JPG", dev="fuji", city="北京", captured="2026-01-10T09:12:44", scene="architecture", w=1200, h=800, embed_xmp=True),
    dict(path="2026/北京/_DSC3000.JPG", dev="nikon", city="北京", captured="2026-01-10T16:20:30", scene="city", w=1600, h=1067),
    dict(path="2026/北京/IMG_7001.JPG", dev="iphone", city="北京", captured="2026-01-11T11:05:09", scene="travel", w=900, h=1200, sidecar_kind="cos"),
]

OTHER_IMAGE_SPECS = [
    dict(path="2024/东京/DSCF1003.PNG", dev="fuji", city="东京", captured="2024-11-04T15:00:00", scene="abstract", w=800, h=600, fmt="PNG"),
    dict(path="2025/巴黎/R5A0003.PNG", dev="canon", city="巴黎", captured="2025-10-05T16:00:00", scene="architecture", w=800, h=600, fmt="PNG"),
    dict(path="2023/杭州/R5A0013.TIFF", dev="canon", city="杭州", captured="2023-11-06T17:00:00", scene="landscape", w=800, h=533, fmt="TIFF"),
]

# --------------------------------------------------------------------------------------
# RAW 清单（8 个，全部 TIFF 结构 + 完整内嵌 JPEG + 513/514 登记）
# pair 表示同目录下有同名 JPEG（JPG-RAW 配对分类用）
# --------------------------------------------------------------------------------------
RAW_SPECS = [
    dict(path="2023/上海/DSC01235.ARW", dev="sony", city="上海", captured="2023-04-08T17:41:02", scene="city", w=1200, h=800, raw="ARW", xmp_sidecar="lightroom"),
    dict(path="2023/上海/DSC01239.ARW", dev="sony", city="上海", captured="2023-06-18T20:20:00", scene="night", w=1600, h=1067, raw="ARW"),
    dict(path="2024/东京/DSC02001.ARW", dev="sony", city="东京", captured="2024-11-03T16:42:10", scene="street", w=1200, h=800, raw="ARW", xmp_sidecar="lightroom"),
    dict(path="2025/巴黎/R5A0002.CR2", dev="canon", city="巴黎", captured="2025-10-05T15:22:40", scene="architecture", w=1200, h=800, raw="CR2", xmp_sidecar="lightroom"),
    dict(path="2026/北京/_DSC3000.NEF", dev="nikon", city="北京", captured="2026-01-10T16:20:30", scene="city", w=1200, h=800, raw="NEF"),
    dict(path="2024/冰岛/DJI_0001.DNG", dev="dji", city="冰岛", captured="2024-03-16T10:05:18", scene="aerial", w=1200, h=900, raw="DNG"),
    dict(path="2023/杭州/R5A0013.DNG", dev="canon", city="杭州", captured="2023-11-06T17:05:00", scene="landscape", w=1600, h=1067, raw="DNG"),
    dict(path="2023/成都/DSC03005.ARW", dev="sony", city="成都", captured="2023-07-23T19:10:00", scene="travel", w=1600, h=1067, raw="ARW"),
]

# --------------------------------------------------------------------------------------
# 视频 / 音频清单
# --------------------------------------------------------------------------------------
VIDEO_SPECS = [
    dict(path="Video/2024_东京_街景_Sony.mp4", ext="mp4", w=320, h=180, fps=25, dur=2.0,
         make="Sony", model="ILCE-7M4", device="Sony A7 IV", city="东京",
         created="2024-11-03T07:42:10Z", audio=True, codec="libx264", hlg=False, comment=None, dur_hint=2.0),
    dict(path="Video/2025_巴黎_屋顶_Canon.mp4", ext="mp4", w=320, h=180, fps=25, dur=1.5,
         make="Canon", model="Canon EOS R5", device="Canon EOS R5", city="巴黎",
         created="2025-10-05T13:22:40Z", audio=False, codec="libx264", hlg=False, comment=None, dur_hint=1.5),
    dict(path="Video/2024_冰岛_航拍_DJI_HLG.mov", ext="mov", w=320, h=180, fps=25, dur=2.0,
         make="DJI", model="FC3582", device="DJI FC3582", city="冰岛",
         created="2024-03-16T10:05:18Z", audio=False, codec="libx264", hlg=True, comment=None, dur_hint=2.0),
    dict(path="Video/2025_东京_夜景_Fuji_HLG.mov", ext="mov", w=320, h=180, fps=25, dur=2.0,
         make="FUJIFILM", model="X-T5", device="Fujifilm X-T5", city="东京",
         created="2025-10-06T19:30:00Z", audio=False, codec="libx264", hlg=True, comment=None, dur_hint=2.0),
    dict(path="Video/2024_巴黎_SLog3_Sony.mov", ext="mov", w=320, h=180, fps=25, dur=2.0,
         make="Sony", model="ILCE-7M4", device="Sony A7 IV", city="巴黎",
         created="2024-06-12T08:15:00Z", audio=False, codec="libx264", hlg=False, comment="S-Log3", dur_hint=2.0),
    dict(path="Video/2026_北京_竖屏_iPhone.mov", ext="mov", w=180, h=320, fps=30, dur=1.5,
         make="Apple", model="iPhone 15 Pro", device="Apple iPhone 15 Pro", city="北京",
         created="2026-01-11T03:05:09Z", audio=False, codec="libx264", hlg=False, comment=None, dur_hint=1.5),
]

AUDIO_SPECS = [
    dict(path="Audio/城市夜雨.mp3", fmt="MP3", dur=2.0, title="城市夜雨", artist="陈墨", album="环境录音 Vol.1", freq=440),
    dict(path="Audio/ambient_drone.wav", fmt="WAV", dur=1.5, title="Ambient Drone", artist="陈墨", album="环境录音 Vol.1", freq=220),
    dict(path="Audio/voice_memo.m4a", fmt="M4A", dur=1.0, title="Voice Memo 2026-01-11", artist="陈墨", album="Voice Memos", freq=330),
    dict(path="Audio/field_recording.flac", fmt="FLAC", dur=1.5, title="Field Recording", artist="陈墨", album="Field", freq=550),
]

# 非媒体 / 反例文件：(相对路径, 类型, 内容描述, 期望排除原因)
JUNK_SPECS = [
    dict(path="notes.txt", kind="json", content="拍摄笔记：上海外滩蓝调时刻，光圈优先。\n", reason="ignored-extension"),
    dict(path="settings.json", kind="json", content='{"libraryName":"样例素材库","version":1}\n', reason="ignored-extension"),
    dict(path="cache.tmp", kind="binary", content=b"\x00\x01TMPDATA" * 8, reason="ignored-extension"),
    dict(path="Thumbs.db", kind="binary", content=b"\x00\x00\x00\x00Thumbs.db thumbnail cache\x00" * 3, reason="ignored-filename"),
    dict(path=".DS_Store", kind="binary", content=b"\x00\x00\x00\x01Bud1\x00\x00\x10\x00\x00\x00\x08\x00\x00\x00\x00" + b"\x00" * 32, reason="ignored-filename"),
    dict(path="desktop.ini", kind="text", content="[.ShellClassInfo]\r\nIconResource=C:\\Windows\\System32\\shell32.dll,3\r\n", reason="ignored-filename"),
    dict(path="import_log.csv", kind="text", content="file,status\nDSC0001.JPG,ok\n", reason="unsupported-extension"),
    dict(path="broken.jpg", kind="text", content="this is definitely not a jpeg file\n" * 8, reason="broken-media"),
    dict(path="empty.jpg", kind="empty", content=b"", reason="empty-file"),
    dict(path=".hidden_photo.jpg", kind="hidden_jpeg", content=b"", reason="hidden-file"),
    dict(path="._DSC02001.JPG", kind="appledouble", content=b"\x00\x05\x16\x07" + b"\x00" * 16, reason="appledouble"),
    dict(path="_junk/random.tmp", kind="binary", content=b"junk-tmp-data" * 4, reason="ignored-extension"),
    dict(path="_junk/readme.txt", kind="text", content="这个目录故意保留旧备份，用来验证重复检测。\n", reason="ignored-extension"),
    dict(path="$RECYCLE.BIN/deleted_but_real.jpg", kind="real_jpeg_bad_dir", content=b"", reason="ignored-directory"),
    dict(path="node_modules/package.json", kind="text", content='{"name":"thumbnails","version":"1.0.0"}\n', reason="ignored-directory"),
    dict(path="node_modules/preview.png", kind="real_png_bad_dir", content=b"", reason="ignored-directory"),
]

# 重复 / 近重复组
DUP_GROUPS = [
    dict(group="exact-1", base="2024/东京/DSC02001.JPG",
         copies=["_junk/dup_a/DSC02001(1).JPG"]),
    dict(group="exact-2", base="2025/巴黎/R5A0002.CR2",
         copies=["_junk/dup_b/巴黎RAW备份.CR2"]),
    dict(group="exact-3", base="2023/上海/DSC01234.JPG",
         copies=["_junk/dup_c/DSC01234_copy.JPG"]),
]
NEAR_DUP_GROUPS = [
    dict(group="near-1", base="2024/冰岛/R5A0100.JPG", copy="_junk/near_dup/R5A0100_q55.JPG", quality=55),
    dict(group="near-2", base="2024/纽约/IMG_5001.JPG", copy="_junk/near_dup/IMG_5001_压缩版.JPG", quality=52),
]


# --------------------------------------------------------------------------------------
# 通用小工具
# --------------------------------------------------------------------------------------
WARNINGS: list[str] = []


def warn(msg: str) -> None:
    WARNINGS.append(msg)
    print(f"  [WARN] {msg}")


def rng_for(rel_path: str) -> random.Random:
    """按相对路径派生确定性随机源。"""
    h = hashlib.sha256(rel_path.encode("utf-8")).digest()
    return random.Random(int.from_bytes(h[:8], "big") ^ SAMPLE_SEED)


def ensure_parent(path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def utc_offset_delta(offset: str):
    from datetime import timedelta, timezone
    sign = 1 if offset[0] == "+" else -1
    hours = int(offset[1:3])
    minutes = int(offset[4:6])
    return timezone(sign * timedelta(hours=hours, minutes=minutes))


def parse_local_dt(captured: str):
    from datetime import datetime
    return datetime.strptime(captured, "%Y-%m-%dT%H:%M:%S")


def exif_dt(captured: str) -> str:
    return parse_local_dt(captured).strftime("%Y:%m:%d %H:%M:%S")


def decimal_to_dms(value: float):
    """十进制度 -> (度, 分, 秒) 浮点，秒保留 3 位小数。"""
    sign = -1.0 if value < 0 else 1.0
    value = abs(value)
    deg = int(value)
    minutes_f = (value - deg) * 60.0
    minutes = int(minutes_f)
    seconds = round((minutes_f - minutes) * 60.0, 3)
    if seconds >= 60.0:  # 浮点进位保护
        seconds = 0.0
        minutes += 1
    if minutes >= 60:
        minutes = 0
        deg += 1
    return sign, deg, minutes, seconds


def dms_to_decimal(sign, deg, minutes, seconds) -> float:
    v = deg + minutes / 60.0 + seconds / 3600.0
    return v * (-1.0 if sign < 0 else 1.0)


def _jsonable(obj):
    if isinstance(obj, dict):
        return {k: _jsonable(v) for k, v in obj.items()}
    if isinstance(obj, (list, tuple)):
        return [_jsonable(v) for v in obj]
    if isinstance(obj, Path):
        return obj.as_posix()
    if isinstance(obj, float):
        return round(obj, 6)
    return obj


# --------------------------------------------------------------------------------------
# XMP
# --------------------------------------------------------------------------------------
SCENE_KW = {
    "landscape": ["风光", "自然", "山", "湖泊"],
    "city": ["城市", "建筑", "天际线"],
    "night": ["夜景", "城市", "灯光"],
    "street": ["街拍", "城市", "人文"],
    "architecture": ["建筑", "几何", "城市"],
    "travel": ["旅行", "城市", "记录"],
    "aerial": ["航拍", "风光", "自然"],
    "portrait": ["人像", "人物", "情绪"],
    "food": ["美食", "静物", "暖调"],
    "abstract": ["艺术", "抽象", "色彩"],
}

SCENE_ZH = {
    "landscape": "风光", "city": "城市", "night": "夜景", "street": "街拍",
    "architecture": "建筑", "travel": "旅行", "aerial": "航拍", "portrait": "人像",
    "food": "美食", "abstract": "抽象",
}


def xmp_packet(title: str, caption: str, keywords, rating: int, label: str,
               has_settings: bool, process_version: str, camera_profile: str,
               engine: str, city: str, country: str) -> str:
    """生成一段完整 XMP packet（JPEG APP1 与边车文件共用）。"""
    kw_xml = "".join(f"<rdf:li>{xml_escape(k)}</rdf:li>" for k in keywords)
    return (
        '<?xpacket begin="\ufeff" id="W5M0MpCehiHzreSzNTczkc9d"?>\n'
        f'<x:xmpmeta xmlns:x="adobe:ns:meta/" x:xmptk="{xml_escape(engine)}">\n'
        ' <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">\n'
        '  <rdf:Description rdf:about=""\n'
        '    xmlns:xmp="http://ns.adobe.com/xap/1.0/"\n'
        '    xmlns:dc="http://purl.org/dc/elements/1.1/"\n'
        '    xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/"\n'
        '    xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/"\n'
        '    xmlns:xmpMM="http://ns.adobe.com/xap/1.0/mm/"\n'
        f'    xmp:Rating="{rating}"\n'
        f'    xmp:Label="{xml_escape(label)}"\n'
        f'    xmp:CreatorTool="{xml_escape(engine)}"\n'
        f'    photoshop:City="{xml_escape(city)}"\n'
        f'    photoshop:Country="{xml_escape(country)}"\n'
        f'    photoshop:Credit="{xml_escape(CREATOR)}"\n'
        f'    crs:Version="{process_version}"\n'
        f'    crs:ProcessVersion="{process_version}"\n'
        f'    crs:HasSettings="{has_settings}"\n'
        f'    crs:CameraProfile="{xml_escape(camera_profile)}"\n'
        '    crs:WhiteBalance="As Shot"\n'
        '    crs:Temperature="5500"\n'
        '    crs:Tint="+6">\n'
        f'   <dc:title><rdf:Alt><rdf:li xml:lang="x-default">{xml_escape(title)}</rdf:li></rdf:Alt></dc:title>\n'
        f'   <dc:description><rdf:Alt><rdf:li xml:lang="x-default">{xml_escape(caption)}</rdf:li></rdf:Alt></dc:description>\n'
        f'   <dc:creator><rdf:Seq><rdf:li>{xml_escape(CREATOR)}</rdf:li></rdf:Seq></dc:creator>\n'
        f'   <dc:subject><rdf:Bag>{kw_xml}</rdf:Bag></dc:subject>\n'
        '  </rdf:Description>\n'
        ' </rdf:RDF>\n'
        '</x:xmpmeta>\n'
        '<?xpacket end="w"?>'
    )


def xmp_for_spec(spec: dict, rng: random.Random):
    """按文件生成 XMP 文本要素。"""
    city = CITIES[spec["city"]]
    dev = DEVICES[spec["dev"]]
    scene = spec["scene"]
    title = f"{spec['city']}{SCENE_ZH.get(scene, '记录')}"
    caption = f"{spec['captured'][:4]} 年用 {dev['device']} 拍摄于{spec['city']}，{SCENE_ZH.get(scene, '')}题材。"
    keywords = list(dict.fromkeys([spec["city"], city["country"], *SCENE_KW.get(scene, ["记录"])]))
    rating = rng.choice([2, 3, 3, 4, 4, 5])
    label = rng.choice(["Red", "Yellow", "Green", "Blue"])
    profile = rng.choice(["Adobe Standard", "Adobe Color", "Camera Standard"])
    return title, caption, keywords, rating, label, profile


def inject_jpeg_xmp(jpeg: bytes, packet: str) -> bytes:
    """把 XMP packet 作为 APP1 段插入 SOI 之后。"""
    if not jpeg.startswith(b"\xff\xd8"):
        raise ValueError("not a jpeg")
    ns = b"http://ns.adobe.com/xap/1.0/\x00"
    body = ns + packet.encode("utf-8")
    if len(body) + 2 > 65533:
        raise ValueError("xmp packet too large for jpeg app1")
    seg = b"\xff\xe1" + struct.pack(">H", len(body) + 2) + body
    return jpeg[:2] + seg + jpeg[2:]


def write_xmp_sidecar(main_rel: str, spec: dict, engine: str = "Adobe Lightroom",
                      process_version: str = "15.4", camera_profile: str = "Adobe Standard") -> dict:
    """写 Lightroom 风格边车 .xmp，返回清单条目。

    同一 stem 的 JPG/RAW 会共用同一个同名 .xmp（只写一次，两边都挂接）。
    """
    rng = rng_for(main_rel + "#xmp")
    title, caption, keywords, rating, label, profile = xmp_for_spec(spec, rng)
    side_rel = Path(main_rel).with_suffix(".xmp").as_posix()
    path = LIB_DIR / side_rel
    entry = find_entry(side_rel)
    if entry is None:
        packet = xmp_packet(title, caption, keywords, rating, label, True,
                            process_version, camera_profile, engine,
                            spec["city"], CITIES[spec["city"]]["country"])
        ensure_parent(path)
        path.write_text(packet, encoding="utf-8")
        entry = base_entry(side_rel, "Sidecar", "XMP")
        entry.update(expectIncluded=False, expectExcludedReason="sidecar",
                     expectAttachedTo=main_rel, expectDevelopEngine=engine,
                     expectHasDevelopEdits=True, expectRating=rating, expectColorLabel=label,
                     expectTitle=title, expectCaption=caption, expectKeywords=keywords,
                     expectProcessVersion=process_version)
        fill_file_meta(entry, path)
        MANIFEST.append(entry)
    # 主文件条目补上边车期望
    main = find_entry(main_rel)
    if main is not None:
        main["expectSidecars"] = sorted(set(main["expectSidecars"]) | {side_rel})
        main["expectXmpPath"] = side_rel
        main["expectPostProcess"] = True
        main["expectHasDevelopEdits"] = True
        main["expectDevelopEngine"] = engine
        main["expectKeywords"] = keywords
        main["expectRating"] = rating
        main["expectColorLabel"] = label
        main["expectTitle"] = title
        main["expectCaption"] = caption
        main["expectProcessVersion"] = process_version
    return entry


def write_capture_one_sidecar(main_rel: str, spec: dict, ext: str) -> dict:
    """Capture One 的 .dop / .cos 空壳（仍是合法 XMP，含 crs:HasSettings）。"""
    rng = rng_for(main_rel + "#dop")
    title, caption, keywords, rating, label, _ = xmp_for_spec(spec, rng)
    engine = "Capture One 23"
    packet = (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<?xpacket begin="\ufeff" id="W5M0MpCehiHzreSzNTczkc9d"?>\n'
        f'<x:xmpmeta xmlns:x="adobe:ns:meta/" x:xmptk="{engine}">\n'
        ' <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">\n'
        '  <rdf:Description rdf:about=""\n'
        '    xmlns:xmp="http://ns.adobe.com/xap/1.0/"\n'
        '    xmlns:dc="http://purl.org/dc/elements/1.1/"\n'
        '    xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/"\n'
        f'    xmp:Rating="{rating}" xmp:Label="{xml_escape(label)}"\n'
        '    crs:Version="23.0" crs:ProcessVersion="15.0"\n'
        '    crs:HasSettings="True" crs:CameraProfile="Capture One">\n'
        f'   <dc:title><rdf:Alt><rdf:li xml:lang="x-default">{xml_escape(title)}</rdf:li></rdf:Alt></dc:title>\n'
        f'   <dc:subject><rdf:Bag>{"".join(f"<rdf:li>{xml_escape(k)}</rdf:li>" for k in keywords)}</rdf:Bag></dc:subject>\n'
        '  </rdf:Description>\n'
        ' </rdf:RDF>\n'
        '</x:xmpmeta>\n'
        '<?xpacket end="w"?>'
    )
    side_rel = Path(main_rel).with_suffix(ext).as_posix()
    path = LIB_DIR / side_rel
    ensure_parent(path)
    path.write_text(packet, encoding="utf-8")
    entry = base_entry(side_rel, "Sidecar", ext.lstrip(".").upper())
    entry.update(expectIncluded=False, expectExcludedReason="sidecar",
                 expectAttachedTo=main_rel, expectDevelopEngine="CaptureOne",
                 expectHasDevelopEdits=True, expectRating=rating, expectColorLabel=label,
                 expectKeywords=keywords, expectTitle=title)
    fill_file_meta(entry, path)
    MANIFEST.append(entry)
    main = find_entry(main_rel)
    if main is not None:
        main["expectSidecars"] = sorted(set(main["expectSidecars"]) | {side_rel})
        main["expectXmpPath"] = side_rel
        main["expectPostProcess"] = True
        main["expectHasDevelopEdits"] = True
        main["expectDevelopEngine"] = "CaptureOne"
        main["expectKeywords"] = keywords
        main["expectRating"] = rating
        main["expectColorLabel"] = label
        main["expectTitle"] = title
    return entry


# --------------------------------------------------------------------------------------
# EXIF（JPEG 用 Pillow Exif；RAW 用自写 TIFF IFD）
# --------------------------------------------------------------------------------------
def camera_params(rel_path: str) -> dict:
    """按文件名（去扩展名）派生相机参数，保证 JPG/RAW 同名对参数一致。"""
    stem_rel = Path(rel_path).with_suffix("").as_posix()
    rng = rng_for(stem_rel + "#cam")
    return rng


def pick_camera(rel_path: str, dev_key: str) -> dict:
    dev = DEVICES[dev_key]
    rng = camera_params(rel_path)
    lens = rng.choice(dev["lenses"])
    fnum = rng.choice(dev["apertures"])
    iso = rng.choice(dev["isos"])
    sh = rng.choice(dev["shutter"])
    focal = rng.choice(dev["focal"])
    return dict(lens=lens, fnumber=fnum, iso=iso, shutter_num=sh[0], shutter_den=sh[1],
                shutter=sh[0] / sh[1], focal=focal)


def _gps_tag_values(lat: float, lon: float, altitude: float, captured: str, offset: str):
    """返回 GPS IFD 需要的字段（含 UTC 时间戳）。"""
    from datetime import datetime, timedelta, timezone
    sign_lat, dlat, mlat, slat = decimal_to_dms(lat)
    sign_lon, dlon, mlon, slon = decimal_to_dms(lon)
    tz = utc_offset_delta(offset)
    dt_local = parse_local_dt(captured).replace(tzinfo=tz)
    dt_utc = dt_local.astimezone(timezone.utc)
    return dict(
        lat_ref="N" if sign_lat > 0 else "S", lat=(float(dlat), float(mlat), slat),
        lon_ref="E" if sign_lon > 0 else "W", lon=(float(dlon), float(mlon), slon),
        alt=float(altitude), utc=(float(dt_utc.hour), float(dt_utc.minute), float(dt_utc.second)),
        utc_date=dt_utc.strftime("%Y:%m:%d"),
    )


ALTITUDE_BY_CITY = {"上海": 8.0, "北京": 44.0, "成都": 500.0, "杭州": 12.0,
                    "东京": 40.0, "巴黎": 35.0, "纽约": 10.0, "冰岛": 55.0}


def build_jpeg_exif(spec: dict, cam: dict, dims) -> Image.Exif:
    dev = DEVICES[spec["dev"]]
    city = CITIES[spec["city"]]
    captured = spec["captured"]
    offset = city["offset"]
    dt = exif_dt(captured)
    exif = Image.Exif()
    # IFD0
    exif[0x010F] = dev["make"]
    exif[0x0110] = dev["model"]
    exif[0x0112] = 1
    exif[0x011A] = 72.0
    exif[0x011B] = 72.0
    exif[0x0128] = 2
    exif[0x0131] = SOFTWARE
    exif[0x0132] = dt
    exif[0x013B] = CREATOR
    exif[0x8298] = COPYRIGHT
    # Exif 子 IFD
    sub = exif.get_ifd(ExifTags.IFD.Exif)
    sub[0x9000] = b"0232"
    sub[0x9003] = dt
    sub[0x9004] = dt
    sub[0x9010] = offset
    sub[0x9011] = offset
    sub[0x9012] = offset
    sub[0x829A] = cam["shutter"]
    sub[0x829D] = cam["fnumber"]
    sub[0x8827] = cam["iso"]
    sub[0x920A] = cam["focal"]
    sub[0xA405] = int(round(cam["focal"] * (1.5 if spec["dev"] == "fuji" else 1.0)))
    sub[0xA434] = cam["lens"]
    sub[0xA433] = dev["make"]
    sub[0xA002] = dims[0]
    sub[0xA003] = dims[1]
    sub[0x8822] = 3      # ExposureProgram: Aperture priority
    sub[0x9204] = 0.0    # ExposureBiasValue
    sub[0x9207] = 5      # MeteringMode: pattern
    sub[0x9209] = 16     # Flash: off
    sub[0x9209] = 16
    sub[0xA001] = 1      # ColorSpace: sRGB
    sub[0xA402] = 0      # ExposureMode: auto
    sub[0xA403] = 0      # WhiteBalance: auto
    sub[0xA406] = 0      # SceneCaptureType: standard
    # GPS
    g = _gps_tag_values(city["lat"], city["lon"], ALTITUDE_BY_CITY.get(spec["city"], 20.0), captured, offset)
    gps = exif.get_ifd(ExifTags.IFD.GPSInfo)
    gps[0] = b"\x02\x03\x00\x00"  # GPSVersionID
    gps[1] = g["lat_ref"]
    gps[2] = g["lat"]
    gps[3] = g["lon_ref"]
    gps[4] = g["lon"]
    gps[5] = 0
    gps[6] = g["alt"]
    gps[7] = g["utc"]
    gps[29] = g["utc_date"]
    return exif


# --------------------------------------------------------------------------------------
# 场景渲染（Pillow + numpy，全部确定性）
# --------------------------------------------------------------------------------------
PALETTES = {
    "landscape": ((66, 116, 186), (247, 196, 138)),
    "city": ((36, 44, 82), (238, 140, 96)),
    "night": ((8, 10, 26), (28, 30, 66)),
    "street": ((92, 96, 104), (196, 178, 158)),
    "architecture": ((196, 202, 210), (238, 240, 244)),
    "travel": ((88, 146, 204), (250, 214, 158)),
    "aerial": ((20, 96, 140), (156, 214, 206)),
    "portrait": ((44, 30, 34), (96, 62, 58)),
    "food": ((88, 50, 30), (238, 188, 120)),
    "abstract": ((30, 40, 90), (210, 60, 120)),
}


def _base_image(scene: str, w: int, h: int, rng: random.Random) -> Image.Image:
    top, bottom = PALETTES.get(scene, ((40, 40, 40), (200, 200, 200)))
    if np is not None:
        seed = rng.randrange(1, 2 ** 31)
        rngnp = np.random.default_rng(seed)
        t = np.linspace(0.0, 1.0, h, dtype=np.float32).reshape(h, 1, 1)
        arr = np.array(top, dtype=np.float32) * (1.0 - t) + np.array(bottom, dtype=np.float32) * t
        arr = np.broadcast_to(arr, (h, w, 3)).copy()
        arr += rngnp.normal(0.0, 3.0, (h, w, 1)).astype(np.float32)
        return Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8), "RGB")
    img = Image.new("RGB", (w, h), top)
    d = ImageDraw.Draw(img)
    for y in range(h):
        f = y / max(1, h - 1)
        c = tuple(int(top[i] * (1 - f) + bottom[i] * f) for i in range(3))
        d.line([(0, y), (w, y)], fill=c)
    return img


def render_scene(scene: str, w: int, h: int, rel_path: str) -> Image.Image:
    rng = rng_for(rel_path + "#scene")
    img = _base_image(scene, w, h, rng)
    d = ImageDraw.Draw(img, "RGBA")

    if scene == "landscape":
        sx = int(w * rng.uniform(0.15, 0.8))
        sy = int(h * rng.uniform(0.18, 0.38))
        r = int(min(w, h) * rng.uniform(0.03, 0.06))
        d.ellipse([sx - r, sy - r, sx + r, sy + r], fill=(255, 226, 160, 230))
        for layer, (col, base) in enumerate([((122, 134, 160), 0.52), ((84, 100, 120), 0.62), ((46, 62, 74), 0.72)]):
            pts = [(-40, h)]
            x = -40
            while x < w + 60:
                step = int(w * rng.uniform(0.08, 0.18))
                px = x + step
                py = int(h * base - rng.uniform(0.09, 0.22) * h * (1 - layer * 0.25))
                pts.append((px, py))
                x = px
            pts.append((w + 60, h))
            d.polygon(pts, fill=col + (255,))
        d.rectangle([0, int(h * 0.82), w, h], fill=(28, 42, 34, 255))

    elif scene == "city":
        horizon = int(h * rng.uniform(0.55, 0.7))
        for i in range(rng.randint(10, 16)):
            bw = int(w * rng.uniform(0.04, 0.11))
            bh = int(h * rng.uniform(0.12, 0.46))
            bx = rng.randint(-bw, w - bw // 2)
            by = horizon - bh
            shade = rng.randint(28, 78)
            d.rectangle([bx, by, bx + bw, horizon + int(h * 0.1)], fill=(shade, shade + 8, shade + 22, 255))
            for wy in range(by + 8, horizon - 8, 12):
                for wx in range(bx + 6, bx + bw - 6, 10):
                    if rng.random() < 0.42:
                        d.rectangle([wx, wy, wx + 4, wy + 5], fill=(255, 214, 140, 210))
        d.rectangle([0, horizon + int(h * 0.1), w, h], fill=(18, 20, 30, 255))

    elif scene == "night":
        for i in range(rng.randint(8, 14)):
            bw = int(w * rng.uniform(0.05, 0.16))
            bh = int(h * rng.uniform(0.2, 0.6))
            bx = rng.randint(-bw // 2, w - bw // 2)
            by = h - bh - int(h * rng.uniform(0.02, 0.12))
            d.rectangle([bx, by, bx + bw, h], fill=(10, 12, 24, 255))
        overlay = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        od = ImageDraw.Draw(overlay)
        for i in range(rng.randint(30, 60)):
            cx = rng.randint(0, w)
            cy = rng.randint(int(h * 0.2), h)
            r = int(min(w, h) * rng.uniform(0.004, 0.022))
            color = rng.choice([(255, 190, 90), (255, 120, 60), (90, 200, 255), (255, 240, 180)])
            od.ellipse([cx - r, cy - r, cx + r, cy + r], fill=color + (rng.randint(120, 230),))
        img = Image.alpha_composite(img.convert("RGBA"), overlay).convert("RGB")
        d = ImageDraw.Draw(img, "RGBA")
        d.rectangle([int(w * 0.62), int(h * 0.34), int(w * 0.86), int(h * 0.42)], fill=(255, 40, 140, 210))
        d.rectangle([int(w * 0.65), int(h * 0.43), int(w * 0.83), int(h * 0.47)], fill=(60, 220, 255, 190))

    elif scene == "street":
        d.polygon([(0, h), (int(w * 0.35), int(h * 0.45)), (int(w * 0.65), int(h * 0.45)), (w, h)],
                  fill=(52, 54, 60, 255))
        d.line([(0, int(h * 0.45)), (w, int(h * 0.45))], fill=(210, 200, 186, 200), width=2)
        for i in range(rng.randint(4, 8)):
            pw = int(w * rng.uniform(0.03, 0.07))
            ph = int(h * rng.uniform(0.18, 0.34))
            px = rng.randint(0, w - pw)
            d.rectangle([px, int(h * 0.45) - ph, px + pw, int(h * 0.45)], fill=(22, 22, 26, 235))
        for i in range(rng.randint(10, 18)):
            lx = rng.randint(0, w)
            ly = rng.randint(int(h * 0.55), h)
            d.rectangle([lx, ly, lx + 6, ly + 3], fill=(255, 236, 190, 210))

    elif scene == "architecture":
        for i in range(rng.randint(6, 10)):
            x = rng.randint(-int(w * 0.1), int(w * 0.9))
            y = rng.randint(-int(h * 0.1), int(h * 0.7))
            ww = int(w * rng.uniform(0.08, 0.3))
            hh = int(h * rng.uniform(0.12, 0.4))
            col = rng.choice([(172, 184, 200), (210, 216, 222), (120, 132, 148), (240, 240, 236)])
            d.rectangle([x, y, x + ww, y + hh], fill=col + (255,))
            d.line([x, y, x + ww, y], fill=(70, 76, 88, 220), width=2)
        d.polygon([(0, h), (int(w * 0.5), int(h * 0.2)), (w, h)], fill=(240, 240, 236, 120))
        d.rectangle([int(w * 0.7), int(h * 0.6), int(w * 0.78), h], fill=(200, 90, 60, 235))

    elif scene == "travel":
        sx = int(w * rng.uniform(0.2, 0.8))
        sy = int(h * rng.uniform(0.25, 0.45))
        r = int(min(w, h) * 0.06)
        d.ellipse([sx - r, sy - r, sx + r, sy + r], fill=(255, 236, 170, 240))
        for i in range(3):
            y = int(h * (0.55 + i * 0.09))
            d.polygon([(0, h), (int(w * 0.3), y - int(h * 0.12)), (int(w * 0.6), y + int(h * 0.04)), (w, y - int(h * 0.08)), (w, h)],
                      fill=(rng.randint(40, 90), rng.randint(70, 120), rng.randint(60, 100), 235))
        for i in range(rng.randint(2, 4)):
            px = rng.randint(int(w * 0.1), int(w * 0.9))
            py = rng.randint(int(h * 0.6), int(h * 0.85))
            d.ellipse([px, py, px + 10, py + 10], fill=(30, 24, 20, 220))

    elif scene == "aerial":
        for i in range(rng.randint(7, 12)):
            cx = rng.randint(0, w)
            cy = rng.randint(0, h)
            rx = int(w * rng.uniform(0.05, 0.2))
            ry = int(h * rng.uniform(0.04, 0.16))
            col = rng.choice([(58, 92, 68), (86, 120, 84), (120, 140, 100), (44, 78, 96)])
            d.ellipse([cx - rx, cy - ry, cx + rx, cy + ry], fill=col + (255,))
        d.polygon([(0, int(h * 0.7)), (int(w * 0.3), int(h * 0.55)), (int(w * 0.55), int(h * 0.78)),
                   (int(w * 0.8), int(h * 0.62)), (w, int(h * 0.8)), (w, h), (0, h)],
                  fill=(36, 104, 130, 255))

    elif scene == "portrait":
        skin = rng.choice([(196, 148, 118), (172, 124, 96), (220, 176, 146)])
        cx, cy = int(w * rng.uniform(0.42, 0.58)), int(h * 0.5)
        rw, rh = int(w * 0.22), int(h * 0.3)
        d.ellipse([cx - rw, cy - rh, cx + rw, cy + rh], fill=skin + (255,))
        d.ellipse([cx - rw // 3, cy - rh // 3, cx - rw // 3 + 12, cy - rh // 3 + 12], fill=(30, 24, 22, 220))
        d.ellipse([cx + rw // 4, cy - rh // 3, cx + rw // 4 + 12, cy - rh // 3 + 12], fill=(30, 24, 22, 220))
        overlay = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        od = ImageDraw.Draw(overlay)
        for i in range(rng.randint(14, 26)):
            bx = rng.randint(0, w)
            by = rng.randint(0, h)
            br = int(min(w, h) * rng.uniform(0.01, 0.04))
            od.ellipse([bx - br, by - br, bx + br, by + br],
                       fill=rng.choice([(255, 220, 150), (255, 180, 120), (180, 220, 255)]) + (90,))
        img = Image.alpha_composite(img.convert("RGBA"), overlay).convert("RGB")
        d = ImageDraw.Draw(img, "RGBA")

    elif scene == "food":
        d.rectangle([0, int(h * 0.7), w, h], fill=(60, 36, 24, 255))
        for i in range(rng.randint(2, 4)):
            cx = int(w * rng.uniform(0.2, 0.8))
            cy = int(h * rng.uniform(0.35, 0.7))
            r = int(min(w, h) * rng.uniform(0.12, 0.2))
            d.ellipse([cx - r, cy - r // 2, cx + r, cy + r // 2], fill=(244, 240, 232, 255))
            d.ellipse([cx - r // 2, cy - r // 3, cx + r // 2, cy + r // 4],
                      fill=rng.choice([(210, 120, 60), (150, 180, 90), (220, 190, 110)]) + (255,))

    elif scene == "abstract":
        for i in range(rng.randint(12, 22)):
            x = rng.randint(0, w)
            y = rng.randint(0, h)
            ww = int(w * rng.uniform(0.05, 0.35))
            hh = int(h * rng.uniform(0.05, 0.35))
            col = rng.choice([(255, 80, 120), (80, 220, 200), (255, 200, 60), (90, 110, 255), (240, 240, 240)])
            if rng.random() < 0.5:
                d.ellipse([x - ww // 2, y - hh // 2, x + ww // 2, y + hh // 2], fill=col + (rng.randint(110, 220),))
            else:
                d.rectangle([x, y, x + ww, y + hh], fill=col + (rng.randint(110, 220),))

    if scene in ("night", "portrait", "street"):
        img = img.filter(ImageFilter.GaussianBlur(0.6))
    return img


# --------------------------------------------------------------------------------------
# manifest 条目骨架
# --------------------------------------------------------------------------------------
MANIFEST: list[dict] = []

ENTRY_KEYS = [
    "path", "kind", "format", "sizeBytes", "sha256",
    "expectIncluded", "expectExcludedReason", "expectAttachedTo", "expectParseError",
    "expectDevice", "expectCameraMake", "expectCameraModel", "expectLensModel",
    "expectFNumber", "expectExposureTime", "expectIso", "expectFocalLengthMm",
    "expectWidth", "expectHeight", "expectCapturedAt", "expectGps",
    "expectCity", "expectCountry", "expectProvince",
    "expectIsRaw", "expectRawFormat", "expectIsLog", "expectLogProfile",
    "expectColorPrimaries", "expectTransferCharacteristics", "expectVideoCodec",
    "expectDurationSeconds", "expectFrameRate", "expectHasAudio",
    "expectPostProcess", "expectHasDevelopEdits", "expectDevelopEngine",
    "expectHasEmbeddedXmp", "expectXmpPath", "expectSidecars",
    "expectRating", "expectColorLabel", "expectTitle", "expectCaption", "expectKeywords",
    "expectProcessVersion", "expectHasEmbeddedPreview",
    "expectEmbeddedPreviewWidth", "expectEmbeddedPreviewHeight",
    "expectRawPairWith", "expectDuplicateGroup", "expectDuplicateOf",
    "expectExactDuplicate", "expectNearDuplicateOf",
    "expectThemeHint", "expectToneHint", "probe",
]


def base_entry(rel: str, kind: str, fmt: str) -> dict:
    e = {k: None for k in ENTRY_KEYS}
    e["path"] = rel
    e["kind"] = kind
    e["format"] = fmt
    e["expectIncluded"] = True
    e["expectIsRaw"] = False
    e["expectHasDevelopEdits"] = False
    e["expectHasEmbeddedXmp"] = False
    e["expectSidecars"] = []
    e["expectKeywords"] = []
    return e


def find_entry(rel: str) -> dict | None:
    for e in MANIFEST:
        if e["path"] == rel:
            return e
    return None


def fill_file_meta(entry: dict, path: Path) -> None:
    entry["sizeBytes"] = path.stat().st_size
    entry["sha256"] = sha256_of(path)


def raw_format_code(raw: str) -> str:
    return {"ARW": "ARW", "CR2": "CR2", "NEF": "NEF", "DNG": "DNG"}.get(raw.upper(), raw.upper())


def set_image_expectations(entry: dict, spec: dict, cam: dict, dims) -> None:
    dev = DEVICES[spec["dev"]]
    city = CITIES[spec["city"]]
    entry.update(
        expectDevice=dev["device"],
        expectCameraMake=dev["make"],
        expectCameraModel=dev["model"],
        expectLensModel=cam["lens"],
        expectFNumber=round(cam["fnumber"], 3),
        expectExposureTime=round(cam["shutter"], 6),
        expectIso=cam["iso"],
        expectFocalLengthMm=round(cam["focal"], 3),
        expectWidth=dims[0],
        expectHeight=dims[1],
        expectCapturedAt=f"{spec['captured']}{city['offset']}",
        expectGps={"lat": round(city["lat"], 6), "lon": round(city["lon"], 6)},
        expectCity=spec["city"],
        expectCountry=city["country"],
        expectProvince=city.get("province"),
        expectThemeHint=SCENE_THEME.get(spec["scene"], (None, None))[0],
        expectToneHint=SCENE_THEME.get(spec["scene"], (None, None))[1],
    )


def apply_xmp_expectations(entry: dict, xmp: dict | None) -> None:
    if not xmp:
        return
    entry.update(
        expectHasEmbeddedXmp=True,
        expectPostProcess=True,
        expectHasDevelopEdits=True,
        expectDevelopEngine="Lightroom",
        expectRating=xmp["rating"],
        expectColorLabel=xmp["label"],
        expectTitle=xmp["title"],
        expectCaption=xmp["caption"],
        expectKeywords=xmp["keywords"],
        expectProcessVersion=xmp["process_version"],
    )


# --------------------------------------------------------------------------------------
# 普通图片（JPEG / PNG / TIFF）
# --------------------------------------------------------------------------------------
def write_jpeg_file(spec: dict) -> dict:
    rel = spec["path"]
    path = LIB_DIR / rel
    ensure_parent(path)
    dims = (int(spec["w"]), int(spec["h"]))
    img = render_scene(spec["scene"], dims[0], dims[1], rel)
    cam = pick_camera(rel, spec["dev"])
    exif = build_jpeg_exif(spec, cam, dims)
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=JPEG_QUALITY, exif=exif)
    data = buf.getvalue()
    xmp_info = None
    if spec.get("embed_xmp"):
        rng = rng_for(rel + "#xmp")
        title, caption, keywords, rating, label, profile = xmp_for_spec(spec, rng)
        packet = xmp_packet(title, caption, keywords, rating, label, True, "15.4", profile,
                            "Adobe Lightroom", spec["city"], CITIES[spec["city"]]["country"])
        data = inject_jpeg_xmp(data, packet)
        xmp_info = dict(title=title, caption=caption, keywords=keywords, rating=rating,
                        label=label, process_version="15.4")
    path.write_bytes(data)
    entry = base_entry(rel, "Image", "JPEG")
    set_image_expectations(entry, spec, cam, dims)
    entry["expectPostProcess"] = False
    apply_xmp_expectations(entry, xmp_info)
    fill_file_meta(entry, path)
    MANIFEST.append(entry)
    return entry


def write_other_image_file(spec: dict) -> dict:
    rel = spec["path"]
    path = LIB_DIR / rel
    ensure_parent(path)
    dims = (int(spec["w"]), int(spec["h"]))
    img = render_scene(spec["scene"], dims[0], dims[1], rel)
    fmt = spec["fmt"]
    if fmt == "PNG":
        img.save(path, "PNG", optimize=False)
        code = "PNG"
    else:
        img.save(path, "TIFF", compression="tiff_deflate")
        code = "TIFF"
    entry = base_entry(rel, "Image", code)
    entry.update(expectWidth=dims[0], expectHeight=dims[1],
                 expectThemeHint=SCENE_THEME.get(spec["scene"], (None, None))[0],
                 expectToneHint=SCENE_THEME.get(spec["scene"], (None, None))[1],
                 expectPostProcess=None)
    fill_file_meta(entry, path)
    MANIFEST.append(entry)
    return entry


# --------------------------------------------------------------------------------------
# RAW：合法 TIFF（deflate 主图）+ 完整内嵌 JPEG + IFD1 513/514 登记 + EXIF/GPS
# --------------------------------------------------------------------------------------
TYPE_SIZE = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 6: 1, 7: 1, 8: 2, 9: 4, 10: 8, 11: 4, 12: 8}


def _pack_typed(typ: int, values):
    if typ == 2:  # ASCII
        raw = values if isinstance(values, bytes) else str(values).encode("utf-8")
        if not raw.endswith(b"\x00"):
            raw += b"\x00"
        return raw, len(raw)
    vals = list(values)
    if typ == 1 or typ == 7:
        return bytes(vals), len(vals)
    if typ == 3:
        return struct.pack("<" + "H" * len(vals), *vals), len(vals)
    if typ == 4:
        return struct.pack("<" + "I" * len(vals), *vals), len(vals)
    if typ == 5:
        return b"".join(struct.pack("<II", int(n), int(d)) for n, d in vals), len(vals)
    if typ == 9:
        return struct.pack("<" + "i" * len(vals), *vals), len(vals)
    if typ == 10:
        return b"".join(struct.pack("<ii", int(n), int(d)) for n, d in vals), len(vals)
    raise ValueError(f"unsupported TIFF type {typ}")


def assemble_tiff(ifds, blobs, cr2: bool = False) -> bytes:
    """ifds: [[(tag, type, values_or_callable(offsets), blob_index|None), ...], ...]"""
    header_len = 16 if cr2 else 8
    sizes = [2 + 12 * len(entries) + 4 for entries in ifds]
    offsets: list[int] = []
    cur = header_len
    for s in sizes:
        offsets.append(cur)
        cur += s
    data_start = cur
    resolved = []
    for entries in ifds:
        res = []
        for (tag, typ, values, blob) in entries:
            if callable(values):
                values = values(offsets)
            res.append((tag, typ, values, blob))
        resolved.append(res)
    data = bytearray()
    ext_off: dict[tuple[int, int], int] = {}
    for i, entries in enumerate(resolved):
        for j, (tag, typ, values, blob) in enumerate(entries):
            if blob is not None:
                continue
            raw, _count = _pack_typed(typ, values)
            if len(raw) > 4:
                if len(data) % 2:
                    data.append(0)
                ext_off[(i, j)] = data_start + len(data)
                data += raw
    blob_offs = []
    for b in blobs:
        if len(data) % 2:
            data.append(0)
        blob_offs.append(data_start + len(data))
        data += b
    out = bytearray(struct.pack("<2sHI", b"II", 42, offsets[0]))
    if cr2:
        out += b"CR\x02\x00" + struct.pack("<I", offsets[0])
    assert len(out) == header_len, (len(out), header_len)
    for i, entries in enumerate(resolved):
        out += struct.pack("<H", len(entries))
        for j, (tag, typ, values, blob) in enumerate(entries):
            if blob is not None:
                out += struct.pack("<HHI", tag, 4, 1)
                out += struct.pack("<I", blob_offs[blob])
                continue
            raw, count = _pack_typed(typ, values)
            out += struct.pack("<HHI", tag, typ, count)
            if len(raw) <= 4:
                out += raw + b"\x00" * (4 - len(raw))
            else:
                out += struct.pack("<I", ext_off[(i, j)])
        out += struct.pack("<I", offsets[i + 1] if i + 1 < len(ifds) else 0)
    assert len(out) == data_start, (len(out), data_start)
    out += data
    return bytes(out)


def write_raw_file(spec: dict) -> dict:
    rel = spec["path"]
    path = LIB_DIR / rel
    ensure_parent(path)
    dims = (int(spec["w"]), int(spec["h"]))
    img = render_scene(spec["scene"], dims[0], dims[1], rel)
    cam = pick_camera(rel, spec["dev"])
    dev = DEVICES[spec["dev"]]
    city = CITIES[spec["city"]]
    # 内嵌预览 JPEG（完整 SOI..EOI）
    jbuf = io.BytesIO()
    img.save(jbuf, "JPEG", quality=JPEG_QUALITY)
    preview = jbuf.getvalue()
    # TIFF 主图：同画面 deflate RGB
    strip = zlib.compress(img.tobytes(), 6)
    dt = exif_dt(spec["captured"])
    offset = city["offset"]
    g = _gps_tag_values(city["lat"], city["lon"], ALTITUDE_BY_CITY.get(spec["city"], 20.0),
                        spec["captured"], offset)
    shutter = (cam["shutter_num"], cam["shutter_den"])
    fnum = (int(round(cam["fnumber"] * 10)), 10)
    focal = (int(round(cam["focal"] * 100)), 100)

    ifd0 = [
        (254, 4, [0], None),                    # NewSubfileType: main image
        (256, 4, [dims[0]], None),              # ImageWidth
        (257, 4, [dims[1]], None),              # ImageLength
        (258, 3, [8, 8, 8], None),              # BitsPerSample
        (259, 3, [8], None),                    # Compression: Adobe deflate
        (262, 3, [2], None),                    # Photometric: RGB
        (271, 2, dev["make"], None),            # Make
        (272, 2, dev["model"], None),           # Model
        (273, 4, [0], 0),                       # StripOffsets -> blob 0
        (274, 3, [1], None),                    # Orientation
        (277, 3, [3], None),                    # SamplesPerPixel
        (278, 4, [dims[1]], None),              # RowsPerStrip
        (279, 4, [len(strip)], None),           # StripByteCounts
        (282, 5, [(72, 1)], None),              # XResolution
        (283, 5, [(72, 1)], None),              # YResolution
        (284, 3, [1], None),                    # PlanarConfiguration
        (296, 3, [2], None),                    # ResolutionUnit: inch
        (305, 2, "StjMediaViewer SampleGenerator", None),  # Software
        (306, 2, dt, None),                     # DateTime
        (315, 2, CREATOR, None),                # Artist
        (33432, 2, COPYRIGHT, None),            # Copyright
        (34665, 4, lambda offs: [offs[1]], None),  # ExifIFD
        (34853, 4, lambda offs: [offs[2]], None),  # GPS IFD
    ]
    if spec["raw"].upper() == "DNG":
        ifd0 += [
            (50706, 1, [1, 4, 0, 0], None),     # DNGVersion
            (50707, 1, [1, 1, 0, 0], None),     # DNGBackwardVersion
            (50708, 2, f"{dev['make']} {dev['model']} DNG", None),  # UniqueCameraModel
        ]
    exif_ifd = [
        (33434, 5, [shutter], None),            # ExposureTime
        (33437, 5, [fnum], None),               # FNumber
        (34855, 3, [cam["iso"]], None),         # ISOSpeedRatings
        (36864, 7, b"0232", None),              # ExifVersion
        (36867, 2, dt, None),                   # DateTimeOriginal
        (36868, 2, dt, None),                   # DateTimeDigitized
        (36880, 2, offset, None),               # OffsetTime
        (36881, 2, offset, None),               # OffsetTimeOriginal
        (36882, 2, offset, None),               # OffsetTimeDigitized
        (37386, 5, [focal], None),              # FocalLength
        (40961, 3, [1], None),                  # ColorSpace: sRGB
        (40962, 4, [dims[0]], None),            # PixelXDimension
        (40963, 4, [dims[1]], None),            # PixelYDimension
        (41989, 3, [int(round(cam["focal"]))], None),  # FocalLengthIn35mmFilm
        (42035, 2, dev["make"], None),          # LensMake
        (42036, 2, cam["lens"], None),          # LensModel
    ]
    gps_ifd = [
        (0, 1, [2, 3, 0, 0], None),             # GPSVersionID
        (1, 2, g["lat_ref"], None),
        (2, 5, [(int(g["lat"][0]), 1), (int(g["lat"][1]), 1), (int(round(g["lat"][2] * 1000)), 1000)], None),
        (3, 2, g["lon_ref"], None),
        (4, 5, [(int(g["lon"][0]), 1), (int(g["lon"][1]), 1), (int(round(g["lon"][2] * 1000)), 1000)], None),
        (5, 1, [0], None),                      # AltitudeRef: above sea level
        (6, 5, [(int(round(g["alt"] * 100)), 100)], None),
        (7, 5, [(int(g["utc"][0]), 1), (int(g["utc"][1]), 1), (int(g["utc"][2]), 1)], None),
        (29, 2, g["utc_date"], None),           # GPSDateStamp
    ]
    ifd1 = [
        (254, 4, [1], None),                    # thumbnail
        (256, 4, [dims[0]], None),
        (257, 4, [dims[1]], None),
        (258, 3, [8, 8, 8], None),
        (259, 3, [6], None),                    # JPEG compression
        (262, 3, [6], None),                    # YCbCr
        (277, 3, [3], None),
        (282, 5, [(72, 1)], None),
        (283, 5, [(72, 1)], None),
        (284, 3, [1], None),
        (296, 3, [2], None),
        (513, 4, [0], 1),                       # JPEGInterchangeFormat -> blob 1 (内嵌预览)
        (514, 4, [len(preview)], None),         # JPEGInterchangeFormatLength
    ]
    data = assemble_tiff([ifd0, exif_ifd, gps_ifd, ifd1], [strip, preview],
                         cr2=spec["raw"].upper() == "CR2")
    path.write_bytes(data)
    entry = base_entry(rel, "Image", raw_format_code(spec["raw"]))
    set_image_expectations(entry, spec, cam, dims)
    entry.update(
        expectIsRaw=True,
        expectRawFormat=raw_format_code(spec["raw"]),
        expectPostProcess=False,
        expectHasEmbeddedPreview=True,
        expectEmbeddedPreviewWidth=dims[0],
        expectEmbeddedPreviewHeight=dims[1],
    )
    fill_file_meta(entry, path)
    MANIFEST.append(entry)
    return entry


# --------------------------------------------------------------------------------------
# 反例 / 非媒体文件
# --------------------------------------------------------------------------------------
def write_junk_files() -> None:
    for spec in JUNK_SPECS:
        rel = spec["path"]
        path = LIB_DIR / rel
        ensure_parent(path)
        kind = spec["kind"]
        try:
            if kind == "empty":
                path.write_bytes(b"")
            elif kind == "real_jpeg_bad_dir":
                img = render_scene("city", 800, 533, rel)
                img.save(path, "JPEG", quality=60)
            elif kind == "real_png_bad_dir":
                img = render_scene("abstract", 320, 240, rel)
                img.save(path, "PNG")
            elif kind == "hidden_jpeg":
                img = render_scene("night", 800, 600, rel)
                img.save(path, "JPEG", quality=60)
            elif kind == "binary" or kind == "appledouble":
                path.write_bytes(spec["content"])
            else:
                text = spec["content"]
                path.write_text(text, encoding="utf-8")
        except Exception as exc:  # pragma: no cover
            warn(f"反例文件生成失败 {rel}: {exc}")
            continue
        # 期望归属
        if kind in ("real_jpeg_bad_dir", "real_png_bad_dir"):
            fmt = "JPEG" if kind == "real_jpeg_bad_dir" else "PNG"
            entry = base_entry(rel, "Image", fmt)
            entry.update(expectIncluded=False, expectExcludedReason=spec["reason"],
                         expectWidth=800 if kind == "real_jpeg_bad_dir" else 320,
                         expectHeight=533 if kind == "real_jpeg_bad_dir" else 240)
        elif kind == "hidden_jpeg":
            entry = base_entry(rel, "Image", "JPEG")
            entry.update(expectIncluded=False, expectExcludedReason=spec["reason"])
        elif kind == "appledouble":
            # macOS AppleDouble：以 "._" 开头、扩展名是 .JPG 但内容不是媒体，按文件名规则排除
            entry = base_entry(rel, "NonMedia", "OTHER")
            entry.update(expectIncluded=False, expectExcludedReason="appledouble")
        elif kind == "empty":
            # 0 字节文件：扫描器按 ScanOptions.MinFileSize=1 直接跳过，不产生 asset
            # （产品决策：0 字节不可能是有效媒体，入库只会污染列表）
            entry = base_entry(rel, "Image", "JPEG")
            entry.update(expectIncluded=False, expectExcludedReason="empty-file",
                         expectParseError=None)
        elif rel.lower().endswith((".jpg", ".jpeg", ".png")):
            entry = base_entry(rel, "Image", "JPEG" if rel.lower().endswith((".jpg", ".jpeg")) else "PNG")
            entry.update(expectIncluded=True, expectExcludedReason=None, expectParseError=True)
        else:
            entry = base_entry(rel, "NonMedia", "OTHER")
            entry.update(expectIncluded=False, expectExcludedReason=spec["reason"])
        fill_file_meta(entry, path)
        MANIFEST.append(entry)


# --------------------------------------------------------------------------------------
# 重复 / 近重复
# --------------------------------------------------------------------------------------
EXACT_COPY_KEYS = [
    "kind", "format", "expectDevice", "expectCameraMake", "expectCameraModel", "expectLensModel",
    "expectFNumber", "expectExposureTime", "expectIso", "expectFocalLengthMm",
    "expectWidth", "expectHeight", "expectCapturedAt", "expectGps",
    "expectCity", "expectCountry", "expectProvince", "expectIsRaw", "expectRawFormat",
    "expectIsLog", "expectLogProfile", "expectColorPrimaries", "expectTransferCharacteristics",
    "expectVideoCodec", "expectDurationSeconds", "expectHasAudio",
    "expectPostProcess", "expectHasDevelopEdits", "expectDevelopEngine",
    "expectHasEmbeddedXmp", "expectRating", "expectColorLabel", "expectTitle",
    "expectCaption", "expectKeywords", "expectProcessVersion", "expectHasEmbeddedPreview",
    "expectEmbeddedPreviewWidth", "expectEmbeddedPreviewHeight",
    "expectRawPairWith", "expectThemeHint", "expectToneHint",
]


def copy_entry_expectations(base: dict, rel: str) -> dict:
    e = base_entry(rel, base["kind"], base["format"])
    for key in EXACT_COPY_KEYS:
        e[key] = base.get(key)
    for key in ("expectKeywords", "expectSidecars"):
        e[key] = list(base.get(key) or [])
    # 边车不会被复制（sidecar 只挂原文件）
    e["expectSidecars"] = []
    e["expectXmpPath"] = None
    return e


def clear_sidecar_derived_expectations(entry: dict) -> None:
    """副本没有配套边车：凡是从 .xmp/.dop/.cos 推导的后期/评分/标签/关键词期望都清空。

    否则会出现"manifest 期望有 rating/keywords，但文件本身读不到"的错误期望（宁少勿错）。
    精确副本若带的是**内嵌** XMP（不依赖边车），不调用本函数。
    """
    entry.update(
        expectPostProcess=None,
        expectHasDevelopEdits=False,
        expectDevelopEngine=None,
        expectRating=None,
        expectColorLabel=None,
        expectTitle=None,
        expectCaption=None,
        expectKeywords=[],
        expectProcessVersion=None,
        expectXmpPath=None,
        expectSidecars=[],
    )


def write_duplicates() -> None:
    for grp in DUP_GROUPS:
        base = find_entry(grp["base"])
        if base is None:
            warn(f"重复组 {grp['group']} 的基准文件不存在: {grp['base']}")
            continue
        base_path = LIB_DIR / grp["base"]
        base["expectDuplicateGroup"] = grp["group"]
        sidecar_derived = bool(base.get("expectXmpPath") or base.get("expectSidecars"))
        for copy_rel in grp["copies"]:
            path = LIB_DIR / copy_rel
            ensure_parent(path)
            shutil.copyfile(base_path, path)
            entry = copy_entry_expectations(base, copy_rel)
            entry.update(expectDuplicateGroup=grp["group"],
                         expectDuplicateOf=grp["base"],
                         expectExactDuplicate=True)
            if sidecar_derived:
                # 基准的后期信息来自边车，但 copyfile 只复制主文件、不带边车
                clear_sidecar_derived_expectations(entry)
            fill_file_meta(entry, path)
            MANIFEST.append(entry)
    for grp in NEAR_DUP_GROUPS:
        base = find_entry(grp["base"])
        if base is None:
            warn(f"近重复组 {grp['group']} 的基准文件不存在: {grp['base']}")
            continue
        base_path = LIB_DIR / grp["base"]
        copy_rel = grp["copy"]
        path = LIB_DIR / copy_rel
        ensure_parent(path)
        try:
            im = Image.open(base_path)
            exif = im.info.get("exif", b"")
            im.load()
            if exif:
                im.save(path, "JPEG", quality=grp["quality"], exif=exif)
            else:
                im.save(path, "JPEG", quality=grp["quality"])
        except Exception as exc:
            warn(f"近重复文件生成失败 {copy_rel}: {exc}")
            continue
        entry = copy_entry_expectations(base, copy_rel)
        # 近重复：只保留画面/EXIF 层面的期望；嵌入 XMP 在重编码中丢失
        entry.update(expectDuplicateGroup=grp["group"],
                     expectNearDuplicateOf=grp["base"],
                     expectExactDuplicate=False,
                     expectHasEmbeddedXmp=False,
                     expectPostProcess=False,
                     expectHasDevelopEdits=False,
                     expectDevelopEngine=None,
                     expectRating=None,
                     expectColorLabel=None,
                     expectTitle=None,
                     expectCaption=None,
                     expectKeywords=[])
        fill_file_meta(entry, path)
        MANIFEST.append(entry)


# --------------------------------------------------------------------------------------
# ffmpeg 探测与调用
# --------------------------------------------------------------------------------------
class Toolchain:
    def __init__(self):
        self.ffmpeg: Path | None = None
        self.ffprobe: Path | None = None
        self.source = "none"
        self.video_fallback = False
        self.audio_fallback: list[str] = []

    # -- 探测 --
    def detect(self) -> None:
        env = os.environ.get("FFMPEG_PATH", "").strip()
        if env and Path(env).is_file():
            self.ffmpeg = Path(env)
            self.ffprobe = Path(env).with_name("ffprobe.exe" if env.lower().endswith(".exe") else "ffprobe")
            if not self.ffprobe.is_file():
                self.ffprobe = None
            self.source = "FFMPEG_PATH"
        if self.ffmpeg is None:
            for name in ("ffmpeg.exe", "ffmpeg"):
                cand = FFMPEG_BIN / name
                if cand.is_file():
                    self.ffmpeg = cand
                    probe = cand.with_name("ffprobe.exe" if name.endswith(".exe") else "ffprobe")
                    self.ffprobe = probe if probe.is_file() else None
                    self.source = str(cand.relative_to(REPO_ROOT)) if cand.is_relative_to(REPO_ROOT) else str(cand)
                    break
        if self.ffmpeg is None and FFMPEG_ZIP.is_file():
            self._extract_from_zip()
        if self.ffmpeg is None:
            which = shutil.which("ffmpeg")
            if which:
                self.ffmpeg = Path(which)
                self.ffprobe = Path(shutil.which("ffprobe")) if shutil.which("ffprobe") else None
                self.source = "PATH"
        if self.ffmpeg is not None and self.ffprobe is None:
            sibling = self.ffmpeg.with_name("ffprobe.exe" if self.ffmpeg.suffix.lower() == ".exe" else "ffprobe")
            if sibling.is_file():
                self.ffprobe = sibling
        if self.ffmpeg is None:
            warn("未找到 ffmpeg（tools/ffmpeg/bin、tools/ffmpeg/ff.zip、PATH 都没有），视频/音频将走纯 Python 兜底")
        else:
            print(f"  ffmpeg: {self.ffmpeg}  (来源: {self.source}, ffprobe: {self.ffprobe is not None})")

    def _extract_from_zip(self) -> None:
        try:
            with zipfile.ZipFile(FFMPEG_ZIP) as z:
                names = z.namelist()
                ff = next((n for n in names if n.replace("\\", "/").endswith("/bin/ffmpeg.exe") or n.endswith("ffmpeg.exe")), None)
                fp = next((n for n in names if n.replace("\\", "/").endswith("/bin/ffprobe.exe") or n.endswith("ffprobe.exe")), None)
                if not ff:
                    warn("tools/ffmpeg/ff.zip 里没有 ffmpeg.exe，忽略")
                    return
                dest = TOOLING_DIR / "ffmpeg"
                dest.mkdir(parents=True, exist_ok=True)
                out_ff = dest / "ffmpeg.exe"
                out_ff.write_bytes(z.read(ff))
                self.ffmpeg = out_ff
                if fp:
                    out_fp = dest / "ffprobe.exe"
                    out_fp.write_bytes(z.read(fp))
                    self.ffprobe = out_fp
                self.source = "zip:tools/ffmpeg/ff.zip"
        except (zipfile.BadZipFile, OSError) as exc:
            warn(f"tools/ffmpeg/ff.zip 不完整或损坏（可能仍在下载），跳过: {exc}")

    # -- 执行 --
    def run(self, args, timeout: int = 120):
        if self.ffmpeg is None:
            return False, "ffmpeg missing"
        cmd = [str(self.ffmpeg), "-hide_banner", "-y", "-loglevel", "error"] + [str(a) for a in args]
        try:
            proc = subprocess.run(cmd, capture_output=True, timeout=timeout)
        except Exception as exc:
            return False, str(exc)
        if proc.returncode != 0:
            return False, proc.stderr.decode("utf-8", "replace")[-1200:]
        return True, ""

    def probe(self, path: Path):
        """优先 ffprobe JSON；没有 ffprobe 时退回 `ffmpeg -i` stderr 解析。"""
        if self.ffprobe is not None:
            cmd = [str(self.ffprobe), "-v", "error", "-show_entries",
                   "format=duration,size,bit_rate:format_tags:stream=codec_name,codec_type,width,height,"
                   "channels,sample_rate,color_primaries,color_transfer,color_space,avg_frame_rate,r_frame_rate",
                   "-of", "json", str(path)]
            try:
                proc = subprocess.run(cmd, capture_output=True, timeout=60)
                if proc.returncode == 0:
                    return json.loads(proc.stdout.decode("utf-8", "replace") or "{}")
            except Exception:
                pass
        if self.ffmpeg is not None:
            return probe_with_ffmpeg(self.ffmpeg, path)
        return None

    def probe_method(self) -> str:
        if self.ffprobe is not None:
            return "ffprobe"
        if self.ffmpeg is not None:
            return "ffmpeg-stderr"
        return "none"

    def can_decode_video(self, path: Path) -> bool:
        """抽一帧验证真的能解码（视频缩略图链路）。"""
        if self.ffmpeg is None:
            return False
        cmd = [str(self.ffmpeg), "-hide_banner", "-loglevel", "error",
               "-ss", "0.2", "-i", str(path), "-frames:v", "1", "-f", "null", "-"]
        try:
            return subprocess.run(cmd, capture_output=True, timeout=60).returncode == 0
        except Exception:
            return False


def _loc_string(lat: float, lon: float) -> str:
    return f"{lat:+.4f}{lon:+.4f}/"


def _parse_loc_string(loc: str):
    """解析 ISO6709 简写 "+35.6762+139.6503/" 或 "35.6762+139.6503/"。"""
    try:
        s = loc.strip().rstrip("/")
        if s and s[0].isdigit():
            s = "+" + s
        cut = max(s.rfind("+", 1), s.rfind("-", 1))
        return float(s[:cut]), float(s[cut:])
    except Exception:
        return None, None


def probe_stream(probe: dict, codec_type: str) -> dict | None:
    for s in (probe or {}).get("streams", []) or []:
        if s.get("codec_type") == codec_type:
            return s
    return None


def probe_tags(probe: dict) -> dict:
    tags = (probe or {}).get("format", {}).get("tags", {}) or {}
    out = {str(k).lower(): v for k, v in tags.items()}
    for key in list(out.keys()):
        if key.endswith("-eng") and key[:-4] not in out:
            out[key[:-4]] = out[key]
    return out


# ---- ffmpeg -i（stderr）解析：没有 ffprobe 时的探测方案 ----
_FF_RE_DURATION = re.compile(r"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)")
_FF_RE_STREAM = re.compile(r"Stream #\d+:\d+.*?: (Video|Audio): (\w+)")
_FF_RE_DIMS = re.compile(r"\b(\d{2,5})x(\d{2,5})\b")
_FF_RE_FPS = re.compile(r"([\d.]+)\s+fps")
_FF_RE_AUDIO = re.compile(r"(\d+) Hz,\s*(mono|stereo|[\d.]+ channels?)")
_FF_RE_META = re.compile(r"^\s{4,}([A-Za-z0-9_.\-]+)\s*:\s*(.*)$")
_FF_COLOR_TOKENS = re.compile(
    r"^(bt\d+[a-z]*|smpte\d+[a-z]*|iec[0-9\-]+|arib-std-b67|arib-std-b67|linear|log\d+|unknown|reserved)$")


def _parse_ffmpeg_color(stream_line: str) -> dict:
    """从 `yuv420p(tv, bt2020nc/bt2020/arib-std-b67, progressive)` 里取色彩三要素。"""
    match = re.search(r"(?:yuv|gbr|rgb|gray|ya\d+)[0-9a-z]*\(([^)]*)\)", stream_line)
    if not match:
        return {}
    for part in (p.strip() for p in match.group(1).split(",")):
        if "/" in part:
            bits = part.split("/")
            if len(bits) == 3:
                return {"color_space": bits[0], "color_primaries": bits[1], "color_transfer": bits[2]}
    for part in (p.strip() for p in match.group(1).split(",")):
        if _FF_COLOR_TOKENS.match(part):
            return {"color_space": part, "color_primaries": part, "color_transfer": part}
    return {}


def probe_with_ffmpeg(ffmpeg: Path, path: Path) -> dict | None:
    """`ffmpeg -i file` 没有 ffprobe 时也能拿到时长/分辨率/帧率/codec/color/tags。"""
    try:
        proc = subprocess.run([str(ffmpeg), "-hide_banner", "-i", str(path)],
                              capture_output=True, timeout=60)
    except Exception:
        return None
    text = proc.stderr.decode("utf-8", "replace")
    if "Input #" not in text:
        return None
    probe: dict = {"format": {"size": path.stat().st_size, "duration": None, "tags": {}}, "streams": []}
    match = _FF_RE_DURATION.search(text)
    if match:
        hours, minutes, seconds = match.groups()
        probe["format"]["duration"] = int(hours) * 3600 + int(minutes) * 60 + float(seconds)
    lines = text.splitlines()
    first_stream = next((i for i, line in enumerate(lines) if "Stream #" in line), len(lines))
    for line in lines[:first_stream]:
        meta_match = _FF_RE_META.match(line)
        if meta_match:
            key = meta_match.group(1).strip().lower()
            if key in {"duration", "start", "bitrate", "input"}:
                continue
            probe["format"]["tags"][key] = meta_match.group(2).strip()
    for line in lines[first_stream:]:
        if "Stream #" not in line:
            continue
        stream_match = _FF_RE_STREAM.search(line)
        if not stream_match:
            continue
        media_type, codec = stream_match.group(1), stream_match.group(2)
        if media_type == "Video":
            item = {"codec_type": "video", "codec_name": codec}
            dims = _FF_RE_DIMS.search(line)
            if dims:
                item["width"], item["height"] = int(dims.group(1)), int(dims.group(2))
            fps = _FF_RE_FPS.search(line)
            if fps:
                item["avg_frame_rate"] = fps.group(1)
            item.update(_parse_ffmpeg_color(line))
        else:
            item = {"codec_type": "audio", "codec_name": codec}
            audio = _FF_RE_AUDIO.search(line)
            if audio:
                item["sample_rate"] = int(audio.group(1))
                channels = audio.group(2)
                item["channels"] = {"mono": 1, "stereo": 2}.get(channels, 2)
        probe["streams"].append(item)
    return probe


def patch_colr(path: Path, primaries: int, transfer: int, matrix: int) -> int:
    """把容器 colr(nclc/nclx) 的 primaries/transfer/matrix 改成预期值（等长字节补丁）。

    ffmpeg 的 mov muxer 在 `-movflags +write_colr` 时写 colr，但 lavfi 源经 x264 重编码后
    容器侧 primaries/transfer 常是 unspecified(2)；这里在生成后按 CICP 编号精确回写：
      BT.709 = (1,1,1)；HLG/BT.2020 = (9,18,9)。
    """
    data = bytearray(path.read_bytes())
    patched = 0
    pos = 0
    while True:
        idx = data.find(b"colr", pos)
        if idx < 0:
            break
        ctype = bytes(data[idx + 4:idx + 8])
        if ctype in (b"nclc", b"nclx") and idx + 14 <= len(data):
            data[idx + 8:idx + 10] = primaries.to_bytes(2, "big")
            data[idx + 10:idx + 12] = transfer.to_bytes(2, "big")
            data[idx + 12:idx + 14] = matrix.to_bytes(2, "big")
            patched += 1
        pos = idx + 4
    if patched:
        path.write_bytes(data)
    return patched


def read_colr(path: Path) -> dict | None:
    """读取视频容器里的 colr 盒子（供自检断言）。"""
    data = path.read_bytes()
    pos = 0
    while True:
        idx = data.find(b"colr", pos)
        if idx < 0:
            return None
        ctype = data[idx + 4:idx + 8]
        if ctype in (b"nclc", b"nclx") and idx + 14 <= len(data):
            return {"type": ctype.decode("ascii"),
                    "primaries": int.from_bytes(data[idx + 8:idx + 10], "big"),
                    "transfer": int.from_bytes(data[idx + 10:idx + 12], "big"),
                    "matrix": int.from_bytes(data[idx + 12:idx + 14], "big")}
        pos = idx + 4


# --------------------------------------------------------------------------------------
# 视频
# --------------------------------------------------------------------------------------
def make_video(spec: dict, tools: Toolchain) -> dict | None:
    rel = spec["path"]
    path = LIB_DIR / rel
    ensure_parent(path)
    city = CITIES[spec["city"]]
    loc = _loc_string(city["lat"], city["lon"])
    if tools.ffmpeg is None:
        return make_video_fallback(spec)

    w, h, fps, dur = spec["w"], spec["h"], spec["fps"], spec["dur"]
    is_mov = spec["ext"] == "mov"
    hlg = bool(spec.get("hlg"))
    is_slog3 = bool(spec.get("comment")) and str(spec["comment"]).lower().startswith("s-log3")
    # HLG = BT.2020 primaries(9) + HLG transfer(18)；其余统一 BT.709(1,1,1)
    colr = (9, 18, 9) if hlg else (1, 1, 1)

    cmd = ["-f", "lavfi", "-i", f"testsrc2=size={w}x{h}:rate={fps}:duration={dur}"]
    if spec.get("audio"):
        cmd += ["-f", "lavfi", "-i", f"sine=frequency=440:duration={dur}"]
    cmd += ["-c:v", "libx264", "-preset", "veryfast", "-crf", "30", "-pix_fmt", "yuv420p"]
    if spec.get("audio"):
        cmd += ["-c:a", "aac", "-b:a", "32k", "-shortest"]
    if hlg:
        cmd += ["-color_trc", "arib-std-b67", "-color_primaries", "bt2020", "-colorspace", "bt2020nc"]
        cmd += ["-x264-params", "colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc"]
    else:
        cmd += ["-color_trc", "bt709", "-color_primaries", "bt709", "-colorspace", "bt709"]
        cmd += ["-x264-params", "colorprim=bt709:transfer=bt709:colormatrix=bt709"]
    # 同时写经典 ©mak/©mod/©xyz/©cmt 与 QuickTime metadata keys（com.apple.quicktime.*），
    # 覆盖自研解析器可能走的两种读取路径。
    meta = [
        "-metadata", f"make={spec['make']}",
        "-metadata", f"model={spec['model']}",
        "-metadata", f"creation_time={spec['created']}",
        "-metadata", f"location={loc}",
        "-metadata", f"com.apple.quicktime.make={spec['make']}",
        "-metadata", f"com.apple.quicktime.model={spec['model']}",
        "-metadata", f"com.apple.quicktime.location.ISO6709={loc}",
        "-metadata", f"com.apple.quicktime.creationdate={spec['created']}",
    ]
    if spec.get("comment"):
        meta += ["-metadata", f"comment={spec['comment']}",
                 "-metadata", f"com.apple.quicktime.comment={spec['comment']}"]
    cmd += meta
    cmd += ["-movflags", "+use_metadata_tags+write_colr+faststart", str(path)]

    ok, err = tools.run(cmd, timeout=120)
    if not ok or not path.exists():
        warn(f"视频 {rel} ffmpeg 生成失败，改用兜底骨架: {err[-400:]}")
        return make_video_fallback(spec)
    colr_patched = patch_colr(path, *colr)
    if colr_patched == 0:
        warn(f"视频 {rel}: 未找到 colr 盒子，色彩元数据缺失")
    if not tools.can_decode_video(path):
        warn(f"视频 {rel}: ffmpeg 无法解码抽帧（缩略图链路可能失败）")

    probe = tools.probe(path)
    tags = probe_tags(probe)
    entry = base_entry(rel, "Video", "MOV" if is_mov else "MP4")
    entry.update(
        expectDevice=spec["device"],
        expectCameraMake=spec["make"],
        expectCameraModel=spec["model"],
        expectCity=spec["city"],
        expectCountry=city["country"],
        expectGps={"lat": round(city["lat"], 6), "lon": round(city["lon"], 6)},
        expectCapturedAt=spec["created"].replace("Z", "+00:00"),
        expectIsLog=(True if is_slog3 else None),
        expectLogProfile=("S-Log3" if is_slog3 else None),
        expectPostProcess=None,
        expectTransferCharacteristics=("arib-std-b67" if hlg else "bt709"),
        expectColorPrimaries=("bt2020" if hlg else "bt709"),
        expectWidth=w,
        expectHeight=h,
        expectDurationSeconds=float(dur),
        expectHasAudio=bool(spec.get("audio")),
        expectRawPairWith=None,
    )
    vstream = probe_stream(probe, "video")
    astream = probe_stream(probe, "audio")
    if probe is not None:
        fmt = probe.get("format", {}) or {}
        if fmt.get("duration"):
            entry["expectDurationSeconds"] = round(float(fmt["duration"]), 2)
        if vstream:
            entry["expectWidth"] = int(vstream.get("width") or w)
            entry["expectHeight"] = int(vstream.get("height") or h)
            entry["expectVideoCodec"] = vstream.get("codec_name")
            if vstream.get("color_transfer"):
                entry["expectTransferCharacteristics"] = vstream["color_transfer"]
            if vstream.get("color_primaries"):
                entry["expectColorPrimaries"] = vstream["color_primaries"]
            if vstream.get("avg_frame_rate"):
                try:
                    entry["expectFrameRate"] = round(float(str(vstream["avg_frame_rate"]).split("/")[0]), 3)
                except ValueError:
                    pass
        entry["expectHasAudio"] = astream is not None
        entry["probe"] = {
            "duration": fmt.get("duration"),
            "tags": tags,
            "video": {k: vstream.get(k) for k in ("codec_name", "width", "height", "color_transfer",
                                                  "color_primaries", "avg_frame_rate")} if vstream else None,
            "audio": {k: astream.get(k) for k in ("codec_name", "channels", "sample_rate")} if astream else None,
            "colr": read_colr(path),
        }
        # 用容器实测标签校正期望（宁少勿错）：只有容器里真的有才保留该期望
        entry["expectCameraMake"] = tags.get("make") or tags.get("com.apple.quicktime.make") or spec["make"]
        entry["expectCameraModel"] = tags.get("model") or tags.get("com.apple.quicktime.model") or spec["model"]
        loc_value = tags.get("location") or tags.get("com.apple.quicktime.location.iso6709") \
            or tags.get("com.apple.quicktime.location.ISO6709")
        if not loc_value:
            entry["expectGps"] = None
            entry["expectCity"] = None
            entry["expectCountry"] = None
        else:
            lat, lon = _parse_loc_string(loc_value)
            if lat is not None:
                entry["expectGps"] = {"lat": round(lat, 6), "lon": round(lon, 6)}
        comment_text = tags.get("comment") or tags.get("com.apple.quicktime.comment") or ""
        if "s-log3" in str(comment_text).lower():
            entry["expectIsLog"] = True
            entry["expectLogProfile"] = "S-Log3"
    fill_file_meta(entry, path)
    MANIFEST.append(entry)
    return entry


# ------------------------------- 纯 Python ISOBMFF 兜底 ------------------------------
def _box(typ: bytes, payload: bytes = b"") -> bytes:
    return struct.pack(">I", len(payload) + 8) + typ + payload


def _fullbox(typ: bytes, version: int, flags: int, payload: bytes = b"") -> bytes:
    return _box(typ, struct.pack(">B", version) + struct.pack(">I", flags)[1:] + payload)


def _mvhd(timescale: int, duration: int) -> bytes:
    matrix = struct.pack(">9i", 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000)
    payload = struct.pack(">IIII", 0, 0, timescale, duration) + struct.pack(">I", 0x00010000)
    payload += struct.pack(">HH", 0x0100, 0) + b"\x00" * 8 + matrix + b"\x00" * 24 + struct.pack(">I", 2)
    return _fullbox(b"mvhd", 0, 0, payload)


def _tkhd(duration: int, w: int, h: int, audio: bool, rotate90: bool = False) -> bytes:
    if rotate90:
        # 竖屏素材常见做法：coded 宽高 320x180 + 90° 旋转矩阵，播放器显示为 180x320
        matrix = struct.pack(">9i", 0, 0x00010000, 0, -0x00010000, 0, 0, 0, 0, 0x40000000)
    else:
        matrix = struct.pack(">9i", 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000)
    # v0: creation, modification, track_ID, reserved(4), duration, reserved(8),
    #     layer(2), alternate_group(2), volume(2), reserved(2), matrix(36), width(4), height(4)
    payload = struct.pack(">III", 0, 0, 1) + b"\x00" * 4 + struct.pack(">I", duration)
    payload += b"\x00" * 8
    payload += struct.pack(">HHH", 0, 0, 0x0100 if audio else 0) + b"\x00\x00" + matrix
    payload += struct.pack(">II", w << 16, h << 16)
    return _fullbox(b"tkhd", 0, 7, payload)


def _mdhd(timescale: int, duration: int) -> bytes:
    payload = struct.pack(">IIII", 0, 0, timescale, duration) + struct.pack(">HH", 0x55C4, 0)
    return _fullbox(b"mdhd", 0, 0, payload)


def _hdlr(handler: bytes, name: str) -> bytes:
    payload = b"\x00" * 4 + handler + b"\x00" * 12 + name.encode() + b"\x00"
    return _fullbox(b"hdlr", 0, 0, payload)


def _stsd_video(w: int, h: int, color=(1, 1, 1)) -> bytes:
    entry = b"\x00" * 6 + struct.pack(">H", 1) + b"\x00" * 16
    entry += struct.pack(">HH", w, h) + struct.pack(">II", 0x00480000, 0x00480000)
    entry += b"\x00" * 4 + struct.pack(">H", 1) + b"\x00" * 32
    entry += struct.pack(">Hh", 0x0018, -1)
    # colr / nclx：primaries, transfer, matrix, full_range_flag
    primaries, transfer, matrix = color
    entry += _box(b"colr", b"nclx" + struct.pack(">HHHB", primaries, transfer, matrix, 0))
    return _fullbox(b"stsd", 0, 0, struct.pack(">I", 1) + _box(b"avc1", entry))


def _stsd_audio(channels: int, rate: int) -> bytes:
    entry = b"\x00" * 6 + struct.pack(">H", 1) + b"\x00" * 8
    entry += struct.pack(">HHHHI", channels, 16, 0, 0, rate << 16)
    return _fullbox(b"stsd", 0, 0, struct.pack(">I", 1) + _box(b"mp4a", entry))


def _stbl(w: int, h: int, audio: bool, color=(1, 1, 1)) -> bytes:
    payload = _stsd_audio(1, 44100) if audio else _stsd_video(w, h, color)
    payload += _fullbox(b"stts", 0, 0, struct.pack(">I", 0))
    payload += _fullbox(b"stsc", 0, 0, struct.pack(">I", 0))
    payload += _fullbox(b"stsz", 0, 0, struct.pack(">II", 0, 0))
    payload += _fullbox(b"stco", 0, 0, struct.pack(">I", 0))
    return _box(b"stbl", payload)


def _minf(w: int, h: int, audio: bool, color=(1, 1, 1)) -> bytes:
    header = _fullbox(b"smhd", 0, 0, b"\x00\x00\x00\x00") if audio else _fullbox(b"vmhd", 0, 1, b"\x00" * 8)
    dref = _fullbox(b"dref", 0, 0, struct.pack(">I", 1) + _fullbox(b"url ", 0, 1, b""))
    dinf = _box(b"dinf", dref)
    return _box(b"minf", header + dinf + _stbl(w, h, audio, color))


def _trak(w: int, h: int, duration: int, audio: bool, color=(1, 1, 1),
          rotate90: bool = False) -> bytes:
    mdia = _box(b"mdia", _mdhd(1000, duration) + _hdlr(b"soun" if audio else b"vide",
                                                        "SoundHandler" if audio else "VideoHandler")
                + _minf(w, h, audio, color))
    return _box(b"trak", _tkhd(duration, w, h, audio, rotate90) + mdia)


def _udta_text(typ: bytes, text: str) -> bytes:
    data = text.encode("utf-8")
    # QuickTime 经典文本原子: 2 字节长度 + 2 字节语言 + 文本
    return _box(typ, struct.pack(">HH", len(data), 0x55C4) + data)


def write_mp4_skeleton(path: Path, rel: str, w: int, h: int, duration_sec: float,
                       tags: dict, audio: bool = False, color=(1, 1, 1),
                       rotate90: bool = False) -> None:
    ensure_parent(path)
    brands = b"isom" + struct.pack(">I", 512) + b"isomiso2mp41"
    ftyp = _box(b"ftyp", brands)
    duration = int(round(duration_sec * 1000))  # mvhd / tkhd / mdhd 统一 timescale=1000
    moov_payload = _mvhd(1000, duration) + _trak(w, h, duration, audio, color, rotate90)
    udta_payload = b""
    for key, typ in (("make", b"\xa9mak"), ("model", b"\xa9mod"), ("location", b"\xa9xyz"),
                     ("comment", b"\xa9cmt"), ("creation_time", b"\xa9day")):
        if tags.get(key):
            udta_payload += _udta_text(typ, str(tags[key]))
    if udta_payload:
        moov_payload += _box(b"udta", udta_payload)
    moov = _box(b"moov", moov_payload)
    mdat = _box(b"mdat", b"")
    path.write_bytes(ftyp + moov + mdat)


def make_video_fallback(spec: dict) -> dict | None:
    rel = spec["path"]
    path = LIB_DIR / rel
    city = CITIES[spec["city"]]
    hlg = bool(spec.get("hlg"))
    vertical = spec["w"] < spec["h"]
    # 竖屏：coded 宽高按横屏写 (320x180)，用 tkhd 90° 旋转矩阵表达显示方向
    coded_w, coded_h = (spec["h"], spec["w"]) if vertical else (spec["w"], spec["h"])
    color = (9, 18, 9) if hlg else (1, 1, 1)  # nclx: primaries, transfer, matrix
    try:
        write_mp4_skeleton(path, rel, coded_w, coded_h, spec["dur"], {
            "make": spec["make"], "model": spec["model"],
            "location": _loc_string(city["lat"], city["lon"]),
            "creation_time": spec["created"],
            **({"comment": spec["comment"]} if spec.get("comment") else {}),
        }, audio=False, color=color, rotate90=vertical)
    except Exception as exc:
        warn(f"兜底视频生成失败 {rel}: {exc}")
        return None
    warn(f"{rel}: 使用纯 Python ISOBMFF 骨架（ffmpeg 不可用），无法保证解码")
    entry = base_entry(rel, "Video", "MOV" if spec["ext"] == "mov" else "MP4")
    is_slog3 = bool(spec.get("comment")) and str(spec["comment"]).lower().startswith("s-log3")
    entry.update(
        expectWidth=spec["w"], expectHeight=spec["h"],
        expectDurationSeconds=float(spec["dur"]),
        expectIsLog=(True if is_slog3 else None),
        expectLogProfile=("S-Log3" if is_slog3 else None),
        expectPostProcess=None,
        expectCameraMake=None, expectCameraModel=None, expectDevice=None,
        expectCity=None, expectGps=None, expectCapturedAt=None,
        expectColorPrimaries=("bt2020" if hlg else "bt709"),
        expectTransferCharacteristics=("arib-std-b67" if hlg else "bt709"),
        probe={"fallback": True, "ffmpeg": False,
               "codedWidth": coded_w, "codedHeight": coded_h, "rotate90": vertical,
               "colr": {"primaries": color[0], "transfer": color[1], "matrix": color[2]},
               "udta": {"make": spec["make"], "model": spec["model"],
                        "comment": spec.get("comment")}},
    )
    fill_file_meta(entry, path)
    MANIFEST.append(entry)
    return entry


# --------------------------------------------------------------------------------------
# 音频
# --------------------------------------------------------------------------------------
def make_audio(spec: dict, tools: Toolchain) -> dict | None:
    rel = spec["path"]
    path = LIB_DIR / rel
    ensure_parent(path)
    fmt = spec["fmt"]
    if tools.ffmpeg is None:
        return make_audio_fallback(spec)
    cmd = ["-f", "lavfi", "-i", f"sine=frequency={spec['freq']}:duration={spec['dur']}",
           "-ac", "1", "-ar", "44100",
           "-metadata", f"title={spec['title']}",
           "-metadata", f"artist={spec['artist']}",
           "-metadata", f"album={spec['album']}"]
    if fmt == "WAV":
        cmd += ["-c:a", "pcm_s16le"]
    elif fmt == "MP3":
        cmd += ["-c:a", "libmp3lame", "-b:a", "64k", "-id3v2_version", "3"]
    elif fmt == "FLAC":
        cmd += ["-c:a", "flac", "-compression_level", "8"]
    elif fmt == "M4A":
        cmd += ["-c:a", "aac", "-b:a", "48k"]
    cmd += [str(path)]
    ok, err = tools.run(cmd, timeout=120)
    if not ok or not path.exists():
        warn(f"音频 {rel} ffmpeg 生成失败，改用兜底: {err[-300:]}")
        return make_audio_fallback(spec)
    probe = tools.probe(path)
    tags = probe_tags(probe)
    entry = base_entry(rel, "Audio", fmt)
    entry.update(expectHasAudio=True, expectPostProcess=None)
    if probe is not None:
        entry["expectDurationSeconds"] = round(float((probe.get("format") or {}).get("duration", spec["dur"])), 2)
        astream = probe_stream(probe, "audio")
        if astream:
            entry["expectAudioCodec"] = astream.get("codec_name")
            entry["expectAudioChannels"] = int(astream.get("channels") or 1)
            entry["expectAudioSampleRate"] = int(astream.get("sample_rate") or 44100)
        entry["expectAudioTags"] = {k: tags[k] for k in ("title", "artist", "album") if k in tags}
    fill_file_meta(entry, path)
    MANIFEST.append(entry)
    return entry


def _id3v23_frame(fid: bytes, text: str) -> bytes:
    data = b"\x01" + "\ufeff".encode("utf-16-be") + text.encode("utf-16-be")
    return fid + struct.pack(">I", len(data)) + b"\x00\x00" + data


def _id3v23_tag(tags: dict) -> bytes:
    frames = b""
    mapping = [("title", b"TIT2"), ("artist", b"TPE1"), ("album", b"TALB")]
    for key, fid in mapping:
        if tags.get(key):
            frames += _id3v23_frame(fid, str(tags[key]))
    size = len(frames)
    sync = bytes([(size >> 21) & 0x7F, (size >> 14) & 0x7F, (size >> 7) & 0x7F, size & 0x7F])
    return b"ID3\x03\x00" + b"\x00" + sync + frames


def _mp3_silent_frames(count: int) -> bytes:
    # MPEG1 Layer III, 128kbps, 44.1kHz, stereo, no CRC -> 417 字节/帧
    header = b"\xff\xfb\x90\x00"
    frame_len = 417
    return (header + b"\x00" * (frame_len - 4)) * max(1, count)


def make_audio_fallback(spec: dict) -> dict | None:
    rel = spec["path"]
    path = LIB_DIR / rel
    ensure_parent(path)
    fmt = spec["fmt"]
    try:
        if fmt == "WAV":
            import wave as _wave
            with _wave.open(str(path), "wb") as wf:
                wf.setnchannels(1)
                wf.setsampwidth(2)
                wf.setframerate(44100)
                n = int(44100 * spec["dur"])
                amp = 6000
                data = bytearray()
                for i in range(n):
                    v = int(amp * math.sin(2 * math.pi * spec["freq"] * i / 44100))
                    data += struct.pack("<h", v)
                wf.writeframes(bytes(data))
        elif fmt == "MP3":
            n = max(1, int(round(spec["dur"] * 44100 / 1152)))
            payload = _id3v23_tag(spec) + _mp3_silent_frames(n)
            path.write_bytes(payload)
        elif fmt == "FLAC":
            # fLaC + STREAMINFO + VORBIS_COMMENT（无音频帧；时长/采样率可被解析）
            total = int(44100 * spec["dur"])
            sr = 44100
            packed = (sr << 44) | (0 << 41) | (15 << 36) | total
            streaminfo = struct.pack(">HH", 4096, 4096) + b"\x00" * 6 + struct.pack(">Q", packed) + b"\x00" * 16
            block = bytes([0x00, 0x00, 0x00, 0x22]) + streaminfo
            comments = [b"StjMediaViewer SampleGenerator"]
            for k in ("title", "artist", "album"):
                if spec.get(k):
                    comments.append(f"{k.upper()}={spec[k]}".encode("utf-8"))
            vc_payload = struct.pack("<I", len(comments[0])) + comments[0] + struct.pack("<I", len(comments) - 1)
            for c in comments[1:]:
                vc_payload += struct.pack("<I", len(c)) + c
            # 最后一个 metadata block：last=1, type=4 (VORBIS_COMMENT)
            vc = bytes([0x84, (len(vc_payload) >> 16) & 0xFF, (len(vc_payload) >> 8) & 0xFF, len(vc_payload) & 0xFF])
            path.write_bytes(b"fLaC" + block + vc + vc_payload)
        elif fmt == "M4A":
            write_mp4_skeleton(path, rel, 0, 0, spec["dur"], {
                "make": None, "model": None,
            }, audio=True)
        else:
            warn(f"未知音频格式 {fmt}，跳过 {rel}")
            return None
    except Exception as exc:
        warn(f"兜底音频生成失败 {rel}: {exc}")
        return None
    warn(f"{rel}: 使用纯 Python 兜底音频（ffmpeg 不可用）")
    entry = base_entry(rel, "Audio", fmt)
    entry.update(expectHasAudio=True, expectPostProcess=None,
                 probe={"fallback": True, "ffmpeg": False})
    if fmt == "WAV":
        entry.update(expectDurationSeconds=float(spec["dur"]),
                     expectAudioCodec="pcm_s16le", expectAudioChannels=1, expectAudioSampleRate=44100)
    elif fmt == "MP3":
        n = max(1, int(round(spec["dur"] * 44100 / 1152)))
        entry.update(expectDurationSeconds=round(n * 1152 / 44100, 2),
                     expectAudioCodec="mp3", expectAudioChannels=2, expectAudioSampleRate=44100,
                     expectAudioTags={"title": spec["title"], "artist": spec["artist"], "album": spec["album"]})
    elif fmt == "FLAC":
        entry.update(expectDurationSeconds=float(spec["dur"]),
                     expectAudioCodec="flac", expectAudioChannels=1, expectAudioSampleRate=44100,
                     expectAudioTags={"title": spec["title"], "artist": spec["artist"], "album": spec["album"]})
    else:
        entry.update(expectDurationSeconds=float(spec["dur"]))
    fill_file_meta(entry, path)
    MANIFEST.append(entry)
    return entry


# --------------------------------------------------------------------------------------
# 阶段编排
# --------------------------------------------------------------------------------------
def phase_images() -> int:
    n = 0
    for spec in JPEG_SPECS:
        try:
            write_jpeg_file(spec)
            n += 1
        except Exception as exc:
            warn(f"JPEG 生成失败 {spec['path']}: {exc}")
    for spec in OTHER_IMAGE_SPECS:
        try:
            write_other_image_file(spec)
            n += 1
        except Exception as exc:
            warn(f"图片生成失败 {spec['path']}: {exc}")
    return n


def phase_raw() -> int:
    n = 0
    for spec in RAW_SPECS:
        try:
            write_raw_file(spec)
            n += 1
        except Exception as exc:
            warn(f"RAW 生成失败 {spec['path']}: {exc}")
    return n


def phase_sidecars() -> int:
    n = 0
    for spec in JPEG_SPECS + RAW_SPECS:
        rel = spec["path"]
        if find_entry(rel) is None:
            continue
        try:
            if spec.get("xmp_sidecar") == "lightroom":
                write_xmp_sidecar(rel, spec)
                n += 1
            elif spec.get("sidecar_kind") == "dop":
                write_capture_one_sidecar(rel, spec, ".dop")
                n += 1
            elif spec.get("sidecar_kind") == "cos":
                write_capture_one_sidecar(rel, spec, ".cos")
                n += 1
        except Exception as exc:
            warn(f"边车生成失败 {rel}: {exc}")
    return n


def phase_dups() -> int:
    before = len(MANIFEST)
    try:
        write_duplicates()
    except Exception as exc:
        warn(f"重复文件生成失败: {exc}")
    return len(MANIFEST) - before


def phase_videos(tools: Toolchain) -> int:
    n = 0
    for spec in VIDEO_SPECS:
        try:
            entry = make_video(spec, tools)
            if entry is not None:
                n += 1
        except Exception as exc:
            warn(f"视频生成失败 {spec['path']}: {exc}")
    if tools.ffmpeg is None:
        tools.video_fallback = True
    return n


def phase_audio(tools: Toolchain) -> int:
    n = 0
    for spec in AUDIO_SPECS:
        try:
            entry = make_audio(spec, tools)
            if entry is not None:
                n += 1
                if entry.get("probe", {}) and entry["probe"].get("fallback"):
                    tools.audio_fallback.append(spec["path"])
        except Exception as exc:
            warn(f"音频生成失败 {spec['path']}: {exc}")
    return n


def phase_junk() -> int:
    before = len(MANIFEST)
    try:
        write_junk_files()
    except Exception as exc:
        warn(f"反例文件生成失败: {exc}")
    return len(MANIFEST) - before


def link_raw_pairs() -> int:
    """给同 stem 的 JPG/RAW 互相标记配对关系（JPG-RAW 分类用）。"""
    jpeg_by_stem: dict[str, dict] = {}
    for e in MANIFEST:
        if e["kind"] == "Image" and e["format"] == "JPEG" and e["expectIncluded"]:
            jpeg_by_stem[Path(e["path"]).with_suffix("").as_posix()] = e
    linked = 0
    for e in MANIFEST:
        if e["kind"] == "Image" and e["expectIsRaw"]:
            stem = Path(e["path"]).with_suffix("").as_posix()
            jpeg = jpeg_by_stem.get(stem)
            if jpeg is not None:
                e["expectRawPairWith"] = jpeg["path"]
                jpeg["expectRawPairWith"] = e["path"]
                linked += 1
    return linked


# --------------------------------------------------------------------------------------
# 校验（生成后自检；失败只告警，方便定位样本 bug）
# --------------------------------------------------------------------------------------
def verify_library(tools: Toolchain) -> list[str]:
    issues: list[str] = []
    actual_files = [p for p in LIB_DIR.rglob("*") if p.is_file()]
    actual_rel = {p.relative_to(LIB_DIR).as_posix() for p in actual_files}
    listed = {e["path"] for e in MANIFEST}
    for rel in sorted(actual_rel - listed - {"catalog-manifest.json"}):
        issues.append(f"文件未登记进 manifest: {rel}")
    for rel in sorted(listed - actual_rel):
        issues.append(f"manifest 登记但文件不存在: {rel}")

    for e in MANIFEST:
        path = LIB_DIR / e["path"]
        if not path.is_file():
            continue
        if e["sizeBytes"] is not None and path.stat().st_size != e["sizeBytes"]:
            issues.append(f"大小与 manifest 不符: {e['path']}")
        if e["expectExcludedReason"] or e["kind"] in ("Sidecar", "NonMedia"):
            continue
        if e["kind"] == "Image" and e["format"] in ("JPEG", "PNG", "TIFF", "ARW", "CR2", "NEF", "DNG"):
            if e["expectParseError"]:
                continue
            try:
                with Image.open(path) as im:
                    if e["expectWidth"] and im.size[0] != e["expectWidth"]:
                        issues.append(f"宽度不符 {e['path']}: 实际 {im.size[0]} 期望 {e['expectWidth']}")
                    if e["expectHeight"] and im.size[1] != e["expectHeight"]:
                        issues.append(f"高度不符 {e['path']}: 实际 {im.size[1]} 期望 {e['expectHeight']}")
                    if e["format"] in ("JPEG", "PNG") and e["expectCameraMake"]:
                        ex = im.getexif()
                        make = ex.get(0x010F)
                        if make != e["expectCameraMake"]:
                            issues.append(f"EXIF Make 不符 {e['path']}: {make!r} != {e['expectCameraMake']!r}")
                        if e["expectGps"] and not ex.get_ifd(ExifTags.IFD.GPSInfo):
                            issues.append(f"EXIF GPS 缺失: {e['path']}")
            except Exception as exc:
                issues.append(f"图片无法用 Pillow 打开 {e['path']}: {exc}")
            if e["expectIsRaw"]:
                try:
                    blob = path.read_bytes()
                    soi = blob.find(b"\xff\xd8\xff")
                    eoi = blob.rfind(b"\xff\xd9")
                    if soi < 0 or eoi <= soi:
                        issues.append(f"RAW 内嵌 JPEG 缺失: {e['path']}")
                except Exception:
                    pass
        elif e["kind"] == "Video" and tools.ffmpeg is not None:
            probe = tools.probe(path)
            if probe is None:
                issues.append(f"无法解析视频（{tools.probe_method()}）: {e['path']}")
            else:
                fmt = probe.get("format", {}) or {}
                vstream = probe_stream(probe, "video")
                try:
                    real_dur = float(fmt.get("duration") or 0)
                    if e["expectDurationSeconds"] and abs(real_dur - e["expectDurationSeconds"]) > 0.35:
                        issues.append(f"视频时长不符 {e['path']}: {real_dur:.2f} vs {e['expectDurationSeconds']}")
                except Exception:
                    pass
                if vstream and e["expectWidth"] and vstream.get("width") and int(vstream["width"]) != e["expectWidth"]:
                    issues.append(f"视频宽度不符 {e['path']}: {vstream.get('width')} vs {e['expectWidth']}")
                if vstream and e["expectHeight"] and vstream.get("height") and int(vstream["height"]) != e["expectHeight"]:
                    issues.append(f"视频高度不符 {e['path']}: {vstream.get('height')} vs {e['expectHeight']}")
                if not tools.can_decode_video(path):
                    issues.append(f"视频无法抽帧解码（缩略图链路会失败）: {e['path']}")
                colr = read_colr(path)
                if colr is None:
                    issues.append(f"视频缺少 colr 盒子: {e['path']}")
                else:
                    expected_colr = (9, 18, 9) if e["expectTransferCharacteristics"] == "arib-std-b67" else (1, 1, 1)
                    got = (colr["primaries"], colr["transfer"], colr["matrix"])
                    if got != expected_colr:
                        issues.append(f"视频 colr 不符 {e['path']}: {got} != {expected_colr}")
        elif e["kind"] == "Audio" and tools.ffmpeg is not None:
            probe = tools.probe(path)
            if probe is None:
                issues.append(f"无法解析音频（{tools.probe_method()}）: {e['path']}")
    return issues


# --------------------------------------------------------------------------------------
# manifest 输出 + 统计
# --------------------------------------------------------------------------------------
def build_manifest(tools: Toolchain, elapsed: float, table_counts: dict) -> dict:
    # 自身条目（catalog-manifest.json 也在库目录里，登记但 size/hash 留空）
    self_entry = base_entry("catalog-manifest.json", "NonMedia", "JSON")
    self_entry.update(expectIncluded=False, expectExcludedReason="ignored-extension",
                      sizeBytes=None, sha256=None)
    entries = sorted(list(MANIFEST) + [self_entry], key=lambda e: e["path"])
    actual_files = [p for p in LIB_DIR.rglob("*") if p.is_file()]
    media_bytes = sum(p.stat().st_size for p in actual_files)
    by_kind: dict[str, int] = {}
    by_format: dict[str, int] = {}
    for e in entries:
        by_kind[e["kind"]] = by_kind.get(e["kind"], 0) + 1
        by_format[e["format"]] = by_format.get(e["format"], 0) + 1
    included = [e for e in entries if e["expectIncluded"]]
    excluded = [e for e in entries if not e["expectIncluded"]]
    by_scene: dict[str, int] = {}
    by_device: dict[str, int] = {}
    for e in entries:
        if e.get("expectThemeHint"):
            by_scene[e["expectThemeHint"]] = by_scene.get(e["expectThemeHint"], 0) + 1
        if e.get("expectDevice"):
            by_device[e["expectDevice"]] = by_device.get(e["expectDevice"], 0) + 1
    return {
        "schemaVersion": 1,
        "project": "stj-media-viewer",
        "description": "测试素材库逐文件期望值清单。expect* 是 SelfCheck 断言目标，null = 该项不适用/不作断言；"
                       "详见 tools/SampleLibrary/README.md。",
        "generatedBy": "tools/SampleLibrary/generate.py",
        "libraryRoot": "tools/SampleLibrary/out/library",
        "generator": {
            "elapsedSeconds": round(elapsed, 2),
            "ffmpegAvailable": tools.ffmpeg is not None,
            "ffmpegSource": tools.source,
            "ffprobeAvailable": tools.ffprobe is not None,
            "probeMethod": tools.probe_method(),
            "videoFallback": tools.video_fallback,
            "audioFallbackFiles": tools.audio_fallback,
            "warnings": len(WARNINGS),
            "python": sys.version.split()[0],
            "pillow": getattr(__import__("PIL"), "__version__", "?"),
        },
        "tolerances": {
            "gpsDegrees": 0.001,
            "durationSeconds": 0.35,
            "fNumber": 0.05,
            "exposureTime": 0.0005,
            "widthHeight": 2,
            "note": "width±2 / height±2（RAW 预览/视频取整差异）；duration±0.35s；GPS±0.001°",
        },
        "counts": {
            # files == manifestEntries == 磁盘上的常规文件数（含 catalog-manifest.json 自身）
            "files": len(entries),
            "manifestEntries": len(entries),
            "expectedIncluded": len(included),
            "expectedExcluded": len(excluded),
            "byKind": dict(sorted(by_kind.items())),
            "byFormat": dict(sorted(by_format.items())),
            "byDevice": dict(sorted(by_device.items())),
            "byThemeHint": dict(sorted(by_scene.items())),
            # 不含 catalog-manifest.json 自身（自引用无法给出大小）
            "totalBytesExcludingManifest": media_bytes,
            "totalMiBExcludingManifest": round(media_bytes / (1024 * 1024), 2),
            "tableCounts": table_counts,
        },
        "notes": [
            "expectDevice 是期望归一化设备键（商用名）；若 DeviceKey 归一化尚未实现，请用 expectCameraMake/expectCameraModel 断言。",
            "RAW 的 expectWidth/expectHeight 是内嵌预览 / TIFF 主图尺寸（本样本两者一致）。",
            "expectPostProcess=true 表示存在 Lightroom / Capture One 后期编辑痕迹（内嵌 XMP crs:HasSettings 或 .xmp/.dop/.cos 边车）。",
            "expectIsLog 仅在 S-Log3 注释视频上为 true；HLG 样本的 Log 口径待定，故留 null。",
            "expectExcludedReason 取值：ignored-directory / ignored-filename / ignored-extension / unsupported-extension / hidden-file / appledouble / empty-file / sidecar / broken-media。",
            "0 字节文件（empty.jpg）按 ScanOptions.MinFileSize=1 跳过、不产生 asset（expectIncluded=false, reason=empty-file）；扩展名像媒体但内容是文本的 broken.jpg 仍入库并记 LastError（expectParseError=true）。",
            "重复组副本只复制主文件、不复制边车：副本的 expectRating/expectColorLabel/expectKeywords 置 null/空、expectPostProcess=null（内嵌 XMP 的副本不受影响）。",
            "SelfCheck 断言口径：width/height ±2、duration ±0.35s、GPS ±0.001°；expect* 为 null 表示不作断言。",
            "expectRawPairWith 互相指向同 stem 的 JPG/RAW，用于 JPG-RAW 配对分类。",
            "冰岛样本只给到国家级期望（expectCity=冰岛），避免离线地理库粒度差异导致误报。",
            "catalog-manifest.json 自身登记为 entry，但 sizeBytes/sha256 为 null。",
        ],
        "entries": entries,
    }


def print_summary(manifest: dict, elapsed: float) -> None:
    c = manifest["counts"]
    print()
    print("=" * 78)
    print("SampleLibrary 生成完成")
    print(f"  文件总数 : {c['files']}  (期望入库 {c['expectedIncluded']} / 排除 {c['expectedExcluded']})")
    print(f"  总大小   : {c['totalMiBExcludingManifest']} MiB   ({c['totalBytesExcludingManifest']} bytes，不含 manifest 自身)")
    print(f"  耗时     : {elapsed:.1f}s")
    print(f"  分类计数 : {json.dumps(c['byKind'], ensure_ascii=False)}")
    print(f"  格式计数 : {json.dumps(c['byFormat'], ensure_ascii=False)}")
    g = manifest["generator"]
    print(f"  ffmpeg   : {'可用 (' + g['ffmpegSource'] + ')' if g['ffmpegAvailable'] else '缺失 -> 视频/音频使用纯 Python 兜底'}"
          f"  probe={g.get('probeMethod')}")
    if g["videoFallback"]:
        print("  [注意] 视频是 ISOBMFF 骨架（ffmpeg 缺失）；ISOBMFF 深度解析可能不完整。")
    if WARNINGS:
        print(f"  警告     : {len(WARNINGS)} 条")
        for w in WARNINGS[:20]:
            print(f"    - {w}")
        if len(WARNINGS) > 20:
            print(f"    ... 其余 {len(WARNINGS) - 20} 条省略")
    else:
        print("  警告     : 无")
    print(f"  manifest : {(LIB_DIR / 'catalog-manifest.json').relative_to(REPO_ROOT)}")
    print("=" * 78)


# --------------------------------------------------------------------------------------
# main
# --------------------------------------------------------------------------------------
ALL_PHASES = ["images", "raw", "sidecars", "dups", "video", "audio", "junk"]


def main() -> int:
    parser = argparse.ArgumentParser(description="生成 stj-media-viewer 测试素材库（幂等，可重跑）")
    parser.add_argument("--only", default="all",
                        choices=["all"] + ALL_PHASES + ["manifest"],
                        help="开发用：只跑指定阶段（默认 all）")
    parser.add_argument("--keep", action="store_true", help="开发用：不清理 out/（默认每次清空）")
    args = parser.parse_args()

    t0 = time.time()
    if not args.keep and OUT_DIR.exists():
        shutil.rmtree(OUT_DIR)
    LIB_DIR.mkdir(parents=True, exist_ok=True)
    TOOLING_DIR.mkdir(parents=True, exist_ok=True)

    print(f"仓库根 : {REPO_ROOT}")
    print(f"输出   : {LIB_DIR}")
    tools = Toolchain()
    tools.detect()

    phases = ALL_PHASES if args.only == "all" else ([args.only] if args.only in ALL_PHASES else [])
    counts = {"images": 0, "raw": 0, "sidecars": 0, "dups": 0, "video": 0, "audio": 0, "junk": 0}
    if "images" in phases:
        counts["images"] = phase_images()
        print(f"  图片: {counts['images']}")
    if "raw" in phases:
        counts["raw"] = phase_raw()
        print(f"  RAW : {counts['raw']}")
    if "sidecars" in phases:
        counts["sidecars"] = phase_sidecars()
        print(f"  边车: {counts['sidecars']}")
    if "dups" in phases:
        counts["dups"] = phase_dups()
        print(f"  重复/近重复副本: {counts['dups']}")
    if "video" in phases:
        counts["video"] = phase_videos(tools)
        print(f"  视频: {counts['video']}")
    if "audio" in phases:
        counts["audio"] = phase_audio(tools)
        print(f"  音频: {counts['audio']}")
    if "junk" in phases:
        counts["junk"] = phase_junk()
        print(f"  反例/非媒体: {counts['junk']}")

    pairs = link_raw_pairs()
    if pairs:
        print(f"  JPG-RAW 配对: {pairs} 对")

    if args.only in ("all", "manifest"):
        issues = verify_library(tools)
        if issues:
            print(f"  [自检] 发现 {len(issues)} 个问题（不影响产物，请人工确认）:")
            for msg in issues[:20]:
                print(f"    - {msg}")
            if len(issues) > 20:
                print(f"    ... 其余 {len(issues) - 20} 条省略")
        else:
            print("  [自检] 全部通过")
        elapsed = time.time() - t0
        manifest = build_manifest(tools, elapsed, counts)
        manifest_path = LIB_DIR / "catalog-manifest.json"
        manifest_path.write_text(json.dumps(_jsonable(manifest), ensure_ascii=False, indent=1),
                                 encoding="utf-8")
        print_summary(manifest, elapsed)
    else:
        print(f"  [dev] --only {args.only} 完成，未生成完整 manifest（耗时 {time.time() - t0:.1f}s）")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\n中断")
        sys.exit(130)