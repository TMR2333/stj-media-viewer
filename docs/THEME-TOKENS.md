# THEME-TOKENS —— 设计语言 token 契约与三套皮肤

> 本文档是 `src/StjMediaViewer.App/Themes/` 的说明书，也是"界面禁止硬编码颜色/圆角/字号"的
> 唯一依据。**任何页面/控件只允许使用 `{ThemeResource Xxx}`**，`Xxx` 必须出现在本文档的键表里。
>
> 维护者：ui-shell（`Themes/`、`Controls/`、`Services/DesignLanguageService.cs`、`Services/WindowChromeService.cs`）
> 视觉参考：[LiquidGlassUI 设计原则](https://wei-zhang09.github.io/LiquidGlassUI/guide/design.html)（液态玻璃的九要素与反模式）

---

## 1. 架构：一套键，三套皮

```
App.xaml
└── Application.Resources.MergedDictionaries
    ├── XamlControlsResources                  ← WinUI 3 自带控件样式
    ├── Themes/Tokens.Base.xaml                ← token 契约（兜底值 = Fluent），永远在
    └── [运行时追加] Themes/Tokens.{皮肤}.xaml  ← 后合并者胜出
        └── [MD3/Glass] Themes/Controls.{皮肤}.xaml  ← 结构性差异（模板/字阶）
```

* **外层 MergedDictionaries = 设计语言**：`DesignLanguageService.ApplyAsync` 移除旧皮肤词典、
  追加新皮肤词典。
* **内层 ThemeDictionaries = 明暗**：每套皮肤都提供 `Default`（深色）与 `Light`（浅色）。
  **不要求 HighContrast**（系统高对比度时由框架兜底）。
* `Tokens.Base.xaml` 是"键必须存在"的兜底定义。皮肤词典只提供**同名同类型**的键，
  App 代码只认键、不认具体值。新增键必须同时写进 Base 和全部三套皮肤。
* `App*Brush` 的类型是 **Brush**（不限于 SolidColorBrush）：Glass 皮肤里
  `AppWindowBackgroundBrush`、`AppAmbient*`、`AppGlassHighlightBrush`、`AppGlassSheenBrush`
  是渐变 Brush；Fluent / MD3 用纯色或透明占位 —— 引用方只当 Background/BorderBrush 用，完全兼容。

> 界面里出现 `#FF...`、`Colors.Red`、`new SolidColorBrush(...)` 都是 bug。
> 唯一例外：`WindowChromeService` 的标题栏配色（`AppWindowTitleBar` 只接受 `Windows.UI.Color`）
> 和 `ThumbnailImage` 的程序化渐变占位（必须按文件 hash 生成，不能来自皮肤）。

---

## 2. 三套语言的设计坐标

### 2.1 从 LiquidGlassUI 吸收的原则（改造后的硬规则）

1. **背景光场是地基**：没有东西可折射，玻璃就不存在。`AmbientBackdrop` 在应用内画
   底色渐变 + 四个大半径彩色光斑（蓝/紫/青/暖）+ 四周压暗；Fluent/MD3 下全透明。
2. **边缘高光必须是渐变，不是实心边框**：实心白边是"廉价玻璃感"的头号来源。
   玻璃卡片/按钮用 `AppGlassHighlightBrush`（两端透明、峰值 ~50% 白的线性渐变）做顶部高光，
   再叠 `AppGlassSheenBrush`（45° 白渐隐）做镜面反射。
3. **阴影组合给厚度**：WinUI 没有 CSS `box-shadow/inset`，用
   `ThemeShadow`（外阴影）+ 多层 Border（内阴影/高光）组合；只有 Glass 皮肤挂外阴影
   （`AppGlassShadowOpacity > 0` 才启用）。
4. **统一光源永远左上**：顶部高光、45° sheen、底部内阴影方向一致；所有皮肤同一方向。
5. **材质分级，不要所有元素同一强度**：`regular`（导航/顶栏，`AppSurfaceBrush`）→
   `elevated`（卡片，`AppSurfaceRaisedBrush`）→ `focused`（浮层/对话框，`AppSurfaceOverlayBrush`）。
6. **圆角随尺寸成比例**：Glass 10/18/26/32，MD3 4/12/16/28，Fluent 4/8/12/16。
7. **可读性优先于材质**：正文永远用 `TextPrimaryBrush`/`TextSecondaryBrush`；玻璃面板底色
   保持足够不透明度（Glass 卡片 65% 深 / 80% 浅），对话框类用 `AppSurfaceOverlayBrush` 的高不透明度。
8. **反模式**：所有面都玻璃、实心白边、霓虹/彩虹配色、光向不一致、牺牲对比度。

### 2.2 四维度对照

| 维度 | Fluent（微软） | Material Design 3（谷歌） | Liquid Glass（苹果） |
|---|---|---|---|
| **材质** | Mica / Acrylic：窗口底色半透明（`#CC`），表面 `#FF202024`，细描边，**有**系统背板模糊 | **零透明**：5 级 surface container 靠色调分层，关闭系统背板 | 玻璃：**背景光场** + 三层材质（regular/elevated/focused）+ 渐变高光 + 45° sheen + 内阴影 + ThemeShadow 外阴影；系统背板 Acrylic |
| **圆角** | 4 / 8 / 12（卡片 8） | 4 / 12 / 16 / 28（卡片 16，按钮 pill，浮层 28） | 10 / 18 / 26 / 32（卡片 26，按钮 18，浮窗 32） |
| **密度** | 控件 36/28/44；页距 24,18；间距 8/16；卡片内距 12 | 控件 40/32/48；页距 32,24；间距 12/20；卡片内距 20（大留白） | 控件 40/32/50；页距 32,26；间距 12/20；卡片内距 22（更松） |
| **字体** | Segoe UI Variable；32/24/17/14/12/11 | 34/22/16/14/12/11；display/title=Regular，label=Medium | 34/26/18/15/12/11；display=Bold；动效 160/320/560 |
| **状态反馈** | hover 6% 白，pressed 8%，无 ripple | state layer overlay 8%/12%，选中=secondary container | hover 提亮（叠 sheen），pressed 压暗；轻度上浮感由阴影表达 |
| **主色** | 深 `#60CDFF` / 浅 `#0078D4` | 深 `#D0BCFF` / 浅 `#6750A4` | 深 `#0A84FF` / 浅 `#007AFF` |

### 2.3 MD3 的 5 级 surface 层次（这是 MD3 "像不像"的关键）

| MD3 语义 | 我们的 token | 深色 | 浅色 | 用在哪 |
|---|---|---|---|---|
| surface-container-lowest | `AppWindowBackgroundBrush` | `#0F0D13` | `#EDE8F0` | 窗口最底、内容区最外层 |
| surface | `AppSurfaceBrush` | `#1D1B20` | `#F7F2FA` | 内容区底板 / 顶栏 / 侧栏 |
| surface-container-low | `AppSurfaceAltBrush` | `#26232B` | `#ECE6F0` | 状态栏 / 输入框 / 列表行卡（比 surface 深一级，形成内凹层次） |
| surface-container-high | `AppSurfaceRaisedBrush` | `#2B2930` | `#FFFFFF` | **卡片**（比内容底亮 2 级，浮起来；`GlassPanel/SectionCard` 默认） |
| surface-container-highest | `AppSurfaceOverlayBrush` | `#36343B` | `#E6E0E9` | 菜单 / Flyout / Dialog |

选中态用 **secondary container**：深 `#4A4458` / 浅 `#E8DEF8`（`AppSelectedBrush`），
卡片描边用 **outline-variant**（`AppCardStrokeBrush` = 深 `#1F49454F` / 浅 `#1F79747E`），
配合大留白（页距 32,24、卡片内距 20）和 state layer，才有 MD3 的"色调分层"而不只是"深灰底+紫主色"。

### 2.4 Liquid Glass 的材质分级与七层结构

| 层级 | token | 深色 | 浅色 |
|---|---|---|---|
| regular（导航/顶栏） | `AppSurfaceBrush` | `#8C10141E`（55%） | `#B3FFFFFF`（70%） |
| lower（状态栏/输入） | `AppSurfaceAltBrush` | `#66121825`（40%） | `#8CFFFFFF`（55%） |
| elevated（卡片） | `AppSurfaceRaisedBrush` | `#A61E2230`（65%） | `#CCFFFFFF`（80%） |
| focused（浮层/对话框） | `AppSurfaceOverlayBrush` | `#E61C202D`（90%） | `#F2FFFFFF`（95%） |

`GlassPanel` / `SectionCard` 的模板分层（全部 token 驱动）：

```
0. ThemeShadow 外阴影        ← AppGlassShadowOpacity / AppGlassShadowDepth（仅 Glass）
1. 底座                      ← AppSurfaceRaisedBrush + AppCardStrokeBrush + AppCardRadius
2. 玻璃 tint                 ← AppGlassTintBrush
3. 45° 镜面 sheen            ← AppGlassSheenBrush（光源左上）
4. 顶部渐变高光（1px）        ← AppGlassHighlightBrush（两端透明、峰值 50% 白）
5. 底部内阴影（1px）          ← AppGlassInnerShadowBrush（给"厚度"）
6. 内容                      ← ContentPresenter
```

Fluent / MD3 下第 2~5 层全部透明，第 0 层不挂 → 自动退化成普通卡片（不需要任何 if-else）。

---

## 3. 颜色 token 总表

`Default` = 深色，`Light` = 浅色；格式 `#AARRGGBB`。类型均为 **Brush**（可能是 SolidColorBrush 或渐变）。

| 键 | 语义 | Fluent 深 / 浅 | MD3 深 / 浅 | Glass 深 / 浅 |
|---|---|---|---|---|
| `AppWindowBackgroundBrush` | 窗口底层（可铺在系统背板上） | `#CC1A1A1C` / `#CCF3F3F3` | `#FF0F0D13` / `#FFF2ECF4` | `渐变(线性)` / `渐变(线性)` |
| `AppSurfaceBrush` | 主表面（导航区/顶栏/面板） | `#FF202024` / `#FFFFFFFF` | `#FF1D1B20` / `#FFFEF7FF` | `#8C10141E` / `#B3FFFFFF` |
| `AppSurfaceAltBrush` | 次级表面（输入框/状态栏） | `#FF27272C` / `#FFF7F7F9` | `#FF211F26` / `#FFF3EDF7` | `#66121825` / `#8CFFFFFF` |
| `AppSurfaceRaisedBrush` | 卡片/浮起层 | `#FF2D2D33` / `#FFFFFFFF` | `#FF2B2930` / `#FFFFFFFF` | `#A61E2230` / `#CCFFFFFF` |
| `AppSurfaceOverlayBrush` | 弹出层（菜单/Flyout/Dialog） | `#F22A2A30` / `#F7FFFFFF` | `#FF36343B` / `#FFE6E0E9` | `#E61C202D` / `#F2FFFFFF` |
| `AppDividerBrush` | 分隔线 | `#14FFFFFF` / `#14000000` | `#3349454F` / `#2679747E` | `#14FFFFFF` / `#1F000000` |
| `AppStrokeBrush` | 输入类描边 | `#1FFFFFFF` / `#1F000000` | `#FF49454F` / `#FF79747E` | `#1FFFFFFF` / `#2E000000` |
| `AppCardStrokeBrush` **NEW** | 卡片/容器描边（更柔） | `#0FFFFFFF` / `#0F000000` | `#1F49454F` / `#1F79747E` | `#1FFFFFFF` / `#1F000000` |
| `AppHoverBrush` | hover 覆盖 | `#0FFFFFFF` / `#0A000000` | `#14FFFFFF` / `#141D1B20` | `#14FFFFFF` / `#0F000000` |
| `AppPressedBrush` | pressed 覆盖 | `#14FFFFFF` / `#14000000` | `#1FFFFFFF` / `#1F1D1B20` | `#24FFFFFF` / `#1F000000` |
| `AppSelectedBrush` | 选中容器 | `#2660CDFF` / `#260078D4` | `#4A4458` / `#E8DEF8` | `#330A84FF` / `#26007AFF` |
| `AppSelectedBorderBrush` | 选中描边 | `#FF60CDFF` / `#FF0078D4` | `#33D0BCFF` / `#33B9A7D6` | `#FF0A84FF` / `#FF007AFF` |
| `AppFocusRingBrush` | 键盘焦点环 | `#FFFFFFFF` / `#FF000000` | `#FFD0BCFF` / `#FF6750A4` | `#FF0A84FF` / `#FF007AFF` |
| `TextPrimaryBrush` | 主文本 | `#FFFFFFFF` / `#FF1A1A1A` | `#FFE6E0E9` / `#FF1D1B20` | `#FFFFFFFF` / `#FF0B0B10` |
| `TextSecondaryBrush` | 次要文本 | `#B3FFFFFF` / `#B3000000` | `#FFCAC4D0` / `#FF49454F` | `#C2FFFFFF` / `#99000000` |
| `TextTertiaryBrush` | 提示/占位 | `#73FFFFFF` / `#73000000` | `#FF938F99` / `#FF79747E` | `#8CFFFFFF` / `#73000000` |
| `TextDisabledBrush` | 禁用文本 | `#4DFFFFFF` / `#4D000000` | `#61E6E0E9` / `#611D1B20` | `#4DFFFFFF` / `#4D000000` |
| `TextOnAccentBrush` | 主色上的文字 | `#FF00212F` / `#FFFFFFFF` | `#FF381E72` / `#FFFFFFFF` | `#FFFFFFFF` / `#FFFFFFFF` |
| `AccentBrush` | 主色 | `#FF60CDFF` / `#FF0078D4` | `#FFD0BCFF` / `#FF6750A4` | `#FF0A84FF` / `#FF007AFF` |
| `AccentHoverBrush` | 主色 hover | `#FF75D4FF` / `#FF106EBE` | `#FFDCC9FF` / `#FF7965B8` | `#FF409CFF` / `#FF3395FF` |
| `AccentPressedBrush` | 主色 pressed | `#FF4FB8E8` / `#FF005A9E` | `#FFC5ACFF` / `#FF4F378B` | `#FF0060DF` / `#FF0062CC` |
| `AccentSubtleBrush` | 主色 12% 容器 | `#1F60CDFF` / `#1F0078D4` | `#1FD0BCFF` / `#1F6750A4` | `#330A84FF` / `#26007AFF` |
| `AccentBorderBrush` | 主色描边 | `#4D60CDFF` / `#4D0078D4` | `#66D0BCFF` / `#666750A4` | `#660A84FF` / `#66007AFF` |
| `DangerBrush` / `DangerSubtleBrush` | 危险/错误 | `#FFFF99A4` / `#1FFF99A4` | `#FFF2B8B5` / `#1FF2B8B5` | `#FFFF453A` / `#26FF453A` |
| `WarningBrush` / `WarningSubtleBrush` | 警告 | `#FFFCE100` / `#1FFCE100` | `#FFF5C56B` / `#1FF5C56B` | `#FFFFD60A` / `#26FFD60A` |
| `SuccessBrush` / `SuccessSubtleBrush` | 成功 | `#FF6CCB5F` / `#1F6CCB5F` | `#FFA6E3AE` / `#1FA6E3AE` | `#FF30D158` / `#2630D158` |
| `InfoBrush` | 信息 | `#FF60CDFF` / `#FF0078D4` | `#FFD0BCFF` / `#FF6750A4` | `#FF64D2FF` / `#FF007AFF` |
| `BadgeRawBrush` / `BadgeRawTextBrush` | RAW 徽标 | `#FF8A5A00` / `#FFFFE0A3` | `#FF4E3F1A` / `#FFF3E0B0` | `#40FFD60A` / `#FFFFE68A` |
| `BadgeLogBrush` / `BadgeLogTextBrush` | Log 徽标 | `#FF533B96` / `#FFDDD1FF` | `#FF46315E` / `#FFE8DDFF` | `#40BF5AF2` / `#FFE2C7FF` |
| `BadgeVideoBrush` / `BadgeVideoTextBrush` | 视频徽标 | `#FF134A6B` / `#FFA9DBFF` | `#FF1F3B4C` / `#FFC6E7FF` | `#330A84FF` / `#FF9ECBFF` |
| `BadgeImageBrush` / `BadgeImageTextBrush` | 图片徽标 | `#FF1F4E28` / `#FFB7E8BF` | `#FF29402A` / `#FFC9E8CB` | `#4030D158` / `#FFA8F0B8` |
| `BadgeAudioBrush` / `BadgeAudioTextBrush` | 音频徽标 | `#FF6B2E4A` / `#FFFFC2DC` | `#FF4C2A3E` / `#FFFFD9E6` | `#40FF375F` / `#FFFFB3C6` |
| `BadgeAiBrush` / `BadgeAiTextBrush` | AI 徽标 | `#FF3B2F6B` / `#FFD5CBFF` | `#FF3E2E5C` / `#FFE6DDFF` | `#40FF9F0A` / `#FFFFE0B3` |
| `AppGlassTintBrush` | 玻璃 tint 层 | `#00FFFFFF` | `#00FFFFFF` | `#14FFFFFF` / `#4DFFFFFF` |
| `AppGlassStrokeBrush` | 玻璃轮廓（极淡，仅兜底） | `#00FFFFFF` | `#00FFFFFF` | `#1AFFFFFF` / `#33000000` |
| `AppGlassHighlightBrush` **改** | 玻璃顶部渐变高光 | `#00FFFFFF` | `#00FFFFFF` | `渐变(线性)` |
| `AppGlassSheenBrush` **NEW** | 45° 镜面 sheen | `#00FFFFFF` | `#00FFFFFF` | `渐变(线性)` |
| `AppGlassInnerShadowBrush` **NEW** | 底部内阴影（厚度） | `#00FFFFFF` | `#00FFFFFF` | `#26000000` / `#14000000` |
| `AppAmbientBrush` **NEW** | 环境光场底色 | `#00FFFFFF` | `#00FFFFFF` | `渐变(线性)` |
| `AppAmbientGlowBlueBrush` **NEW** | 光场：左上蓝 | `#00FFFFFF` | `#00FFFFFF` | `渐变(径向)` |
| `AppAmbientGlowVioletBrush` **NEW** | 光场：右上紫 | `#00FFFFFF` | `#00FFFFFF` | `渐变(径向)` |
| `AppAmbientGlowTealBrush` **NEW** | 光场：右下青 | `#00FFFFFF` | `#00FFFFFF` | `渐变(径向)` |
| `AppAmbientGlowWarmBrush` **NEW** | 光场：左下暖 | `#00FFFFFF` | `#00FFFFFF` | `渐变(径向)` |
| `AppAmbientVignetteBrush` **NEW** | 光场：四周压暗 | `#00FFFFFF` | `#00FFFFFF` | `渐变(径向)` |
| `AppScrimBrush` | 模态遮罩 | `#B3000000` / `#8C000000` | `#B3000000` / `#8C000000` | `#99000000` / `#66000000` |
| `AppTransparentBrush` | 透明刷 | `#00000000` | `#00000000` | `#00000000` |

> 浅色取值见各皮肤 `Light` 字典（表单每格为 `深 / 浅`）。
> 新增/改动的键已在"语义"列标 **NEW** / **改**；旧键**一个都没删、没改名**。

---

## 4. 几何 / 尺寸 / 字体 / 动效 token

| 键 | 语义 | Fluent | MD3 | Glass |
|---|---|---|---|---|
| `AppRadiusSmall` | 小圆角(输入/微件) | `4` | `4` | `10` |
| `AppRadiusMedium` | 中圆角(按钮) | `8` | `12` | `18` |
| `AppRadiusLarge` | 大圆角(面板) | `12` | `16` | `26` |
| `AppRadiusXLarge` | 超大圆角(浮窗) | `16` | `28` | `32` |
| `AppRadiusPill` | 胶囊 | `999` | `999` | `999` |
| `AppCardRadius` | 卡片圆角 | `8` | `16` | `26` |
| `AppControlHeight` | 控件高 | `36` | `40` | `40` |
| `AppControlHeightSmall` | 小控件高 | `28` | `32` | `32` |
| `AppControlHeightLarge` | 大控件高 | `44` | `48` | `50` |
| `AppCardStrokeThickness` | 卡片描边宽 | `1` | `1` | `1` |
| `AppGlassStrokeThickness` | 玻璃描边宽（保留键） | `1` | `1` | `1.5` |
| `AppGap` | 小间距 | `8` | `12` | `12` |
| `AppGapLarge` | 大间距 | `16` | `20` | `20` |
| `AppCardElevation` | 卡片高度感 | `8` | `12` | `20` |
| `AppDividerThickness` | 分隔线宽 | `1` | `1` | `1` |
| `AppFontDisplaySize` | 展示字号 | `32` | `34` | `34` |
| `AppFontTitleSize` | 标题字号 | `24` | `22` | `26` |
| `AppFontSubtitleSize` | 副标题字号 | `17` | `16` | `18` |
| `AppFontBodySize` | 正文字号 | `14` | `14` | `15` |
| `AppFontCaptionSize` | 说明字号 | `12` | `12` | `12` |
| `AppFontMicroSize` | 微字号 | `11` | `11` | `11` |
| `AppMotionFastMs` | 快动效 | `120` | `150` | `160` |
| `AppMotionStandardMs` | 标准动效 | `240` | `250` | `320` |
| `AppMotionEmphasizedMs` | 强调动效 | `380` | `400` | `560` |
| `AppGlassShadowOpacity` **NEW** | 玻璃外阴影开关（>0 才挂 ThemeShadow） | `0` | `0` | `0.34` |
| `AppGlassShadowDepth` **NEW** | 玻璃阴影 Z 深度 | `0` | `0` | `18` |
| `ControlCornerRadius` | 框架控件圆角 | `4` | `12` | `18` |
| `OverlayCornerRadius` | 框架浮层圆角 | `8` | `28` | `26` |
| `ControlContentThemeFontSize` | 框架控件字号 | `14` | `14` | `15` |
| `TextControlThemeMinHeight` | 输入框最小高 | `32` | `40` | `42` |
| `AppPagePadding` | 页面内边距 | `24,18,24,18` | `32,24,32,24` | `32,26,32,26` |
| `AppCardPadding` | 卡片内边距 | `12` | `20` | `22` |
| `AppControlPadding` | 控件内边距 | `12,0,12,0` | `20,0,20,0` | `16,0,16,0` |
| `AppPanePadding` | 侧栏内边距 | `16,12,16,12` | `20,20,20,20` | `20,18,20,18` |

### 文本样式（`Style TargetType="TextBlock"`）

| 键 | 用途 |
|---|---|
| `AppDisplayTextStyle` | 展示级大标题（Fluent SemiBold / MD3 Regular / Glass Bold） |
| `AppTitleTextStyle` | 页面标题（MD3 Regular / 其余 SemiBold） |
| `AppSubtitleTextStyle` | 分区/卡片标题（MD3 Medium） |
| `AppBodyTextStyle` | 正文（次要色、自动换行） |
| `AppCaptionTextStyle` | 说明文字 |
| `AppMicroTextStyle` | 徽标/状态栏/极小字 |
| `AppSectionHeaderTextStyle` | 列表分区头 |

### 容器 / 控件样式

| 键 | TargetType | 说明 |
|---|---|---|
| `AppCardStyle` | Border | 标准卡片（surfaceRaised + **AppCardStrokeBrush** + cardRadius + cardPadding） |
| `AppPaneStyle` | Border | 侧栏容器（左侧 1px 分隔线） |
| `AppDividerLineStyle` | Rectangle | 横向分隔线 |
| `AppSubtleButtonStyle` | Button | 次级按钮（Fluent 描边钮 / MD3 tonal=secondary container / Glass 主色 tint 玻璃） |
| `AppIconButtonStyle` | Button | 图标按钮（MD3 圆形 / Glass 圆形玻璃 / Fluent 8px） |
| `AppChipStyle` | ToggleButton | 筛选胶囊（MD3 FilterChip=secondary container / Glass 玻璃胶囊） |

---

## 5. WinUI 3 "控件桥"

WinUI 3 自带控件的默认样式引用的是**框架 Fluent 资源键**（`ControlFillColorDefaultBrush`、
`NavigationViewItemForeground`、`TextControlBackground`…），不是我们的 token。
框架里这些键大多是指向"原语"的 `StaticResource` 别名（加载时固定），
所以**只改原语不生效**，必须覆盖控件实际引用的那个键。

每套皮肤的 ThemeDictionary 末尾都有一段"控件桥"：用 `StaticResource` 别名把框架键
指到本皮肤 token，例如：

```xml
<StaticResource x:Key="NavigationViewItemForeground" ResourceKey="TextSecondaryBrush" />
<StaticResource x:Key="TextControlBackground"      ResourceKey="AppSurfaceAltBrush" />
<StaticResource x:Key="AccentFillColorDefaultBrush" ResourceKey="AccentBrush" />
```

覆盖范围（约 140 个键/皮肤）：文本、填充、描边、背景、状态色，以及
Button / TextBox / ComboBox / NavigationView / Flyout / ToolTip / ContentDialog / ScrollBar /
ToggleSwitch / Slider / ProgressBar / CheckBox / RadioButton 的专用键；另外覆盖
`SystemAccentColor`（Light1-3 / Dark1-3），让框架强调色跟随皮肤（MD3 选中指示条 = 紫，
Glass = Apple 蓝）。

**新增皮肤时**：复制 `Tokens.Md3.xaml` 的结构 → 改 §2 四维度 + §3/§4 取值 + 控件桥目标 token →
在 `DesignLanguageService.SkinDictionaries` 注册词典 URI。

---

## 6. `DesignLanguageService` 切换机制

```
ApplyAsync(id):
  1. 校验/归一化 id（fluent | md3 | glass，非法 → fluent）
  2. 从 Application.Resources.MergedDictionaries 移除上一套皮肤词典
  3. 按顺序追加：Tokens.{皮肤}.xaml → Controls.{皮肤}.xaml（后者胜出）
  4. CurrentId = id；persist 时写 AppSettings.DesignLanguage 并 await Settings.SaveAsync()
  5. ApplyThemeToRoot()：根元素 RequestedTheme = ResolveTheme()
  6. Changed 事件
  7. WindowChrome.RefreshBackdropAsync()（Mica/Acrylic/纯色 + 标题栏配色）
  8. **重建视觉树**：保存 RootFrame.CurrentSourcePageType → CacheSize=0 → Content=null →
     Navigate(pageType)（投递到 DispatcherQueue，避免在页面自己的事件里同步销毁自己）
```

**为什么必须重建视觉树**：`{ThemeResource}` 的解析结果会缓存在已实例化元素上；
替换 `MergedDictionaries` 只对**之后创建**的元素生效。明暗切换（`SetThemeModeAsync`）不同：
改根元素 `RequestedTheme` 会触发框架对所有既有元素重解析 ThemeResource，所以**不需要重建**。

* `ApplySavedAsync()`：App 启动时由 Lead 调用，从 `AppSettings.DesignLanguage` 恢复，
  不回写设置，但会重建一次视觉树（保证首帧就是目标皮肤）。
* `SetThemeModeAsync("system"|"light"|"dark")`：写 `AppSettings.ThemeMode`，改根 `RequestedTheme`，
  刷新窗口标题栏，触发 `Changed`。
* `ResolveTheme()`：`light → ElementTheme.Light`、`dark → ElementTheme.Dark`、
  `system/未知 → ElementTheme.Default`。与 `SetThemeModeAsync` 使用同一份设置。
* 线程：所有 `ResourceDictionary` 操作都在 UI 线程；跨线程自动 `DispatcherQueue.TryEnqueue`。
* 容错：`Controls.{皮肤}.xaml` 加载失败只降级为"token 生效、按钮回默认模板"；重建失败回退
  为"明暗主题抖一帧"强制重解析。

### 窗口镀层（`WindowChromeService`）

| 皮肤 | `Window.SystemBackdrop` | 标题栏 |
|---|---|---|
| Fluent | `MicaBackdrop`（Win11）→ 不可用则 `DesktopAcrylicBackdrop` → 再不行纯色 | 透明底（让 Mica 透出），按钮跟随明暗 |
| MD3 | `null`（零透明） | 实色 surface 底，跟随明暗 |
| Glass | `DesktopAcrylicBackdrop` | 透明底（让 Acrylic 透出），按钮跟随明暗 |

* `MicaController.IsSupported()` / `DesktopAcrylicController.IsSupported()` + `try/catch` 双重兜底。
* 标题栏配色用 `AppWindowTitleBar.IsCustomizationSupported()` 保护（Win10 跳过）。
* `ActualThemeChanged` 时自动重刷标题栏颜色。

---

## 7. 字体说明

任务要求 MD3 优先 Roboto（若能下载 Apache-2.0 的 Roboto + Noto Sans SC 到 `Assets/Fonts/`）。

**本次没有内置字体**，三套皮肤统一使用系统字体（Segoe UI Variable / Segoe UI Variable Display），原因：

1. 构建环境访问 `raw.githubusercontent.com` 超时 / 证书吊销检查失败，无法稳定下载；
2. WinUI 3 **unpackaged** 应用对 `ms-appx:///Assets/Fonts/...` 自定义字体支持不稳定，失败是静默回退；
3. 包体：Noto Sans SC 全量单字重 ~10MB，四字重会显著增加仓库体积。

**接入方法**（已预留改动点）：
1. 下载 Roboto（Regular/Medium/SemiBold/Bold）与 Noto Sans SC 到 `src/StjMediaViewer.App/Assets/Fonts/`
   （csproj 已把 `Assets\**` 作为 Content 复制到输出）；
2. 用目录字体引用：`ms-appx:///Assets/Fonts/#Roboto`；
3. 只改三处：Glass/MD3 `Tokens.*.xaml` 的 `AppFontFamily` / `AppDisplayFontFamily` / `ControlContentThemeFontFamily`；
4. CJK：Roboto 无中文字形时会自动回落系统中文（Microsoft YaHei UI）。

---

## 8. `Controls/` 公共 API（ui-views 直接用）

所有控件：只读 token、支持 Light/Dark、三套皮肤自动变形、不硬编码颜色
（`ThumbnailImage` 的程序化渐变占位是唯一例外，见 §1）。

| 控件 | 基类 | 公共 API |
|---|---|---|
| `AmbientBackdrop` **NEW** | `UserControl` | 无参数；铺满父容器、`IsHitTestVisible=False`。放在 ShellPage 根 Grid 最底层，画出 Glass 的"背景光场"；Fluent/MD3 下透明无感 |
| `GlassPanel` | `ContentControl` | `ContentPadding: Thickness`、`Content`；玻璃七层结构（见 §2.4），Fluent/MD3 自动退化成卡片 |
| `SectionCard` | `ContentControl` | `Header: string`、`Subtitle: string`、`Glyph: string`、`Content` |
| `FacetChip` | `UserControl` | `Label: string`、`Count: int`（-1 隐藏）、`IsChecked: bool`（双向）、`CheckedChanged: EventHandler<bool>` |
| `RatingStars` | `UserControl` | `Rating: int`（0..5，双向）、`IsReadOnly: bool`、`RatingChanged: EventHandler<int>`；点第 n 颗=n，再点当前=归 0 |
| `Badge` | `UserControl` | `Text: string`、`Kind: BadgeKind`（Neutral/Raw/Log/Video/Image/Audio/Ai） |
| `StatTile` | `UserControl` | `Value: string`、`Label: string`、`Glyph: string`、`Hint: string`（内部用 GlassPanel，Glass 下也是玻璃卡片） |
| `EmptyState` | `UserControl` | `Glyph`/`Title`/`Description`/`ActionText`（空=无按钮）/`ActionCommand`、`ActionInvoked` |
| `ThumbnailImage` | `UserControl` | `Asset: MediaAsset`、`DecodeSize: int`（默认 320）、`ShowTypeGlyph: bool`、只读 `IsLoading` / `Source` |

推荐接入（ui-views 的 ShellPage 根层）：

```xml
<Grid>
    <controls:AmbientBackdrop />          <!-- 玻璃的背景光场，放在最底层 -->
    <Grid Background="{ThemeResource AppWindowBackgroundBrush}"> ... </Grid>
</Grid>
```

> 若只想用单个 Brush：`{ThemeResource AppAmbientBrush}` 是光场的**底色渐变**；
> 完整的四色光斑效果需要用 `AmbientBackdrop`（单个 Brush 放不下多个径向光源）。

### 设计约定（重要）

* **不要在代码里给控件赋 `SolidColorBrush`**：明暗切换时 ThemeResource 会自动重解析，
  但代码赋值的 Brush 不会。需要"按状态换颜色"时用预置的多个 XAML 元素切 `Visibility`
  （`Badge`、`RatingStars` 就是这么做的）。
* 换**设计语言**时 `DesignLanguageService` 会重建视觉树（Page 会被重新实例化），
  所以 Page 必须可重复创建；跨切换需要保留的状态放 ViewModel/服务层。
* 换**明暗**不重建，只重解析 ThemeResource → 任何颜色都必须来自 token。
* 玻璃的"厚度"只能靠 token 分层 + ThemeShadow；不要在页面里写 `Shadow`。

---

## 9. 命名规则（新增 token）

1. 前缀 `App` = 应用级语义（`AppSurface*`、`AppStroke*`、`AppRadius*`、`AppFont*`、`AppAmbient*`）；
   无前缀 = 内容语义（`TextPrimaryBrush`、`AccentBrush`、`Badge*`）。
2. 后缀：颜色 `*Brush`（类型 **Brush**，允许渐变）、圆角 `AppRadius*`（`CornerRadius`）、
   尺寸/字号/时长 `*Size/*Height/*Ms/*Thickness`、间距 `AppGap*`、内边距 `App*Padding`（`Thickness`）、
   字体 `*FontFamily`、阴影 `AppGlassShadow*`。
3. 状态/材质后缀：`Hover` / `Pressed` / `Selected` / `Disabled` / `Subtle`（低透明度容器）/
   `Border`（描边）/ `Text`（前景）/ `Sheen` / `Highlight` / `InnerShadow` / `Glow*` / `Vignette`。
4. 新增键必须**四件套**：`Tokens.Base.xaml`（Default + Light 兜底）与三套皮肤（各 Default + Light）。
   类型必须一致（Brush / CornerRadius / Double / Thickness / FontFamily）。
5. 皮肤**不许新增**"只有自己有"的键——皮肤是 Base 契约的覆盖，不是扩充。结构性差异放
   `Themes/Controls.{皮肤}.xaml`（只覆盖已有样式键、或定义隐式样式/模板）。
6. 键名一旦发布 = 契约冻结，只能新增不能改名（ui-views 的页面直接引用）。

---

## 10. 已知限制 / 风险

* `Tokens.Base.xaml` 里定义了 `GlassPanel` / `SectionCard` 的隐式默认样式（多层模板）。这是
  "App.xaml 只合并 Base、皮肤词典运行时才追加"决定的：控件在任何时刻都必须能渲染。
* **WinUI 没有给页面内元素做 backdrop-filter 的能力**：Glass 的"透"是
  半透明叠加 + 背景光场 + 渐变高光模拟出来的，不是真实折射；`Window.SystemBackdrop` 的系统级
  Mica/Acrylic 只作用于窗口背板。
* `ThemeShadow` 只在 Glass 皮肤启用（`AppGlassShadowOpacity=0.34`），Fluent/MD3 为 0。
  阴影在 ScrollViewer 边缘可能被裁剪，属 WinUI 限制。
* `ThumbnailImage` 的圆角：WinUI 的 `Border/Grid.CornerRadius` 只影响自身背景，不裁剪子元素，
  所以图片用方形 `Clip` 防溢出；严格圆角裁剪需要 Composition clip（未做）。
* 标题栏颜色是代码里的唯一颜色表（`WindowChromeService.TitleBarPalette`），因为
  `AppWindowTitleBar` 只接受 `Windows.UI.Color`。
* 系统级强调色（任务栏/系统对话框）不受皮肤影响，只覆盖了应用内 `SystemAccentColor`。
* 皮肤切换会重建 ShellPage；ShellPage 内部视图状态需 ui-views 用 `AppSettings.LastViewId` 恢复。