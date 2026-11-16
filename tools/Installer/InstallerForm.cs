using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace StjMediaViewer.Installer;

/// <summary>安装界面：选安装目录 + 是否创建桌面快捷方式。纯代码布局，不依赖 designer。</summary>
internal sealed class InstallerForm : Form
{
    /// <summary>安装包自己的目录名：用户选了上级目录时，程序装进这个子目录，不污染所选目录。</summary>
    internal const string AppDirectoryName = "STJ Media Viewer";

    /// <summary>历史目录名（默认安装路径用的就是这个），当作"已经是自己的目录"处理，避免重复套娃。</summary>
    private static readonly string[] OwnDirectoryNames = { AppDirectoryName, "StjMediaViewer" };

    /// <summary>
    /// 把用户选择的目录规整成最终安装目录：
    /// - 选的是 `D:\Apps` → `D:\Apps\STJ Media Viewer`（先建自己的子目录）；
    /// - 选的已经是自己的目录（`...\STJ Media Viewer` / 默认的 `...\StjMediaViewer`）→ 原样返回；
    /// - 便携版不调用这个方法（便携版按用户给的确切目录解压）。
    /// </summary>
    internal static string NormalizeInstallDirectory(string selected)
    {
        var trimmed = (selected ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed)) return trimmed;

        var full = Path.GetFullPath(trimmed);
        var leaf = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        foreach (var own in OwnDirectoryNames)
        {
            if (string.Equals(leaf, own, StringComparison.OrdinalIgnoreCase))
                return full;
        }
        return Path.Combine(full, AppDirectoryName);
    }

    private readonly TextBox _pathBox;
    private readonly CheckBox _desktopCheck;
    private readonly Label _finalPathLabel;

    public string SelectedDirectory => _pathBox.Text.Trim().Trim('"');

    public bool CreateDesktopShortcut => _desktopCheck.Checked;

    private readonly bool _elevated;

    public InstallerForm(string defaultDirectory, bool elevated = false)
    {
        _elevated = elevated;
        Text = "STJ Media Viewer 安装程序";
        ClientSize = new Size(600, 290);
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

        var scopeText = new Label
        {
            Text = _elevated
                ? "当前以管理员身份运行：可安装到 Program Files 等受保护目录，快捷方式装给所有用户。"
                : "当前为普通权限：默认装到你的用户目录（不需要 UAC）。选到受保护目录时会在安装前请求管理员授权。",
            Location = new Point(22, 74),
            Size = new Size(560, 18),
            ForeColor = _elevated ? Color.FromArgb(0, 120, 60) : Color.FromArgb(96, 96, 104),
        };

        var pathLabel = new Label
        {
            Text = "安装位置",
            Location = new Point(22, 98),
            AutoSize = true,
        };

        _pathBox = new TextBox
        {
            Text = defaultDirectory,
            Location = new Point(24, 120),
            Size = new Size(452, 26),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        var browse = new Button
        {
            Text = "浏览…",
            Location = new Point(484, 119),
            Size = new Size(88, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        browse.Click += (_, _) => BrowseForFolder();

        _finalPathLabel = new Label
        {
            Text = "",
            Location = new Point(24, 150),
            Size = new Size(552, 18),
            ForeColor = Color.FromArgb(0, 110, 70),
            AutoEllipsis = true,
        };
        _pathBox.TextChanged += (_, _) => UpdateFinalPathHint();
        UpdateFinalPathHint();

        _desktopCheck = new CheckBox
        {
            Text = "创建桌面快捷方式",
            Location = new Point(24, 176),
            AutoSize = true,
            Checked = false,
        };

        var hint = new Label
        {
            Text = "卸载：Windows 设置 → 应用 → 已安装的应用（或再次运行本安装程序 → /uninstall）。\n" +
                   "用户数据（索引库、缩略图缓存、设置）保存在 %LOCALAPPDATA%\\StjMediaViewer，卸载不会删除。",
            Location = new Point(24, 202),
            Size = new Size(552, 40),
            ForeColor = Color.FromArgb(96, 96, 104),
        };

        var install = new Button
        {
            Text = "安装",
            Location = new Point(392, 246),
            Size = new Size(90, 30),
            DialogResult = DialogResult.None,
        };
        install.Click += (_, _) => ConfirmAndClose();

        var cancel = new Button
        {
            Text = "取消",
            Location = new Point(486, 246),
            Size = new Size(90, 30),
            DialogResult = DialogResult.Cancel,
        };

        AcceptButton = install;
        CancelButton = cancel;

        Controls.AddRange(new Control[]
        {
            title, subtitle, scopeText, pathLabel, _pathBox, browse, _finalPathLabel, _desktopCheck, hint, install, cancel,
        });
    }

    /// <summary>把「所选目录 → 实际安装目录」实时显示出来，避免用户以为文件会直接堆在选择的那层。</summary>
    private void UpdateFinalPathHint()
    {
        var selected = SelectedDirectory;
        if (string.IsNullOrWhiteSpace(selected))
        {
            _finalPathLabel.Text = "";
            return;
        }

        try
        {
            var final = NormalizeInstallDirectory(selected);
            _finalPathLabel.Text = string.Equals(final, Path.GetFullPath(selected), StringComparison.OrdinalIgnoreCase)
                ? "✓ 直接装进这个目录"
                : $"✓ 会先创建自己的子目录：{final}";
        }
        catch
        {
            _finalPathLabel.Text = "";
        }
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
            // 兜底：用户手输的目录也要补上自己的子目录（`D:\Apps` → `D:\Apps\STJ Media Viewer`）
            var full = NormalizeInstallDirectory(dir);
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
            Description = "选择安装位置（程序会装进这个目录下的「STJ Media Viewer」子目录）",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(SelectedDirectory)
                ? SelectedDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ShowNewFolderButton = true,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            // 选的是"上级目录"：自动补上自己的目录名，不把文件直接堆在用户选的目录里
            _pathBox.Text = NormalizeInstallDirectory(dialog.SelectedPath);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _pathBox.SelectAll();
        _pathBox.Focus();
    }
}