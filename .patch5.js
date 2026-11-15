const fs = require('fs');
const p = 'src/StjMediaViewer.App/ViewModels/LibraryViewModel.cs';
let s = fs.readFileSync(p, 'utf8');

// 网格默认按 JPG+RAW 分组折叠：一张照片只出现一次（代表 = JPG，没有 JPG 才用 RAW）
const anchor = `            Descending = Descending,
            OnlyUnclassified = OnlyUnclassified,
        };`;
if (!s.includes(anchor)) throw new Error('query anchor');
s = s.replace(anchor, `            Descending = Descending,
            OnlyUnclassified = OnlyUnclassified,
            // 同一张照片的 JPG 与 RAW 在网格里合成一项（代表是 JPG），
            // 展开查看两个文件走检视面板；这样图库不会被 RAW 副本撑成两倍。
            CollapseGroups = ShowGroupCopies is false,
        };`);

// 新增一个"显示 RAW 副本"开关的字段（默认关闭 = 折叠）
const propAnchor = '    public AssetQuery BuildQuery(int offset, int limit)';
if (!s.includes('ShowGroupCopies')) {
    s = s.replace(propAnchor, `    /// <summary>是否把组内副本（RAW 与 JPG 各算一项）也铺在网格里；默认折叠成一张照片一项。</summary>
    [ObservableProperty]
    private bool _showGroupCopies;

    public AssetQuery BuildQuery(int offset, int limit)`);
}

fs.writeFileSync(p, s, 'utf8');
console.log('library vm patched');