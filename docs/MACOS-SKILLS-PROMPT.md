# 给 macOS 仓库的提示词：用 BadGoods 技能做同规格优化

> 仓库：`stj-media-viewer-macos`（Swift / SwiftUI，含 `ios/` iPadOS 端）。
> 本文件可直接作为 macOS 侧 DSH 的任务提示词。Windows/Android 侧已按同一方法做过，产物在
> `stj-media-viewer/docs/skills/`（`README.md`、`quick-review-windows.md`、`quick-review-android.md`、
> `walkthrough-cases.md`、`page-map.md`）。

---

你在 **stj-media-viewer-macos** 仓库工作（SwiftUI 原生界面，macOS + iPadOS，代码在 `Sources/` 与 `ios/`）。

## 任务

1. 拉取技能仓库 <https://github.com/BadbotAI/BadGoods>（或用户提供的 ZIP），读取以下四个技能的 `SKILL.md` 及其关键参考：
   - `skills/review-frontend-production/`（按 quick review 流程；本任务**不需要** full 全站审查）
   - `skills/ux-writer/`（先定文案策略卡与对象/动作词表，再评审/改写界面文案）
   - `skills/ui-walkthrough/`（页面五态、0/1/多、中断与异常、视图与设备的用例设计）
   - `skills/frontend-to-ue/`（本产品是原生应用，无前端路由；按它的"页面—入口—交互—跳转"信息结构产出文字版地图即可，不必生成 UE HTML）
2. 以 **quick review** 方式审查 macOS 与 iPadOS 的这四条核心旅程：
   - 素材库：浏览/筛选/排序/批量选择/删除到回收站
   - 云相册：连接/浏览/下载到素材库/上传/同步
   - 详情与编辑：评分/色标/选用/地点/关键词保存；预览/全屏
   - 设置与首次启动：媒体库添加、权限/安全作用域、主题
3. 按技能要求交付（中文）：
   - `docs/skills/README.md`：技能应用说明 + **文案策略卡**（定位/用户三问/语气表/对象动作词表）
   - `docs/skills/quick-review-macos.md`：范围与基线、发现（位置/证据/影响/优先级/修复/复验）、修复顺序、未验证项、验收清单
   - `docs/skills/walkthrough-cases.md`：页面五态与 0/1/多、中断、边界、视图用例表（标注哪些未实测）
   - `docs/skills/page-map.md`：macOS/iPadOS 的页面—入口—交互地图（文字版）
4. 按下述清单实施修复（与 Windows/Android 对齐；可按 macOS 实际情况补充）：

### 必做修复（对应已发现的同类问题）
- **云相册失败可恢复**：加载/连接失败要有错误提示（原因 + 「重试」），重试保留当前目录；不要把异常只写进一行状态字。
- **操作前置条件可见**：未勾选文件时「下载到素材库」禁用；「上传所选素材」要说明"先在素材库选中，含 XMP 边车"。
- **空态可行动**：无素材→「添加媒体库/扫描」；筛选无结果→「清空筛选」。
- **保存失败反馈**：详情保存失败（只读/占用/权限）要提示原因且不崩溃；成功才显示"已保存"。
- **长文本不撑破布局**：错误/状态文案最多 2 行省略，完整信息进日志或可展开。
- **键盘与焦点**（macOS）：主要按钮可 Tab 到达、有可见焦点环；F5/⌘R 触发刷新（与 Windows F5 对齐）。
- **iPadOS 触控**：可点元素 ≥44pt；网格卡片补 accessibilityLabel（文件名 + 选用/RAW 状态）。
- **文案**：按 ux-writer 词表统一「素材/云相册/地点/合并组/废片/选用/未分类」；按钮=动词+对象；错误提示三段式（发生了什么 + 可做什么 + 重试）。
- **不可逆操作**：删除/解散合并组/覆盖导入必须有明确二次确认与后果说明。

### 明确标注、不要假装验证
- 没有真机/模拟器的项标"未验证"；只有源码的结论不能写成实测。
- 不要承诺"没有缺陷"；说明未覆盖的页面与未做的性能/读屏测试。

## 交付与发布

- 更新 `CHANGELOG.md` / `README.md`，版本号 +1（macOS 与 iPadOS 各自版本按其现有节奏）；
- 发行版仍按平台分开：macOS 只放 dmg，iPadOS 只放 ipa；不混入 Windows/Android 安装包；
- 构建/测试命令、截图与报告一并提交；若做 UI 走查，附关键页面截图（真实数据脱敏）。

## 验收

- `docs/skills/quick-review-macos.md` 中每个"已修"项都有复验步骤；
- 三个失败态（云相册失败、保存失败、筛选空）在 iPadOS 与 macOS 上都能给出可执行下一步；
- 文案词表与 Windows/Android 的 `docs/skills/README.md` 一致（同名对象同一叫法）；
- 发行版只包含 macOS/iPadOS 安装包，且 CHANGELOG 能对应到具体修复。