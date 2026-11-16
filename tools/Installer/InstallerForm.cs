using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace StjMediaViewer.Installer;

/// <summary>安装界面：选安装目录 + 是否创建桌面快捷方式。纯代码布局，不依赖 designer。</summary>
internal sealed class InstallerForm : Form
{
    private readonly TextBox _pathBox;
    private readonly CheckBox _desktopCheck;

    public string SelectedDirectory => _pathBox.Text.Trim().Trim('"');

    public bool CreateDesktopShortcut => _desktopCheck.Checked;

    public InstallerForm(string defaultDirectory)
    {
        Text = "STJ Media Viewer 安装程序";
        ClientSize = new Size(600, 258);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        var title = new Label
        {
            Text = "安装 STJ Media Viewer",
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
            Location = new Point(20, 18),
            AutoSize = true,
        };

        var subtitle = new Label
        {
            Text = "素材管理与 AI 分类工作台 · 安装到当前用户目录，不需要管理员权限。",
            Location = new Point(22, 52),
            Size = new Size(560, 20),
            ForeColor = Color.FromArgb(96, 96, 104),
        };

        var pathLabel = new Label
        {
            Text = "安装位置",
            Location = new Point(22, 88),
            AutoSize = true,
        };

        _pathBox = new TextBox
        {
            Text = defaultDirectory,
            Location = new Point(24, 110),
            Size = new Size(452, 26),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        var browse = new Button
        {
            Text = "浏览…",
            Location = new Point(484, 109),
            Size = new Size(88, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        browse.Click += (_, _) => BrowseForFolder();

        _desktopCheck = new CheckBox
        {
            Text = "创建桌面快捷方式",
            Location = new Point(24, 148),
            AutoSize = true,
            Checked = false,
        };

        var hint = new Label
        {
            Text = "卸载：Windows 设置 → 应用 → 已安装的应用（或再次运行本安装程序 → /uninstall）。\n" +
                   "用户数据（索引库、缩略图缓存、设置）保存在 %LOCALAPPDATA%\\StjMediaViewer，卸载不会删除。",
            Location = new Point(24, 176),
            Size = new Size(552, 40),
            ForeColor = Color.FromArgb(96, 96, 104),
        };

        var install = new Button
        {
            Text = "安装",
            Location = new Point(392, 218),
            Size = new Size(90, 30),
            DialogResult = DialogResult.None,
        };
        install.Click += (_, _) => ConfirmAndClose();

        var cancel = new Button
        {
            Text = "取消",
            Location = new Point(486, 218),
            Size = new Size(90, 30),
            DialogResult = DialogResult.Cancel,
        };

        AcceptButton = install;
        CancelButton = cancel;

        Controls.AddRange(new Control[]
        {
            title, subtitle, pathLabel, _pathBox, browse, _desktopCheck, hint, install, cancel,
        });
    }

    private void ConfirmAndClose()
    {
        var dir = SelectedDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            MessageBox.Show(this, "请先选择安装位置。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var full = Path.GetFullPath(dir);
            if (full.Length > 180)
            {
                MessageBox.Show(this, "路径太长，请换一个短一点的目录。", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (full.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "不要把程序装进 Windows 目录。", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _pathBox.Text = full;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void BrowseForFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择安装位置（会在这个目录里放 STJ Media Viewer 的程序文件）",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(SelectedDirectory)
                ? SelectedDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ShowNewFolderButton = true,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            _pathBox.Text = dialog.SelectedPath;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _pathBox.SelectAll();
        _pathBox.Focus();
    }
}