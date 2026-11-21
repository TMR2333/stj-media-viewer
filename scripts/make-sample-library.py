#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""重建 STJ Media Viewer 的样例素材库（.testdata）。

用法：
    cd macos
    python3 scripts/make-sample-library.py --data-dir .testdata
    python3 scripts/make-sample-library.py --data-dir .testdata --verbose

脚本只依赖 Python 3 标准库（sqlite3 / hashlib / json / pathlib / subprocess）。

行为：
1. 扫描 ``<data-dir>/media/images`` 与 ``<data-dir>/media/videos``，按内置的 28 行
   fixture 元数据表重建 ``catalog.db``（**只重建 catalog.db，绝不修改媒体文件**）。
2. schema 对齐 Windows 版 ``src/StjMediaViewer.Core/Catalog/CatalogSchema.cs`` 的全量 DDL，
   并补齐 macOS 端口额外依赖的列：
     - ``libraries.root_path``（``Catalog.libraries()`` 读这一列）
     - ``collections.created_ms`` / ``collection_assets.added_ms`` /
       ``merge_groups.created_ms`` / ``merge_group_assets.added_ms``（写表 SQL 需要）
     - ``assets.color_space``（样例库自检列，Windows 端忽略）
3. 关键词 ``keywords_flat`` 用 ``|`` 分隔，``keywords_json`` 同步为 JSON 数组；
   ``themes_json`` 支持多主题，第一个主题写回 ``theme``。
4. ``is_pick`` 三态（1 精选 / 0 废片 / NULL 未标记）、``city=''``（未填写）、
   ``tone`` 缺失、``rating`` 0–5、``is_log`` 2 行、``post_process`` 1/2 混合。
5. 缩略图：文件名规则 ``{sha1(full_path 小写)}_1_360.jpg``；``assets.thumb_key`` 写
   ``{sha1}_1``，这样 macOS ``ThumbnailStore`` 的索引键 ``{key}_{size}`` 才能命中
   （``ThumbnailNaming.parse`` 对 ``_1_360`` 解析出 size=1）。20 行写缓存文件，
   其余 8 行 ``thumb_key`` 留 NULL，走 QuickLook 兜底。
6. 最后 ``PRAGMA journal_mode=DELETE``（app 端只读打开，不要 WAL 残留）。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

# --------------------------------------------------------------------------- #
# schema
# --------------------------------------------------------------------------- #

_ASSET_COLUMNS = [
    # 与 Windows CatalogSchema.DdlAssets 完全一致的 91 列
    ("id", "TEXT PRIMARY KEY NOT NULL"),
    ("library_id", "TEXT NOT NULL DEFAULT ''"),
    ("full_path", "TEXT NOT NULL"),
    ("file_name", "TEXT NOT NULL DEFAULT ''"),
    ("relative_path", "TEXT"),
    ("size_bytes", "INTEGER NOT NULL DEFAULT 0"),
    ("file_modified", "TEXT"),
    ("file_modified_ms", "INTEGER"),
    ("file_created", "TEXT"),
    ("kind", "INTEGER NOT NULL DEFAULT 0"),
    ("format_code", "TEXT NOT NULL DEFAULT ''"),
    ("mime_type", "TEXT"),
    ("container_name", "TEXT"),
    ("image_flavor", "INTEGER NOT NULL DEFAULT 0"),
    ("raw_format", "INTEGER NOT NULL DEFAULT 0"),
    ("width", "INTEGER"),
    ("height", "INTEGER"),
    ("duration_seconds", "REAL"),
    ("frame_rate", "REAL"),
    ("bitrate_bps", "INTEGER"),
    ("bit_depth", "INTEGER"),
    ("video_codec", "TEXT"),
    ("audio_codec", "TEXT"),
    ("audio_channels", "INTEGER"),
    ("audio_sample_rate", "INTEGER"),
    ("has_audio", "INTEGER NOT NULL DEFAULT 0"),
    ("color_primaries", "TEXT"),
    ("transfer_characteristics", "TEXT"),
    ("color_matrix", "TEXT"),
    ("color_profile_name", "TEXT"),
    ("is_log", "INTEGER"),
    ("log_profile", "TEXT"),
    ("color_temp_k", "REAL"),
    ("camera_make", "TEXT"),
    ("camera_model", "TEXT"),
    ("device_key", "TEXT"),
    ("lens_model", "TEXT"),
    ("focal_length_mm", "REAL"),
    ("aperture_f", "REAL"),
    ("shutter_speed_seconds", "REAL"),
    ("iso", "INTEGER"),
    ("captured_at", "TEXT"),
    ("captured_ms", "INTEGER"),
    ("captured_day", "TEXT"),
    ("captured_at_source", "TEXT"),
    ("latitude", "REAL"),
    ("longitude", "REAL"),
    ("altitude_m", "REAL"),
    ("country", "TEXT"),
    ("province", "TEXT"),
    ("city", "TEXT"),
    ("city_source", "TEXT"),
    ("title", "TEXT"),
    ("caption", "TEXT"),
    ("creator", "TEXT"),
    ("copyright", "TEXT"),
    ("keywords_json", "TEXT NOT NULL DEFAULT '[]'"),
    ("keywords_flat", "TEXT NOT NULL DEFAULT ''"),
    ("post_process", "INTEGER NOT NULL DEFAULT 0"),
    ("has_develop_edits", "INTEGER NOT NULL DEFAULT 0"),
    ("develop_engine", "TEXT"),
    ("xmp_path", "TEXT"),
    ("sidecars_json", "TEXT NOT NULL DEFAULT '[]'"),
    ("has_embedded_xmp", "INTEGER NOT NULL DEFAULT 0"),
    ("theme", "TEXT"),
    ("themes_json", "TEXT NOT NULL DEFAULT '[]'"),
    ("rotation_degrees", "INTEGER NOT NULL DEFAULT 0"),
    ("flip_h", "INTEGER NOT NULL DEFAULT 0"),
    ("flip_v", "INTEGER NOT NULL DEFAULT 0"),
    ("tone", "TEXT"),
    ("rating", "INTEGER NOT NULL DEFAULT 0"),
    ("color_label", "TEXT"),
    ("is_pick", "INTEGER"),
    ("ai_subject", "TEXT"),
    ("ai_mood", "TEXT"),
    ("quality_score", "REAL"),
    ("provenance_json", "TEXT NOT NULL DEFAULT '{}'"),
    ("quick_hash", "TEXT"),
    ("full_hash", "TEXT"),
    ("thumb_key", "TEXT"),
    ("indexed_at", "TEXT"),
    ("indexed_ms", "INTEGER"),
    ("metadata_parsed_at", "TEXT"),
    ("last_error", "TEXT"),
    ("group_id", "TEXT"),
    ("is_group_cover", "INTEGER NOT NULL DEFAULT 1"),
    ("group_has_raw", "INTEGER NOT NULL DEFAULT 0"),
    ("group_has_jpeg", "INTEGER NOT NULL DEFAULT 0"),
    ("companions_json", "TEXT NOT NULL DEFAULT '[]'"),
    # 下面 3 列不在 Windows DDL 里：lut_id / origin 由 Windows 的 ALTER 迁移补入，
    # color_space 是样例库的兼容扩展列（app 不读，填了更完整）。
    ("lut_id", "TEXT"),
    ("origin", "TEXT"),
    ("color_space", "TEXT"),
]

_LIBRARY_COLUMNS = [
    ("id", "TEXT PRIMARY KEY NOT NULL"),
    ("path", "TEXT NOT NULL"),
    ("root_path", "TEXT"),
    ("name", "TEXT"),
    ("include_subfolders", "INTEGER NOT NULL DEFAULT 1"),
    ("watch_for_changes", "INTEGER NOT NULL DEFAULT 1"),
    ("added_at", "TEXT"),
    ("last_scan_at", "TEXT"),
    ("asset_count", "INTEGER NOT NULL DEFAULT 0"),
    ("total_bytes", "INTEGER NOT NULL DEFAULT 0"),
    ("sort_order", "INTEGER NOT NULL DEFAULT 0"),
]

_META_COLUMNS = [
    ("key", "TEXT PRIMARY KEY NOT NULL"),
    ("value", "TEXT"),
]

_ASSET_CUSTOM_COLUMNS = [
    ("asset_id", "TEXT NOT NULL"),
    ("dimension", "TEXT NOT NULL"),
    ("value", "TEXT NOT NULL"),
]

_COLLECTION_COLUMNS = [
    ("id", "TEXT PRIMARY KEY"),
    ("name", "TEXT NOT NULL DEFAULT ''"),
    ("description", "TEXT"),
    ("created_at", "TEXT"),
    ("created_ms", "INTEGER"),
    ("sort_order", "INTEGER NOT NULL DEFAULT 0"),
]

_COLLECTION_ASSET_COLUMNS = [
    ("collection_id", "TEXT NOT NULL"),
    ("asset_id", "TEXT NOT NULL"),
    ("added_at", "TEXT"),
    ("added_ms", "INTEGER"),
]

_MERGE_GROUP_COLUMNS = [
    ("id", "TEXT PRIMARY KEY"),
    ("name", "TEXT NOT NULL DEFAULT ''"),
    ("cover_asset_id", "TEXT"),
    ("created_at", "TEXT"),
    ("created_ms", "INTEGER"),
    ("sort_order", "INTEGER NOT NULL DEFAULT 0"),
]

_MERGE_GROUP_ASSET_COLUMNS = [
    ("group_id", "TEXT NOT NULL"),
    ("asset_id", "TEXT NOT NULL"),
    ("position", "INTEGER NOT NULL DEFAULT 0"),
    ("added_at", "TEXT"),
    ("added_ms", "INTEGER"),
]

_INDEXES = [
    "CREATE UNIQUE INDEX IF NOT EXISTS ux_assets_full_path ON assets(full_path);",
    "CREATE INDEX IF NOT EXISTS ix_assets_library ON assets(library_id);",
    "CREATE INDEX IF NOT EXISTS ix_assets_kind ON assets(kind);",
    "CREATE INDEX IF NOT EXISTS ix_assets_format ON assets(format_code);",
    "CREATE INDEX IF NOT EXISTS ix_assets_captured ON assets(captured_ms);",
    "CREATE INDEX IF NOT EXISTS ix_assets_captured_day ON assets(captured_day);",
    "CREATE INDEX IF NOT EXISTS ix_assets_device ON assets(device_key);",
    "CREATE INDEX IF NOT EXISTS ix_assets_city ON assets(city);",
    "CREATE INDEX IF NOT EXISTS ix_assets_theme ON assets(theme);",
    "CREATE INDEX IF NOT EXISTS ix_assets_tone ON assets(tone);",
    "CREATE INDEX IF NOT EXISTS ix_assets_raw ON assets(raw_format);",
    "CREATE INDEX IF NOT EXISTS ix_assets_is_log ON assets(is_log);",
    "CREATE INDEX IF NOT EXISTS ix_assets_rating ON assets(rating);",
    "CREATE INDEX IF NOT EXISTS ix_assets_quickhash ON assets(quick_hash);",
    "CREATE INDEX IF NOT EXISTS ix_assets_size ON assets(size_bytes);",
    "CREATE INDEX IF NOT EXISTS ix_assets_file_modified ON assets(file_modified_ms);",
    "CREATE INDEX IF NOT EXISTS ix_assets_indexed ON assets(indexed_ms);",
    "CREATE INDEX IF NOT EXISTS ix_assets_postprocess ON assets(post_process);",
    "CREATE INDEX IF NOT EXISTS ix_assets_group ON assets(group_id);",
    "CREATE INDEX IF NOT EXISTS ix_assets_cover ON assets(is_group_cover);",
    "CREATE INDEX IF NOT EXISTS ix_asset_custom_dim ON asset_custom(dimension, value);",
    "CREATE INDEX IF NOT EXISTS idx_collection_assets_asset ON collection_assets(asset_id);",
    "CREATE INDEX IF NOT EXISTS idx_merge_group_assets_asset ON merge_group_assets(asset_id);",
]


def create_table_sql(table: str, columns, extra_pk: str | None = None) -> str:
    body = ",\n    ".join(f"{name} {decl}" for name, decl in columns)
    if extra_pk:
        body += f",\n    PRIMARY KEY ({extra_pk})"
    return f"CREATE TABLE IF NOT EXISTS {table} (\n    {body}\n);"


# --------------------------------------------------------------------------- #
# fixture 元数据（28 行，路径/文件名与磁盘真实文件一致）
# --------------------------------------------------------------------------- #

DEVICES = {
    "iphone": {
        "key": "iPhone15Pro",
        "make": "Apple",
        "model": "Apple iPhone 15 Pro",
        "lens": "主摄 24mm",
        "focal": 6.86,
        "aperture": 1.78,
        "shutter": 1.0 / 120,
        "iso": 64,
    },
    "xt5": {
        "key": "X-T5",
        "make": "Fujifilm",
        "model": "Fujifilm X-T5",
        "lens": "XF 35mm F1.4 R",
        "focal": 35.0,
        "aperture": 1.4,
        "shutter": 1.0 / 250,
        "iso": 160,
    },
    "a7r5": {
        "key": "ILCE-7RM5",
        "make": "Sony",
        "model": "Sony α7R V",
        "lens": "FE 24-70mm F2.8 GM II",
        "focal": 24.0,
        "aperture": 2.8,
        "shutter": 1.0 / 320,
        "iso": 200,
    },
}

CITY_INFO = {
    "上海": ("中国", "上海市", 31.2304, 121.4737),
    "北京": ("中国", "北京市", 39.9042, 116.4074),
    "巴黎": ("法国", "法兰西岛大区", 48.8566, 2.3522),
}

# 内置 28 行元数据；``city`` 用空字符串表示"未填写"（测 __unset__ 筛选）。
# ``pick`` 三态：1 精选 / 0 废片 / None 未标记。
ROWS = [
    dict(
        file="BJ_20240704_0017_breakfast.jpg",
        title="豆浆油条早餐",
        caption="清晨在胡同口吃一份热豆浆和现炸油条。",
        keywords=["早餐", "豆浆", "油条", "北京", "胡同"],
        city="", device="iphone", captured_ms=1720053065000,
        themes=["静物", "美食"], tone="暖调", rating=1, label=None,
        pick=None, post=1, lut=None, log=None, thumb=True,
        subject="早餐桌", mood="温暖", iso=80,
        custom=[("胶片模拟", "Classic Negative")],
    ),
    dict(
        file="BJ_20240704_0018_platform.jpg",
        title="早高峰站台",
        caption="地铁站台人流密集，列车即将进站。",
        keywords=["地铁", "站台", "通勤", "北京"],
        city="", device="iphone", captured_ms=1720087961000,
        themes=[], tone=None, rating=0, label=None,
        pick=0, post=1, lut=None, log=None, thumb=False,
        subject="站台", mood="匆忙", iso=320,
        custom=[("拍摄场景", "通勤")],
    ),
    dict(
        file="BJ_20250921_0015_hutong-bike.jpg",
        title="胡同单车",
        caption="秋日午后，一辆单车停在灰砖墙边。",
        keywords=["胡同", "单车", "生活", "北京"],
        city="北京", device="iphone", captured_ms=1758436010000,
        themes=["街拍", "人文"], tone="中性", rating=3, label=None,
        pick=None, post=1, lut=None, log=None, thumb=True,
        subject="单车", mood="安静", iso=64,
        custom=[("拍摄场景", "胡同")],
    ),
    dict(
        file="BJ_20250921_0016_guomao-light.jpg",
        title="国贸车流",
        caption="长曝光把国贸桥的车流拉成连续光带。",
        keywords=["国贸", "车流", "长曝光", "北京"],
        city="北京", device="xt5", captured_ms=1758456197000,
        themes=["城市夜景", "建筑"], tone="冷调", rating=2, label="蓝",
        pick=0, post=1, lut=None, log=None, thumb=True,
        subject="车流", mood="都市", iso=200,
        custom=[("胶片模拟", "Classic Chrome")],
    ),
    dict(
        file="BJ_20251107_0013_gugong-snow.jpg",
        title="故宫初雪",
        caption="红墙金瓦覆上初雪，游人稀少。",
        keywords=["故宫", "红墙", "雪", "北京"],
        city="北京", device="a7r5", captured_ms=1762473312000,
        themes=["风光", "建筑", "雪景"], tone="冷调", rating=5, label="红",
        pick=1, post=2, lut="stj-film", log=None, thumb=True,
        subject="宫墙", mood="肃静", iso=100,
        custom=[("后期软件", "Capture One")],
    ),
    dict(
        file="BJ_20251107_0014_greatwall.jpg",
        title="长城秋色",
        caption="山脊上的长城被秋色层层包围。",
        keywords=["长城", "秋色", "山", "北京"],
        city="北京", device="a7r5", captured_ms=1762504838000,
        themes=["风光", "山景"], tone="暖调", rating=4, label="橙",
        pick=0, post=2, lut=None, log=None, thumb=True,
        subject="长城", mood="辽阔", iso=100,
        custom=[],
    ),
    dict(
        file="BJ_20260314_0021_vlog-arch.mov",
        title="建筑 vlog 片段",
        caption="手持运镜记录胡同里的现代建筑。",
        keywords=["vlog", "建筑", "北京", "视频"],
        city="北京", device="xt5", captured_ms=1773465730000,
        themes=["旅行", "建筑"], tone="冷调", rating=2, label=None,
        pick=0, post=1, lut=None, log=None, thumb=False,
        subject="建筑", mood="随性", iso=400,
        custom=[("胶片模拟", "Eterna")],
    ),
    dict(
        file="BJ_20260502_0019_hutong-portrait.jpg",
        title="胡同人像",
        caption="午后侧光下的胡同人像，带一点胶片色调。",
        keywords=["人像", "胡同", "午后", "北京"],
        city="北京", device="xt5", captured_ms=1777708109000,
        themes=["人像", "街拍"], tone="暖调", rating=4, label="红",
        pick=1, post=2, lut="fuji-cc", log=None, thumb=True,
        subject="人物", mood="松弛", iso=160,
        custom=[("胶片模拟", "Classic Chrome"), ("后期软件", "Lightroom")],
    ),
    dict(
        file="BJ_20260502_0020_summer-palace.jpg",
        title="颐和园倒影",
        caption="湖面倒映着十七孔桥与远处的山影。",
        keywords=["颐和园", "湖", "倒影", "北京"],
        city="北京", device="a7r5", captured_ms=1777691276000,
        themes=["风光", "水景"], tone=None, rating=3, label="绿",
        pick=None, post=1, lut=None, log=None, thumb=True,
        subject="湖面", mood="平静", iso=100,
        custom=[],
    ),
    dict(
        file="PAR_20240211_0024_cafe-corner.jpg",
        title="雨天的咖啡馆",
        caption="巴黎街角咖啡馆，雨点打在遮阳棚上。",
        keywords=["咖啡馆", "街角", "雨天", "巴黎"],
        city="", device="iphone", captured_ms=1707622587000,
        themes=["街拍", "城市"], tone=None, rating=3, label=None,
        pick=None, post=1, lut=None, log=None, thumb=False,
        subject="咖啡馆", mood="慵懒", iso=200,
        custom=[],
    ),
    dict(
        file="PAR_20240211_0025_bakery.jpg",
        title="面包房清晨",
        caption="刚出炉的可颂摆满木架，空气里都是黄油味。",
        keywords=["面包", "烘焙", "早餐", "巴黎"],
        city="巴黎", device="iphone", captured_ms=1707612734000,
        themes=["静物", "美食"], tone="暖调", rating=2, label="橙",
        pick=None, post=1, lut=None, log=None, thumb=True,
        subject="面包", mood="温暖", iso=125,
        custom=[],
    ),
    dict(
        file="PAR_20240211_0026_louvre-night.jpg",
        title="卢浮宫夜色",
        caption="金字塔的灯光与深蓝天色形成强烈对比。",
        keywords=["卢浮宫", "建筑", "夜景", "巴黎"],
        city="巴黎", device="xt5", captured_ms=1707656988000,
        themes=["风光", "建筑"], tone="冷调", rating=1, label=None,
        pick=None, post=2, lut=None, log=None, thumb=True,
        subject="金字塔", mood="静谧", iso=400,
        custom=[],
    ),
    dict(
        file="PAR_20250630_0022_eiffel-dusk.jpg",
        title="铁塔黄昏",
        caption="黄昏时分的埃菲尔铁塔与层层云。",
        keywords=["埃菲尔铁塔", "旅行", "黄昏", "巴黎"],
        city="巴黎", device="a7r5", captured_ms=1751285733000,
        themes=["旅行", "建筑", "黄昏"], tone="暖调", rating=5, label="黄",
        pick=1, post=2, lut="paris-film", log=None, thumb=True,
        subject="铁塔", mood="浪漫", iso=100,
        custom=[("后期软件", "Capture One")],
    ),
    dict(
        file="PAR_20250630_0023_seine-bridge.jpg",
        title="塞纳河桥",
        caption="塞纳河上的石桥与散步的人。",
        keywords=["塞纳河", "桥", "散步", "巴黎"],
        city="巴黎", device="a7r5", captured_ms=1751280242000,
        themes=["旅行", "街拍"], tone="冷调", rating=4, label="蓝",
        pick=0, post=1, lut=None, log=None, thumb=True,
        subject="石桥", mood="悠闲", iso=100,
        custom=[],
    ),
    dict(
        file="PAR_20260314_0027_metro-poster.jpg",
        title="地铁海报",
        caption="地铁站墙上的老电影海报。",
        keywords=["地铁", "海报", "城市", "巴黎"],
        city="巴黎", device="iphone", captured_ms=1773480425000,
        themes=["街拍", "平面"], tone=None, rating=0, label=None,
        pick=0, post=1, lut=None, log=None, thumb=False,
        subject="海报", mood="复古", iso=640,
        custom=[],
    ),
    dict(
        file="PAR_20260314_0028_bookstore.jpg",
        title="书店光线",
        caption="书店窗边逆光柔和，适合人像。",
        keywords=["人像", "书店", "光线", "巴黎"],
        city="巴黎", device="xt5", captured_ms=1773489516000,
        themes=["人像", "静物"], tone="暖调", rating=3, label="蓝",
        pick=None, post=2, lut="paris-film", log=None, thumb=False,
        subject="人物", mood="安静", iso=320,
        custom=[("胶片模拟", "Pro Neg. Hi")],
    ),
    dict(
        file="SH_20241225_0009_xmas-lights.jpg",
        title="圣诞灯饰",
        caption="商场外的圣诞灯饰和往来人群。",
        keywords=["圣诞", "灯光", "夜景", "上海"],
        city="上海", device="a7r5", captured_ms=1735134446000,
        themes=["城市夜景", "节日", "蓝调"], tone="冷调", rating=5, label="紫",
        pick=1, post=2, lut="stj-night-blue", log=None, thumb=True,
        subject="灯饰", mood="热闹", iso=200,
        custom=[("后期软件", "Lightroom")],
    ),
    dict(
        file="SH_20241225_0010_dessert.jpg",
        title="节日甜品",
        caption="节日限定甜品，暖色灯下的静物。",
        keywords=["甜品", "节日", "室内", "上海"],
        city="", device="iphone", captured_ms=1735111203000,
        themes=[], tone="暖调", rating=2, label="黄",
        pick=0, post=1, lut=None, log=None, thumb=True,
        subject="甜品", mood="温暖", iso=160,
        custom=[],
    ),
    dict(
        file="SH_20250630_0008_metro-rain.jpg",
        title="雨天通勤",
        caption="雨天的地铁站，伞面上全是水光。",
        keywords=["地铁", "通勤", "雨天", "上海"],
        city="上海", device="iphone", captured_ms=1751242449000,
        themes=["街拍", "城市"], tone="冷调", rating=1, label=None,
        pick=0, post=1, lut=None, log=None, thumb=False,
        subject="站厅", mood="潮湿", iso=400,
        custom=[],
    ),
    dict(
        file="SH_20250921_0006_street-portrait.jpg",
        title="街头人像",
        caption="胶片模拟的街头人像，肤色偏暖。",
        keywords=["人像", "街头", "胶片感", "上海"],
        city="上海", device="xt5", captured_ms=1758443598000,
        themes=["人像", "街拍"], tone="暖调", rating=4, label="红",
        pick=None, post=2, lut="fuji-cc", log=None, thumb=True,
        subject="人物", mood="自然", iso=160,
        custom=[("胶片模拟", "Classic Chrome")],
    ),
    dict(
        file="SH_20250921_0007_tourist-dusk.jpg",
        title="外滩游客",
        caption="黄昏的外滩观景台，游客举起手机。",
        keywords=["外滩", "游客", "黄昏", "上海"],
        city="上海", device="xt5", captured_ms=1758452324000,
        themes=["人像", "旅行"], tone=None, rating=3, label=None,
        pick=None, post=1, lut=None, log=None, thumb=True,
        subject="游客", mood="喧闹", iso=320,
        custom=[],
    ),
    dict(
        file="SH_20260314_0012_vlog-morning.mov",
        title="清晨 vlog",
        caption="清晨的城市街景 vlog 片段（V-Log 录制）。",
        keywords=["旅行", "vlog", "城市", "上海"],
        city="上海", device="xt5", captured_ms=1773450682000,
        themes=["旅行", "Vlog"], tone="暖调", rating=3, label=None,
        pick=None, post=1, lut=None, log="V-Log", thumb=False,
        subject="街景", mood="清晨", iso=800,
        custom=[("胶片模拟", "Eterna")],
    ),
    dict(
        file="SH_20260502_0004_huangpu-sunset.jpg",
        title="黄浦江日落",
        caption="江面被落日染成橙色，货船缓慢驶过。",
        keywords=["黄浦江", "日落", "水面", "上海"],
        city="上海", device="xt5", captured_ms=1777713150000,
        themes=["风光", "水景"], tone=None, rating=2, label=None,
        pick=None, post=2, lut=None, log=None, thumb=True,
        subject="江面", mood="壮阔", iso=160,
        custom=[],
    ),
    dict(
        file="SH_20260502_0005_cafe-table.jpg",
        title="咖啡桌",
        caption="咖啡馆桌面的静物：咖啡、书和光斑。",
        keywords=["咖啡", "静物", "桌面", "上海"],
        city="", device="iphone", captured_ms=1777687552000,
        themes=["静物", "日常生活"], tone="暖调", rating=0, label="橙",
        pick=0, post=1, lut=None, log=None, thumb=True,
        subject="咖啡", mood="惬意", iso=100,
        custom=[],
    ),
    dict(
        file="SH_20260819_0001_bund.jpg",
        title="外滩夜色",
        caption="外滩万国建筑群的夜景，蓝调时刻。",
        keywords=["外滩", "夜景", "建筑", "上海"],
        city="上海", device="a7r5", captured_ms=1787135025000,
        themes=["城市夜景", "建筑", "蓝调"], tone="冷调", rating=5, label="蓝",
        pick=1, post=2, lut="stj-night-blue", log=None, thumb=True,
        subject="建筑群", mood="璀璨", iso=100,
        custom=[("后期软件", "Lightroom")],
    ),
    dict(
        file="SH_20260819_0002_nanjing-road.jpg",
        title="南京路人流",
        caption="南京路步行街的夜晚人流与霓虹。",
        keywords=["南京路", "街拍", "人流", "上海"],
        city="上海", device="iphone", captured_ms=1787137331000,
        themes=["街拍", "城市"], tone="暖调", rating=3, label="黄",
        pick=None, post=1, lut=None, log=None, thumb=True,
        subject="人流", mood="繁华", iso=320,
        custom=[],
    ),
    dict(
        file="SH_20260819_0003_lujiazui.jpg",
        title="陆家嘴天际线",
        caption="蓝调时刻的陆家嘴天际线（竖幅）。",
        keywords=["陆家嘴", "天际线", "蓝调", "上海"],
        city="上海", device="a7r5", captured_ms=1787143267000,
        themes=["城市夜景", "建筑", "蓝调"], tone="冷调", rating=4, label="蓝",
        pick=1, post=2, lut="stj-night-blue", log=None, thumb=True,
        subject="天际线", mood="现代", iso=200,
        custom=[("后期软件", "Lightroom")],
    ),
    dict(
        file="SH_20260819_0011_lujiazui-timelapse.mov",
        title="夜景延时",
        caption="陆家嘴夜景的延时片段（S-Log3 录制）。",
        keywords=["航拍", "延时", "夜景", "上海"],
        city="上海", device="a7r5", captured_ms=1787139000000,
        themes=["城市夜景", "延时"], tone="冷调", rating=4, label="蓝",
        pick=1, post=2, lut="sony-s709", log="S-Log3", thumb=False,
        subject="天际线", mood="流动", iso=400,
        custom=[("后期软件", "DaVinci Resolve")],
    ),
]


# 稳定 id（沿用旧样例库的 UUID，重复运行结果一致）。
IDS = {
    "BJ_20240704_0017_breakfast.jpg": "fb580343-1613-4cd3-b08b-98b1b0d4ac9e",
    "BJ_20240704_0018_platform.jpg": "e5a8a7a7-2218-470f-b729-40a3c88310e1",
    "BJ_20250921_0015_hutong-bike.jpg": "6956deb3-6096-453e-88fb-2e4a7e97fbfa",
    "BJ_20250921_0016_guomao-light.jpg": "bd5914b6-b79c-4c73-baa8-25285234087e",
    "BJ_20251107_0013_gugong-snow.jpg": "8790b927-4cfd-4927-99a9-45461d80f3c1",
    "BJ_20251107_0014_greatwall.jpg": "c5ec2f2a-c9c1-46a1-80c4-50e71993d6d2",
    "BJ_20260314_0021_vlog-arch.mov": "563aafeb-6e2d-4884-b29f-d48bd74f0ea1",
    "BJ_20260502_0019_hutong-portrait.jpg": "64b7c117-1b78-40a3-9dd1-76ec2bbea9aa",
    "BJ_20260502_0020_summer-palace.jpg": "41af2f7e-a472-4454-a7bf-a7385721b3c8",
    "PAR_20240211_0024_cafe-corner.jpg": "89015a81-6b8a-4cbd-86aa-3a6752253b15",
    "PAR_20240211_0025_bakery.jpg": "bbe41edd-4f24-48e8-8bd3-52f4ee2e56b5",
    "PAR_20240211_0026_louvre-night.jpg": "1ee8fd86-e749-48a2-bfdc-07654ebd42d3",
    "PAR_20250630_0022_eiffel-dusk.jpg": "c6bcf5d9-6b36-4b64-92a7-11700ec5a0f0",
    "PAR_20250630_0023_seine-bridge.jpg": "831e300b-d87c-4409-a1b1-2b489ecece20",
    "PAR_20260314_0027_metro-poster.jpg": "9b40c7a0-b2e1-4430-bfcf-f7e136d0973f",
    "PAR_20260314_0028_bookstore.jpg": "47adbce9-0d41-49e5-b026-196a6ccbaa55",
    "SH_20241225_0009_xmas-lights.jpg": "acd894fa-7270-45ef-bffa-503c63a7ef43",
    "SH_20241225_0010_dessert.jpg": "6fcdbda6-99be-4294-b78c-119bff30dd46",
    "SH_20250630_0008_metro-rain.jpg": "676a321e-3f3b-4676-9955-08b5bc3a2ffc",
    "SH_20250921_0006_street-portrait.jpg": "fc7951d7-009a-438c-916e-799447742d38",
    "SH_20250921_0007_tourist-dusk.jpg": "64e9f543-b2b5-483e-8976-65b35207009b",
    "SH_20260314_0012_vlog-morning.mov": "594c785c-cbdb-4e22-a547-8b5dfb2553c5",
    "SH_20260502_0004_huangpu-sunset.jpg": "f1bff073-2d4e-45d1-9fbb-887f8e9906e8",
    "SH_20260502_0005_cafe-table.jpg": "9e66d575-9010-4626-a3f5-1803b5c8d4e5",
    "SH_20260819_0001_bund.jpg": "51c75e1e-bbde-408c-bf76-d54dcf7e6598",
    "SH_20260819_0002_nanjing-road.jpg": "a3c232e8-efec-4faf-92d0-d2301be83dbf",
    "SH_20260819_0003_lujiazui.jpg": "730a5d3a-8a30-4bd5-a401-709b5fe44723",
    "SH_20260819_0011_lujiazui-timelapse.mov": "2a22dc10-1c12-4bd7-9499-91eb07b03827",
}

# 尺寸（真实文件）：竖幅 4 张，其余横幅；视频按容器实际宽高。
PORTRAIT = {
    "BJ_20250921_0015_hutong-bike.jpg",
    "PAR_20240211_0025_bakery.jpg",
    "SH_20250921_0007_tourist-dusk.jpg",
    "SH_20260819_0003_lujiazui.jpg",
}
VIDEO_INFO = {
    "BJ_20260314_0021_vlog-arch.mov": (1.5, 30.0, 360, 640),
    "SH_20260314_0012_vlog-morning.mov": (3.0, 30.0, 640, 360),
    "SH_20260819_0011_lujiazui-timelapse.mov": (2.0, 24.0, 640, 360),
}

# 合并组（cover 指向真实素材，非 cover 的成员在默认「折叠组」模式下被隐藏）
GROUPS = [
    dict(
        id="grp-bund-night",
        name="外滩·陆家嘴夜景组",
        cover="SH_20260819_0001_bund.jpg",
        members=[("SH_20260819_0001_bund.jpg", 0), ("SH_20260819_0003_lujiazui.jpg", 1)],
        sort_order=0,
    ),
    dict(
        id="grp-paris-dusk",
        name="巴黎黄昏漫步组",
        cover="PAR_20250630_0022_eiffel-dusk.jpg",
        members=[
            ("PAR_20250630_0022_eiffel-dusk.jpg", 0),
            ("PAR_20250630_0023_seine-bridge.jpg", 1),
            ("PAR_20260314_0028_bookstore.jpg", 2),
        ],
        sort_order=1,
    ),
]

# 合集（每个至少 3 个素材）
COLLECTIONS = [
    dict(
        id="col-night-0001",
        name="城市夜景精选",
        description="外滩、陆家嘴、国贸与圣诞灯饰的夜景合集",
        files=[
            "BJ_20250921_0016_guomao-light.jpg",
            "SH_20241225_0009_xmas-lights.jpg",
            "SH_20260819_0001_bund.jpg",
            "SH_20260819_0003_lujiazui.jpg",
        ],
        sort_order=0,
    ),
    dict(
        id="col-film-0002",
        name="胶片调色",
        description="套用胶片 LUT / 胶片模拟的素材",
        files=[
            "BJ_20251107_0013_gugong-snow.jpg",
            "BJ_20260502_0019_hutong-portrait.jpg",
            "PAR_20250630_0022_eiffel-dusk.jpg",
            "PAR_20260314_0028_bookstore.jpg",
            "SH_20241225_0009_xmas-lights.jpg",
            "SH_20250921_0006_street-portrait.jpg",
            "SH_20260819_0001_bund.jpg",
            "SH_20260819_0003_lujiazui.jpg",
            "SH_20260819_0011_lujiazui-timelapse.mov",
        ],
        sort_order=1,
    ),
    dict(
        id="col-paris-0003",
        name="巴黎旅行",
        description="2024–2026 年巴黎拍摄的旅行素材",
        files=[
            "PAR_20240211_0025_bakery.jpg",
            "PAR_20240211_0026_louvre-night.jpg",
            "PAR_20250630_0022_eiffel-dusk.jpg",
            "PAR_20250630_0023_seine-bridge.jpg",
            "PAR_20260314_0027_metro-poster.jpg",
            "PAR_20260314_0028_bookstore.jpg",
        ],
        sort_order=2,
    ),
]

# 1x1 黑色 JPEG：既没有旧缓存文件、也没有 sips 时的兜底内容（仍是合法 JPEG）。
FALLBACK_JPEG_B64 = (
    "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0a"
    "HBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAA"
    "AAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AVN//2Q=="
)


# --------------------------------------------------------------------------- #
# 工具函数
# --------------------------------------------------------------------------- #


def sha1_hex(data: bytes) -> str:
    return hashlib.sha1(data).hexdigest()


def iso_utc(ms: int) -> str:
    return datetime.fromtimestamp(ms / 1000.0, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def file_hashes(path: Path) -> tuple[str, str]:
    """(quick_hash, full_hash)：quick 只取前 64KB，full 覆盖整文件。"""
    with path.open("rb") as fh:
        head = fh.read(65536)
    quick = sha1_hex(head)
    with path.open("rb") as fh:
        full = sha1_hex(fh.read())
    return quick, full


def media_dir_for(name: str) -> str:
    return "media/videos" if name.lower().endswith(".mov") else "media/images"


def build_assets(data_dir: Path, verbose: bool) -> list[dict]:
    now = int(time.time() * 1000)
    now_iso = iso_utc(now)
    group_of: dict[str, tuple[str, int]] = {}
    for group in GROUPS:
        for file_name, _pos in group["members"]:
            group_of[file_name] = (group["id"], 1 if file_name == group["cover"] else 0)

    assets: list[dict] = []
    for idx, row in enumerate(ROWS):
        name = row["file"]
        rel = f"{media_dir_for(name)}/{name}"
        path = data_dir / rel
        if not path.is_file():
            raise SystemExit(f"缺少媒体文件：{path}（样例库素材文件必须存在，脚本不会创建/修改媒体）")
        full_path = str(path.resolve())
        stat = path.stat()
        is_video = name.lower().endswith(".mov")
        if is_video:
            duration, fps, width, height = VIDEO_INFO[name]
        else:
            duration, fps = None, None
            width, height = (800, 1200) if name in PORTRAIT else (1200, 800)

        device = DEVICES[row["device"]]
        city = row["city"]
        if city:
            country, province, lat0, lon0 = CITY_INFO[city]
            lat = round(lat0 + ((idx % 5) - 2) * 0.0013, 6)
            lon = round(lon0 + ((idx % 7) - 3) * 0.0017, 6)
            city_source = "gps"
        else:
            country, province, city, lat, lon = "", "", "", None, None
            city_source = None

        themes = list(row["themes"])
        keywords = list(row["keywords"])
        captured_ms = row["captured_ms"]
        captured_day = datetime.fromtimestamp(
            captured_ms / 1000.0, timezone.utc
        ).strftime("%Y-%m-%d")
        quick, full = file_hashes(path)
        thumb_hash = sha1_hex(full_path.lower().encode("utf-8"))
        thumb_key = f"{thumb_hash}_1" if row["thumb"] else None
        group_id, is_cover = group_of.get(name, (None, 1))

        post = int(row["post"])
        asset = {
            "id": IDS[name],
            "library_id": "lib-main",
            "full_path": full_path,
            "file_name": name,
            "relative_path": rel,
            "size_bytes": stat.st_size,
            "file_modified": iso_utc(int(stat.st_mtime * 1000)),
            "file_modified_ms": int(stat.st_mtime * 1000),
            "file_created": iso_utc(int(stat.st_mtime * 1000)),
            "kind": 2 if is_video else 1,
            "format_code": "mov" if is_video else "jpg",
            "mime_type": "video/quicktime" if is_video else "image/jpeg",
            "container_name": "QuickTime" if is_video else "JPEG",
            "image_flavor": 0 if is_video else 1,
            "raw_format": 0,
            "width": width,
            "height": height,
            "duration_seconds": duration,
            "frame_rate": fps,
            "bitrate_bps": 8_000_000 if is_video else None,
            "bit_depth": 8,
            "video_codec": "h264" if is_video else None,
            "audio_codec": "aac" if is_video else None,
            "audio_channels": 2 if is_video else None,
            "audio_sample_rate": 48000 if is_video else None,
            "has_audio": 1 if is_video else 0,
            "color_primaries": "Rec.709" if is_video else "sRGB",
            "transfer_characteristics": "sRGB" if is_video else None,
            "color_matrix": None,
            "color_profile_name": "Rec.709" if is_video else "sRGB IEC61966-2.1",
            "is_log": 1 if row["log"] else 0,
            "log_profile": row["log"],
            "color_temp_k": None,
            "camera_make": device["make"],
            "camera_model": device["model"],
            "device_key": device["key"],
            "lens_model": device["lens"],
            "focal_length_mm": device["focal"],
            "aperture_f": device["aperture"],
            "shutter_speed_seconds": device["shutter"],
            "iso": row.get("iso") or device["iso"],
            "captured_at": iso_utc(captured_ms),
            "captured_ms": captured_ms,
            "captured_day": captured_day,
            "captured_at_source": "container" if is_video else "exif",
            "latitude": lat,
            "longitude": lon,
            "altitude_m": None,
            "country": country,
            "province": province,
            "city": city,
            "city_source": city_source,
            "title": row["title"],
            "caption": row["caption"],
            "creator": "STJ Sample Library",
            "copyright": "© 2026 STJ Media Viewer",
            "keywords_json": json.dumps(keywords, ensure_ascii=False),
            "keywords_flat": "|".join(keywords),
            "post_process": post,
            "has_develop_edits": 1 if post == 2 else 0,
            "develop_engine": "Camera Raw" if post == 2 else None,
            "xmp_path": None,
            "sidecars_json": "[]",
            "has_embedded_xmp": 0,
            "theme": themes[0] if themes else None,
            "themes_json": json.dumps(themes, ensure_ascii=False),
            "rotation_degrees": 0,
            "flip_h": 0,
            "flip_v": 0,
            "tone": row["tone"],
            "rating": int(row["rating"]),
            "color_label": row["label"],
            "is_pick": row["pick"],
            "ai_subject": row.get("subject"),
            "ai_mood": row.get("mood"),
            "quality_score": round(0.6 + (idx % 9) * 0.04, 2),
            "provenance_json": "{}",
            "quick_hash": quick,
            "full_hash": full,
            "thumb_key": thumb_key,
            "indexed_at": now_iso,
            "indexed_ms": now,
            "metadata_parsed_at": now_iso,
            "last_error": None,
            "group_id": group_id,
            "is_group_cover": is_cover,
            "group_has_raw": 0,
            "group_has_jpeg": 0,
            "companions_json": "[]",
            "lut_id": row["lut"],
            "origin": "sample",
            "color_space": "Rec.709" if is_video else "sRGB",
        }
        asset["_custom"] = row["custom"]
        assets.append(asset)
    return assets


# --------------------------------------------------------------------------- #
# 缩略图
# --------------------------------------------------------------------------- #


def generate_thumbnails(data_dir: Path, assets: list[dict], verbose: bool) -> tuple[int, str]:
    """生成 ``{sha1}_1_360.jpg`` 缓存文件，返回 (写入数量, 内容来源说明)。"""
    cache_dir = data_dir / "Cache" / "thumbs"
    cache_dir.mkdir(parents=True, exist_ok=True)

    # 旧缓存当作「合成缩略图内容池」：没有 sips 或视频无法抽帧时复用其字节。
    pool: list[bytes] = []
    for old in sorted(cache_dir.glob("*.jpg")):
        try:
            blob = old.read_bytes()
        except OSError:
            continue
        if blob.startswith(b"\xff\xd8"):
            pool.append(blob)
    fallback = __import__("base64").b64decode(FALLBACK_JPEG_B64)

    want: list[tuple[dict, Path]] = []
    for asset in assets:
        key = asset["thumb_key"]
        if not key:
            continue
        want.append((asset, cache_dir / f"{key}_360.jpg"))

    sips = shutil.which("sips")
    written = 0
    sources = set()
    for index, (asset, dest) in enumerate(want):
        src = Path(asset["full_path"])
        ok = False
        if sips and asset["kind"] == 1:
            tmp = dest.with_suffix(".tmp.jpg")
            proc = subprocess.run(
                [sips, "-Z", "360", "-s", "format", "jpeg", str(src), "--out", str(tmp)],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            if proc.returncode == 0 and tmp.is_file() and tmp.stat().st_size > 0:
                tmp.replace(dest)
                ok = True
                sources.add("sips")
            elif tmp.is_file():
                tmp.unlink()
        if not ok and pool:
            dest.write_bytes(pool[index % len(pool)])
            ok = True
            sources.add("pool")
        if not ok:
            dest.write_bytes(fallback)
            sources.add("fallback")
        written += 1

    keep = {dest.name for _asset, dest in want}
    removed = 0
    for stale in cache_dir.iterdir():
        if stale.is_file() and stale.name not in keep:
            stale.unlink()
            removed += 1

    if verbose:
        print(f"  缩略图：写入 {written} 个，清理旧文件 {removed} 个（内容来源：{', '.join(sorted(sources)) or '无'}）")
    return written, ", ".join(sorted(sources)) or "none"


# --------------------------------------------------------------------------- #
# 建库
# --------------------------------------------------------------------------- #


def rebuild_database(data_dir: Path, assets: list[dict], verbose: bool) -> dict:
    db_path = data_dir / "catalog.db"
    for suffix in ("", "-wal", "-shm", "-journal"):
        candidate = Path(str(db_path) + suffix) if suffix else db_path
        if candidate.exists():
            candidate.unlink()

    conn = sqlite3.connect(str(db_path))
    try:
        conn.execute("PRAGMA journal_mode=DELETE;")
        conn.execute("PRAGMA synchronous=FULL;")
        conn.execute("PRAGMA foreign_keys=OFF;")

        conn.execute(create_table_sql("assets", _ASSET_COLUMNS))
        conn.execute(create_table_sql("libraries", _LIBRARY_COLUMNS))
        conn.execute(create_table_sql("meta", _META_COLUMNS))
        conn.execute(create_table_sql("asset_custom", _ASSET_CUSTOM_COLUMNS, "asset_id, dimension"))
        conn.execute(create_table_sql("collections", _COLLECTION_COLUMNS))
        conn.execute(create_table_sql("collection_assets", _COLLECTION_ASSET_COLUMNS, "collection_id, asset_id"))
        conn.execute(create_table_sql("merge_groups", _MERGE_GROUP_COLUMNS))
        conn.execute(
            create_table_sql("merge_group_assets", _MERGE_GROUP_ASSET_COLUMNS, "group_id, asset_id")
        )
        for sql in _INDEXES:
            conn.execute(sql)

        asset_columns = [name for name, _decl in _ASSET_COLUMNS]
        insert_sql = (
            f"INSERT INTO assets ({', '.join(asset_columns)}) "
            f"VALUES ({', '.join('?' for _ in asset_columns)})"
        )
        conn.executemany(
            insert_sql,
            [tuple(asset[name] for name in asset_columns) for asset in assets],
        )

        now_ms = int(time.time() * 1000)
        now_iso = iso_utc(now_ms)
        total_bytes = sum(asset["size_bytes"] for asset in assets)
        conn.execute(
            "INSERT INTO libraries (id, path, root_path, name, include_subfolders, watch_for_changes,"
            " added_at, last_scan_at, asset_count, total_bytes, sort_order) VALUES (?,?,?,?,?,?,?,?,?,?,?)",
            (
                "lib-main",
                str(data_dir),
                str(data_dir),
                "样例素材库",
                1,
                0,
                now_iso,
                now_iso,
                len(assets),
                total_bytes,
                0,
            ),
        )

        id_of = {asset["file_name"]: asset["id"] for asset in assets}

        custom_rows = []
        for asset in assets:
            for dimension, value in asset["_custom"]:
                custom_rows.append((asset["id"], dimension, value))
        conn.executemany(
            "INSERT OR REPLACE INTO asset_custom (asset_id, dimension, value) VALUES (?,?,?)",
            custom_rows,
        )

        for collection in COLLECTIONS:
            conn.execute(
                "INSERT INTO collections (id, name, description, created_at, created_ms, sort_order)"
                " VALUES (?,?,?,?,?,?)",
                (
                    collection["id"],
                    collection["name"],
                    collection["description"],
                    now_iso,
                    now_ms,
                    collection["sort_order"],
                ),
            )
            conn.executemany(
                "INSERT OR IGNORE INTO collection_assets (collection_id, asset_id, added_at, added_ms)"
                " VALUES (?,?,?,?)",
                [(collection["id"], id_of[name], now_iso, now_ms) for name in collection["files"]],
            )

        for group in GROUPS:
            conn.execute(
                "INSERT INTO merge_groups (id, name, cover_asset_id, created_at, created_ms, sort_order)"
                " VALUES (?,?,?,?,?,?)",
                (
                    group["id"],
                    group["name"],
                    id_of[group["cover"]],
                    now_iso,
                    now_ms,
                    group["sort_order"],
                ),
            )
            conn.executemany(
                "INSERT OR IGNORE INTO merge_group_assets (group_id, asset_id, position, added_at, added_ms)"
                " VALUES (?,?,?,?,?)",
                [(group["id"], id_of[name], pos, now_iso, now_ms) for name, pos in group["members"]],
            )

        # meta 保持空表（sample library 不写 schema_version，交由宿主应用迁移）。
        conn.commit()
        conn.execute("PRAGMA optimize;")
        conn.execute("PRAGMA journal_mode=DELETE;")
        conn.commit()

        journal_mode = conn.execute("PRAGMA journal_mode;").fetchone()[0]
        asset_count = conn.execute("SELECT count(*) FROM assets;").fetchone()[0]
        db_size = db_path.stat().st_size
    finally:
        conn.close()

    if verbose:
        print(f"  catalog.db：{asset_count} 行素材，{db_size} 字节，journal_mode={journal_mode}")
    if journal_mode.lower() != "delete":
        raise SystemExit(f"journal_mode 必须是 delete，实际为 {journal_mode}")
    return {
        "db_path": db_path,
        "asset_count": asset_count,
        "db_size": db_size,
        "journal_mode": journal_mode,
    }


# --------------------------------------------------------------------------- #
# main
# --------------------------------------------------------------------------- #


def resolve_data_dir(raw: str | None) -> Path:
    if raw is None:
        return (Path(__file__).resolve().parent.parent / ".testdata").resolve()
    path = Path(raw).expanduser()
    if path.name == "catalog.db" or path.is_file():
        path = path.parent
    if not path.is_absolute():
        path = (Path.cwd() / path).resolve()
    else:
        path = path.resolve()
    return path


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        description="重建 STJ Media Viewer macOS 样例素材库（只改 catalog.db / Cache/thumbs）",
    )
    parser.add_argument(
        "--data-dir",
        default=None,
        help="样例库目录（默认脚本所在仓库的 .testdata；也可传 catalog.db 路径）",
    )
    parser.add_argument("--verbose", action="store_true", help="打印详细统计")
    args = parser.parse_args(argv)

    data_dir = resolve_data_dir(args.data_dir)
    if not data_dir.is_dir():
        raise SystemExit(f"数据目录不存在：{data_dir}")
    media_images = data_dir / "media" / "images"
    media_videos = data_dir / "media" / "videos"
    if not media_images.is_dir() and not media_videos.is_dir():
        raise SystemExit(f"{data_dir} 下没有 media/images 或 media/videos，无法重建样例库")

    print(f"样例库目录：{data_dir}")
    assets = build_assets(data_dir, args.verbose)
    if len(assets) != len(ROWS):
        raise SystemExit(f"素材行数异常：期望 {len(ROWS)}，实际 {len(assets)}")

    thumb_written, thumb_source = generate_thumbnails(data_dir, assets, args.verbose)
    info = rebuild_database(data_dir, assets, args.verbose)

    if args.verbose:
        grouped = [a for a in assets if a["group_id"]]
        unset_city = sum(1 for a in assets if not a["city"])
        print()
        print("样例库统计")
        print(f"  素材总行数      : {len(assets)}（图片 {sum(1 for a in assets if a['kind'] == 1)} / 视频 {sum(1 for a in assets if a['kind'] == 2)}）")
        print(f"  默认可见行数    : {len(assets) - sum(1 for a in assets if a['group_id'] and not a['is_group_cover'])}（折叠合并组的非封面成员）")
        print(f"  合集            : {len(COLLECTIONS)} 个，共 {sum(len(c['files']) for c in COLLECTIONS)} 条关联")
        print(f"  合并组          : {len(GROUPS)} 个，{len(grouped)} 个成员（cover {len(GROUPS)}）")
        print(f"  city 未填写     : {unset_city} 行（city = ''）")
        print(f"  theme 未填写    : {sum(1 for a in assets if not a['themes_json'] or a['themes_json'] == '[]')} 行")
        print(f"  tone 缺失       : {sum(1 for a in assets if a['tone'] is None)} 行")
        pick1 = sum(1 for a in assets if a["is_pick"] == 1)
        pick0 = sum(1 for a in assets if a["is_pick"] == 0)
        pickn = sum(1 for a in assets if a["is_pick"] is None)
        print(f"  is_pick 三态    : 精选 {pick1} / 废片 {pick0} / 未标记 {pickn}")
        print(f"  post_process 2  : {sum(1 for a in assets if a['post_process'] == 2)} 行")
        print(f"  is_log = 1      : {sum(1 for a in assets if a['is_log']) } 行")
        print(f"  缩略图缓存文件  : {thumb_written} 个（thumb_key 非空 {sum(1 for a in assets if a['thumb_key'])} 行，QuickLook 兜底 {len(assets) - thumb_written} 行）")
        print()
        print(f"完成：{info['db_path']}")
    else:
        print(f"完成：{info['db_path']}（{info['asset_count']} 行素材，缩略图 {thumb_written} 个）")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))