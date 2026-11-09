using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using FEngLib.Assets;

namespace FEngViewer;

/// <summary>
/// Blocking recovery dialog for a .fengproj whose verified container baseline is unavailable.
/// It only ever reads the selected container and writes the content-addressed cache; the project
/// file and the user's container are never modified here.
/// </summary>
internal sealed class ProjectBaselineRecoveryDialog : Form
{
    private readonly string projectPath;
    private readonly ContainerProjectInfo info;
    private readonly Label reasonLabel;
    private readonly Label hintLabel;
    private readonly Label errorLabel;
    private readonly Label progressLabel;
    private readonly ProgressBar progress;
    private readonly LinkLabel detailsLink;
    private readonly Panel detailsPanel;
    private readonly TextBox detailsText;
    private readonly Button chooseButton;
    private readonly Button recheckButton;
    private readonly Button cancelButton;
    private readonly int collapsedHeight;
    private bool busy;
    private bool detailsExpanded;

    public ProjectBaselineRecoveryDialog(string projectPath, ContainerProjectInfo info)
    {
        this.projectPath = Path.GetFullPath(projectPath);
        this.info = info ?? throw new ArgumentNullException(nameof(info));

        Text = "恢复工程资源";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 560);
        collapsedHeight = ClientSize.Height;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 10,
            Padding = new Padding(24, 20, 24, 16)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 8; row++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 12)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(48)));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var projectName = Path.GetFileName(projectPath);
        var sourceName = string.IsNullOrWhiteSpace(info.SourcePath)
            ? "基线容器"
            : Path.GetFileName(info.SourcePath);
        var warningIcon = new PictureBox
        {
            Size = new Size(LogicalToDeviceUnits(32), LogicalToDeviceUnits(32)),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = SystemIcons.Warning.ToBitmap(),
            Margin = new Padding(0, 2, 12, 0)
        };
        warningIcon.Disposed += (_, _) => warningIcon.Image?.Dispose();
        var title = new Label
        {
            Text = string.IsNullOrWhiteSpace(info.SourcePath)
                ? "找不到工程所需的基线容器"
                : $"找不到工程所需的基线容器：{sourceName}",
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(690), 0),
            Font = new Font(Font.FontFamily, 13f, FontStyle.Bold),
            Margin = new Padding(0, 2, 0, 0)
        };
        header.Controls.Add(warningIcon, 0, 0);
        header.Controls.Add(title, 1, 0);
        root.Controls.Add(header, 0, 0);

        var body = new Label
        {
            Text = $"工程 {projectName} 需要打开时记录的基线容器 {sourceName} 才能继续打开。\r\n" +
                   "请重新定位该文件，或选择相同版本的备份。",
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(690), 0),
            Margin = new Padding(0, 0, 0, 14)
        };
        root.Controls.Add(body, 0, 1);

        var card = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = EditorTheme.Input,
            Padding = new Padding(16, 14, 16, 12),
            Margin = new Padding(0, 0, 0, 14)
        };
        var cardRows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 4
        };
        cardRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 4; row++) cardRows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cardRows.Controls.Add(new Label
        {
            Text = string.IsNullOrWhiteSpace(info.SourcePath) ? "基线容器" : $"基线容器：{sourceName}",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        }, 0, 0);
        cardRows.Controls.Add(new Label
        {
            Text = "记录位置",
            AutoSize = true,
            ForeColor = EditorTheme.Muted,
            Margin = new Padding(0, 0, 0, 2)
        }, 0, 1);
        cardRows.Controls.Add(new TextBox
        {
            Text = info.SourcePath ?? "",
            ReadOnly = true,
            Multiline = true,
            WordWrap = true,
            ScrollBars = ScrollBars.None,
            BorderStyle = BorderStyle.None,
            BackColor = EditorTheme.Input,
            ForeColor = EditorTheme.Text,
            Dock = DockStyle.Fill,
            Height = LogicalToDeviceUnits(36),
            TabStop = false,
            Margin = new Padding(0, 0, 0, 8)
        }, 0, 2);
        reasonLabel = new Label
        {
            Text = "ⓘ " + DescribeSourceState(),
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(640), 0),
            ForeColor = EditorTheme.Muted,
            Margin = new Padding(0, 0, 0, 0)
        };
        cardRows.Controls.Add(reasonLabel, 0, 3);
        card.Controls.Add(cardRows);
        root.Controls.Add(card, 0, 2);

        hintLabel = new Label
        {
            Text = "选择后将自动检查文件是否匹配。恢复不会修改基线容器或当前工程。",
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(690), 0),
            ForeColor = EditorTheme.Muted,
            Margin = new Padding(0, 0, 0, 10)
        };
        root.Controls.Add(hintLabel, 0, 3);

        detailsLink = new LinkLabel
        {
            Text = "▸ 详细信息",
            AutoSize = true,
            LinkBehavior = LinkBehavior.NeverUnderline,
            LinkColor = EditorTheme.Accent,
            ActiveLinkColor = EditorTheme.Accent,
            Margin = new Padding(0, 0, 0, 8)
        };
        detailsLink.LinkClicked += (_, _) => ToggleDetails();
        root.Controls.Add(detailsLink, 0, 4);

        detailsText = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = EditorTheme.Input,
            ForeColor = EditorTheme.Text,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            Text = BuildDetailsText(),
            WordWrap = true,
            Margin = new Padding(0)
        };
        detailsPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Height = LogicalToDeviceUnits(170),
            MinimumSize = new Size(0, LogicalToDeviceUnits(170)),
            Visible = false,
            Margin = new Padding(0, 0, 0, 8)
        };
        detailsPanel.Controls.Add(detailsText);
        root.Controls.Add(detailsPanel, 0, 5);

        var statusRows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 10)
        };
        statusRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusRows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statusRows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        errorLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(690), 0),
            ForeColor = Color.Firebrick,
            Visible = false,
            Margin = new Padding(0, 0, 0, 4)
        };
        progressLabel = new Label
        {
            AutoSize = true,
            ForeColor = EditorTheme.Muted,
            Visible = false,
            Margin = new Padding(0, 0, 0, 4)
        };
        progress = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = LogicalToDeviceUnits(4),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 24,
            Visible = false,
            Margin = new Padding(0)
        };
        statusRows.Controls.Add(errorLabel, 0, 0);
        statusRows.Controls.Add(progressLabel, 0, 1);
        root.Controls.Add(statusRows, 0, 6);
        root.Controls.Add(progress, 0, 7);
        root.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) }, 0, 8);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var leftButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        recheckButton = new Button
        {
            Text = "重新检查",
            AutoSize = true,
            Margin = new Padding(0)
        };
        recheckButton.Click += async (_, _) => await RecheckAsync();
        leftButtons.Controls.Add(recheckButton);
        var rightButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0)
        };
        chooseButton = new Button
        {
            Text = "选择容器文件…",
            AutoSize = true,
            Margin = new Padding(8, 0, 0, 0)
        };
        chooseButton.Click += async (_, _) => await ChooseFileAsync();
        cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Margin = new Padding(0)
        };
        rightButtons.Controls.Add(chooseButton);
        rightButtons.Controls.Add(cancelButton);
        footer.Controls.Add(leftButtons, 0, 0);
        footer.Controls.Add(rightButtons, 1, 0);
        root.Controls.Add(footer, 0, 9);

        Controls.Add(root);
        AcceptButton = chooseButton;
        CancelButton = cancelButton;
        FormClosing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            progressLabel.Text = "正在校验文件，请稍候…";
        };
        Shown += (_, _) => chooseButton.Focus();

        EditorTheme.Apply(this);
        card.BackColor = EditorTheme.Input;
        detailsText.BackColor = EditorTheme.Input;
        detailsText.ForeColor = EditorTheme.Text;
        reasonLabel.ForeColor = EditorTheme.Muted;
        hintLabel.ForeColor = EditorTheme.Muted;
        chooseButton.BackColor = EditorTheme.Accent;
        chooseButton.ForeColor = Color.White;
        chooseButton.FlatAppearance.BorderColor = EditorTheme.Accent;
        chooseButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 110, 180);
    }

    /// <summary>Canonical cache path created by the successful recovery.</summary>
    public string RecoveredBaselinePath { get; private set; }

    private string DescribeSourceState()
    {
        if (string.IsNullOrWhiteSpace(info.SourcePath)) return "记录位置无效。";
        try
        {
            var file = new FileInfo(info.SourcePath);
            if (!file.Exists) return "记录位置不可用：文件不存在。";
            if (file.Length != info.BaseLength)
                return $"记录位置的文件大小与工程记录不一致（实际 {file.Length:N0} 字节，期望 {info.BaseLength:N0} 字节）。";
            return "记录位置的文件仍在，但内容与工程记录不一致，或无法读取。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            return "记录位置不可用：无法读取该文件。";
        }
    }

    private string BuildDetailsText(BaselineProbe sourceProbe = null, BaselineProbe cacheProbe = null,
        BaselineProbe selectedProbe = null, string selectedPath = null)
    {
        var text = new StringBuilder();
        text.AppendLine($"工程路径        {projectPath}");
        text.AppendLine($"工程版本        {info.Version}{(info.HasIndependentBaseline ? "（独立缓存基线）" : "（旧版工程，无独立缓存）")}");
        text.AppendLine($"记录位置        {info.SourcePath}");
        text.AppendLine($"基线容器名称    {Path.GetFileName(info.SourcePath)}");
        text.AppendLine($"期望文件大小    {info.BaseLength:N0} 字节");
        text.AppendLine($"期望 SHA256     {info.BaseSha256}");
        try
        {
            text.AppendLine($"标准缓存路径    {ProjectBaselineStore.CachePath(info.BaseSha256, Path.GetFileName(info.SourcePath))}");
        }
        catch (Exception)
        {
            text.AppendLine("标准缓存路径    （工程记录无效）");
        }
        text.AppendLine($"工程内容        {info.Edits.Count} 个 FNG 修改、{info.Textures.Count} 个贴图覆盖、{info.TabCount} 个标签");
        foreach (var edit in info.Edits.Take(8))
            text.AppendLine($"  FNG 修改      {edit.PackageName}（0x{edit.Offset:X}）");
        if (info.Edits.Count > 8) text.AppendLine($"  ……另有 {info.Edits.Count - 8} 个 FNG 修改");
        foreach (var texture in info.Textures.Take(8))
            text.AppendLine($"  贴图覆盖      {texture.RequestName}（0x{texture.FngOffset:X}）· " +
                            (texture.InputKind == FEngLib.Assets.TextureInputKind.Dds
                                ? $"DDS {texture.Format} · {texture.Width} × {texture.Height} · {texture.MipCount} mip"
                                : $"PNG · {texture.Width} × {texture.Height}"));
        if (info.Textures.Count > 8) text.AppendLine($"  ……另有 {info.Textures.Count - 8} 个贴图覆盖");
        text.AppendLine();
        text.AppendLine("检查结果");
        text.AppendLine("  记录位置      " + (sourceProbe == null ? "尚未重新检查" : DescribeProbe(sourceProbe)));
        text.AppendLine("  缓存          " + (cacheProbe == null ? "尚未重新检查" : DescribeProbe(cacheProbe)));
        if (selectedProbe != null)
        {
            text.AppendLine("  所选文件      " + DescribeProbe(selectedProbe));
            if (!string.IsNullOrWhiteSpace(selectedPath)) text.AppendLine($"  所选路径      {selectedPath}");
        }
        return text.ToString();
    }

    private static string DescribeProbe(BaselineProbe probe)
    {
        if (probe == null) return "—";
        var actual = probe.ActualLength.HasValue ? $"{probe.ActualLength.Value:N0} 字节" : "—";
        var sha = string.IsNullOrWhiteSpace(probe.ActualSha256) ? "—" : probe.ActualSha256;
        return $"{DescribeStatus(probe.Status)}：{probe.Detail} 实际大小 {actual}；实际 SHA256 {sha}";
    }

    private static string DescribeStatus(BaselineProbeStatus status) => status switch
    {
        BaselineProbeStatus.Match => "匹配",
        BaselineProbeStatus.Missing => "文件不存在",
        BaselineProbeStatus.LengthMismatch => "大小不符",
        BaselineProbeStatus.Sha256Mismatch => "SHA256 不符",
        BaselineProbeStatus.Unreadable => "无法读取",
        BaselineProbeStatus.InvalidPath => "路径无效",
        _ => status.ToString()
    };

    private void ToggleDetails()
    {
        detailsExpanded = !detailsExpanded;
        detailsPanel.Visible = detailsExpanded;
        detailsLink.Text = detailsExpanded ? "▾ 详细信息" : "▸ 详细信息";
        var desired = collapsedHeight + (detailsExpanded ? LogicalToDeviceUnits(178) : 0);
        var working = Screen.FromControl(this).WorkingArea.Height;
        ClientSize = new Size(ClientSize.Width, Math.Min(desired, working - LogicalToDeviceUnits(60)));
        PerformLayout();
    }

    private async Task RecheckAsync()
    {
        if (busy) return;
        SetBusy(true, "正在重新检查记录位置和缓存…");
        try
        {
            var sourceProbe = await Task.Run(() =>
                ProjectBaselineStore.Probe(info.SourcePath, info.BaseLength, info.BaseSha256));
            if (sourceProbe.IsMatch)
            {
                await FinishRecoveryAsync(sourceProbe.Path);
                return;
            }

            string cachePath = null;
            try
            {
                cachePath = ProjectBaselineStore.CachePath(info.BaseSha256, Path.GetFileName(info.SourcePath));
            }
            catch (Exception) { }
            var cacheProbe = cachePath == null ? null : await Task.Run(() =>
                ProjectBaselineStore.Probe(cachePath, info.BaseLength, info.BaseSha256));
            if (cacheProbe?.IsMatch == true)
            {
                await FinishRecoveryAsync(cacheProbe.Path);
                return;
            }

            reasonLabel.Text = "ⓘ " + DescribeSourceState();
            UpdateDetails(sourceProbe, cacheProbe);
            ShowError("仍未找到与工程记录一致的文件。请重新连接记录位置，或选择相同版本的备份。");
            SetBusy(false);
        }
        catch (Exception)
        {
            ShowError("重新检查失败。请稍后重试，或直接选择容器文件。");
            SetBusy(false);
        }
    }

    private async Task ChooseFileAsync()
    {
        if (busy) return;
        using var dialog = new OpenFileDialog
        {
            Title = "选择基线容器",
            Filter = "容器文件 (*.lzc;*.bun;*.bin)|*.lzc;*.bun;*.bin|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        try
        {
            var directory = Path.GetDirectoryName(info.SourcePath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                dialog.InitialDirectory = directory;
            else
                dialog.InitialDirectory = Path.GetDirectoryName(projectPath);
        }
        catch (Exception) { }
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        await VerifyAndRecoverAsync(dialog.FileName);
    }

    private async Task VerifyAndRecoverAsync(string path)
    {
        SetBusy(true, "正在检查文件大小和 SHA256…");
        BaselineProbe probe;
        try
        {
            probe = await Task.Run(() => ProjectBaselineStore.Probe(path, info.BaseLength, info.BaseSha256));
        }
        catch (Exception)
        {
            ShowError("无法检查所选文件。请重试或选择其他文件。");
            SetBusy(false);
            return;
        }

        if (!probe.IsMatch)
        {
            UpdateDetails(selectedProbe: probe, selectedPath: path);
            ShowError(DescribeMismatch(probe));
            SetBusy(false);
            return;
        }
        await FinishRecoveryAsync(probe.Path);
    }

    private async Task FinishRecoveryAsync(string path)
    {
        try
        {
            SetBusy(true, "正在创建基线缓存并验证…");
            var cachePath = await Task.Run(() =>
                ProjectBaselineStore.Ensure(path, info.BaseLength, info.BaseSha256,
                    Path.GetFileName(info.SourcePath)));
            RecoveredBaselinePath = cachePath;
            if (!IsDisposed && !Disposing)
            {
                SetBusy(false);
                DialogResult = DialogResult.OK;
                Close();
            }
        }
        catch (Exception)
        {
            ShowError("无法创建基线缓存。请检查本机缓存目录的写入权限和磁盘空间后重试。" +
                      " 基线容器和工程都没有被修改。");
            SetBusy(false);
        }
    }

    private void UpdateDetails(BaselineProbe sourceProbe = null, BaselineProbe cacheProbe = null,
        BaselineProbe selectedProbe = null, string selectedPath = null)
    {
        detailsText.Text = BuildDetailsText(sourceProbe, cacheProbe, selectedProbe, selectedPath);
    }

    private void ShowError(string message)
    {
        errorLabel.Text = message;
        errorLabel.Visible = true;
        errorLabel.ForeColor = Color.Firebrick;
    }

    private void SetBusy(bool value, string message = null)
    {
        busy = value;
        chooseButton.Enabled = !value;
        recheckButton.Enabled = !value;
        cancelButton.Enabled = !value;
        progress.Visible = value;
        progressLabel.Visible = value;
        progressLabel.Text = value ? message ?? "正在处理…" : "";
        if (value) errorLabel.Visible = false;
        Cursor = value ? Cursors.WaitCursor : Cursors.Default;
        UseWaitCursor = value;
    }

    private string DescribeMismatch(BaselineProbe probe) => probe.Status switch
    {
        BaselineProbeStatus.Missing => "所选文件不存在。",
        BaselineProbeStatus.LengthMismatch =>
            $"所选文件大小不符：实际 {probe.ActualLength ?? 0:N0} 字节，期望 {info.BaseLength:N0} 字节。请选择正确版本的容器。",
        BaselineProbeStatus.Sha256Mismatch =>
            "所选文件的内容与工程记录不一致（SHA256 不同）。请选择与工程记录完全一致的基线容器。",
        BaselineProbeStatus.Unreadable => "无法读取所选文件：" + probe.Detail,
        BaselineProbeStatus.InvalidPath => "所选路径无效。",
        _ => "所选文件与工程记录不一致。"
    };
}
