const fs = require('fs');
const p = 'README.md';
let s = fs.readFileSync(p, 'utf8');

if (s.includes('崩溃诊断')) { console.log('already'); process.exit(0); }

const anchor = '## 三、';
const idx = s.indexOf(anchor);
if (idx < 0) throw new Error('section anchor');

const block = [
    '### 2.11 崩溃诊断（0xC000027B / 闪退）',
    '',
    'WinUI 的 **stowed exception**（进程退出码 `0xC000027B`）不会走 `UnhandledException`，日志里看不到任何"未处理异常"。为此内置了三层留痕：',
    '',
    '1. `AppDomain.FirstChanceException`：记录每一次"首次异常"的完整堆栈（过滤取消异常与 WinUI 探测 `ApplicationData` 的正常噪声）；',
    '2. `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException`：后台线程的致命异常也会先写进日志再终止；',
    '3. 日志位置：`%LOCALAPPDATA%\\StjMediaViewer\\stj-media-viewer.log`。',
    '',
    '已知并已修复的一类崩溃：缩略图用 `InMemoryRandomAccessStream` + `BitmapImage.SetSourceAsync` 之后立刻 `Dispose()` 流。`SetSourceAsync` 返回时解码尚未结束，渲染线程随后读到已释放的流 → 原生崩溃（**只在视频这种"冷缓存 + 多路并发"的场景炸**）。现在统一改成**按磁盘缓存文件的 URI 加载**（文件生命周期由系统管理），并对缓存文件做 SOI/EOI 完整性校验，半截 JPEG 直接删掉重生成；Shell 缩略图调用也串行化了。',
    '',
    '复现脚本：`tools/diagnostics/repro-video-crash-click.ps1`（UI 自动化真点一次"视频"筛选）、`tools/diagnostics/stress-grid-scroll.ps1`（清缓存 + 滚动画廊压测）。',
    '',
].join('\n');

s = s.slice(0, idx) + block + s.slice(idx);
fs.writeFileSync(p, s, 'utf8');
console.log('readme diagnostics section added');