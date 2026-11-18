using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.Core.Models;

/// <summary>
/// 操作目标展开：素材库里合并组的封面卡片只代表 1 个素材，
/// 但批量填写 / 批量删除 / 加入合集这类操作应该作用到**组内全部成员**。
/// </summary>
public static class SelectionExpander
{
    /// <summary>
    /// 把传入素材里的"合并组封面"展开成组内全部成员（其它素材原样保留，去重、保持顺序）。
    /// </summary>
    public static async Task<IReadOnlyList<MediaAsset>> ExpandMergeGroupsAsync(
        IAssetStore store, IReadOnlyList<MediaAsset> assets, CancellationToken ct = default)
    {
        if (store is null || assets is null || assets.Count == 0) return assets ?? Array.Empty<MediaAsset>();

        var ids = assets.Select(a => a.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        IReadOnlyDictionary<string, string> membership;
        try
        {
            membership = await store.GetMergeMembershipAsync(ids, ct);
        }
        catch
        {
            return assets;
        }

        if (membership.Count == 0) return assets;

        var groupIds = membership.Values.Distinct(StringComparer.Ordinal).ToList();
        var expanded = new List<MediaAsset>(assets.Count + 8);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var membersByGroup = new Dictionary<string, IReadOnlyList<MediaAsset>>(StringComparer.Ordinal);

        foreach (var groupId in groupIds)
        {
            try
            {
                var memberIds = await store.GetMergeGroupAssetIdsAsync(groupId, ct);
                if (memberIds.Count == 0) continue;
                membersByGroup[groupId] = await store.GetByIdsAsync(memberIds, ct);
            }
            catch
            {
                // 单个组读失败就跳过展开，不影响其它素材
            }
        }

        foreach (var asset in assets)
        {
            if (membership.TryGetValue(asset.Id, out var groupId) &&
                membersByGroup.TryGetValue(groupId, out var members) && members.Count > 0)
            {
                foreach (var member in members)
                    if (seen.Add(member.Id)) expanded.Add(member);
            }
            else if (seen.Add(asset.Id))
            {
                expanded.Add(asset);
            }
        }
        return expanded;
    }
}