import re
p = 'src/StjMediaViewer.App/Views/SettingsPage.xaml'
s = open(p, encoding='utf-8').read()

anchor = '''                        <Button Grid.Column="2" Style="{ThemeResource AppSubtleButtonStyle}"
                                Padding="12,2,12,2" MinHeight="30" Click="ClearCache_Click">
                            <StackPanel Orientation="Horizontal" Spacing="6">
                                <FontIcon Glyph="&#xE74D;" FontSize="{ThemeResource AppFontCaptionSize}" />
                                <TextBlock Text="清空缓存" Style="{ThemeResource AppCaptionTextStyle}" />
                            </StackPanel>
                        </Button>
                    </Grid>'''

if 'Warmup_Click' not in s:
    assert anchor in s, 'cache row anchor'
    block = anchor + '''

                    <!-- 后台全库预热：低并发把整库缩略图生成并落盘，之后滚动都走 0ms 缓存命中 -->
                    <StackPanel Spacing="6">
                        <Grid ColumnSpacing="10">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <TextBlock Grid.Column="0" Text="{x:Bind ViewModel.WarmupText, Mode=OneWay}"
                                       Style="{ThemeResource AppCaptionTextStyle}"
                                       VerticalAlignment="Center" TextWrapping="Wrap" />
                            <Button Grid.Column="1" Style="{ThemeResource AppSubtleButtonStyle}"
                                    Padding="12,2,12,2" MinHeight="30" Click="Warmup_Click"
                                    ToolTipService.ToolTip="后台低优先级生成整库缩略图（可随时停止，下次继续）">
                                <StackPanel Orientation="Horizontal" Spacing="6">
                                    <FontIcon Glyph="&#xE896;" FontSize="{ThemeResource AppFontCaptionSize}" />
                                    <TextBlock Text="{x:Bind ViewModel.WarmupButtonText, Mode=OneWay}"
                                               Style="{ThemeResource AppCaptionTextStyle}" />
                                </StackPanel>
                            </Button>
                        </Grid>
                        <ProgressBar Value="{x:Bind ViewModel.WarmupProgress, Mode=OneWay}"
                                     Maximum="100"
                                     Height="4"
                                     Visibility="{x:Bind ViewModel.WarmupProgressVisibility, Mode=OneWay}" />
                        <TextBlock Style="{ThemeResource AppMicroTextStyle}"
                                   Foreground="{ThemeResource TextTertiaryBrush}"
                                   TextWrapping="Wrap"
                                   Text="预热只生成缩略图，不改素材；第二次打开或滚回已预热区间都是 0ms 命中。" />
                    </StackPanel>'''
    s = s.replace(anchor, block, 1)
    open(p, 'w', encoding='utf-8').write(s)
    print('settings xaml: warmup row')

# code-behind handler
p2 = 'src/StjMediaViewer.App/Views/SettingsPage.xaml.cs'
s2 = open(p2, encoding='utf-8').read()
if 'Warmup_Click' not in s2:
    m = re.search(r'    private void ClearCache_Click\(object sender, RoutedEventArgs e\)', s2)
    assert m, 'clear cache handler anchor'
    handler = '''    private void Warmup_Click(object sender, RoutedEventArgs e)
    {
        try { ViewModel.ToggleWarmup(); }
        catch (Exception ex) { Core.Util.Log.Warn("启动缩略图预热失败", ex); }
    }

'''
    s2 = s2[:m.start()] + handler + s2[m.start():]
    open(p2, 'w', encoding='utf-8').write(s2)
    print('settings code-behind: handler')