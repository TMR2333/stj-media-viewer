# 恢复地点数据（GeoRestore）

用于「之前写过拍摄地点，某次版本更新/重扫后大面积丢失」的恢复。

## 数据从哪来

1. **旧索引备份**（首选）：应用数据目录里的 `catalog.db.bak-*`（自动挑最新的、能打开的）。
   按**完整路径**与当前索引匹配，旧库里的 `city / province / country` 直接回填。
2. **目录结构推断**（补充）：对 `--library` 指定根目录下的素材，仍没有地点时，
   取根目录下第一层文件夹名当地点（跳过 `国庆 / 未标注地点 / 学校 / 素材 / 视频 / 照片` 等非地点名和日期目录）。

## 写入什么

- 当前 `catalog.db` 的 `city/province/country`（默认只填空值，`--overwrite` 可覆盖）；
- 同名 **XMP 边车**（RAW 写 `xxx.xmp`，其它写 `xxx.JPG.xmp`），内容为 `photoshop:City` 等；
  这样以后重装、换机、重扫都不会再丢。

## 用法

```powershell
# 先预览（不写库）
tools\GeoRestore\bin\Debug\net10.0-windows10.0.19041\win-x64\StjMediaViewer.GeoRestore.exe --dry-run

# 正式恢复（默认只填空值、写 XMP）
...GeoRestore.exe --library "G:\媒体\图片"

# 指定旧备份 / 覆盖已有值 / 不写 XMP
...GeoRestore.exe --backup "C:\...\catalog.db.bak-20261120-102426" --overwrite --no-xmp
```

参数：

| 参数 | 说明 |
| --- | --- |
| `--data <dir>` | 应用数据目录，默认 `%LOCALAPPDATA%\StjMediaViewer` |
| `--backup <file>` | 旧索引库；默认自动找最新 `catalog.db.bak-*` |
| `--library <dir>` | 图片根目录（目录推断只在该目录内生效），默认 `G:\媒体\图片` |
| `--dry-run` | 只预览统计 |
| `--overwrite` | 覆盖已有地点（默认只填空值） |
| `--no-xmp` | 只写索引，不生成 XMP 边车 |

> 运行前请先退出应用（SQLite 被占用会写失败）。
> 恢复完成后打开应用点顶栏「刷新」或重启即可看到地点筛选。

## 已知边界

- 旧库里存在、但当前索引里已没有的路径（文件被删/改名/移出媒体库）无法恢复；报数里会体现差额。
- 目录推断只取第一层文件夹名；`国庆`、`山东`（省名）这类会按原样写，必要时运行后用批量编辑改。
- 只动索引与边车，绝不修改原始 RAW/JPG。