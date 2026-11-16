const fs = require('fs');

// ============================================================================
// 拖出到外部软件：改用 ListView.CanDragItems + DragItemsStarting（同步事件，无 deferral），
// 因此先用 DragFileCache 预热 StorageFile，事件里同步取用（未命中时短暂等待）。
// ============================================================================

// 1) 新增 DragFileCache
{
    const p = 'src/StjMediaViewer.App/Services/DragFileCache.cs';
    fs.writeFileSync(p, `using System.Collections.Concurrent;
using Windows.Storage;

namespace StjMediaViewer.App.Services;

/// <summary>
/// 拖拽到外部软件需要在同步的 DragItemsStarting 里交出 StorageFile，
/// 而 <see cref="StorageFile.GetFileFromPathAsync"/> 是异步的 —— 这里提前把可见卡片的
/// StorageFile 预热好（命中即秒开），未命中时同步等待一个很短的超时作为兜底。
/// </summary>
public static class DragFileCache
{
    private static readonly ConcurrentDictionary<string, Task<StorageFile?>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>预热若干路径（卡片进入视野时调用）。</summary>
    public static void Preload(IEnumerable<string?> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            _cache.GetOrAdd(path!, p => Task.Run(async () =>
            {
                try { return await StorageFile.GetFileFromPathAsync(p); }
                catch { return (StorageFile?)null; }
            }));
        }
    }

    /// <summary>同步取 StorageFile（最多等 timeoutMs，通常命中缓存立即返回）。</summary>
    public static StorageFile? Take(string path, int timeoutMs = 1500)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!_cache.TryGetValue(path, out var task))
        {
            task = Task.Run(async () =>
            {
                try { return await StorageFile.GetFileFromPathAsync(path); }
                catch { return (StorageFile?)null; }
            });
            _cache[path] = task;
        }

        try
        {
            if (!task.IsCompleted) task.Wait(timeoutMs);
            return task.IsCompletedSuccessfully ? task.Result : null;
        }
        catch
        {
            return null;
        }
    }
}
`, 'utf8');
    console.log('DragFileCache added');
}

// 2) XAML：卡片根去掉 CanDrag（ListViewItem 才是拖拽源），ListView 打开 CanDragItems
{
    const p = 'src/StjMediaViewer.App/Views/LibraryPage.xaml';
    let s = fs.readFileSync(p, 'utf8');

    s = s.replace(`            <!-- CanDrag + DragStarting：把素材（含 JPG/RAW 配对的两个文件）拖到 Photoshop / Lightroom 等外部软件 -->
            <Grid Margin="4"
                  CanDrag="True"
                  DragStarting="Card_DragStarting"
                  ToolTipService.ToolTip="{x:Bind TooltipText, Mode=OneWay}">`,
`            <Grid Margin="4" ToolTipService.ToolTip="{x:Bind TooltipText, Mode=OneWay}">`);

    // ListView：CanDragItems + DragItemsStarting
    s = s.replace(`                <ListView x:Name="AssetList"
                          Grid.Row="0"
                          SelectionMode="Extended"`,
`                <ListView x:Name="AssetList"
                          Grid.Row="0"
                          SelectionMode="Extended"
                          CanDragItems="True"
                          DragItemsStarting="AssetList_DragItemsStarting"
                          ContainerContentChanging="AssetList_ContainerContentChanging"`);

    fs.writeFileSync(p, s, 'utf8');
    console.log('xaml: listview drag');
}

// 3) code-behind：替换 DragStarting 为 DragItemsStarting + 预热
{
    const p = 'src/StjMediaViewer.App/Views/LibraryPage.xaml.cs';
    let s = fs.readFileSync(p, 'utf8');

    const start = s.indexOf('    /// <summary>\n    /// 把卡片拖到外部软件');
    if (start < 0) throw new Error('drag handler anchor');
    const endMarker = '\n    }\n';
    // 找到该方法的收尾：从 start 起数大括号
    let i = s.indexOf('{', start), depth = 0, end = -1;
    for (; i < s.Length; i++)
    {
        if (s[i] == '{') depth++;
        else if (s[i] == '}') { depth--; if (depth == 0) { end = i; break; } }
    }
    if (end < 0) throw new Error('handler end not found');
    const tail = s.indexOf('\n', end) + 1;
    s = s.slice(0, start) + s.slice(tail);

    s = s.replace('    private async void Card_DragStarting(UIElement sender, DragStartingEventArgs args)\n', '');
    // 上面可能已删掉方法名，保险起见再清一次残留签名
    s = s.replace(/private async void Card_DragStarting[^\n]*\n\s*\{\n?/, '');

    const handler = `    /// <summary>卡片进入视野时预热 StorageFile，拖出时不至于卡住。</summary>
    private void AssetList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not AssetCardItem card) return;
        try
        {
            DragFileCache.Preload(new[] { card.Asset.FullPath }.Concat(card.Asset.CompanionPaths));
        }
        catch { /* 预热失败不影响展示 */ }
    }

    /// <summary>
    /// 把素材拖到 Photoshop / Lightroom 等外部软件：DataPackage 里放真实磁盘文件
    /// （JPG/RAW 配对的两个文件一起给），目标程序直接按路径打开。
    /// </summary>
    private void AssetList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        try
        {
            var assets = e.Items.OfType<AssetCardItem>().Select(i => i.Asset).ToList();
            if (assets.Count == 0) assets = ViewModel.SelectedItems.Select(i => i.Asset).ToList();
            if (assets.Count == 0) { e.Cancel = true; return; }

            var files = new List<Windows.Storage.StorageFile>();
            foreach (var asset in assets)
            {
                foreach (var path in new[] { asset.FullPath }.Concat(asset.CompanionPaths))
                {
                    var file = DragFileCache.Take(path);
                    if (file is not null) files.Add(file);
                }
            }

            if (files.Count == 0) { e.Cancel = true; return; }

            e.Data.SetStorageItems(files);
            e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.Data.Properties.Description = files.Count == 1
                ? Path.GetFileName(files[0].Path)
                : $"{files.Count} 个文件";

            Log.Info("[drag] 拖出 " + files.Count + " 个文件：" +
                     string.Join(", ", files.Take(4).Select(f => Path.GetFileName(f.Path))));
            AppEvents.SetStatus(files.Count == 1
                ? $"拖出 1 个文件：{Path.GetFileName(files[0].Path)}"
                : $"拖出 {files.Count} 个文件");
        }
        catch (Exception ex)
        {
            Log.Warn("拖拽素材失败", ex);
            e.Cancel = true;
        }
    }
`;

    // 放到 OnlyColored_Click 方法之后
    const anchor = '    private void OnlyColored_Click(object sender, RoutedEventArgs e) =>\n        ViewModel.OnlyColored = (sender as ToggleButton)?.IsChecked == true;';
    if (!s.includes(anchor)) throw new Error('anchor for handler insert');
    s = s.replace(anchor, anchor + '\n\n' + handler);

    fs.writeFileSync(p, s, 'utf8');
    console.log('code-behind: DragItemsStarting');
}
console.log('done');