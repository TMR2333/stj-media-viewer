import io

# =============== 1) SettingsPage.xaml：设备 ↔ LUT 卡片 ===============
p = 'src/StjMediaViewer.App/Views/SettingsPage.xaml'
s = io.open(p, encoding='utf-8').read()
if 'DeviceLutRows' not in s:
    card = '''
            <!-- ==================================================================
                 设备 ↔ LUT 绑定
                 ================================================================== -->
            <uic:SectionCard Header="设备 ↔ LUT 绑定"
                             Subtitle="给每个设备指定一条监视 LUT：只有这台设备拍、并且标记为 Log 的视频/照片才会自动套；别的设备的 Log 不再被误套索尼 LUT"
                             Glyph="&#xE790;">
                <StackPanel Spacing="10">
                    <StackPanel Orientation="Horizontal" Spacing="8">
                        <Button Style="{ThemeResource AppSubtleButtonStyle}" Click="LoadDeviceLuts_Click">
                            <StackPanel Orientation="Horizontal" Spacing="6">
                                <FontIcon Glyph="&#xE72C;" FontSize="{ThemeResource AppFontCaptionSize}" />
                                <TextBlock Text="刷新设备与 LUT" Style="{ThemeResource AppCaptionTextStyle}" />
                            </StackPanel>
                        </Button>
                        <Button Style="{ThemeResource AppSubtleButtonStyle}" Click="SuggestSonyLuts_Click">
                            <TextBlock Text="为 Sony 设备推荐 Log LUT" Style="{ThemeResource AppCaptionTextStyle}" />
                        </Button>
                        <Button Background="{ThemeResource AccentSubtleBrush}"
                                BorderBrush="{ThemeResource AccentBorderBrush}"
                                Style="{ThemeResource AppSubtleButtonStyle}"
                                Click="SaveDeviceLuts_Click">
                            <StackPanel Orientation="Horizontal" Spacing="6">
                                <FontIcon Glyph="&#xE74E;" FontSize="{ThemeResource AppFontCaptionSize}" />
                                <TextBlock Text="保存绑定" Style="{ThemeResource AppCaptionTextStyle}" />
                            </StackPanel>
                        </Button>
                        <Button Style="{ThemeResource AppSubtleButtonStyle}" Click="ResetDeviceLuts_Click">
                            <TextBlock Text="清空选择" Style="{ThemeResource AppCaptionTextStyle}" />
                        </Button>
                        <TextBlock Text="{x:Bind ViewModel.DeviceLutStatus, Mode=OneWay}"
                                   Style="{ThemeResource AppMicroTextStyle}"
                                   Foreground="{ThemeResource TextTertiaryBrush}"
                                   VerticalAlignment="Center"
                                   TextWrapping="Wrap" />
                    </StackPanel>

                    <ItemsControl ItemsSource="{x:Bind ViewModel.DeviceLutRows, Mode=OneWay}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate x:DataType="vm:DeviceLutRow">
                                <Grid ColumnSpacing="10" Padding="0,2">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="340" />
                                        <ColumnDefinition Width="Auto" />
                                    </Grid.ColumnDefinitions>
                                    <StackPanel Grid.Column="0" Spacing="1" VerticalAlignment="Center">
                                        <TextBlock Text="{x:Bind DisplayName}" Style="{ThemeResource AppCaptionTextStyle}"
                                                   TextTrimming="CharacterEllipsis" />
                                        <TextBlock Text="{x:Bind Key}" Style="{ThemeResource AppMicroTextStyle}"
                                                   Foreground="{ThemeResource TextTertiaryBrush}"
                                                   TextTrimming="CharacterEllipsis" />
                                    </StackPanel>
                                    <ComboBox Grid.Column="1"
                                              HorizontalAlignment="Stretch"
                                              MinHeight="30"
                                              ItemsSource="{x:Bind ViewModel.DeviceLutOptions, Mode=OneWay}"
                                              SelectedValuePath="Id"
                                              SelectedValue="{x:Bind SelectedLutId, Mode=TwoWay}"
                                              DisplayMemberPath="Name"
                                              PlaceholderText="不自动套用" />
                                    <TextBlock Grid.Column="2" Text="{x:Bind CountText}"
                                               Style="{ThemeResource AppMicroTextStyle}"
                                               Foreground="{ThemeResource TextTertiaryBrush}"
                                               VerticalAlignment="Center" MinWidth="64" />
                                </Grid>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>

                    <TextBlock Text="还没有设备信息（扫描素材后出现）；LUT 列表来自「LUT」页的内置 + 用户目录。"
                               Style="{ThemeResource AppCaptionTextStyle}"
                               Visibility="{x:Bind ViewModel.DeviceLutEmptyVisibility, Mode=OneWay}" />
                </StackPanel>
            </uic:SectionCard>
'''
    anchor = '''            <!-- ==================================================================
                 工具链
                 ================================================================== -->'''
    assert anchor in s
    s = s.replace(anchor, card + anchor, 1)
    io.open(p, 'w', encoding='utf-8').write(s)
    print('xaml: device lut card')

# =============== 2) SettingsPage.xaml.cs 处理器 ===============
p = 'src/StjMediaViewer.App/Views/SettingsPage.xaml.cs'
s = io.open(p, encoding='utf-8').read()
if 'SaveDeviceLuts_Click' not in s:
    anchor = '''    private void ResetDeviceAliases_Click(object sender, RoutedEventArgs e) => ViewModel.ResetDeviceAliases();'''
    assert anchor in s
    handlers = '''    private async void LoadDeviceLuts_Click(object sender, RoutedEventArgs e)
        => await ViewModel.LoadDeviceLutBindingsAsync();

    private void SuggestSonyLuts_Click(object sender, RoutedEventArgs e) => ViewModel.SuggestSonyLuts();

    private async void SaveDeviceLuts_Click(object sender, RoutedEventArgs e)
        => await ViewModel.SaveDeviceLutBindingsAsync();

    private void ResetDeviceLuts_Click(object sender, RoutedEventArgs e) => ViewModel.ResetDeviceLutBindings();

''' + anchor
    s = s.replace(anchor, handlers, 1)
    io.open(p, 'w', encoding='utf-8').write(s)
    print('code-behind: device lut handlers')