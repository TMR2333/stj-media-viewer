# 设计系统（Fluent）与 token 契约

> 界面遵循**微软 Fluent 设计语言**，实现方式是"语义 token + 一套 Fluent 取值"。
> 页面里**不允许出现硬编码颜色/圆角/字号**，一律 `{ThemeResource ...}`。
> 本文档是 `src/StjMediaViewer.App/Themes/` 的说明书。

## 1. 为什么是 token 层

```
页面 / 控件  ──只引用──▶  语义 token（App*/Text*/Badge*/…）
                              │
                              ├─ Themes/Tokens.Base.xaml      （兜底 + 契约：键必须存在）
                              └─ Themes/Tokens.Fluent.xaml    （Fluent 取值 + Light/Dark 两套）
```

- `Tokens.Base.xaml` 由 `App.xaml` 常驻合并，保证任何页面在皮肤词典加载前也能渲染（键永远齐）。
- `Tokens.Fluent.xaml` 在 `DesignLanguageService.ApplyAsync` 时追加合并，**同名键覆盖** Base。
- 明暗维度放在字典内部的 `ThemeDictionaries`（`Default` = 深色、`Light` = 浅色），与外层词典正交。
- 除自己的 `App*` token 外，`Tokens.Fluent.xaml` 还覆盖约 140 个 **WinUI 框架资源键**
  （`NavigationView*` / `TextControl*` / `ComboBox*` / `Button*` / `Flyout*` / `ScrollBar*`…），
  所以标准控件也随 token 走，页面不需要自定义模板。

## 2. 颜色 token

| 分组 | 键 | Fluent 深色 | Fluent 浅色 | 用途 |
|---|---|---|---|---|
| 窗口/表面 | `AppWindowBackgroundBrush` | `#1A1A1C`（Mica 下透出） | `#F3F3F3` | 窗口底 |
| | `AppSurfaceBrush` | `#202024` | `#FFFFFF` | 内容面板 / 三栏 / chrome |
| | `AppSurfaceAltBrush` | `#27272C` | `#F7F7F9` | 次级面板 / 输入框 / 行卡 |
| | `AppSurfaceRaisedBrush` | `#2D2D33` | `#FFFFFF` | 卡片（浮起） |
| | `AppSurfaceOverlayBrush` | `#F22A2A30` | `#F7FFFFFF` | 浮层 / 菜单 / 对话框 |
| 线 | `AppStrokeBrush` | 12% 白 | 12% 黑 | **输入类**控件边界 |
| | `AppCardStrokeBrush` | 6% 白 | 6% 黑 | **容器/卡片**柔描边 |
| | `AppDividerBrush` | 8% 白 | 8% 黑 | 分隔线 |
| 文本 | `TextPrimaryBrush` / `TextSecondaryBrush` / `TextTertiaryBrush` / `TextDisabledBrush` | 100%/70%/45%/30% 白 | 对应黑 | 正文/次要/提示/禁用 |
| 强调 | `AccentBrush` / `AccentHoverBrush` / `AccentPressedBrush` | `#60CDFF` 系 | `#0078D4` 系 | 主操作 |
| | `AccentSubtleBrush` / `AccentBorderBrush` | 12% / 30% 主色 | 同 | 弱强调底/边 |
| 语义 | `DangerBrush` / `WarningBrush` / `SuccessBrush` / `InfoBrush`（各带 `*SubtleBrush`） | 见 `Tokens.Fluent.xaml` | 同 | 状态色 |
| 徽标 | `BadgeRawBrush/TextBrush`、`BadgeLog*`、`BadgeVideo*`、`BadgeImage*`、`BadgeAudio*`、`BadgeAi*` | 见文件 | 同 | RAW 琥珀 / Log 紫 / 视频蓝 / 图片绿 / 音频粉 / AI 靛 |
| 状态 | `AppHoverBrush` / `AppPressedBrush` / `AppSelectedBrush` / `AppSelectedBorderBrush` / `AppFocusRingBrush` | 6% / 8% / 15% / 100% | 同 | 交互反馈 |
| 遮罩 | `AppScrimBrush` | `#B3000000` | `#8C000000` | 全屏预览 |

## 3. 几何 / 密度

| 键 | 值 | 说明 |
|---|---|---|
| `AppRadiusSmall` / `Medium` / `Large` / `XLarge` / `Pill` | 4 / 8 / 12 / 20 / 999 | 小控件 → 大面板 |
| `AppCardRadius` | 12 | 卡片 |
| `AppControlHeight` / `Small` / `Large` | 36 / 28 / 44 | 控件高度 |
| `AppCardStrokeThickness` | 1 | 卡片描边 |
| `AppGap` / `AppGapLarge` | 8 / 16 | 间距 |
| `AppPagePadding` / `AppCardPadding` / `AppPanePadding` | 24,18 / 12 / 16,12 | 页面 / 卡片 / 栏内边距 |

## 4. 字体与字阶

- 字体族：`AppFontFamily` = *Segoe UI Variable Text*；`AppDisplayFontFamily` = *Segoe UI Variable Display*；
  `AppMonoFontFamily` = *Cascadia Mono*；`AppIconFontFamily` = *Segoe Fluent Icons*（系统内置，不额外分发）。
- 字号：`AppFontDisplaySize` 32 / `Title` 24 / `Subtitle` 17 / `Body` 14 / `Caption` 12 / `Micro` 11。
- 预置文本样式：`AppDisplayTextStyle`、`AppTitleTextStyle`、`AppSubtitleTextStyle`、`AppBodyTextStyle`、
  `AppCaptionTextStyle`、`AppMicroTextStyle`、`AppSectionHeaderTextStyle`。

## 5. 动效

`AppMotionFastMs` 120 / `AppMotionStandardMs` 240 / `AppMotionEmphasizedMs` 380。
动画只用于进出场与状态过渡，不做夸张形变。

## 6. 控件样式 API

页面可直接引用的命名样式：

| 键 | TargetType | 用途 |
|---|---|---|
| `AppIconButtonStyle` | Button | 图标按钮（28×28，图标字体） |
| `AppSubtleButtonStyle` | Button | 次级按钮（有底 + 描边） |
| `AppChipStyle` | ToggleButton | 筛选胶囊（`FacetChip` 使用） |
| `AppCardStyle` / `AppPaneStyle` | Border | 卡片 / 侧栏面板 |
| `AppDividerLineStyle` | Rectangle | 1px 分隔线 |

自研控件（`App/Controls/`，全部 token 驱动）：`Badge`、`FacetChip`、`RatingStars`、`EmptyState`、
`SectionCard`、`GlassPanel`（历史命名，现为普通卡片容器）、`StatTile`、`ThumbnailImage`。

## 7. 已下线的设计语言

早期版本实现过 Material Design 3 与 Liquid Glass 两套皮肤（各自的 token 词典、控件词典、背景光场控件）。
因观感不达标，**已按要求整体删除**：

- 删除：`Themes/Tokens.Md3.xaml`、`Themes/Tokens.Glass.xaml`、`Themes/Controls.Md3.xaml`、
  `Themes/Controls.Glass.xaml`、`Controls/AmbientBackdrop.xaml(.cs)`；
- 删除：设置页的"设计语言"三选一、顶栏的皮肤下拉；
- `DesignLanguageIds` 只剩 `Fluent`，`Normalize("md3"|"glass")` 安全回落到 Fluent（旧 settings.json 不会崩）。

## 8. 改样式的正确姿势

1. 想改颜色/圆角/字号 → 改 `Tokens.Fluent.xaml`（同名键），**不要改页面**。
2. 想新增语义 → 在 `Tokens.Base.xaml` 与 `Tokens.Fluent.xaml` 同时加键（Base 给兜底值）。
3. 想改某类控件的默认外观 → 覆盖对应的 WinUI 框架资源键（参考 `Tokens.Fluent.xaml` 里的控件桥）。
4. 只有结构性差异（模板树变化）才写新 `Style`/`ControlTemplate`，并放进 `Themes/`。