import io

# ============ 1) AssetCardItem：合并组卡片信息 ============
p = 'src/StjMediaViewer.App/ViewModels/AssetCardItem.cs'
s = io.open(p, encoding='utf-8').read()
if 'MergeGroup' not in s:
    s = s.replace('''    public MediaAsset Asset
    {''','''    /// <summary>如果这张卡片代表一个"合并组"（多重曝光 / 星轨 / 演示视频合并成的大文件夹），这里存组信息。</summary>
    public MergeGroup? MergeGroup { get; private set; }

    public bool IsMergeGroupCard => MergeGroup is not null;

    public string MergeBadgeText => MergeGroup is null ? "" : $"合并 · {MergeGroup.AssetCount} 张";
    public Visibility MergeBadgeVisibility => MergeGroup is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>把卡片标记成合并组卡片（显示封面素材 + 组徽标）。</summary>
    public void ApplyMergeGroup(MergeGroup? group)
    {
        MergeGroup = group;
        OnPropertyChanged(nameof(IsMergeGroupCard));
        OnPropertyChanged(nameof(MergeBadgeText));
        OnPropertyChanged(nameof(MergeBadgeVisibility));
    }

    public MediaAsset Asset
    {''', 1)
    io.open(p, 'w', encoding='utf-8').write(s)
    print('card: merge group fields')

# ============ 2) LibraryViewModel：合并组筛选 + 折叠 ============
p = 'src/StjMediaViewer.App/ViewModels/LibraryViewModel.cs'
s = io.open(p, encoding='utf-8').read()
if 'MergeGroupGroup' not in s:
    # 2.1 构造 + 分组
    s = s.replace('''        CollectionGroup = new FilterGroupViewModel(FilterGroupKind.Collection, "影像合集", true);''','''        CollectionGroup = new FilterGroupViewModel(FilterGroupKind.Collection, "影像合集", true);
        MergeGroupGroup = new FilterGroupViewModel(FilterGroupKind.MergeGroup, "合并组", true);''', 1)
    s = s.replace('''            KindGroup, FormatGroup, FrequentDeviceGroup, DeviceGroup, CollectionGroup, YearGroup, MonthGroup, CityGroup,''','''            KindGroup, FormatGroup, FrequentDeviceGroup, DeviceGroup, CollectionGroup, MergeGroupGroup, YearGroup, MonthGroup, CityGroup,''', 1)
    s = s.replace('''    /// <summary>影像合集筛选（用户自建的素材分组）。</summary>
    public FilterGroupViewModel CollectionGroup { get; }''','''    /// <summary>影像合集筛选（用户自建的素材分组）。</summary>
    public FilterGroupViewModel CollectionGroup { get; }

    /// <summary>合并组筛选（多重曝光 / 星轨 / 演示视频合并成的大文件夹）。</summary>
    public FilterGroupViewModel MergeGroupGroup { get; }''', 1)

    # 2.2 查询
    s = s.replace('''        foreach (var id in CollectionGroup.SelectedIds) q.Collections.Add(id);''','''        foreach (var id in CollectionGroup.SelectedIds) q.Collections.Add(id);
        foreach (var id in MergeGroupGroup.SelectedIds) q.MergeGroups.Add(id);''', 1)

    # 2.3 分面（名称 + 计数）
    s = s.replace('''            SyncOptions(CollectionGroup, collectionCounts, key => collectionNames[key],''','''            var mergeNames = MergeGroups.ToDictionary(g => g.Id, g => g.DisplayName, StringComparer.Ordinal);
            var mergeCounts = facets.ByMergeGroup
                .Where(kv => mergeNames.ContainsKey(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            SyncOptions(MergeGroupGroup, mergeCounts, key => mergeNames[key]);
            SyncOptions(CollectionGroup, collectionCounts, key => collectionNames[key],''', 1)

    # 2.4 合并组列表 + 加载
    s = s.replace('''    public void Initialize()
    {
        _ = EnsureCollectionsLoadedAsync(CancellationToken.None);
        RequestRefresh(immediate: true);
    }''','''    /// <summary>合并组（网格折叠 + 筛选面板显示名称用）。</summary>
    public IReadOnlyList<MergeGroup> MergeGroups { get; private set; } = Array.Empty<MergeGroup>();

    private bool _mergeGroupsLoaded;

    public async Task RefreshMergeGroupsAsync()
    {
        try
        {
            MergeGroups = await AppServices.Store.GetMergeGroupsAsync();
            _mergeGroupsLoaded = true;
        }
        catch (Exception ex)
        {
            Log.Warn("加载合并组失败", ex);
        }
        RequestRefresh(immediate: true);
    }

    private async Task EnsureMergeGroupsLoadedAsync(CancellationToken ct)
    {
        if (_mergeGroupsLoaded) return;
        try
        {
            MergeGroups = await AppServices.Store.GetMergeGroupsAsync(ct);
            _mergeGroupsLoaded = true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("加载合并组失败", ex);
        }
    }

    public void Initialize()
    {
        _ = EnsureCollectionsLoadedAsync(CancellationToken.None);
        _ = EnsureMergeGroupsLoadedAsync(CancellationToken.None);
        RequestRefresh(immediate: true);
    }''', 1)

    # 2.5 折叠：把合并组成员收成一张组卡片
    s = s.replace('''            await EnsureCollectionsLoadedAsync(ct);''','''            await EnsureCollectionsLoadedAsync(ct);
            await EnsureMergeGroupsLoadedAsync(ct);''', 1)

    s = s.replace('''            Items.Clear();
            foreach (var a in items) Items.Add(new AssetCardItem(a));''','''            var displayItems = await BuildDisplayItemsAsync(items, ct);
            Items.Clear();
            foreach (var card in displayItems) Items.Add(card);''', 1)

    # 2.6 BuildDisplayItemsAsync 方法
    s = s.replace('''    private void ApplyFacets(FacetCounts facets)''','''    /// <summary>打开某个合并组（= 选中该组合集筛选）。</summary>
    public void OpenMergeGroup(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return;
        _suppressRefresh = true;
        try
        {
            foreach (var o in MergeGroupGroup.Options)
                o.SetSelectedSilently(string.Equals(o.Id, groupId, StringComparison.Ordinal));
        }
        finally { _suppressRefresh = false; }
        RequestRefresh(immediate: true);
    }

    /// <summary>
    /// 网格折叠：合并组里的成员只出现一张卡片（封面 + 数量）。
    /// 正在按"合并组"筛选时不折叠，方便逐张查看/设封面。
    /// </summary>
    private async Task<List<AssetCardItem>> BuildDisplayItemsAsync(IReadOnlyList<MediaAsset> items, CancellationToken ct)
    {
        var cards = new List<AssetCardItem>(items.Count);
        if (items.Count == 0) return cards;

        var collapse = !MergeGroupGroup.HasSelection;
        if (!collapse || MergeGroups.Count == 0)
        {
            foreach (var a in items) cards.Add(new AssetCardItem(a));
            return cards;
        }

        IReadOnlyDictionary<string, string> membership;
        try
        {
            membership = await AppServices.Store.GetMergeMembershipAsync(items.Select(a => a.Id), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("读取合并组成员失败", ex);
            foreach (var a in items) cards.Add(new AssetCardItem(a));
            return cards;
        }

        if (membership.Count == 0)
        {
            foreach (var a in items) cards.Add(new AssetCardItem(a));
            return cards;
        }

        var groups = MergeGroups.ToDictionary(g => g.Id, g => g, StringComparer.Ordinal);
        var covers = new Dictionary<string, MediaAsset>(StringComparer.Ordinal);
        var coverIds = groups.Values
            .Where(g => !string.IsNullOrEmpty(g.CoverAssetId))
            .Select(g => g.CoverAssetId!)
            .Distinct()
            .ToList();
        if (coverIds.Count > 0)
        {
            try
            {
                foreach (var cover in await AppServices.Store.GetByIdsAsync(coverIds, ct))
                    covers[cover.Id] = cover;
            }
            catch (Exception ex) { Log.Warn("读取合并组封面失败", ex); }
        }

        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in items)
        {
            if (!membership.TryGetValue(asset.Id, out var groupId) || !groups.TryGetValue(groupId, out var group))
            {
                cards.Add(new AssetCardItem(asset));
                continue;
            }
            if (!placed.Add(groupId)) continue;   // 同组只留一张

            // 组卡片显示封面素材（没有封面或封面不在当前页时用组内第一张）
            var coverAsset = group.CoverAssetId is { Length: > 0 } cid && covers.TryGetValue(cid, out var cov) ? cov : asset;
            var card = new AssetCardItem(coverAsset);
            card.ApplyMergeGroup(group);
            cards.Add(card);
        }
        return cards;
    }

    private void ApplyFacets(FacetCounts facets)''', 1)
    io.open(p, 'w', encoding='utf-8').write(s)
    print('library vm: merge group collapse')
PYEOF_MARK