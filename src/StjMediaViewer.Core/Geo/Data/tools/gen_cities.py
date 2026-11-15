#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成离线反向地理编码用的嵌入数据源 (Geo/Data/cities.g.cs)。

数据来源：GeoNames cities15000（https://download.geonames.org/export/dump/），授权 CC BY 4.0。
为什么生成 .cs 而不是 .tsv 嵌入资源：Core 的 csproj 是 Lead 冻结的，不允许新增
<EmbeddedResource>；把 gzip+base64 的 TSV 写成 C# 常量可以做到零 csproj 改动。

用法（仓库任意位置执行均可）：
    python src/StjMediaViewer.Core/Geo/Data/tools/gen_cities.py
    python .../gen_cities.py --zip /tmp/cities15000.zip        # 复用已下载的 zip
    python .../gen_cities.py --dataset cities5000             # 换更小的数据集
    python .../gen_cities.py --keep-tsv                       # 额外留一份明文 TSV 便于排查

输出列（制表符分隔，供 OfflineGeocoder 解析）：
    lat, lon, name, asciiname, country, admin1, population, cjkName, rank

关键口径：
* population 取 GeoNames 第 15 列（索引 14；文件布局是
  geonameid,name,asciiname,alternatenames,lat,lon,fclass,fcode,country,cc2,
  admin1,admin2,admin3,admin4,population,elevation,dem,timezone,moddate）。
  注意不是索引 11（那是 admin2 代码）。
* cjkName 优先取 alternatenames 里的 CJK 名，并统一转成**简体中文**：
  优先"官方短名"（是"官方全称"前缀的那个，如 成都 ⊂ 成都市、东京 ⊂ 东京都），
  避免 geonames 别名顺序里的昵称（如成都的首个别名是"天府"）。
  繁→简映射来自同目录 ts_characters.txt（OpenCC TSCharacters 的子集，Apache-2.0），
  缺文件时退回脚本内置常用字表 + 日文新字体表。
* rank 是 GeoNames feature code 映射出的重要性等级（1~9，见 FEATURE_RANK），
  用于在很近的距离内优先返回行政中心/大城市，而不是大城市内部的居民点碎片。
"""

import argparse
import base64
import gzip
import hashlib
import os
import re
import sys
import tempfile
import urllib.request
import zipfile

DATASETS = {
    "cities15000": ("https://download.geonames.org/export/dump/cities15000.zip", 15000),
    "cities5000": ("https://download.geonames.org/export/dump/cities5000.zip", 5000),
}

# CJK 统一表意文字（基本区 + 扩展 A + 兼容区 + 扩展 B）
CJK_RE = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\U00020000-\U0002a6df]")
CHUNK_SIZE = 10000  # 每个 C# 字符串字面量的长度

# 这些 feature code 是"大城市内部的区/街道/居民点碎片"，不适合作为反向地理编码的最近城市。
SKIP_FEATURE_CODES = {
    "PPLX",   # section of populated place（如上海的黄浦区、巴黎的某街区）
    "PPLQ",   # abandoned populated place
    "PPLL",   # locality
    "PPLS",   # populated places（复数聚集）
    "PPLH",   # historical populated place
    "PPLW",   # destroyed populated place
    "PPLG",   # seat of government (generic)
    "PPLF",   # farm village
    "PPLR",   # religious populated place
    "STLMT",  # Israeli settlement
}

# feature code → 重要性等级（第 9 列）。反向地理编码在很近的距离内偏好行政中心
# （例如上海坐标最近的是普通居民点"黄浦"，但应该返回"上海"这个 PPLA 行政中心）。
# 注意 PPLA3/PPLA4 是区/县级碎片，权重低于普通城市 PPL。
FEATURE_RANK = {
    "PPLC": 9,   # 首都
    "PPLA": 8,   # 一级行政区中心（省会/直辖市）
    "PPLA2": 6,  # 二级行政区中心（地级市）
    "PPL": 5,    # 普通城市/镇（大城市本体多为 PPL 或 PPLA）
    "PPLA3": 3,  # 三级行政区中心（区/县）
    "PPLA4": 2,  # 四级行政区中心（街道/新城）
    "PPLA5": 2,
    "PPLL": 1,
    "PPLX": 1,
    "PPLG": 2,
    "PPLF": 1,
    "PPLH": 1,
    "PPLW": 1,
    "PPLQ": 1,
    "PPLS": 1,
    "PPLR": 1,
}

# ts_characters.txt 缺失时的兜底繁→简表（城市名常见字 + Lead 指定字 + 日文新字体）。
BUILTIN_T2S = {
    "東": "东", "亞": "亚", "紐": "纽", "約": "约", "爾": "尔", "維": "维", "魯": "鲁",
    "羅": "罗", "聖": "圣", "薩": "萨", "門": "门", "灣": "湾", "島": "岛", "廣": "广",
    "慶": "庆", "漢": "汉", "龍": "龙", "鳳": "凤", "臺": "台", "縣": "县", "澤": "泽",
    "濱": "滨", "岡": "冈", "愛": "爱", "邊": "边", "區": "区", "國": "国", "華": "华",
    "長": "长", "馬": "马", "鳥": "鸟", "魚": "鱼", "車": "车", "貝": "贝", "頁": "页",
    "風": "风", "飛": "飞", "養": "养", "黨": "党", "齊": "齐", "齒": "齿", "龜": "龟",
    "樂": "乐", "樓": "楼", "蘭": "兰", "紅": "红", "綠": "绿", "藍": "蓝", "鐵": "铁",
    "銀": "银", "錢": "钱", "鎮": "镇", "陽": "阳", "陰": "阴", "際": "际", "陸": "陆",
    "隊": "队", "隨": "随", "隱": "隐", "雙": "双", "雲": "云", "電": "电", "頓": "顿",
    "頭": "头", "類": "类", "顯": "显", "飯": "饭", "館": "馆", "驅": "驱", "驗": "验",
    "體": "体", "髮": "发", "麥": "麦", "黃": "黄", "點": "点", "圖": "图", "團": "团",
    "圍": "围", "栄": "荣", "絵": "绘",
    # 日文新字体（地名常见；OpenCC TSCharacters 不覆盖）
    "広": "广", "沢": "泽", "浜": "滨", "県": "县", "桜": "樱", "竜": "龙", "瀬": "濑",
    "塩": "盐", "縄": "绳", "鉄": "铁", "銭": "钱", "駅": "驿", "郷": "乡", "圏": "圈",
    "検": "检", "験": "验", "様": "样", "戸": "户", "児": "儿", "徳": "德", "恵": "惠",
}


def repo_paths() -> tuple[str, str]:
    """返回 (仓库里 Data 目录, 默认输出文件)。脚本位于 .../Geo/Data/tools/。"""
    here = os.path.dirname(os.path.abspath(__file__))
    data_dir = os.path.dirname(here)
    return data_dir, os.path.join(data_dir, "cities.g.cs")


def load_t2s(script_dir: str) -> dict:
    """加载繁→简映射：内置表打底，ts_characters.txt 覆盖。"""
    mapping = dict(BUILTIN_T2S)
    path = os.path.join(script_dir, "ts_characters.txt")
    if os.path.isfile(path):
        with open(path, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                parts = line.split("\t")
                if len(parts) < 2:
                    continue
                traditional = parts[0].strip()
                simplified = parts[1].split()
                if len(traditional) == 1 and simplified:
                    mapping[traditional] = simplified[0]
        print(f"[gen-cities] 繁→简映射 {len(mapping)} 字（{path}）")
    else:
        print("[gen-cities] 未找到 ts_characters.txt，使用内置繁→简兜底表")
    return mapping


def to_simplified(text: str, t2s: dict) -> str:
    return "".join(t2s.get(ch, ch) for ch in text)


def fetch_zip(dataset: str, zip_path: str) -> str:
    if os.path.isfile(zip_path) and zipfile.is_zipfile(zip_path):
        print(f"[gen-cities] 复用本地 zip: {zip_path}")
        return zip_path

    urls = [DATASETS[dataset][0]]
    if dataset == "cities15000":
        urls.append(DATASETS["cities5000"][0])

    last_error = None
    for url in urls:
        try:
            print(f"[gen-cities] 下载 {url} ...")
            req = urllib.request.Request(url, headers={"User-Agent": "StjMediaViewer-cities-gen/1.0"})
            with urllib.request.urlopen(req, timeout=240) as resp:
                data = resp.read()
            if zipfile.is_zipfile(__import__("io").BytesIO(data)):
                os.makedirs(os.path.dirname(zip_path) or ".", exist_ok=True)
                with open(zip_path, "wb") as f:
                    f.write(data)
                print(f"[gen-cities] 已保存 {len(data) / 1e6:.2f} MB → {zip_path}")
                return zip_path
            last_error = f"{url} 返回的不是 zip"
        except Exception as exc:  # noqa: BLE001 - 生成脚本，尽量给出可操作信息
            last_error = f"{url}: {exc}"
            print(f"[gen-cities] 下载失败：{last_error}", file=sys.stderr)
    raise SystemExit(f"[gen-cities] 无法获取地理数据（{last_error}）。可手动下载后用 --zip 指定。")


def is_pure_cjk(text: str) -> bool:
    return all(CJK_RE.match(ch) or ch in " ・·—-" for ch in text)


def pick_cjk_name(alternatenames: str, name: str, asciiname: str, t2s: dict) -> str:
    """从 alternatenames 挑中文名：统一简体，并优先"官方短名"。

    经验：geonames 的别名顺序不保证第一项是官方名（例如成都的首个中文别名是"天府"），
    但官方短名通常是官方全称的前缀（成都 ⊂ 成都市、东京 ⊂ 东京都、纽约 ⊂ 纽约市）。
    """
    name_simplified = to_simplified(name, t2s)
    ascii_simplified = to_simplified(asciiname, t2s)

    candidates = []
    for alt in alternatenames.split(","):
        alt = alt.strip().strip("\u200e\u200f")
        if not alt:
            continue
        simplified = to_simplified(alt, t2s).strip()
        if not CJK_RE.search(simplified):
            continue
        if len(simplified) > 40 or "#" in simplified or "=" in simplified:
            continue
        if simplified in (name_simplified, ascii_simplified):
            continue
        if simplified not in candidates:
            candidates.append(simplified)

    if not candidates:
        return ""

    pure = [c for c in candidates if is_pure_cjk(c)]
    # 规则 1：官方短名（是另一个候选的前缀，且至少 2 字）优先，取最短者。
    prefixed = [c for c in pure if len(c) >= 2 and any(other != c and other.startswith(c) for other in candidates)]
    if prefixed:
        return min(prefixed, key=len)
    # 规则 2：优先纯 CJK 名字（避免 "上海 Shanghai" 这类混合形式）。
    if pure:
        return pure[0]
    return candidates[0]


def sanitize(text: str) -> str:
    return (text or "").replace("\t", " ").replace("\r", " ").replace("\n", " ").strip()


def build_tsv(raw_bytes: bytes, t2s: dict) -> tuple[bytes, int]:
    rows = []
    skipped = 0
    for raw_line in raw_bytes.decode("utf-8", errors="replace").splitlines():
        fields = raw_line.split("\t")
        if len(fields) < 16:
            skipped += 1
            continue
        _geonameid, name, asciiname, alternatenames, lat, lon = fields[0:6]
        fclass, fcode = fields[6], fields[7]
        if fclass != "P":  # 只要居民点，排除机场/山峰之类
            skipped += 1
            continue
        # 反向地理编码要的是"最近的城/镇"，而不是大城市的区/街道。
        # PPLX=section of populated place、PPLQ=abandoned、PPLL=locality 等会干扰最近邻
        #（例如上海坐标最近的是"黄浦区"而不是"上海"），生成时剔除。
        if fcode in SKIP_FEATURE_CODES:
            skipped += 1
            continue
        country, admin1 = fields[8], fields[10]
        # GeoNames cities15000.txt 共 19 列，population 在索引 14（索引 11 是 admin2 代码）。
        population = fields[14] if len(fields) >= 19 else fields[11]
        try:
            lat_f, lon_f = float(lat), float(lon)
        except ValueError:
            skipped += 1
            continue

        name = sanitize(to_simplified(name, t2s))
        asciiname = sanitize(to_simplified(asciiname, t2s))
        if not name and not asciiname:
            skipped += 1
            continue
        if not name:
            name = asciiname

        cjk = pick_cjk_name(alternatenames, name, asciiname, t2s)
        try:
            pop = int(population)
        except ValueError:
            pop = 0

        rows.append(
            "\t".join(
                [
                    f"{lat_f:.4f}",
                    f"{lon_f:.4f}",
                    name,
                    "" if asciiname == name else asciiname,
                    sanitize(country),
                    sanitize(admin1),
                    str(max(pop, 0)),
                    "" if cjk in (name, asciiname) else cjk,
                    str(FEATURE_RANK.get(fcode, 2)),
                ]
            )
        )

    # 按纬度/经度数值排序：让相邻行坐标相近，gzip 压缩率更高（也便于人工核对）。
    rows.sort(key=lambda r: (float(r.split("\t")[0]), float(r.split("\t")[1])))
    tsv = ("\n".join(rows) + "\n").encode("utf-8")
    print(f"[gen-cities] 有效城市 {len(rows)} 行，跳过 {skipped} 行；TSV {len(tsv) / 1e6:.2f} MB")
    return tsv, len(rows)


def write_csharp(out_path: str, tsv: bytes, count: int) -> None:
    gz = gzip.compress(tsv, compresslevel=9, mtime=0)
    b64 = base64.b64encode(gz).decode("ascii")
    sha = hashlib.sha256(gz).hexdigest()
    chunks = [b64[i : i + CHUNK_SIZE] for i in range(0, len(b64), CHUNK_SIZE)]

    lines = [
        "// <auto-generated>",
        "// 本文件由 Geo/Data/tools/gen_cities.py 生成，请勿手工修改。",
        "// 数据来源：GeoNames cities15000（https://download.geonames.org/export/dump/），授权 CC BY 4.0。",
        "// 中文别名已统一为简体（繁→简映射来自 OpenCC TSCharacters 子集，Apache-2.0）。",
        "// 内容为 gzip 压缩的紧凑 TSV，再经 base64 编码后按块存放，运行时解码为内存网格索引。",
        "// TSV 列：lat, lon, name, asciiname, country, admin1, population, cjkName, rank",
        f"// 城市数量：{count}；gzip {len(gz) / 1024:.1f} KB；base64 {len(b64) / 1024:.1f} KB；sha256(gzip)={sha}",
        "// </auto-generated>",
        "",
        "namespace StjMediaViewer.Core.Geo;",
        "",
        "internal static class CitiesGeoData",
        "{",
        f"    internal const int CityCount = {count};",
        '    internal const string Source = "GeoNames cities15000 (CC BY 4.0)";',
        f'    internal const string GzipSha256 = "{sha}";',
        "",
        "    internal static readonly string[] GzipBase64Chunks =",
        "    {",
    ]
    lines.extend(f'        "{chunk}",' for chunk in chunks)
    lines.extend(["    };", "}", ""])

    os.makedirs(os.path.dirname(out_path) or ".", exist_ok=True)
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))
    print(f"[gen-cities] 已生成 {out_path}（{os.path.getsize(out_path) / 1024:.1f} KB 源码）")


def main() -> None:
    data_dir, default_out = repo_paths()
    script_dir = os.path.dirname(os.path.abspath(__file__))
    parser = argparse.ArgumentParser(description="生成 OfflineGeocoder 的嵌入城市数据源")
    parser.add_argument("--dataset", default="cities15000", choices=sorted(DATASETS))
    parser.add_argument("--zip", default=os.path.join(tempfile.gettempdir(), "stj-cities.zip"),
                        help="本地 zip 路径；不存在则自动下载")
    parser.add_argument("--out", default=default_out, help="输出 cities.g.cs 路径")
    parser.add_argument("--keep-tsv", action="store_true", help="同时在 Data 目录留一份明文 cities.tsv")
    args = parser.parse_args()

    t2s = load_t2s(script_dir)
    zip_path = fetch_zip(args.dataset, args.zip)
    with zipfile.ZipFile(zip_path) as zf:
        members = [n for n in zf.namelist() if n.endswith(".txt")]
        if not members:
            raise SystemExit(f"[gen-cities] zip 内没有 .txt：{zf.namelist()}")
        member = members[0]
        print(f"[gen-cities] 解析 {member} ...")
        raw = zf.read(member)

    tsv, count = build_tsv(raw, t2s)
    write_csharp(args.out, tsv, count)

    if args.keep_tsv:
        tsv_path = os.path.join(data_dir, "cities.tsv")
        with open(tsv_path, "wb") as f:
            f.write(tsv)
        print(f"[gen-cities] 调试用明文已写入 {tsv_path}")


if __name__ == "__main__":
    main()