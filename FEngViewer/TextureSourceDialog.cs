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
/// Read-only view of the texture that the renderer actually resolved for one FNG resource.
/// When the hit came from a TPK, the full catalog is shown and the exact entry is selected by
/// archive path, TPK ordinal and texture ordinal; no archive or project data is written.
/// </summary>
internal sealed class TextureSourceDialog : Form
{
    private readonly TextureLookupResult _resolution;
    private readonly ResourceRequest _request;
    private readonly ArchiveTextureSource _source;
    private readonly string _projectSourcePath;
    private readonly string _documentName;
    private readonly string _objectName;

    private readonly Label _headline = new()
    {
        AutoSize = true,
        Font = new Font(DefaultFont, FontStyle.Bold),
        Margin = new Padding(0, 0, 0, 6)
    };
    private readonly Label _summary = new()
    {
        AutoSize = true,
        MaximumSize = new Size(1000, 0),
        Margin = new Padding(0, 0, 0, 4)
    };
    private readonly Label _sourceKind = new()
    {
        AutoSize = true,
        Font = new Font(DefaultFont, FontStyle.Bold),
        Margin = new Padding(0, 2, 0, 0)
    };
    private readonly Label _externalWarning = new()
    {
        AutoSize = true,
        MaximumSize = new Size(1000, 0),
        ForeColor = Color.FromArgb(190, 100, 20),
        Visible = false,
        Margin = new Padding(0, 2, 0, 0)
    };
    private readonly TextBox _filter = new()
    {
        Dock = DockStyle.Fill,
        PlaceholderText = "按贴图名、TPK、BinKey 或档案过滤…",
        Margin = new Padding(0, 0, 6, 4)
    };
    private readonly Label _filterCount = new()
    {
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleRight,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 6, 0, 0)
    };
    private readonly Label _listHint = new()
    {
        AutoSize = true,
        MaximumSize = new Size(760, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 0, 0, 4)
    };
    private readonly ListView _entries = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        ShowGroups = true,
        ShowItemToolTips = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Margin = Padding.Empty
    };
    private readonly PictureBox _preview = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.FromArgb(210, 210, 210),
        BackgroundImageLayout = ImageLayout.Tile,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 0, 0, 4)
    };
    private readonly Label _previewStatus = new()
    {
        AutoSize = true,
        MaximumSize = new Size(560, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 0, 0, 6)
    };
    private readonly TextBox _details = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        WordWrap = true,
        ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.None,
        BackColor = SystemColors.Window,
        Margin = Padding.Empty,
        TabStop = false
    };
    private readonly SplitContainer _split = new()
    {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Vertical,
        SplitterWidth = 6,
        Margin = Padding.Empty
    };
    private readonly Panel _messagePanel = new() { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(12) };
    private readonly Label _message = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        MaximumSize = new Size(940, 0)
    };
    private readonly Label _footer = new()
    {
        AutoSize = true,
        MaximumSize = new Size(880, 0),
        ForeColor = SystemColors.GrayText,
        Text = "预览棋盘格表示透明区域。只读浏览：不会修改档案、工程或未保存修改。TPK 像素按所选条目解码，并复用已有 PNG 缓存。"
    };

    private readonly List<BinaryTextureInfo> _allEntries = new();
    private BinaryTextureInfo _selectedInfo;
    private bool _hasList;
    private bool _populating;

    public TextureSourceDialog(TextureLookupResult resolution, ResourceRequest request,
        ArchiveTextureSource source, string projectSourcePath, string documentName, string objectName)
    {
        _resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _source = source;
        _projectSourcePath = projectSourcePath;
        _documentName = documentName;
        _objectName = objectName;

        Text = "贴图来源 · " + (request.Name ?? $"0x{request.ID:X8}");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = false;
        MinimizeBox = false;
        ClientSize = new Size(1280, 760);
        MinimumSize = new Size(900, 580);
        KeyPreview = true;

        BuildLayout();
        EditorTheme.Apply(this);
        _entries.BackColor = EditorTheme.Input;
        _entries.ForeColor = EditorTheme.Text;
        _details.BackColor = EditorTheme.Input;
        _details.ForeColor = EditorTheme.Text;
        _preview.BackColor = Color.FromArgb(210, 210, 210);
        _preview.BackgroundImage = PreviewBackground.Checkerboard;
        _footer.ForeColor = EditorTheme.Muted;
        _filterCount.ForeColor = EditorTheme.Muted;
        _listHint.ForeColor = EditorTheme.Muted;
        _previewStatus.ForeColor = EditorTheme.Muted;
        Populate();
    }

    private void BuildLayout()
    {
        _entries.Columns.Add("档案", 90);
        _entries.Columns.Add("TPK", 160);
        _entries.Columns.Add("贴图名", 170);
        _entries.Columns.Add("BinKey", 96);
        _entries.Columns.Add("尺寸", 72);
        _entries.Columns.Add("格式", 120);
        _entries.Columns.Add("条目", 58);

        _summary.Text = $"对象：{_objectName}\r\n" +
                        $"FNG：{_documentName}\r\n" +
                        $"FNG 资源请求：{_request.Name ?? "(未命名)"} · FNG 资源请求 ID 0x{_request.ID:X8}\r\n" +
                        $"结果：{StatusText()}";
        _sourceKind.Text = "来源类型：" + SourceKindText();
        _sourceKind.ForeColor = SourceKindColor();

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty
        };
        header.Controls.Add(_headline, 0, 0);
        header.Controls.Add(_summary, 0, 1);
        header.Controls.Add(_sourceKind, 0, 2);
        header.Controls.Add(_externalWarning, 0, 3);

        var filterRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filterRow.Controls.Add(_filter, 0, 0);
        filterRow.Controls.Add(_filterCount, 1, 0);

        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0, 0, 8, 0),
            Margin = Padding.Empty
        };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Controls.Add(_listHint, 0, 0);
        left.Controls.Add(filterRow, 0, 1);
        left.Controls.Add(_entries, 0, 2);
        _split.Panel1.Controls.Add(left);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8, 0, 0, 0),
            Margin = Padding.Empty
        };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        right.Controls.Add(_preview, 0, 0);
        right.Controls.Add(_previewStatus, 0, 1);
        right.Controls.Add(_details, 0, 2);
        _split.Panel2.Controls.Add(right);

        _messagePanel.Controls.Add(_message);
        var content = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        content.Controls.Add(_split);
        content.Controls.Add(_messagePanel);

        var close = new Button { Text = "关闭", AutoSize = true, DialogResult = DialogResult.Cancel };
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 8, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_footer, 0, 0);
        footer.Controls.Add(close, 1, 0);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10),
            Margin = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(content, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
        CancelButton = close;

        _filter.TextChanged += (_, _) => PopulateList();
        _entries.SelectedIndexChanged += (_, _) =>
        {
            if (_populating) return;
            if (_entries.SelectedItems.Count == 0) return;
            if (_entries.SelectedItems[0].Tag is BinaryTextureInfo info) ShowEntry(info);
        };
    }

    private void Populate()
    {
        _headline.Text = "贴图来源 · " + (_request.Name ?? $"0x{_request.ID:X8}");

        if (_resolution.Entry != null)
        {
            var catalog = _source?.GetCatalog(_resolution.Entry.ArchivePath);
            if (catalog != null) _allEntries.AddRange(catalog.Textures);
        }
        else if (_resolution.Status == TextureLookupStatus.Ambiguous)
        {
            _allEntries.AddRange(_resolution.Candidates
                .Where(candidate => candidate.Entry != null)
                .Select(candidate => candidate.Entry));
        }

        _hasList = _allEntries.Count > 0;
        _listHint.Visible = _hasList;
        if (_hasList)
        {
            _listHint.Text = _resolution.Status == TextureLookupStatus.Ambiguous
                ? "名称未精确匹配；以下为候选条目（只读）。同名条目请用 BinKey 和条目序号区分。"
                : _resolution.MatchingEntryCount > 1
                    ? $"当前对象命中已定位；同名条目 {_resolution.MatchingEntryCount} 个，按档案顺序命中第 1 个。下方为同一 TPK 的全部条目（只读）。"
                    : "当前对象命中已定位；下方为同一 TPK 的全部条目（只读）。可用名称 / TPK / BinKey 过滤。";
            _listHint.ForeColor = _resolution.Status == TextureLookupStatus.Ambiguous
                ? Color.FromArgb(190, 100, 20)
                : EditorTheme.Muted;
        }
        if (_hasList)
        {
            _selectedInfo = _resolution.Entry ?? _allEntries.FirstOrDefault();
            PopulateList();
            _split.Panel1Collapsed = false;
            _messagePanel.Visible = false;
            _split.Visible = true;
            SelectInfo(_selectedInfo);
            if (_selectedInfo != null) ShowEntry(_selectedInfo);
        }
        else if (_resolution.Entry != null)
        {
            // The hit has identity metadata but the catalog is no longer available. Still show the
            // exact entry details and its cached/decoded pixels; do not invent a catalog list.
            _split.Panel1Collapsed = true;
            _messagePanel.Visible = false;
            _split.Visible = true;
            _selectedInfo = _resolution.Entry;
            ShowEntry(_selectedInfo);
        }
        else if (_resolution.IsProjectOverride)
        {
            _split.Panel1Collapsed = true;
            _messagePanel.Visible = false;
            _split.Visible = true;
            ShowProjectOverride();
        }
        else if (_resolution.IsFound)
        {
            _split.Panel1Collapsed = true;
            _messagePanel.Visible = false;
            _split.Visible = true;
            ShowLoosePng();
        }
        else
        {
            _split.Visible = false;
            _messagePanel.Visible = true;
            _message.Text = BuildUnresolvedMessage();
            _message.ForeColor = _resolution.Status == TextureLookupStatus.Ambiguous
                ? Color.FromArgb(190, 100, 20)
                : Color.Firebrick;
        }

        var displayedEntry = _resolution.Entry ?? _selectedInfo;
        var external = displayedEntry != null && IsExternal(displayedEntry.ArchivePath);
        _externalWarning.Visible = external;
        if (external)
        {
            _externalWarning.Text = "外部共享包：这是当前容器工程之外的档案。查看只读，当前工程、编辑标签和未保存修改保持不变。";
            _externalWarning.ForeColor = Color.FromArgb(190, 100, 20);
        }
        _sourceKind.Text = "来源类型：" + SourceKindText();
        _sourceKind.ForeColor = SourceKindColor();
    }

    private void PopulateList()
    {
        var selected = _selectedInfo;
        var filter = _filter.Text?.Trim() ?? "";
        _populating = true;
        _entries.BeginUpdate();
        try
        {
            _entries.Items.Clear();
            _entries.Groups.Clear();
            var groups = _allEntries
                .GroupBy(info => (info.ArchivePath, info.TpkName, info.TpkOrdinal))
                .OrderBy(group => group.Key.ArchivePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.Key.TpkOrdinal)
                .ToArray();
            var shown = 0;
            foreach (var grouping in groups)
            {
                var group = new ListViewGroup(
                    $"{Path.GetFileName(grouping.Key.ArchivePath)} :: {grouping.Key.TpkName} #{grouping.Key.TpkOrdinal}")
                {
                    HeaderAlignment = HorizontalAlignment.Left,
                };
                var items = grouping
                    .OrderBy(info => info.TextureOrdinal)
                    .Where(info => MatchesFilter(info, filter))
                    .ToArray();
                if (items.Length == 0) continue;
                _entries.Groups.Add(group);
                foreach (var info in items)
                {
                    var item = new ListViewItem(new[]
                    {
                        Path.GetFileName(info.ArchivePath),
                        $"{info.TpkName} #{info.TpkOrdinal}",
                        info.Name,
                        $"0x{info.BinKey:X8}",
                        $"{info.Width}×{info.Height}",
                        info.Compression,
                        $"#{info.TextureOrdinal}",
                    })
                    {
                        Tag = info,
                        Group = group,
                        ToolTipText = BuildItemToolTip(info),
                    };
                    _entries.Items.Add(item);
                    shown++;
                }
            }
            _filterCount.Text = $"显示 {shown} / {_allEntries.Count}";
        }
        finally
        {
            _entries.EndUpdate();
            _populating = false;
        }

        if (selected != null) SelectInfo(selected);
        if (_entries.SelectedItems.Count == 0 && _entries.Items.Count > 0)
            _entries.Items[0].Selected = true;
        if (_entries.SelectedItems.Count > 0 && _entries.SelectedItems[0].Tag is BinaryTextureInfo current)
        {
            _selectedInfo = current;
            ShowEntry(current);
        }
    }

    private static bool MatchesFilter(BinaryTextureInfo info, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        return Contains(info.Name, filter) ||
               Contains(info.TpkName, filter) ||
               Contains(info.ArchivePath, filter) ||
               Contains(info.Compression, filter) ||
               Contains($"0x{info.BinKey:X8}", filter) ||
               Contains($"#{info.TextureOrdinal}", filter);
    }

    private static bool Contains(string text, string filter) =>
        text != null && text.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private void SelectInfo(BinaryTextureInfo info)
    {
        if (info == null) return;
        var item = FindItem(info);
        if (item == null) return;
        item.Selected = true;
        item.Focused = true;
        try { _entries.TopItem = item; }
        catch (InvalidOperationException) { /* layout is not ready yet */ }
        item.EnsureVisible();
    }

    private ListViewItem FindItem(BinaryTextureInfo info)
    {
        foreach (ListViewItem item in _entries.Items)
            if (ReferenceEquals(item.Tag, info)) return item;
        foreach (ListViewItem item in _entries.Items)
            if (item.Tag is BinaryTextureInfo candidate && SameIdentity(candidate, info)) return item;
        return null;
    }

    private static bool SameIdentity(BinaryTextureInfo left, BinaryTextureInfo right) =>
        ContainerSession.PathsEqual(left.ArchivePath, right.ArchivePath) &&
        left.TpkOrdinal == right.TpkOrdinal &&
        left.TextureOrdinal == right.TextureOrdinal &&
        left.BinKey == right.BinKey &&
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

    private static string BuildItemToolTip(BinaryTextureInfo info)
    {
        var text = $"{info.ArchivePath}\n{info.TpkName} #{info.TpkOrdinal} · 条目 #{info.TextureOrdinal}\n" +
                   $"BinKey 0x{info.BinKey:X8} · {info.Width}×{info.Height} · {info.Compression}";
        if (info.TpkBlockOffset.HasValue)
            text += $"\nTPK 块偏移 0x{info.TpkBlockOffset:X}" + (info.ArchiveHasOuterCompression ? "（逻辑偏移）" : "");
        return text;
    }

    private void ShowEntry(BinaryTextureInfo info)
    {
        if (info == null) return;
        _selectedInfo = info;
        SetDetailsText(BuildArchiveDetails(info));
        _sourceKind.Text = "来源类型：" + SourceKindText();
        _sourceKind.ForeColor = SourceKindColor();
        var external = IsExternal(info.ArchivePath);
        _externalWarning.Visible = external;
        if (external)
        {
            _externalWarning.Text = "外部共享包：这是当前容器工程之外的档案。查看只读，当前工程、编辑标签和未保存修改保持不变。";
            _externalWarning.ForeColor = Color.FromArgb(190, 100, 20);
        }

        byte[] png;
        try { png = info.GetPng(); }
        catch (Exception error)
        {
            _previewStatus.Text = "像素解码失败：" + error.Message;
            _previewStatus.ForeColor = Color.Firebrick;
            SetPreview(null);
            return;
        }

        var previewError = SetPreview(png);
        if (previewError != null)
        {
            _previewStatus.Text = "像素解码失败：" + previewError;
            _previewStatus.ForeColor = Color.Firebrick;
            return;
        }
        _previewStatus.Text = $"{info.Width} × {info.Height} · {info.Compression} · PNG {png.Length:N0} 字节";
        _previewStatus.ForeColor = SystemColors.GrayText;
    }

    private void ShowLoosePng()
    {
        SetDetailsText(BuildLooseDetails());
        _sourceKind.Text = "来源类型：" + SourceKindText();
        _sourceKind.ForeColor = SourceKindColor();
        var previewError = SetPreview(_resolution.PngData);
        if (previewError != null)
        {
            _previewStatus.Text = "像素解码失败：" + previewError;
            _previewStatus.ForeColor = Color.Firebrick;
            return;
        }
        _previewStatus.Text = $"PNG {_resolution.PngData.Length:N0} 字节";
        _previewStatus.ForeColor = SystemColors.GrayText;
    }

    private void ShowProjectOverride()
    {
        SetDetailsText(BuildProjectOverrideDetails());
        _sourceKind.Text = "来源类型：" + SourceKindText();
        _sourceKind.ForeColor = SourceKindColor();
        var previewError = SetPreview(_resolution.PngData);
        if (previewError != null)
        {
            _previewStatus.Text = "像素解码失败：" + previewError;
            _previewStatus.ForeColor = Color.Firebrick;
            return;
        }
        _previewStatus.Text = $"{_resolution.Width} × {_resolution.Height} · PNG · {_resolution.PngData.Length:N0} 字节";
        _previewStatus.ForeColor = SystemColors.GrayText;
    }

    private string BuildProjectOverrideDetails()
    {
        var lines = new List<string>
        {
            "来源类型：工程贴图覆盖（.fengproj，只影响当前 FNG）",
            "覆盖文件：" + _resolution.Source,
            "贴图名：" + (_resolution.SourceTexture ?? _request.Name ?? "(未命名)"),
            $"尺寸：{_resolution.Width} × {_resolution.Height}",
            "格式：PNG（覆盖层）",
            "",
            "FNG 资源请求：" + (_request.Name ?? "(未命名)"),
            $"FNG 资源请求 ID：0x{_request.ID:X8}",
            "说明：覆盖优先于松散 PNG、当前容器和共享档案；保存工程后生效。"
        };
        return string.Join(Environment.NewLine, lines);
    }

    private void SetDetailsText(string text)
    {
        _details.Text = text;
        // Read-only detail text must not open with an all-blue selection.
        if (_details.TextLength > 0) _details.Select(0, 0);
    }

    private string SetPreview(byte[] png)
    {
        var old = _preview.Image;
        _preview.Image = null;
        old?.Dispose();
        if (png == null || png.Length == 0) return "没有像素数据";
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            using var image = Image.FromStream(stream);
            _preview.Image = new Bitmap(image);
            return null;
        }
        catch (Exception error)
        {
            return error.Message;
        }
    }

    private string BuildArchiveDetails(BinaryTextureInfo info)
    {
        var external = IsExternal(info.ArchivePath);
        var lines = new List<string>
        {
            "来源类型：" + (external ? "外部共享包（不属于当前工程）" : "当前容器"),
            "档案：" + info.ArchivePath,
            "档案外层压缩：" + (info.ArchiveHasOuterCompression
                ? "整文件压缩（偏移基于解压后的逻辑流）"
                : "无（偏移即档案物理偏移）"),
            "",
            $"TPK：{info.TpkName} · 目录序号 #{info.TpkOrdinal}",
        };
        if (info.TpkBlockOffset.HasValue)
            lines.Add($"TPK 块偏移：0x{info.TpkBlockOffset:X}" +
                      (info.ArchiveHasOuterCompression ? "（逻辑偏移）" : ""));
        else
            lines.Add("TPK 块偏移：未确认（容器索引的 TPK 块数量与目录不一致）");
        lines.Add("");
        lines.Add("TPK 贴图名：" + info.Name);
        lines.Add($"贴图 BinKey：0x{info.BinKey:X8}");
        lines.Add($"尺寸：{info.Width} × {info.Height}");
        lines.Add("TPK 存储格式：" + info.Compression);
        lines.Add("条目序号：#" + info.TextureOrdinal);
        lines.Add("");
        lines.Add("FNG 资源请求：" + (_request.Name ?? "(未命名)"));
        lines.Add($"FNG 资源请求 ID：0x{_request.ID:X8}");
        lines.Add("说明：请求名的 .tga 只是 FNG 资源名/匹配键；实际像素来自 TPK 条目（DDS 风格压缩），预览转为 PNG。");
        if (!string.IsNullOrEmpty(_resolution.Note))
            lines.Add("命中说明：" + _resolution.Note);
        if (_resolution.Status == TextureLookupStatus.Ambiguous)
            lines.Add($"说明：名称未精确匹配，当前是 {_resolution.Candidates.Count} 个候选之一；未自动选择。");
        if (external)
            lines.Add("提示：查看外部共享包不会切换工程、关闭编辑标签或丢失未保存修改。");
        return string.Join(Environment.NewLine, lines);
    }

    private string BuildLooseDetails()
    {
        var lines = new List<string>
        {
            "来源类型：松散 PNG（textures/*.png 覆盖层，优先于档案 TPK）",
            "文件：" + _resolution.Source,
        };
        try
        {
            var file = new FileInfo(_resolution.Source);
            if (file.Exists)
            {
                lines.Add($"文件大小：{file.Length:N0} 字节");
                lines.Add("修改时间：" + file.LastWriteTime);
            }
        }
        catch (Exception)
        {
            // Size/time are informative only.
        }
        lines.Add("");
        lines.Add("松散 PNG 名称：" + (_resolution.SourceTexture ?? _resolution.RequestedName));
        lines.Add("FNG 资源请求：" + (_request.Name ?? "(未命名)"));
        lines.Add($"FNG 资源请求 ID：0x{_request.ID:X8}");
        lines.Add("命中说明：松散 PNG 优先，因此本次渲染没有使用容器或共享档案 TPK。");
        return string.Join(Environment.NewLine, lines);
    }

    private string BuildUnresolvedMessage()
    {
        var lines = new List<string>
        {
            "FNG 资源请求：" + (_request.Name ?? "(未命名)"),
            $"FNG 资源请求 ID：0x{_request.ID:X8}",
            "结果：" + StatusText(),
        };
        if (!string.IsNullOrWhiteSpace(_resolution.Message))
            lines.Add("说明：" + _resolution.Message);
        lines.Add("");
        lines.Add("查找顺序：textures/*.png（松散覆盖） → 当前容器档案 → 用户配置的共享档案。");
        if (_resolution.Status == TextureLookupStatus.Ambiguous)
        {
            lines.Add($"候选：{_resolution.Candidates.Count} 个（名称被 TPK 头截断，无法唯一确定）");
            foreach (var candidate in _resolution.Candidates.Take(12))
            {
                lines.Add($"  · {Path.GetFileName(candidate.ArchivePath)} :: {candidate.TpkName} / " +
                          $"{candidate.TextureName} / 0x{candidate.BinKey:X8} / {candidate.Width}×{candidate.Height} / {candidate.Compression}");
            }
            if (_resolution.Candidates.Count > 12) lines.Add("  · …");
        }
        if (_resolution.Status == TextureLookupStatus.Error)
            lines.Add("提示：请检查 Binary 运行库配置、档案路径和 TPK 解码错误。");
        return string.Join(Environment.NewLine, lines);
    }

    private string StatusText() => _resolution.Status switch
    {
        TextureLookupStatus.Found => _resolution.IsArchiveEntry
            ? "已找到（TPK 档案）"
            : _resolution.IsProjectOverride ? "已找到（工程贴图覆盖）" : "已找到（松散 PNG）",
        TextureLookupStatus.NoRequest => "未设置贴图资源",
        TextureLookupStatus.WrongType => "资源类型不是图片",
        TextureLookupStatus.NotFound => "未找到",
        TextureLookupStatus.Ambiguous => $"存在歧义（{_resolution.Candidates.Count} 个候选）",
        TextureLookupStatus.Error => "来源或解码错误",
        _ => _resolution.Status.ToString(),
    };

    private string SourceKindText()
    {
        if (_resolution.IsProjectOverride) return "工程贴图覆盖";
        if (_resolution.Entry != null)
            return IsExternal(_resolution.Entry.ArchivePath) ? "外部共享包" : "当前容器";
        if (_selectedInfo != null)
            return IsExternal(_selectedInfo.ArchivePath) ? "候选：外部共享包" : "候选：当前容器";
        if (_resolution.IsFound) return "松散 PNG";
        if (_resolution.Status == TextureLookupStatus.Ambiguous) return "未确定（存在歧义）";
        return "—";
    }

    private Color SourceKindColor()
    {
        if (_resolution.IsProjectOverride) return Color.MediumPurple;
        if (_resolution.Entry != null)
            return IsExternal(_resolution.Entry.ArchivePath)
                ? Color.FromArgb(190, 100, 20)
                : Color.ForestGreen;
        if (_selectedInfo != null)
            return IsExternal(_selectedInfo.ArchivePath)
                ? Color.FromArgb(190, 100, 20)
                : Color.ForestGreen;
        if (_resolution.IsFound) return Color.SteelBlue;
        if (_resolution.Status == TextureLookupStatus.Ambiguous) return Color.FromArgb(190, 100, 20);
        return Color.Firebrick;
    }

    private bool IsExternal(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || string.IsNullOrWhiteSpace(_projectSourcePath)) return false;
        try { return !ContainerSession.PathsEqual(archivePath, _projectSourcePath); }
        catch (Exception) { return true; }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            _split.Panel1MinSize = 320;
            _split.Panel2MinSize = 300;
            var available = _split.Width - _split.SplitterWidth;
            if (available > 620)
                _split.SplitterDistance = Math.Clamp((int)(available * 0.64), 320, available - 300);
        }
        catch (InvalidOperationException)
        {
            // The current window is narrower than both minimums; keep the default split.
        }
        // The initial selection happened before layout; scroll it into view now that the list has
        // its final width and height. A posted pass also covers the first handle/layout cycle.
        if (_selectedInfo != null) SelectInfo(_selectedInfo);
        _filter.Focus();
        BeginInvoke(new Action(() =>
        {
            if (!IsDisposed && _selectedInfo != null) SelectInfo(_selectedInfo);
        }));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var image = _preview.Image;
            _preview.Image = null;
            image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
