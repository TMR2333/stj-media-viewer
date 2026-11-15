// 一次性补丁脚本：给 LibraryScanner 加 JPG/RAW 分组（用完即删）
const fs = require('fs');
const p = 'src/StjMediaViewer.Core/Scanning/LibraryScanner.cs';
let s = fs.readFileSync(p, 'utf8');

const before = [
    '            // ---------- 2) 并行解析 ----------',
].join('\n');

const after = [
    '            // ---------- 1.5) JPG/RAW 分组 ----------',
    '            // 同一张照片的 JPG 与 RAW 归为一组：JPG 作为组代表（网格里只出现一次），',
    '            // RAW 作为同组原件，两者共享 GroupId，界面显示 RAW&JPEG 标签。',
    '            var groupIndex = BuildGroupIndex(candidates);',
    '            int pairedGroups = groupIndex.Values.Where(g => g.IsCover && g.HasRaw && g.HasJpeg)',
    '                                                .Select(g => g.GroupId).Distinct().Count();',
    '            if (pairedGroups > 0)',
    '            {',
    '                report(new ScanProgress',
    '                {',
    '                    Phase = ScanPhase.Enumerating,',
    '                    Total = candidates.Count,',
    '                    MediaFound = candidates.Count,',
    '                    Message = $"识别到 {pairedGroups} 组 RAW+JPEG 配对",',
    '                });',
    '            }',
    '',
    '            // ---------- 2) 并行解析 ----------',
].join('\n');

if (!s.includes(before)) throw new Error('anchor 1 not found');
s = s.replace(before, after);

if (!s.includes('asset = BuildAsset(root, cand, options, token);')) throw new Error('anchor 2 not found');
s = s.replace('asset = BuildAsset(root, cand, options, token);',
              'asset = BuildAsset(root, cand, options, groupIndex, token);');

if (!s.includes('private MediaAsset BuildAsset(LibraryRoot root, Candidate cand, ScanOptions options, CancellationToken ct)'))
    throw new Error('anchor 3 not found');
s = s.replace('private MediaAsset BuildAsset(LibraryRoot root, Candidate cand, ScanOptions options, CancellationToken ct)',
              'private MediaAsset BuildAsset(LibraryRoot root, Candidate cand, ScanOptions options, IReadOnlyDictionary<string, GroupInfo> groupIndex, CancellationToken ct)');

if (!s.includes('        asset.Sidecars.AddRange(cand.Sidecars);')) throw new Error('anchor 4 not found');
s = s.replace('        asset.Sidecars.AddRange(cand.Sidecars);', [
    '        asset.Sidecars.AddRange(cand.Sidecars);',
    '',
    '        // JPG/RAW 分组信息（扫描阶段算好，图库折叠与徽标直接用）',
    '        if (groupIndex.TryGetValue(cand.FullPath, out var groupInfo))',
    '        {',
    '            asset.GroupId = groupInfo.GroupId;',
    '            asset.IsGroupCover = groupInfo.IsCover;',
    '            asset.GroupHasRaw = groupInfo.HasRaw;',
    '            asset.GroupHasJpeg = groupInfo.HasJpeg;',
    '            asset.CompanionPaths = groupInfo.Companions;',
    '        }',
].join('\n'));

const anchor5 = '    private MediaAsset FallbackAsset(LibraryRoot root, Candidate cand, Exception ex)';
if (!s.includes(anchor5)) throw new Error('anchor 5 not found');

const helper = [
    '    /// <summary>组内附加信息（每个文件一份）。</summary>',
    '    private sealed class GroupInfo',
    '    {',
    '        public string GroupId = "";',
    '        public bool IsCover;',
    '        public bool HasRaw;',
    '        public bool HasJpeg;',
    '        public List<string> Companions = new();',
    '    }',
    '',
    '    /// <summary>',
    '    /// 按“同目录 + 同基名”把候选文件分组成 JPG/RAW 组：',
    '    /// - 组代表（cover）优先 JPEG（预览快、通用），没有 JPEG 才用 RAW；',
    '    /// - 组内其它文件（RAW、连拍副本、边车）挂在 CompanionPaths 上；',
    '    /// - GroupId 用稳定哈希，重建索引后仍然一致。',
    '    /// </summary>',
    '    private static Dictionary<string, GroupInfo> BuildGroupIndex(IReadOnlyList<Candidate> candidates)',
    '    {',
    '        var paths = candidates.Select(c => c.FullPath).ToList();',
    '        var buckets = JpegRawPairing.Group(paths);',
    '        var index = new Dictionary<string, GroupInfo>(paths.Count, StringComparer.OrdinalIgnoreCase);',
    '',
    '        foreach (var bucket in buckets.Values)',
    '        {',
    '            var cover = bucket.Cover;',
    '            if (cover is null) continue;',
    '',
    '            var all = bucket.AllFiles().ToList();',
    '            var groupId = "g" + FileHasher.StableHash(bucket.Key);',
    '',
    '            foreach (var path in all)',
    '            {',
    '                index[path] = new GroupInfo',
    '                {',
    '                    GroupId = groupId,',
    '                    IsCover = string.Equals(path, cover, StringComparison.OrdinalIgnoreCase),',
    '                    HasRaw = bucket.Raws.Count > 0,',
    '                    HasJpeg = bucket.Jpegs.Count > 0,',
    '                    Companions = all.Where(o => !string.Equals(o, path, StringComparison.OrdinalIgnoreCase)).ToList(),',
    '                };',
    '            }',
    '        }',
    '',
    '        return index;',
    '    }',
    '',
    anchor5,
].join('\n');

s = s.replace(anchor5, helper);

// using
if (!s.includes('using StjMediaViewer.Core.Import;'))
{
    s = s.replace('using StjMediaViewer.Core.Models;', 'using StjMediaViewer.Core.Import;\nusing StjMediaViewer.Core.Models;');
}

fs.writeFileSync(p, s, 'utf8');
console.log('scanner patched ok');