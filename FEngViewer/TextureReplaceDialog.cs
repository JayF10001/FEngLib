using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using FEngLib.Assets;
using FEngLib.Packages;

namespace FEngViewer;

/// <summary>
/// Compact replacement view: current texture summary, candidate PNG validation and a project-level
/// override. Nothing is written to the container or the game directory; the PNG is stored in the
/// .fengproj by the caller. A verified container copy can be generated from the applied override.
/// </summary>
internal sealed class TextureReplaceDialog : Form
{
    private readonly string _objectName;
    private readonly string _documentName;
    private readonly ResourceRequest _request;
    private readonly string _projectSourcePath;
    private readonly Func<ContainerTextureOverride, string, string> _writeBack;
    private readonly Func<TextureLookupResult> _resolveCurrent;
    private readonly Func<byte[], string, TextureInputKind, ContainerTextureOverride> _createOverride;
    private readonly Func<ContainerTextureOverride, string> _applyOverride;
    private readonly Func<string> _removeOverride;
    private readonly TextureLookupResult _target;

    private readonly Label _headline = new()
    {
        AutoSize = true,
        Font = new Font(DefaultFont, FontStyle.Bold),
        Margin = new Padding(0, 0, 0, 6)
    };
    private readonly Label _summary = new()
    {
        AutoSize = true,
        MaximumSize = new Size(760, 0),
        Margin = new Padding(0, 0, 0, 8)
    };
    private readonly PictureBox _currentPreview = NewPreview();
    private readonly PictureBox _candidatePreview = NewPreview();
    private readonly Label _currentState = NewStateLabel();
    private readonly Label _candidateState = NewStateLabel();
    private readonly Label _techSummary = new()
    {
        AutoSize = true,
        MaximumSize = new Size(760, 0),
        Margin = new Padding(0, 0, 0, 6)
    };
    private readonly Label _currentDetails = NewDetailsLabel();
    private readonly Label _candidateDetails = NewDetailsLabel();
    private readonly Label _status = new()
    {
        AutoSize = true,
        MaximumSize = new Size(760, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 8, 0, 6)
    };
    private readonly LinkLabel _detailsLink = NewLink("▸ 详细信息");
    private readonly LinkLabel _advancedLink = NewLink("▸ 高级操作（导出贴图档案副本）");
    private readonly Panel _detailsPanel = new() { Dock = DockStyle.Fill, AutoSize = true, Visible = false };
    private readonly Panel _advancedPanel = new() { Dock = DockStyle.Fill, AutoSize = true, Visible = false };
    private readonly Button _select = new() { Text = "选择贴图…", AutoSize = true };
    private readonly Button _apply = new() { Text = "应用替换", AutoSize = true, Enabled = false };
    private readonly Button _remove = new() { Text = "移除替换", AutoSize = true, Enabled = false };
    private readonly Button _writeBackButton = new() { Text = "导出贴图档案副本…", AutoSize = true, Enabled = false };
    private readonly Button _close = new() { Text = "关闭", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly int _collapsedHeight;

    private TextureLookupResult _current;
    private ContainerTextureOverride _existing;
    private byte[] _candidateBytes;
    private string _candidatePath;
    private (int Width, int Height)? _candidateSize;
    private TextureInputKind _candidateKind = TextureInputKind.Png;
    private DdsTextureInfo _candidateDds;
    private byte[] _candidatePreviewPng;
    private string _candidatePreviewError;
    private bool _candidateAccepted;

    public TextureReplaceDialog(string objectName, string documentName, ResourceRequest request,
        TextureLookupResult current, TextureLookupResult target, ContainerTextureOverride existing,
        string projectSourcePath, Func<ContainerTextureOverride, string, string> writeBack,
        Func<TextureLookupResult> resolveCurrent,
        Func<byte[], string, TextureInputKind, ContainerTextureOverride> createOverride,
        Func<ContainerTextureOverride, string> applyOverride,
        Func<string> removeOverride)
    {
        _objectName = objectName;
        _documentName = documentName;
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _current = current ?? new TextureLookupResult
        {
            Status = TextureLookupStatus.NotFound,
            RequestedName = request.Name,
            RequestedId = request.ID,
        };
        _target = target;
        _existing = existing;
        _projectSourcePath = projectSourcePath ?? "";
        _writeBack = writeBack;
        _resolveCurrent = resolveCurrent ?? throw new ArgumentNullException(nameof(resolveCurrent));
        _createOverride = createOverride ?? throw new ArgumentNullException(nameof(createOverride));
        _applyOverride = applyOverride ?? throw new ArgumentNullException(nameof(applyOverride));
        _removeOverride = removeOverride ?? throw new ArgumentNullException(nameof(removeOverride));

        Text = "替换贴图 · " + (request.Name ?? $"0x{request.ID:X8}");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(LogicalToDeviceUnits(820), LogicalToDeviceUnits(640));
        MinimumSize = new Size(LogicalToDeviceUnits(720), LogicalToDeviceUnits(560));
        _collapsedHeight = ClientSize.Height;
        KeyPreview = true;

        BuildLayout();
        EditorTheme.Apply(this);
        _currentPreview.BackgroundImage = PreviewBackground.Checkerboard;
        _candidatePreview.BackgroundImage = PreviewBackground.Checkerboard;
        _currentDetails.ForeColor = EditorTheme.Text;
        _candidateDetails.ForeColor = EditorTheme.Text;
        _techSummary.ForeColor = EditorTheme.Muted;
        _status.ForeColor = EditorTheme.Muted;
        _select.BackColor = EditorTheme.Accent;
        _select.ForeColor = Color.White;
        _select.FlatAppearance.BorderColor = EditorTheme.Accent;
        _select.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 110, 180);
        _detailsLink.LinkColor = EditorTheme.Accent;
        _advancedLink.LinkColor = EditorTheme.Accent;
        RefreshCurrent();
        RefreshCandidate();
        UpdateButtons();
    }

    private static PictureBox NewPreview() => new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.FromArgb(210, 210, 210),
        BackgroundImageLayout = ImageLayout.Tile,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 0, 0, 4)
    };

    private static Label NewDetailsLabel() => new()
    {
        AutoSize = true,
        MaximumSize = new Size(420, 0),
        Margin = new Padding(0, 2, 0, 0)
    };

    private static Label NewStateLabel() => new()
    {
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 4, 0, 0)
    };

    private static LinkLabel NewLink(string text) => new()
    {
        Text = text,
        AutoSize = true,
        LinkBehavior = LinkBehavior.NeverUnderline,
        LinkColor = Color.FromArgb(0, 126, 201),
        ActiveLinkColor = Color.FromArgb(0, 126, 201),
        Margin = new Padding(0, 4, 0, 4)
    };

    private void BuildLayout()
    {
        _headline.Text = "替换贴图 · " + (_request.Name ?? $"0x{_request.ID:X8}");
        _techSummary.Text = $"FNG：{_documentName}\r\n" +
                            $"资源请求：{_request.Name ?? "(未命名)"} · 0x{_request.ID:X8}";

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        header.Controls.Add(_headline, 0, 0);
        header.Controls.Add(_summary, 0, 1);

        var previews = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            MinimumSize = new Size(0, LogicalToDeviceUnits(220))
        };
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        previews.Controls.Add(BuildPreviewGroup("当前贴图", _currentPreview, _currentState), 0, 0);
        previews.Controls.Add(BuildPreviewGroup("新贴图预览", _candidatePreview, _candidateState), 1, 0);
        previews.Controls.Add(BuildActionsPanel(), 2, 0);

        var details = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(2, 2, 2, 4),
            Margin = Padding.Empty
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.Controls.Add(_techSummary, 0, 0);
        details.SetColumnSpan(_techSummary, 2);
        details.Controls.Add(new Label
        {
            Text = "当前贴图信息",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 9f, FontStyle.Bold),
            Margin = new Padding(0, 2, 0, 2)
        }, 0, 1);
        details.Controls.Add(new Label
        {
            Text = "新贴图信息",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 9f, FontStyle.Bold),
            Margin = new Padding(0, 2, 0, 2)
        }, 1, 1);
        _currentDetails.MaximumSize = new Size(360, 0);
        _candidateDetails.MaximumSize = new Size(360, 0);
        details.Controls.Add(_currentDetails, 0, 2);
        details.Controls.Add(_candidateDetails, 1, 2);
        _detailsPanel.Controls.Add(details);

        var advanced = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(2, 2, 2, 4),
            Margin = Padding.Empty
        };
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        advanced.Controls.Add(new Label
        {
            Text = "只导出当前这一张贴图的独立档案副本，不包含 FNG 修改和其他贴图。\r\n" +
                   "完整工程请使用「导出容器…」。",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = EditorTheme.Muted,
            Margin = new Padding(0, 3, 8, 0)
        }, 0, 0);
        advanced.Controls.Add(_writeBackButton, 1, 0);
        _advancedPanel.Controls.Add(advanced);

        _detailsLink.LinkClicked += (_, _) => ToggleDetails();
        _advancedLink.LinkClicked += (_, _) => ToggleAdvanced();

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 0)
        };
        bottom.Controls.Add(_close);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            Padding = new Padding(10),
            Margin = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(previews, 0, 1);
        root.Controls.Add(_status, 0, 2);
        root.Controls.Add(_detailsLink, 0, 3);
        root.Controls.Add(_detailsPanel, 0, 4);
        root.Controls.Add(_advancedLink, 0, 5);
        root.Controls.Add(_advancedPanel, 0, 6);
        root.Controls.Add(bottom, 0, 7);
        Controls.Add(root);
        CancelButton = _close;

        _select.Click += (_, _) => SelectCandidate();
        _apply.Click += (_, _) => ApplyCandidate();
        _remove.Click += (_, _) => RemoveCandidate();
        _writeBackButton.Click += (_, _) => WriteBackCopy();
    }

    private Control BuildActionsPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(8, LogicalToDeviceUnits(24), 0, 0),
            Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 5; row++)
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (var button in new[] { _select, _apply, _remove })
        {
            button.AutoSize = false;
            button.Dock = DockStyle.Fill;
            button.Height = LogicalToDeviceUnits(32);
            button.Margin = new Padding(0, 0, 0, 8);
            panel.Controls.Add(button, 0, panel.Controls.Count);
        }
        panel.Controls.Add(new Label
        {
            Text = "选择 PNG 或 DDS 只更新预览；应用后才修改工程内的替换记录。",
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(220), 0),
            ForeColor = EditorTheme.Muted,
            Margin = new Padding(0, 4, 0, 0)
        }, 0, 3);
        return panel;
    }

    private static GroupBox BuildPreviewGroup(string title, PictureBox preview, Label state)
    {
        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Margin = new Padding(4)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(preview, 0, 0);
        layout.Controls.Add(state, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private void ToggleDetails()
    {
        _detailsPanel.Visible = !_detailsPanel.Visible;
        _detailsLink.Text = _detailsPanel.Visible ? "▾ 详细信息" : "▸ 详细信息";
        ResizeForPanels();
    }

    private void ToggleAdvanced()
    {
        _advancedPanel.Visible = !_advancedPanel.Visible;
        _advancedLink.Text = _advancedPanel.Visible ? "▾ 高级操作（导出贴图档案副本）" : "▸ 高级操作（导出贴图档案副本）";
        ResizeForPanels();
    }

    private void ResizeForPanels()
    {
        var extra = 0;
        if (_detailsPanel.Visible)
        {
            _detailsPanel.PerformLayout();
            extra += _detailsPanel.PreferredSize.Height + LogicalToDeviceUnits(6);
        }
        if (_advancedPanel.Visible)
        {
            _advancedPanel.PerformLayout();
            extra += _advancedPanel.PreferredSize.Height + LogicalToDeviceUnits(6);
        }
        var workingArea = Screen.FromControl(this).WorkingArea;
        var desired = _collapsedHeight + extra;
        ClientSize = new Size(ClientSize.Width,
            Math.Min(desired, workingArea.Height - LogicalToDeviceUnits(80)));
        var maxTop = workingArea.Bottom - Height - LogicalToDeviceUnits(20);
        if (Top > maxTop) Top = Math.Max(workingArea.Top + LogicalToDeviceUnits(20), maxTop);
        PerformLayout();
    }

    private static string FormatSize((int Width, int Height)? size) =>
        size.HasValue ? $"{size.Value.Width} × {size.Value.Height}" : "尺寸未知";

    private void RefreshCurrent()
    {
        var size = CurrentSize();
        _summary.Text = $"对象：{_objectName} · {FormatSize(size)}";
        _currentState.Text = _current.IsProjectOverride ? "已替换"
            : _current.Entry != null ? "原始贴图"
            : _current.IsFound ? "松散 PNG" : "缺失";
        _currentState.ForeColor = _current.IsProjectOverride ? Color.SteelBlue
            : _current.Entry != null ? Color.ForestGreen
            : _current.IsFound ? Color.FromArgb(190, 100, 20) : Color.Firebrick;
        _currentDetails.Text = DescribeCurrent(_current);
        var error = SetPreview(_currentPreview, _current.IsFound ? _current.PngData : null);
        if (error != null) _currentDetails.Text += "\r\n预览失败：" + error;
        UpdateButtons();
    }

    private string DescribeCurrent(TextureLookupResult current)
    {
        var lines = new List<string>();
        if (current.IsProjectOverride)
        {
            lines.Add("来源：工程贴图覆盖");
            lines.Add("覆盖：" + current.Source);
            lines.Add("贴图：" + (current.SourceTexture ?? _request.Name));
            lines.Add($"尺寸：{current.Width} × {current.Height}");
            if (current.InputKind == TextureInputKind.Dds)
            {
                lines.Add($"格式：DDS {current.Format}");
                lines.Add($"Mip：{current.MipCount} 级");
            }
            else
            {
                lines.Add("格式：PNG（覆盖层）");
            }
            if (_existing != null && _existing.StoredLength > 0)
                lines.Add($"大小：{_existing.StoredLength:N0} 字节");
            if (!string.IsNullOrWhiteSpace(current.Message))
                lines.Add("预览：" + current.Message);
            if (_existing != null && _existing.HasTarget)
            {
                lines.Add($"目标：{_existing.TargetTpkName} / {_existing.TargetTextureName} · {_existing.TargetCompression}");
                if (!File.Exists(_existing.TargetArchivePath))
                    lines.Add("导出：目标档案不存在。");
                else if (ContainerSession.PathsEqual(_existing.TargetArchivePath, _projectSourcePath))
                    lines.Add("导出：可生成当前容器副本。");
                else
                    lines.Add("导出：外部共享包；将生成该档案的副本。");
            }
            else
            {
                lines.Add("导出：缺少 TPK 目标信息。");
            }
        }
        else if (current.Entry is { } entry)
        {
            lines.Add("来源：TPK 档案");
            lines.Add("档案：" + entry.ArchivePath);
            lines.Add("TPK：" + entry.TpkName);
            lines.Add("贴图：" + entry.Name);
            lines.Add($"尺寸：{entry.Width} × {entry.Height}");
            lines.Add("格式：" + entry.Compression);
            lines.Add("Mip：" + Math.Max(1, entry.MipCount) + " 级");
        }
        else if (current.IsFound)
        {
            lines.Add("来源：松散 PNG");
            lines.Add("文件：" + current.Source);
            lines.Add("贴图：" + (current.SourceTexture ?? _request.Name));
            var size = TryGetPngSize(current.PngData);
            if (size.HasValue) lines.Add($"尺寸：{size.Value.Width} × {size.Value.Height}");
            lines.Add("格式：PNG");
        }
        else
        {
            lines.Add("来源：未找到");
            lines.Add("请求：" + (_request.Name ?? "(未命名)"));
            if (!string.IsNullOrWhiteSpace(current.Message)) lines.Add("说明：" + current.Message);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private (int Width, int Height)? CurrentSize()
    {
        if (_current.Entry is { } entry) return (entry.Width, entry.Height);
        if (_current.IsProjectOverride && _current.Width > 0 && _current.Height > 0)
            return (_current.Width, _current.Height);
        if (_current.IsFound) return TryGetPngSize(_current.PngData);
        return null;
    }

    private void SelectCandidate()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择替换贴图",
            Filter = "贴图文件 (*.png;*.dds)|*.png;*.dds|PNG 图片 (*.png)|*.png|DDS 贴图 (*.dds)|*.dds",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            SetCandidate(File.ReadAllBytes(dialog.FileName), dialog.FileName);
        }
        catch (Exception error)
        {
            _status.Text = "无法读取贴图：" + error.Message;
            _status.ForeColor = Color.Firebrick;
        }
    }

    private void SetCandidate(byte[] bytes, string path)
    {
        _candidateBytes = bytes;
        _candidatePath = path;
        _candidatePreviewPng = null;
        _candidatePreviewError = null;
        _candidateDds = null;
        if (DdsTextureInfo.LooksLikeDds(bytes))
        {
            var info = DdsTextureInfo.Parse(bytes, Path.GetFileName(path));
            _candidateKind = TextureInputKind.Dds;
            _candidateDds = info;
            _candidateSize = (info.Width, info.Height);
            try
            {
                _candidatePreviewPng = BinaryRuntime.DecodeTextureDds(bytes);
            }
            catch (Exception error)
            {
                _candidatePreviewError = error.Message;
            }
        }
        else
        {
            var size = TryGetPngSize(bytes) ??
                       throw new InvalidDataException("不是可解码的 PNG，也不是有效的 2D DXT1/DXT3/DXT5 DDS。");
            _candidateKind = TextureInputKind.Png;
            _candidateSize = size;
            _candidatePreviewPng = bytes;
        }
        RefreshCandidate();
    }

    private (string Compression, int Width, int Height, int MipCount, string Name)? TargetInfo()
    {
        if (_target?.Entry is { } targetEntry)
            return (targetEntry.Compression, targetEntry.Width, targetEntry.Height,
                Math.Max(1, targetEntry.MipCount), targetEntry.Name);
        if (_current?.Entry is { } currentEntry)
            return (currentEntry.Compression, currentEntry.Width, currentEntry.Height,
                Math.Max(1, currentEntry.MipCount), currentEntry.Name);
        if (_existing?.HasTarget == true)
            return (_existing.TargetCompression, _existing.Width, _existing.Height,
                _existing.IsDds ? _existing.MipCount : -1, _existing.TargetTextureName);
        return null;
    }

    private void RefreshCandidate()
    {
        if (_candidateBytes == null || _candidateSize == null)
        {
            _candidateAccepted = false;
            _candidateState.Text = "尚未选择贴图";
            _candidateState.ForeColor = Color.Gray;
            _candidateDetails.Text = "尚未选择贴图。\r\n支持 PNG 和 2D DXT1/DXT3/DXT5 DDS；选择后会显示尺寸、格式、Mip 与目标校验。";
            SetPreview(_candidatePreview, null);
            UpdateButtons();
            return;
        }

        var size = _candidateSize.Value;
        var lines = new List<string>
        {
            "文件：" + _candidatePath,
            $"尺寸：{size.Width} × {size.Height}",
            _candidateKind == TextureInputKind.Dds
                ? $"格式：DDS {_candidateDds.Format}（原始字节）"
                : "格式：PNG",
        };
        if (_candidateKind == TextureInputKind.Dds)
            lines.Add($"Mip：{_candidateDds.MipCount} 级");
        lines.Add($"大小：{_candidateBytes.Length:N0} 字节");

        if (_candidateKind == TextureInputKind.Dds)
        {
            var info = _candidateDds;
            var target = TargetInfo();
            if (target == null)
            {
                _candidateAccepted = true;
                lines.Add("目标校验：缺少目标 TPK 信息，无法校验格式、尺寸与 Mip");
                _status.Text = "DDS 有效，但缺少目标 TPK 信息；可保存覆盖，无法导出贴图档案副本。";
                _status.ForeColor = Color.FromArgb(190, 100, 20);
                _candidateState.Text = "DDS 有效（无目标）";
                _candidateState.ForeColor = Color.FromArgb(190, 100, 20);
            }
            else
            {
                var expectedFormat = DdsTextureRules.FormatForCompression(target.Value.Compression);
                var problems = new List<string>();
                if (expectedFormat == null)
                {
                    if (!string.IsNullOrWhiteSpace(target.Value.Compression))
                        problems.Add($"目标格式 {target.Value.Compression} 不支持导出 DDS");
                }
                else if (!string.Equals(info.Format, expectedFormat, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"格式不一致（目标 {expectedFormat}，文件 {info.Format}）");
                }
                if (target.Value.Width > 0 && target.Value.Height > 0 &&
                    (info.Width != target.Value.Width || info.Height != target.Value.Height))
                {
                    problems.Add($"尺寸不一致（目标 {target.Value.Width} × {target.Value.Height}）");
                }
                var mipKnown = target.Value.MipCount >= 0;
                if (mipKnown && info.MipCount != target.Value.MipCount)
                    problems.Add($"Mip 数不一致（目标 {target.Value.MipCount}，文件 {info.MipCount}）");

                if (problems.Count > 0)
                {
                    _candidateAccepted = false;
                    lines.Add("目标校验：" + string.Join("；", problems));
                    _status.Text = "候选 DDS 与目标 TPK 不一致，已禁用应用：" + string.Join("；", problems) + "。";
                    _status.ForeColor = Color.Firebrick;
                    _candidateState.Text = "DDS 不一致";
                    _candidateState.ForeColor = Color.Firebrick;
                }
                else if (!mipKnown)
                {
                    _candidateAccepted = true;
                    lines.Add("目标校验：格式与尺寸匹配；目标 Mip 未知，导出时会再次核对");
                    _status.Text = "DDS 格式与尺寸匹配；目标 Mip 未知，导出时仍会严格核对。";
                    _status.ForeColor = Color.FromArgb(190, 100, 20);
                    _candidateState.Text = "DDS 格式/尺寸匹配";
                    _candidateState.ForeColor = Color.FromArgb(190, 100, 20);
                }
                else
                {
                    _candidateAccepted = true;
                    lines.Add($"目标校验：格式 {info.Format}、尺寸和 {info.MipCount} 级 Mip 均匹配");
                    _status.Text = "候选 DDS 的格式、尺寸与 Mip 均匹配，可以应用覆盖。";
                    _status.ForeColor = Color.ForestGreen;
                    _candidateState.Text = "DDS 完全匹配";
                    _candidateState.ForeColor = Color.ForestGreen;
                }
            }
        }
        else
        {
            var currentSize = CurrentSize();
            if (currentSize == null)
            {
                _candidateAccepted = true;
                lines.Add("尺寸校验：当前贴图缺失，无法校验");
                _status.Text = "当前贴图缺失，无法校验尺寸；仍可作为覆盖层应用。";
                _status.ForeColor = Color.FromArgb(190, 100, 20);
                _candidateState.Text = "当前贴图缺失";
                _candidateState.ForeColor = Color.FromArgb(190, 100, 20);
            }
            else if (currentSize.Value == size)
            {
                _candidateAccepted = true;
                lines.Add($"尺寸校验：匹配（{currentSize.Value.Width} × {currentSize.Value.Height}）");
                _status.Text = "候选 PNG 尺寸匹配，可以应用覆盖。";
                _status.ForeColor = Color.ForestGreen;
                _candidateState.Text = "尺寸匹配";
                _candidateState.ForeColor = Color.ForestGreen;
            }
            else
            {
                _candidateAccepted = false;
                lines.Add($"尺寸校验：不一致（当前 {currentSize.Value.Width} × {currentSize.Value.Height}）");
                _status.Text = "候选 PNG 尺寸与当前贴图不一致，已禁用应用。";
                _status.ForeColor = Color.Firebrick;
                _candidateState.Text = "尺寸不一致";
                _candidateState.ForeColor = Color.Firebrick;
            }
        }

        if (!string.IsNullOrWhiteSpace(_candidatePreviewError))
            lines.Add("预览：解码失败 - " + _candidatePreviewError +
                      "（需配置 Binary/DevIL；不影响保存 DDS 原始字节）");
        else if (_candidateKind == TextureInputKind.Dds && _candidatePreviewPng == null)
            lines.Add("预览：DDS 解码不可用。");
        _candidateDetails.Text = string.Join(Environment.NewLine, lines);
        var error = SetPreview(_candidatePreview, _candidatePreviewPng);
        if (error != null) _candidateDetails.Text += "\r\n预览失败：" + error;
        UpdateButtons();
    }

    private void ApplyCandidate()
    {
        if (_candidateBytes == null || _candidateSize == null || !_candidateAccepted) return;
        try
        {
            var replacement = _createOverride(_candidateBytes, Path.GetFileName(_candidatePath),
                _candidateKind);
            var error = _applyOverride(replacement);
            if (!string.IsNullOrWhiteSpace(error))
            {
                _status.Text = error;
                _status.ForeColor = Color.Firebrick;
                return;
            }
            _existing = replacement;
            _current = _resolveCurrent();
            RefreshCurrent();
            RefreshCandidate();
            _status.Text = "已应用工程贴图覆盖（未保存工程）。";
            _status.ForeColor = Color.ForestGreen;
        }
        catch (Exception error)
        {
            _status.Text = "应用失败：" + error.Message;
            _status.ForeColor = Color.Firebrick;
        }
    }

    private void RemoveCandidate()
    {
        try
        {
            var error = _removeOverride();
            if (!string.IsNullOrWhiteSpace(error))
            {
                _status.Text = error;
                _status.ForeColor = Color.Firebrick;
                return;
            }
            _existing = null;
            _current = _resolveCurrent();
            RefreshCurrent();
            RefreshCandidate();
            _status.Text = "已移除工程贴图覆盖（未保存工程）。";
            _status.ForeColor = Color.ForestGreen;
        }
        catch (Exception error)
        {
            _status.Text = "移除失败：" + error.Message;
            _status.ForeColor = Color.Firebrick;
        }
    }

    private void WriteBackCopy()
    {
        if (_existing == null || !_existing.HasTarget || _writeBack == null) return;
        var targetArchive = _existing.TargetArchivePath;
        if (string.IsNullOrWhiteSpace(targetArchive) || !File.Exists(targetArchive))
        {
            _status.Text = "目标档案不存在，无法导出贴图档案副本。";
            _status.ForeColor = Color.Firebrick;
            return;
        }
        var extension = Path.GetExtension(targetArchive);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".lzc";
        using var dialog = new SaveFileDialog
        {
            Title = "导出贴图档案副本",
            Filter = $"贴图档案副本 (*{extension})|*{extension}",
            DefaultExt = extension.TrimStart('.'),
            FileName = Path.GetFileNameWithoutExtension(targetArchive) + ".texture" + extension,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (ContainerSession.PathsEqual(dialog.FileName, targetArchive))
        {
            _status.Text = "输出档案不能覆盖目标档案。";
            _status.ForeColor = Color.Firebrick;
            return;
        }
        UseWaitCursor = true;
        Cursor = Cursors.WaitCursor;
        try
        {
            var error = _writeBack(_existing, dialog.FileName);
            if (!string.IsNullOrWhiteSpace(error))
            {
                _status.Text = error;
                _status.ForeColor = Color.Firebrick;
                return;
            }
            _status.Text = "已导出贴图档案副本：" + dialog.FileName;
            _status.ForeColor = Color.ForestGreen;
        }
        catch (Exception error)
        {
            _status.Text = "导出失败：" + error.Message;
            _status.ForeColor = Color.Firebrick;
        }
        finally
        {
            UseWaitCursor = false;
            Cursor = Cursors.Default;
        }
    }

    private void UpdateButtons()
    {
        _apply.Enabled = _candidateAccepted;
        _remove.Enabled = _existing != null || _current.IsProjectOverride;
        var compression = _existing?.TargetCompression?.ToUpperInvariant();
        var targetArchive = _existing?.TargetArchivePath;
        _writeBackButton.Enabled = _writeBack != null && _existing != null && _existing.HasTarget &&
            !string.IsNullOrWhiteSpace(targetArchive) && File.Exists(targetArchive) &&
            compression is "TEXCOMP_DXTC1" or "TEXCOMP_DXTC3" or "TEXCOMP_DXTC5";
    }

    private string SetPreview(PictureBox box, byte[] png)
    {
        var old = box.Image;
        box.Image = null;
        old?.Dispose();
        if (png == null || png.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            using var image = Image.FromStream(stream);
            box.Image = new Bitmap(image);
            return null;
        }
        catch (Exception error)
        {
            return error.Message;
        }
    }

    private static (int Width, int Height)? TryGetPngSize(byte[] png)
    {
        if (png == null || png.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            using var image = Image.FromStream(stream);
            return (image.Width, image.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var current = _currentPreview.Image;
            _currentPreview.Image = null;
            current?.Dispose();
            var candidate = _candidatePreview.Image;
            _candidatePreview.Image = null;
            candidate?.Dispose();
        }
        base.Dispose(disposing);
    }
}
