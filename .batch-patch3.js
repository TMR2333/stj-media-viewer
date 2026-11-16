const fs = require('fs');

// 1) ImportViewModel：记住本次导入的目标文件 → 供"填写这批文件的信息"跳转
{
    const p = 'src/StjMediaViewer.App/ViewModels/ImportViewModel.cs';
    let s = fs.readFileSync(p, 'utf8');

    if (!s.includes('LastImportedPaths')) {
        s = s.replace('    private void ApplyImportReport(ImportReport report)\n    {',
            '    /// <summary>本次导入成功落盘的文件（供"填写这批文件的信息"使用）。</summary>\n' +
            '    public List<string> LastImportedPaths { get; } = new();\n\n' +
            '    [ObservableProperty] private Visibility _batchEditVisibility = Visibility.Collapsed;\n\n' +
            '    private void ApplyImportReport(ImportReport report)\n    {\n' +
            '        LastImportedPaths.Clear();\n' +
            '        foreach (var item in report.Items)\n' +
            '            if (item.DestinationPath is { Length: > 0 } dest && item.Status == "Copied")\n' +
            '                LastImportedPaths.Add(dest);\n' +
            '        BatchEditVisibility = LastImportedPaths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;\n');
        fs.writeFileSync(p, s, 'utf8');
        console.log('import vm: last paths');
    }
}

// 2) 素材库工具条按钮已经加了；这里给导入页加"填写这批文件的信息"按钮
{
    const p = 'src/StjMediaViewer.App/Views/ImportPage.xaml';
    let s = fs.readFileSync(p, 'utf8');
    if (!s.includes('BatchEdit_Click')) {
        // 放在报告区操作按钮附近：找一个已存在的报告按钮
        const anchor = 'Click="OpenTargetFolder_Click"';
        if (!s.includes(anchor)) throw new Error('report button anchor');
        const idx = s.lastIndexOf('<Button', s.indexOf(anchor));
        const insert = `                    <Button Style="{ThemeResource AppSubtleButtonStyle}" Padding="12,2,12,2" MinHeight="30"
                            Click="BatchEdit_Click"
                            Visibility="{x:Bind ViewModel.BatchEditVisibility, Mode=OneWay}"
                            ToolTipService.ToolTip="为这批刚导入的素材批量填写日期 / 城市 / 分类">
                        <StackPanel Orientation="Horizontal" Spacing="6">
                            <FontIcon Glyph="&#xE70F;" FontSize="{ThemeResource AppFontCaptionSize}" />
                            <TextBlock Text="填写这批文件的信息" Style="{ThemeResource AppCaptionTextStyle}" />
                        </StackPanel>
                    </Button>
`;
        s = s.slice(0, idx) + insert + s.slice(idx);
        fs.writeFileSync(p, s, 'utf8');
        console.log('import page: batch edit button');
    }
}

// 3) 导入页 code-behind：跳转
{
    const p = 'src/StjMediaViewer.App/Views/ImportPage.xaml.cs';
    let s = fs.readFileSync(p, 'utf8');
    if (!s.includes('BatchEdit_Click')) {
        const anchor = '    private void OpenTargetFolder_Click(object sender, RoutedEventArgs e)';
        if (!s.includes(anchor)) throw new Error('handler anchor');
        s = s.replace(anchor,
            '    /// <summary>把这批刚导入的素材带去批量填写页。</summary>\n' +
            '    private void BatchEdit_Click(object sender, RoutedEventArgs e)\n' +
            '    {\n' +
            '        var vm = ImportViewModel.Shared;\n' +
            '        if (vm.LastImportedPaths.Count == 0) return;\n' +
            '        BatchEditViewModel.PendingIds.Clear();\n' +
            '        _ = LoadPendingIdsAsync(vm.LastImportedPaths.ToList());\n' +
            '        Frame.Navigate(typeof(BatchEditPage));\n' +
            '    }\n\n' +
            '    private static async Task LoadPendingIdsAsync(List<string> paths)\n' +
            '    {\n' +
            '        try\n' +
            '        {\n' +
            '            var ids = await AppServices.Store.GetIdsByPathsAsync(paths);\n' +
            '            BatchEditViewModel.PendingIds.Clear();\n' +
            '            BatchEditViewModel.PendingIds.AddRange(ids);\n' +
            '        }\n' +
            '        catch { /* 跳过去后页面会回落到图库选择 */ }\n' +
            '    }\n\n' + anchor);
        fs.writeFileSync(p, s, 'utf8');
        console.log('import code-behind: batch nav');
    }
}
console.log('done');