import io

p = 'src/StjMediaViewer.App/ViewModels/LibraryViewModel.cs'
s = io.open(p, encoding='utf-8').read()

if '_bulkAssets' not in s:
    # 1) 全选（全部筛选结果，而不只是已加载的 200 张）+ 操作目标展开
    s = s.replace('''    /// <summary>全选当前网格里已加载的素材（筛选到合集时通常就是全部成员）。</summary>''','''    /// <summary>全选当前筛选结果（含未加载页）；批量操作以它为准。</summary>
    private List<MediaAsset>? _bulkAssets;

    public bool HasBulkSelection => _bulkAssets is { Count: > 0 };

    /// <summary>全选当前筛选结果：查询全部命中素材（不受 200 张分页限制）。</summary>
    public async Task<int> SelectAllFilteredAsync(CancellationToken ct = default)
    {
        try
        {
            var assets = await AppServices.Store.QueryAsync(BuildQuery(0, 100000), ct);
            _bulkAssets = assets.ToList();
            SelectionText = $"已全选 {_bulkAssets.Count} 张（含未加载页）";
            SelectionVisibility = _bulkAssets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            // 已加载的卡片同步高亮，方便看
            if (Items.Count > 0) SetSelection(Items.ToList(), keepBulk: true);
            return _bulkAssets.Count;
        }
        catch (Exception ex)
        {
            Log.Warn("全选失败", ex);
            return 0;
        }
    }

    /// <summary>批量操作的实际目标：全选结果或当前卡片选择；合并组封面会展开成组内全部成员。</summary>
    public async Task<IReadOnlyList<MediaAsset>> ResolveOperationTargetsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<MediaAsset> baseAssets = _bulkAssets is { Count: > 0 }
            ? _bulkAssets
            : SelectedItems.Select(i => i.Asset).ToList();

        if (baseAssets.Count == 0) return baseAssets;
        try
        {
            return await SelectionExpander.ExpandMergeGroupsAsync(AppServices.Store, baseAssets, ct);
        }
        catch (Exception ex)
        {
            Log.Warn("展开合并组失败", ex);
            return baseAssets;
        }
    }

    /// <summary>清掉"全选"状态（点选 / 框选 / 刷新时调用）。</summary>
    public void ClearBulkSelection()
    {
        if (_bulkAssets is null) return;
        _bulkAssets = null;
        OnPropertyChanged(nameof(HasBulkSelection));
    }

    /// <summary>全选当前网格里已加载的素材（筛选到合集时通常就是全部成员）。</summary>''', 1)

    # 2) SetSelection 支持保留 bulk，同时普通点选清掉 bulk
    s = s.replace('''    public void SetSelection(IReadOnlyList<AssetCardItem> selected)
    {''','''    public void SetSelection(IReadOnlyList<AssetCardItem> selected, bool keepBulk = false)
    {
        if (!keepBulk) ClearBulkSelection();''', 1)

    io.open(p, 'w', encoding='utf-8').write(s)
    print('vm: bulk selection + expand targets')

# 3) 页面：全选走全部筛选结果；批量填写用展开后的目标
p = 'src/StjMediaViewer.App/Views/LibraryPage.xaml.cs'
s = io.open(p, encoding='utf-8').read()

s = s.replace('''    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var count = ViewModel.SelectAllLoaded();
        AppEvents.SetStatus(count > 0 ? $"已全选 {count} 张" : "当前清单为空");
    }''','''    private async void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        // 全选"全部筛选结果"：不再只选已加载的 200 张，批量填写/删除都会覆盖全部
        var count = await ViewModel.SelectAllFilteredAsync();
        AppEvents.SetStatus(count > 0 ? $"已全选 {count} 张（含未加载页）" : "当前清单为空");
    }''', 1)

    s = s.replace('''    private void BatchEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItems.Count == 0)
        {
            AppEvents.SetError("先在图库里选中素材，再批量填写信息");
            return;
        }
        Frame.Navigate(typeof(BatchEditPage));
    }''','''    private async void BatchEdit_Click(object sender, RoutedEventArgs e)
    {
        var targets = await ViewModel.ResolveOperationTargetsAsync();
        if (targets.Count == 0)
        {
            AppEvents.SetError("先在图库里选中素材，再批量填写信息");
            return;
        }
        BatchEditViewModel.PendingAssets = targets;
        Frame.Navigate(typeof(BatchEditPage));
    }''', 1)
    io.open(p, 'w', encoding='utf-8').write(s)
    print('page: select all + batch targets')