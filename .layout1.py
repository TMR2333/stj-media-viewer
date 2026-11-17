import io, re

PAGES = {
    'AboutPage':      {'scroll': 13, 'stack': 14, 'spacing': 16},
    'AiQueuePage':    {'scroll': 15, 'stack': 16},
    'CloudPage':      {'scroll': 16, 'stack': 17},
    'ImportPage':     {'scroll': 13, 'stack': 14},
    'OrganizePage':   {'scroll': 42, 'stack': 43},
    'SettingsPage':   {'scroll': 34, 'stack': 35},
}

for name in PAGES:
    p = f'src/StjMediaViewer.App/Views/{name}.xaml'
    s = io.open(p, encoding='utf-8').read()
    if 'x:Name="PageScroll"' in s:
        continue

    # 1) ScrollViewer 加名字
    s = s.replace('<ScrollViewer VerticalScrollMode="Enabled"', '<ScrollViewer x:Name="PageScroll" VerticalScrollMode="Enabled"', 1)
    # 2) 内容 StackPanel 加名字 + 入场动画
    m = re.search(r'<StackPanel (MaxWidth="1760" )?HorizontalAlignment="Left" Spacing="(\d+)">', s)
    assert m, name
    old_tag = m.group(0)
    new_tag = ('<StackPanel x:Name="PageContent" HorizontalAlignment="Left" '
               f'Spacing="{m.group(2)}">\n'
               '                <StackPanel.ChildrenTransitions>\n'
               '                    <TransitionCollection>\n'
               '                        <EntranceThemeTransition IsStaggeringEnabled="True"'
               ' FromHorizontalOffset="24" FromVerticalOffset="0" />\n'
               '                    </TransitionCollection>\n'
               '                </StackPanel.ChildrenTransitions>')
    s = s.replace(old_tag, new_tag, 1)
    io.open(p, 'w', encoding='utf-8').write(s)
    print('page xaml:', name)

# 3) 代码隐藏：Loaded 时调用 PageLayout.Fit
for name in PAGES:
    p = f'src/StjMediaViewer.App/Views/{name}.xaml.cs'
    s = io.open(p, encoding='utf-8').read()
    if 'PageLayout.Fit' in s:
        continue
    # 找到 Loaded 处理（OnLoaded 或 Loaded += 的 lambda），在开头插入
    inserted = False
    m = re.search(r'(private (?:async )?void OnLoaded\(object sender, RoutedEventArgs e\)\s*\{\n)', s)
    if m:
        s = s[:m.end()] + '        PageLayout.Fit(PageScroll, PageContent);\n' + s[m.end():]
        inserted = True
    if not inserted:
        m = re.search(r'(Loaded \+= (?:OnLoaded|Page_Loaded|OnPageLoaded)[^\n]*\n)', s)
        if m:
            s = s[:m.end()] + '        Loaded += (_, _) => PageLayout.Fit(PageScroll, PageContent);\n' + s[m.end():]
            inserted = True
    if inserted and 'using StjMediaViewer.App.Services;' not in s:
        s = s.replace('using StjMediaViewer.App.ViewModels;', 'using StjMediaViewer.App.Services;\nusing StjMediaViewer.App.ViewModels;')
        if 'using StjMediaViewer.App.Services;' not in s:
            # 找不到锚点时加在第一个 using 之后
            s = re.sub(r'(using [^\n]+;\n)', r'\1using StjMediaViewer.App.Services;\n', s, count=1)
    io.open(p, 'w', encoding='utf-8').write(s)
    print('page code:', name, 'inserted' if inserted else 'SKIP')