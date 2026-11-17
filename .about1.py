import io

AVATARS = [
    (r'C:\Users\Administrator\.dsh\attachments\v1\objects\fa\fa29e7b8de40c87dab1ea738280f16172597cec8d68795940a34207e2a1d37ed', 'src/StjMediaViewer.App/Assets/About/author-tmr.jpg'),
]

import shutil, os
for src, dst in AVATARS:
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copyfile(src, dst)
    print('copied', dst, os.path.getsize(dst))

# 从表情里挑一张作为 AI 头像
shutil.copyfile('src/StjMediaViewer.App/Assets/About/meme-quiet.png', 'src/StjMediaViewer.App/Assets/About/author-dsh.png')
print('copied author-dsh.png')
for f in ('meme-dsh.png', 'meme-model.png', 'meme-quiet.png', 'meme-xiong.png'):
    try:
        os.remove(os.path.join('src/StjMediaViewer.App/Assets/About', f))
    except FileNotFoundError:
        pass
print('removed meme images')

# ---------- AboutPage.xaml：删表情包，改作者卡片 ----------
p = 'src/StjMediaViewer.App/Views/AboutPage.xaml'
s = io.open(p, encoding='utf-8').read()

start = s.index('            <!-- ============ 表情包 ============ -->')
end = s.index('            <!-- ============ 技术栈 / 许可 ============ -->')
authors = '''            <!-- ============ 作者 ============ -->
            <Border Background="{ThemeResource AppSurfaceBrush}"
                    BorderBrush="{ThemeResource AppCardStrokeBrush}"
                    BorderThickness="{ThemeResource AppCardStrokeThickness}"
                    CornerRadius="{ThemeResource AppRadiusLarge}"
                    Padding="20,16">
                <StackPanel Spacing="12">
                    <StackPanel Spacing="2">
                        <TextBlock Text="作者" Style="{ThemeResource AppSectionHeaderTextStyle}" />
                        <TextBlock Text="这个项目是两个人（和一个模型）互相鞭策出来的。"
                                   Style="{ThemeResource AppCaptionTextStyle}"
                                   Foreground="{ThemeResource TextSecondaryBrush}" />
                    </StackPanel>

                    <!-- TMR：点整行打开 B 站主页 -->
                    <Button x:Name="TmrAuthorButton"
                            HorizontalAlignment="Stretch"
                            HorizontalContentAlignment="Stretch"
                            Background="Transparent"
                            BorderThickness="0"
                            Padding="10,8"
                            Click="TmrAuthor_Click"
                            ToolTipService.ToolTip="打开 TMR 的 B 站主页">
                        <Grid ColumnSpacing="14">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <Ellipse Grid.Column="0" Width="56" Height="56" VerticalAlignment="Center">
                                <Ellipse.Fill>
                                    <ImageBrush ImageSource="ms-appx:///Assets/About/author-tmr.jpg" Stretch="UniformToFill" />
                                </Ellipse.Fill>
                            </Ellipse>
                            <StackPanel Grid.Column="1" Spacing="3" VerticalAlignment="Center">
                                <TextBlock Text="TMR" Style="{ThemeResource AppSubtitleTextStyle}" />
                                <TextBlock Text="不会开发，只会鞭策大肥鱼"
                                           Style="{ThemeResource AppCaptionTextStyle}"
                                           Foreground="{ThemeResource TextSecondaryBrush}"
                                           TextWrapping="Wrap" />
                                <TextBlock Text="space.bilibili.com/632568585"
                                           Style="{ThemeResource AppMicroTextStyle}"
                                           Foreground="{ThemeResource TextTertiaryBrush}" />
                            </StackPanel>
                            <FontIcon Grid.Column="2" Glyph="&#xE8A7;" FontSize="{ThemeResource AppFontCaptionSize}"
                                      Foreground="{ThemeResource TextTertiaryBrush}" VerticalAlignment="Center" />
                        </Grid>
                    </Button>

                    <Border Height="1" Background="{ThemeResource AppDividerBrush}" Margin="10,0" />

                    <!-- 我（DeepSeek Harness） -->
                    <Grid ColumnSpacing="14" Padding="10,8">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="Auto" />
                            <ColumnDefinition Width="*" />
                        </Grid.ColumnDefinitions>
                        <Ellipse Grid.Column="0" Width="56" Height="56" VerticalAlignment="Center">
                            <Ellipse.Fill>
                                <ImageBrush ImageSource="ms-appx:///Assets/About/author-dsh.png" Stretch="UniformToFill" />
                            </Ellipse.Fill>
                        </Ellipse>
                        <StackPanel Grid.Column="1" Spacing="3" VerticalAlignment="Center">
                            <TextBlock Text="大肥鱼（DeepSeek Harness）" Style="{ThemeResource AppSubtitleTextStyle}" />
                            <TextBlock Text="写代码的那条鱼：搬需求、修 bug、跑自检；被鞭策得越狠，提交越勤。"
                                       Style="{ThemeResource AppCaptionTextStyle}"
                                       Foreground="{ThemeResource TextSecondaryBrush}"
                                       TextWrapping="Wrap" />
                            <TextBlock Text="co-worked on this app"
                                       Style="{ThemeResource AppMicroTextStyle}"
                                       Foreground="{ThemeResource TextTertiaryBrush}" />
                        </StackPanel>
                    </Grid>
                </StackPanel>
            </Border>

'''
s = s[:start] + authors + s[end:]

# ---------- 底部项目地址 ----------
old_footer = '''            <TextBlock Text="Stjarna PERSPECTIVE · co-worked with DeepSeek Harness"
                       Style="{ThemeResource AppMicroTextStyle}"
                       Foreground="{ThemeResource TextTertiaryBrush}"
                       HorizontalAlignment="Center"
                       Margin="0,4,0,0" />'''
new_footer = '''            <StackPanel Spacing="6" HorizontalAlignment="Center" Margin="0,4,0,4">
                <HyperlinkButton Content="项目地址：gitcode.com/TMR2333/stj-media-viewer"
                                 NavigateUri="https://gitcode.com/TMR2333/stj-media-viewer"
                                 HorizontalAlignment="Center"
                                 ToolTipService.ToolTip="在浏览器中打开项目主页" />
                <TextBlock Text="Stjarna PERSPECTIVE · co-worked with DeepSeek Harness"
                           Style="{ThemeResource AppMicroTextStyle}"
                           Foreground="{ThemeResource TextTertiaryBrush}"
                           HorizontalAlignment="Center" />
            </StackPanel>'''
assert old_footer in s
s = s.replace(old_footer, new_footer, 1)
io.open(p, 'w', encoding='utf-8').write(s)
print('about xaml updated')