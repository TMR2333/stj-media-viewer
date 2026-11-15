using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Transcode;
using StjMediaViewer.Core.Util;
using StjMediaViewer.App.Services;
using StjMediaViewer.App.ViewModels;

namespace StjMediaViewer.App.Views;

/// <summary>
/// 转码 / 压缩对话框：探测源 → 选预设/参数 → 实时进度 → 结果与"打开文件夹"。
/// 所有逻辑都委托给 <see cref="AppServices.Transcode"/>，这里只做交互。
/// </summary>
public sealed partial class TranscodeDialog : ContentDialog
{
    private readonly MediaAsset _asset;
    private VideoProbe? _probe;
    private EncoderAvailability? _encoders;
    private CancellationTokenSource? _cts;
    private bool _running;
    private string? _lastOutput;
    private bool _suppressEvents;

    public TranscodeDialog(MediaAsset asset)
    {
        InitializeComponent();
        _asset = asset;
        SourceTitle.Text = asset.FileName;
        SourceInfo.Text = "正在读取视频信息…";

        var profiles = AppServices.Transcode.BuiltInProfiles;
        foreach (var profile in profiles)
            ProfileBox.Items.Add(new ComboBoxItem { Content = profile.Name, Tag = profile.Id });
        ProfileBox.SelectedIndex = 0;

        // 源编码已知时，编码器默认对齐"是否需要转码"：HEVC 源默认还是 H.265，H.264 源默认 H.265（压缩）
        SelectEncoder(VideoCodecNames.IsHevc(asset.VideoCodec) ? "h265" : "h265");
        ResolutionBox.SelectedIndex = 2;   // 1080p
        SpeedBox.SelectedIndex = 1;        // 均衡

        Opened += OnDialogOpened;
        Closed += OnDialogClosed;
    }

    // ==================================================================================
    //  初始化
    // ==================================================================================

    private async void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        try
        {
            _probe = await AppServices.Transcode.ProbeAsync(_asset.FullPath);
            _encoders = await AppServices.Transcode.DetectEncodersAsync();
            EncoderHint.Text = _encoders.Describe();

            if (_probe.Error is { } err)
            {
                SourceInfo.Text = $"无法读取视频信息：{err}";
                IsPrimaryButtonEnabled = false;
                return;
            }

            SourceInfo.Text = $"{_probe.VideoCodecLabel} · {_probe.ResolutionText} · {_probe.BitrateText} · " +
                              $"{_probe.DurationText}" +
                              (_probe.HasAudio ? $" · 音频 {_probe.AudioCodec}" : " · 无音轨") +
                              (_probe.HasHdr ? " · HDR" : "") +
                              $" · {Formatting.FileSize(_probe.SizeBytes)}";

            // 预设按源分辨率/编码做一次智能推荐：4K 源默认压到 1080p
            if (_probe.Height > 1080) ResolutionBox.SelectedIndex = 2;
            else if (_probe.Height > 0 && _probe.Height <= 720) ResolutionBox.SelectedIndex = 0;

            RefreshPreview();
        }
        catch (Exception ex)
        {
            SourceInfo.Text = "读取失败：" + ex.Message;
            IsPrimaryButtonEnabled = false;
        }
    }

    private void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        try { _cts?.Cancel(); } catch { }
    }

    // ==================================================================================
    //  参数
    // ==================================================================================

    private async void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (ProfileBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        var profile = AppServices.Transcode.BuiltInProfiles.FirstOrDefault(p => p.Id == id);
        if (profile is null) return;

        _suppressEvents = true;
        try
        {
            ProfileDescription.Text = profile.Description;
            SelectEncoder(profile.Encoder switch
            {
                VideoEncoderFamily.H265 => "h265",
                VideoEncoderFamily.H264 => "h264",
                VideoEncoderFamily.Av1 => "av1",
                _ => "copy",
            });
            ResolutionBox.SelectedIndex = profile.MaxHeight switch
            {
                2160 => 1,
                >= 1080 => 2,
                720 => 3,
                480 => 4,
                _ => 0,
            };
            CrfSlider.Value = profile.Crf;
            SpeedBox.SelectedIndex = profile.SpeedPreset switch
            {
                "veryfast" or "fast" or "faster" => 0,
                "slow" or "slower" or "veryslow" => 2,
                _ => 1,
            };
            var isCopy = profile.Encoder == VideoEncoderFamily.Copy;
            CrfSlider.IsEnabled = !isCopy;
            EncoderBox.IsEnabled = !isCopy;
            HardwareSwitch.IsEnabled = !isCopy;
        }
        finally
        {
            _suppressEvents = false;
        }
        RefreshPreview();
        await Task.CompletedTask;
    }

    private void OnOptionChanged(object sender, object e)
    {
        if (_suppressEvents) return;
        RefreshPreview();
    }

    private void OnCrfChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (CrfValue is null) return;
        CrfValue.Text = ((int)CrfSlider.Value).ToString();
        if (_suppressEvents) return;
        RefreshPreview();
    }

    private void SelectEncoder(string tag)
    {
        foreach (var item in EncoderBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                EncoderBox.SelectedItem = item;
                return;
            }
        }
    }

    private TranscodeProfile BuildProfile()
    {
        var encoderTag = (EncoderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "h265";
        var encoder = encoderTag switch
        {
            "h264" => VideoEncoderFamily.H264,
            "av1" => VideoEncoderFamily.Av1,
            "copy" => VideoEncoderFamily.Copy,
            _ => VideoEncoderFamily.H265,
        };
        var height = int.TryParse((ResolutionBox.SelectedItem as ComboBoxItem)?.Tag as string, out var h) ? h : 0;
        var speed = (SpeedBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "medium";

        return new TranscodeProfile
        {
            Id = "dialog",
            Name = encoderTag,
            Encoder = encoder,
            Container = encoder == VideoEncoderFamily.Av1 ? "webm" : "mp4",
            Crf = (int)CrfSlider.Value,
            MaxHeight = height,
            SpeedPreset = speed,
            PreferHardware = HardwareSwitch.IsOn,
            AudioBitrateKbps = 128,
        };
    }

    private void RefreshPreview()
    {
        if (_probe is null) return;
        var profile = BuildProfile();
        var output = AppServices.Transcode.DefaultOutputPath(_asset.FullPath, profile);
        OutputPathText.Text = "输出：" + output;

        if (profile.Encoder == VideoEncoderFamily.Copy)
        {
            EstimateText.Text = "仅换容器：不重新编码，几秒完成、画质无损";
            return;
        }

        var estimate = AppServices.Transcode.EstimateOutputBytes(_probe, profile);
        if (estimate > 0 && _probe.SizeBytes > 0)
        {
            var ratio = 1.0 - (double)estimate / _probe.SizeBytes;
            EstimateText.Text = $"预计输出约 {Formatting.FileSize(estimate)}（" +
                                (ratio >= 0 ? $"体积约减少 {ratio * 100:0}%" : $"体积约增加 {-ratio * 100:0}%") +
                                "，仅供参考）";
        }
        else
        {
            EstimateText.Text = profile.Summary;
        }
    }

    // ==================================================================================
    //  执行
    // ==================================================================================

    private async void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_running)
        {
            args.Cancel = true;
            return;
        }
        args.Cancel = true;   // 保持对话框打开，自己控制关闭时机

        var profile = BuildProfile();
        var request = new TranscodeRequest
        {
            SourcePath = _asset.FullPath,
            OutputPath = AppServices.Transcode.DefaultOutputPath(_asset.FullPath, profile),
            Profile = profile,
            Overwrite = false,
            VerifyOutput = true,
        };

        _running = true;
        IsPrimaryButtonEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        ResultActions.Visibility = Visibility.Collapsed;
        StatusText.Text = "准备转码…";
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<TranscodeProgress>(p =>
            {
                Progress.Value = p.Percent;
                var parts = new List<string> { p.Phase };
                if (p.Processed > TimeSpan.Zero) parts.Add($"已处理 {p.Processed:mm\\:ss}");
                if (p.Speed > 0) parts.Add($"{p.Speed:0.0}x 实时");
                if (p.Eta is { } eta) parts.Add($"剩余约 {eta:mm\\:ss}");
                if (!string.IsNullOrEmpty(p.CurrentFile)) parts.Add(Path.GetFileName(p.CurrentFile));
                StatusText.Text = string.Join(" · ", parts);
            });

            var result = await AppServices.Transcode.TranscodeAsync(request, progress, _cts.Token);
            _lastOutput = result.OutputPath;

            if (result.Success)
            {
                Progress.Value = 100;
                StatusText.Text = "完成：" + result.Summary +
                                  (result.OutputCodec is { } codec ? $" · 输出编码 {VideoCodecNames.DisplayLabel(codec)}" : "");
                ResultActions.Visibility = Visibility.Visible;
                AppEvents.SetStatus($"转码完成：{Path.GetFileName(result.OutputPath)}（{result.SavedText}）");
            }
            else
            {
                StatusText.Text = "失败：" + (result.Error ?? "未知错误");
                AppEvents.SetError("转码失败：" + result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消";
        }
        catch (Exception ex)
        {
            Log.Warn("转码失败", ex);
            StatusText.Text = "失败：" + ex.Message;
        }
        finally
        {
            _running = false;
            IsPrimaryButtonEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastOutput)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutput}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("打开文件夹失败", ex);
        }
    }

    private void OnAgain(object sender, RoutedEventArgs e)
    {
        ResultActions.Visibility = Visibility.Collapsed;
        Progress.Value = 0;
        StatusText.Text = "可以调整参数后再次转码";
    }
}