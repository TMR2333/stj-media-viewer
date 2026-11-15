const fs = require('fs');
const p = 'src/StjMediaViewer.App/ViewModels/LibraryViewModel.cs';
let s = fs.readFileSync(p, 'utf8');

const anchor = '            OnlyUnclassified = OnlyUnclassified,\n        };';
if (!s.includes(anchor)) throw new Error('query anchor');
s = s.replace(anchor, [
    '            OnlyUnclassified = OnlyUnclassified,',
    '            // 同一张照片的 JPG 与 RAW 在网格里合成一项（代表是 JPG，没有 JPG 才用 RAW），',
    '            // 这样图库不会被 RAW 副本撑成两倍；要看全部文件勾"显示 RAW 副本"。',
    '            CollapseGroups = !ShowGroupCopies,',
    '        };',
].join('\n'));

const propAnchor = '    public AssetQuery BuildQuery(int offset, int limit)';
if (!s.includes(propAnchor)) throw new Error('prop anchor');
if (!s.includes('_showGroupCopies')) {
    s = s.replace(propAnchor, [
        '    /// <summary>是否把组内副本（RAW 与 JPG 各算一项）也铺在网格里；默认折叠成一张照片一项。</summary>',
        '    [ObservableProperty]',
        '    private bool _showGroupCopies;',
        '',
        propAnchor,
    ].join('\n'));
}

fs.writeFileSync(p, s, 'utf8');
console.log('library vm patched ok');