const fs = require('fs');

// 1) BatchAssetItem 整体包在 pragma 里（之前 restore 放错位置）
{
    const p = 'src/StjMediaViewer.App/ViewModels/BatchEditViewModel.cs';
    let s = fs.readFileSync(p, 'utf8');
    s = s.replace('#pragma warning restore MVVMTK0045\n\n/// <summary>批量填写页左侧的一行素材。</summary>',
                  '/// <summary>批量填写页左侧的一行素材。</summary>');
    const marker = '/// <summary>批量填写页左侧的一行素材。</summary>';
    if (!s.includes(marker + '\n#pragma warning disable MVVMTK0045')) {
        s = s.replace(marker + '\npublic sealed partial class BatchAssetItem : ObservableObject',
                      marker + '\n#pragma warning disable MVVMTK0045\npublic sealed partial class BatchAssetItem : ObservableObject');
        s = s.trimEnd() + '\n#pragma warning restore MVVMTK0045\n';
    }
    fs.writeFileSync(p, s, 'utf8');
    console.log('pragma fixed');
}

// 2) ImportPage.xaml：报告区加「填写这批文件的信息」
{
    const p = 'src/StjMediaViewer.App/Views/ImportPage.xaml';
    let s = fs.readFileSync(p, 'utf8');
    if (!s.includes('BatchEdit_Click')) {
        const anchor = `                        <Button Style="{ThemeResource AppSubtleButtonStyle}"
                                Padding="12,2,12,2" MinHeight="30"
                                Click="Reimport_Click">`;
        if (!s.includes(anchor)) throw new Error('reimport anchor');
        const insert = `                        <Button Style="{ThemeResource AppSubtleButtonStyle}"
                                Padding="12,2,12,2" MinHeight="30"
                                Click="BatchEdit_Click"
                                Visibility="{x:Bind ViewModel.BatchEditVisibility, Mode=OneWay}"
                                ToolTipService.ToolTip="为这批刚导入的素材批量填写日期 / 城市 / 分类">
                            <StackPanel Orientation="Horizontal" Spacing="6">
                                <FontIcon Glyph="&#xE70F;" FontSize="{ThemeResource AppFontCaptionSize}" />
                                <TextBlock Text="填写这批文件的信息" Style="{ThemeResource AppCaptionTextStyle}" />
                            </StackPanel>
                        </Button>
`;
        s = s.replace(anchor, insert + anchor);
        fs.writeFileSync(p, s, 'utf8');
        console.log('import page: button');
    }
}

// 3) ImportPage code-behind
{
    const p = 'src/StjMediaViewer.App/Views/ImportPage.xaml.cs';
    let s = fs.readFileSync(p, 'utf8');
    if (!s.includes('BatchEdit_Click')) {
        const anchor = '    private void Reimport_Click(object sender, RoutedEventArgs e)';
        if (!s.includes(anchor)) throw new Error('reimport handler anchor');
        const insert = `    /// <summary>把这批刚导入的素材带去批量填写页。</summary>
    private void BatchEdit_Click(object sender, RoutedEventArgs e)
    {
        var paths = ImportViewModel.Shared.LastImportedPaths.ToList();
        if (paths.Count == 0) return;
        _ = LoadPendingIdsAsync(paths);
        Frame.Navigate(typeof(BatchEditPage));
    }

    private static async Task LoadPendingIdsAsync(List<string> paths)
    {
        try
        {
            var ids = await AppServices.Store.GetIdsByPathsAsync(paths);
            BatchEditViewModel.PendingIds.Clear();
            BatchEditViewModel.PendingIds.AddRange(ids);
        }
        catch { /* 页面会回落到图库选择 */ }
    }

`;
        s = s.replace(anchor, insert + anchor);
        fs.writeFileSync(p, s, 'utf8');
        console.log('import code-behind: handler');
    }
}
console.log('done');