import re, sys

p = 'src/StjMediaViewer.App/ViewModels/LibraryViewModel.cs'
s = open(p, encoding='utf-8').read()
orig = s

def sub(old, new, count=1):
    global s
    if old not in s:
        raise SystemExit('anchor missing: ' + old[:60])
    s = s.replace(old, new, count)

# 1) 属性块
sub('''    // ---------------- 快捷筛选 ----------------
    [ObservableProperty]
    private bool _onlyUnclassified;

    [ObservableProperty]
    private bool _onlyRaw;

    [ObservableProperty]
    private bool _onlyLog;
''',
'''    // ---------------- 快捷筛选 ----------------
    [ObservableProperty]
    private bool _onlyRaw;

    [ObservableProperty]
    private bool _onlyLog;

    /// <summary>只看未调色（未后期且没套 LUT）。</summary>
    [ObservableProperty]
    private bool _onlyUncolored;

    /// <summary>只看已调色（已后期，或已经套用 LUT）。</summary>
    [ObservableProperty]
    private bool _onlyColored;

    /// <summary>按当前媒体类型显示"只看 RAW / 只看 Log"：选视频只留 Log，选图片只留 RAW。</summary>
    [ObservableProperty]
    private Visibility _rawChipVisibility = Visibility.Visible;

    [ObservableProperty]
    private Visibility _logChipVisibility = Visibility.Visible;
''')

# 2) 查询：去掉 OnlyUnclassified，加 Colored
sub('            OnlyUnclassified = OnlyUnclassified,\n', '')
sub('        if (OnlyLog) q.IsLog = true;',
    '        if (OnlyLog) q.IsLog = true;\n'
    '        if (OnlyColored) q.Colored = true;\n'
    '        if (OnlyUncolored) q.Colored = false;')

# 3) IsAnyFilterActive
sub('Groups.Any(g => g.HasSelection) || OnlyUnclassified || OnlyRaw || OnlyLog || !string.IsNullOrWhiteSpace(SearchText);',
    'Groups.Any(g => g.HasSelection) || OnlyRaw || OnlyLog || OnlyColored || OnlyUncolored ||\n        !string.IsNullOrWhiteSpace(SearchText);')

# 4) ClearAllFilters
sub('        OnlyUnclassified = false;\n', '')

# 5) 回调
sub('    partial void OnOnlyUnclassifiedChanged(bool value) => OnQuickFilterChanged();\n',
    '')
sub('    partial void OnOnlyLogChanged(bool value) => OnQuickFilterChanged();',
'''    partial void OnOnlyLogChanged(bool value) => OnQuickFilterChanged();

    partial void OnOnlyColoredChanged(bool value)
    {
        if (value && OnlyUncolored) OnlyUncolored = false;
        OnQuickFilterChanged();
    }

    partial void OnOnlyUncoloredChanged(bool value)
    {
        if (value && OnlyColored) OnlyColored = false;
        OnQuickFilterChanged();
    }''')

# 6) 摘要
sub('        if (OnlyUnclassified) parts.Add("只看未分类");\n', '')
sub('        if (OnlyLog) parts.Add("只看 Log");',
    '        if (OnlyLog) parts.Add("只看 Log");\n'
    '        if (OnlyUncolored) parts.Add("只看未调色");\n'
    '        if (OnlyColored) parts.Add("只看已调色");')

# 7) 类型切换时更新 chip 可见性
sub('''    private void OnGroupSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressRefresh) return;
        OnFilterChanged();
    }''',
'''    private void OnGroupSelectionChanged(object? sender, EventArgs e)
    {
        UpdateKindChipVisibility();
        if (_suppressRefresh) return;
        OnFilterChanged();
    }

    /// <summary>
    /// 媒体类型只选了"视频"时隐藏「只看 RAW」（并清掉已勾选的 RAW 条件），
    /// 只选了"图片"时隐藏「只看 Log」，避免出现必然 0 条的组合。
    /// </summary>
    private void UpdateKindChipVisibility()
    {
        var selected = KindGroup.SelectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool onlyVideo = selected.Count == 1 && selected.Contains(nameof(MediaKind.Video));
        bool onlyImage = selected.Count == 1 && selected.Contains(nameof(MediaKind.Image));

        RawChipVisibility = onlyVideo ? Visibility.Collapsed : Visibility.Visible;
        LogChipVisibility = onlyImage ? Visibility.Collapsed : Visibility.Visible;

        if (RawChipVisibility == Visibility.Collapsed && OnlyRaw) OnlyRaw = false;
        if (LogChipVisibility == Visibility.Collapsed && OnlyLog) OnlyLog = false;
    }''')

if s == orig:
    raise SystemExit('no change')
open(p, 'w', encoding='utf-8').write(s)
print('VM patched')

# 8) code-behind: 修正 namespace 引用
p2 = 'src/StjMediaViewer.App/Views/LibraryPage.xaml.cs'
s2 = open(p2, encoding='utf-8').read()
s2 = s2.replace('var assets = new List<CoreModels.MediaAsset>();', 'var assets = new List<StjMediaViewer.Core.Models.MediaAsset>();')
open(p2, 'w', encoding='utf-8').write(s2)
print('code-behind namespace fixed')