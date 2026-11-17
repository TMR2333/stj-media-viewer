using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StjMediaViewer.App.ViewModels;

namespace StjMediaViewer.App.Views;

/// <summary>
/// 云同步页：事件转发到 <see cref="CloudViewModel.Shared"/>，目录选择走 FolderPicker。
/// </summary>
public sealed partial class CloudPage : Page
{
    public CloudPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public CloudViewModel ViewModel => CloudViewModel.Shared;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        SyncEditorSelections();

        // 截图 / 验收用：自动对第一个目标跑一次连接诊断
        if (Environment.GetEnvironmentVariable("STJ_DEMO_CLOUD_DIAG") == "1" &&
            ViewModel.Targets.Count > 0)
        {
            await ViewModel.DiagnoseAsync(ViewModel.Targets[0]);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelCurrentCommand.Execute(null);
    }

    private void SyncEditorSelections()
    {
        // 编辑器里的 ComboBox 用 Tag 与 VM 的字符串字段对应
        foreach (var item in DirectionBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, ViewModel.EditorDirection, StringComparison.OrdinalIgnoreCase))
                DirectionBox.SelectedItem = item;
        }
        foreach (var item in ConflictBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, ViewModel.EditorConflict, StringComparison.OrdinalIgnoreCase))
                ConflictBox.SelectedItem = item;
        }
    }

    // ---------------------------------------------------------------- 目标列表

    private void AddTarget_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.AddTargetCommand.Execute(null);
        SyncEditorSelections();
    }

    /// <summary>EmptyState 的 ActionInvoked 是 EventHandler（不是 RoutedEventHandler）。</summary>
    private void AddTargetFromEmpty_Click(object? sender, EventArgs e)
    {
        ViewModel.AddTargetCommand.Execute(null);
        SyncEditorSelections();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CloudTargetRow row })
        {
            ViewModel.EditTargetCommand.Execute(row);
            SyncEditorSelections();
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CloudTargetRow row })
            await ViewModel.DeleteTargetCommand.ExecuteAsync((object)row);
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CloudTargetRow row })
            await ViewModel.PreviewCommand.ExecuteAsync((object)row);
    }

    private async void SyncOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CloudTargetRow row })
            await ViewModel.SyncNowCommand.ExecuteAsync((object)row);
    }

    private async void AutoSync_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { Tag: CloudTargetRow row } toggle)
        {
            row.AutoSync = toggle.IsOn;
            await ViewModel.ToggleAutoCommand.ExecuteAsync((object)row);
        }
    }

    private async void SyncAll_Click(object sender, RoutedEventArgs e)
        => await ViewModel.SyncAllCommand.ExecuteAsync(null);

    // ---------------------------------------------------------------- 编辑器

    private void Kind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string kind })
            ViewModel.SetKindCommand.Execute(kind);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string preset })
            ViewModel.ApplyPresetCommand.Execute(preset);
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
        => await ViewModel.TestConnectionCommand.ExecuteAsync(null);

    private async void Save_Click(object sender, RoutedEventArgs e)
        => await ViewModel.SaveTargetCommand.ExecuteAsync(null);

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
        => ViewModel.CancelEditCommand.Execute(null);

    private void Direction_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DirectionBox.SelectedItem is ComboBoxItem { Tag: string tag }) ViewModel.EditorDirection = tag;
    }

    private void Conflict_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ConflictBox.SelectedItem is ComboBoxItem { Tag: string tag }) ViewModel.EditorConflict = tag;
    }

    private async void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string which }) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            if (App.MainWindow is null) return;
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            switch (which)
            {
                case "local":
                    ViewModel.EditorLocalFolder = folder.Path;
                    break;
                case "remote":
                    ViewModel.EditorRemoteFolder = folder.Path;
                    break;
            }
        }
        catch (Exception)
        {
            // 用户取消或没有窗口句柄：静默，用户可手填路径
        }
    }

    // ---------------------------------------------------------------- 计划 / 执行

    private async void DryRun_Click(object sender, RoutedEventArgs e)
        => await ViewModel.DryRunCommand.ExecuteAsync(null);

    private async void ExecutePlan_Click(object sender, RoutedEventArgs e)
        => await ViewModel.SyncNowCommand.ExecuteAsync((object?)ViewModel.SelectedTarget);

    private async void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CloudTargetRow row)
            await ViewModel.DiagnoseAsync(row);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e) => ViewModel.OpenLogFile();

    private void CloseDiagnostic_Click(object sender, RoutedEventArgs e)
        => ViewModel.DiagnosticVisibility = Microsoft.UI.Xaml.Visibility.Collapsed;
}
