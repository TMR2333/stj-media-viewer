import io

# =============== 1) LibraryViewModel：Scope ===============
p = 'src/StjMediaViewer.App/ViewModels/LibraryViewModel.cs'
s = io.open(p, encoding='utf-8').read()
if 'LibraryScope Scope' not in s:
    s = s.replace('''    public void Initialize()
    {''','''    /// <summary>当前库范围（照片库 / 视频库 / 不分库）。侧边栏切换时由 LibraryPage 设置。</summary>
    public LibraryScope Scope { get; private set; } = LibraryScope.All;

    /// <summary>切换库范围：照片库只看图片、视频库只看视频/音频。</summary>
    public void SetScope(LibraryScope scope)
    {
        if (Scope == scope) return;
        Scope = scope;
        OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(ScopeTitle));
        RequestRefresh(immediate: true);
    }

    public string ScopeTitle => LibraryScopeRules.Title(Scope);

    public void Initialize()
    {''', 1)

    # BuildQuery：范围覆盖媒体类型
    s = s.replace('''        foreach (var id in KindGroup.SelectedIds)
            if (Enum.TryParse<MediaKind>(id, true, out var kind)) q.Kinds.Add(kind);''','''        if (Scope != LibraryScope.All)
        {
            // 分库模式：照片库/视频库的媒体类型由库本身决定，不再叠加「媒体类型」筛选
            LibraryScopeRules.Apply(q, Scope);
        }
        else
        {
            foreach (var id in KindGroup.SelectedIds)
                if (Enum.TryParse<MediaKind>(id, true, out var kind)) q.Kinds.Add(kind);
        }''', 1)
    io.open(p, 'w', encoding='utf-8').write(s)
    print('vm: scope')

# =============== 2) LibraryPage：导航进入时应用范围 ===============
p = 'src/StjMediaViewer.App/Views/LibraryPage.xaml.cs'
s = io.open(p, encoding='utf-8').read()
if 'ApplyNavScope' not in s:
    s = s.replace('''    private void OnLoaded(object sender, RoutedEventArgs e)''','''    /// <summary>侧边栏切到照片库 / 视频库时调用：只改范围，不重建整页。</summary>
    public void ApplyNavScope(string viewId)
    {
        ViewModel.SetScope(LibraryScopeRules.FromViewId(viewId));
        UpdateScopeChrome();
    }

    /// <summary>把"照片库/视频库"写进页头标题旁（不改页面结构）。</summary>
    private void UpdateScopeChrome()
    {
        try
        {
            TitleText.Text = ViewModel.ScopeTitle;
        }
        catch { }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)''', 1)
    io.open(p, 'w', encoding='utf-8').write(s)
    print('page: ApplyNavScope')

# =============== 3) ShellPage：同页不同库时只换范围 ===============
p = 'src/StjMediaViewer.App/Views/ShellPage.xaml.cs'
s = io.open(p, encoding='utf-8').read()
s = s.replace('''            PlaceholderHost.Visibility = Visibility.Collapsed;
            if (ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType, viewId,
                    new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
            return;''','''            PlaceholderHost.Visibility = Visibility.Collapsed;
            if (ContentFrame.CurrentSourcePageType != pageType)
            {
                ContentFrame.Navigate(pageType, viewId,
                    new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
            }
            else if (ContentFrame.Content is LibraryPage libraryPage &&
                     contentId is "library" or "library-photo" or "library-video")
            {
                // 照片库 ↔ 视频库：同一个页面对象，只切范围
                libraryPage.ApplyNavScope(contentId);
            }
            return;''', 1)
io.open(p, 'w', encoding='utf-8').write(s)
print('shell: same-page scope switch')