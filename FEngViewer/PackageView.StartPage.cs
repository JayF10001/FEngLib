using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace FEngViewer;

public partial class PackageView
{
    private Panel _startPage;
    private Label _startDescription;
    private Label _recentProjectsEmpty;
    private ListBox _recentProjectsList;
    private Button _openContainerButton;
    private Font _recentNameFont;

    private void BuildStartPage()
    {
        _startPage = new Panel { Dock = DockStyle.Fill, Visible = false };
        var frame = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3,
            Padding = new Padding(28, 0, 28, 0)
        };
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var contentWidth = new ColumnStyle(SizeType.Absolute, 0);
        frame.ColumnStyles.Add(contentWidth);
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        frame.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
        frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        frame.RowStyles.Add(new RowStyle(SizeType.Percent, 76));
        frame.SizeChanged += (_, _) =>
        {
            contentWidth.Width = Math.Min(LogicalToDeviceUnits(820), Math.Max(0, frame.ClientSize.Width - frame.Padding.Horizontal));
        };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 5, Margin = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < content.RowCount; row++)
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label { Text = "欢迎使用 FEngEditor", AutoSize = true, Margin = Padding.Empty };
        SetStartFont(title, 28, FontStyle.Bold);
        _startDescription = new Label
        {
            Text = "适用于 EA Black Box《极品飞车》系列的 FNG 界面与游戏资源编辑器",
            AutoSize = true, Margin = new Padding(0, 8, 0, 0)
        };

        var headerText = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 2, Margin = Padding.Empty
        };
        headerText.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        headerText.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerText.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerText.Controls.Add(title, 0, 0);
        headerText.Controls.Add(_startDescription, 0, 1);

        var header = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 24)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(76)));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var startIcon = LoadStartIcon();
        var iconBox = new PictureBox
        {
            Size = new Size(LogicalToDeviceUnits(64), LogicalToDeviceUnits(64)),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Margin = new Padding(0, LogicalToDeviceUnits(4), LogicalToDeviceUnits(12), 0)
        };
        if (startIcon != null)
        {
            iconBox.Image = startIcon;
            iconBox.Disposed += (_, _) => iconBox.Image?.Dispose();
        }
        else
        {
            iconBox.Visible = false;
            header.ColumnStyles[0].Width = 0;
        }
        header.Controls.Add(iconBox, 0, 0);
        header.Controls.Add(headerText, 1, 0);
        content.Controls.Add(header, 0, 0);

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _openContainerButton = StartAction("打开容器或工程…",
            "打开 LZC / BUN 容器或 .fengproj 工程，编辑 FNG、替换贴图并导出容器。",
            () => ChooseContainer(OpenContainerProject, includeProject: true));
        _openContainerButton.Margin = new Padding(0, 0, 8, 0);

        var openFng = StartAction("打开 FNG 文件…",
            "直接编辑单个 FNG 文件，保存时写回原文件。", OpenFilesDialog);
        openFng.Margin = new Padding(8, 0, 0, 0);

        actions.Controls.Add(_openContainerButton, 0, 0);
        actions.Controls.Add(openFng, 1, 0);
        content.Controls.Add(actions, 0, 2);

        var recentTitle = new Label
        {
            Text = "最近打开", AutoSize = true, Margin = new Padding(0, 32, 0, 10)
        };
        SetStartFont(recentTitle, 14, FontStyle.Bold);
        content.Controls.Add(recentTitle, 0, 3);
        var recent = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        _recentProjectsEmpty = new Label
        {
            Dock = DockStyle.Fill, Padding = new Padding(16, 0, 16, 0),
            Text = "打开过的工程、容器和独立 FNG 会显示在这里。", TextAlign = ContentAlignment.MiddleLeft
        };
        _recentProjectsList = new ListBox
        {
            Dock = DockStyle.Fill, AccessibleName = "最近打开", IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed, BorderStyle = BorderStyle.None
        };
        _recentNameFont = new Font(Font, FontStyle.Bold);
        _recentProjectsList.Disposed += (_, _) => _recentNameFont.Dispose();
        _recentProjectsList.DrawItem += DrawRecentProject;
        _recentProjectsList.DoubleClick += (_, _) => OpenSelectedRecentProject();
        _recentProjectsList.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            OpenSelectedRecentProject();
        };
        recent.Controls.Add(_recentProjectsList);
        recent.Controls.Add(_recentProjectsEmpty);
        content.Controls.Add(recent, 0, 4);
        frame.Controls.Add(content, 1, 1);
        _startPage.Controls.Add(frame);
        _pageHost.Controls.Add(_startPage);
        _startPage.DpiChangedAfterParent += (_, _) => RefreshRecentProjects();
    }

    private static Image LoadStartIcon()
    {
        try
        {
            using var stream = typeof(PackageView).Assembly.GetManifestResourceStream(
                "FEngViewer.Assets.FEngEditor.ico");
            if (stream == null) return null;
            using var icon = new Icon(stream, 256, 256);
            return icon.ToBitmap();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void SetStartFont(Control control, float size, FontStyle style)
    {
        var font = new Font(Font.FontFamily, size, style);
        control.Font = font;
        control.Disposed += (_, _) => font.Dispose();
    }

    private Button StartAction(string title, string description, Action open)
    {
        var button = new StartActionButton
        {
            Dock = DockStyle.Fill,
            Height = LogicalToDeviceUnits(104),
            Title = title,
            Description = description,
            Text = title,
            AccessibleName = title,
            AccessibleDescription = description,
            Margin = Padding.Empty
        };
        button.Click += (_, _) => open();
        return button;
    }

    private void ApplyStartPageTheme()
    {
        _startDescription.ForeColor = _recentProjectsEmpty.ForeColor = EditorTheme.Muted;
        _recentProjectsEmpty.BackColor = _recentProjectsList.BackColor = EditorTheme.Input;
        _openContainerButton.BackColor = EditorTheme.Input;
        _openContainerButton.FlatAppearance.BorderColor = EditorTheme.Accent;
        _recentProjectsList.Invalidate();
    }

    private void RefreshRecentProjects()
    {
        SortRecentEntries();
        if (!_recentProjectsList.Items.Cast<RecentEntry>().SequenceEqual(_recentEntries))
        {
            _recentProjectsList.Items.Clear();
            _recentProjectsList.Items.AddRange(_recentEntries.ToArray());
        }
        var count = _recentProjectsList.Items.Count;
        _recentProjectsList.ItemHeight = Math.Max(LogicalToDeviceUnits(52), _recentProjectsList.Font.Height * 2 + 8);
        _recentProjectsList.Parent.Height = count == 0 ? LogicalToDeviceUnits(64) :
            Math.Min(count, 4) * _recentProjectsList.ItemHeight;
        _recentProjectsList.Visible = count > 0;
        _recentProjectsEmpty.Visible = count == 0;
    }

    private void DrawRecentProject(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _recentProjectsList.Items[e.Index] is not RecentEntry entry) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? SystemColors.Highlight : EditorTheme.Input);
        using var border = new Pen(EditorTheme.Border);
        e.Graphics.FillRectangle(background, e.Bounds);
        e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

        var area = Rectangle.Inflate(e.Bounds, -LogicalToDeviceUnits(16), 0);
        var top = area with { Height = area.Height / 2 };
        var bottom = top with { Y = top.Bottom };
        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, entry.DisplayName, _recentNameFont, top,
            selected ? SystemColors.HighlightText : EditorTheme.Text, flags | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, $"{entry.KindLabel} · {entry.Path}", e.Font, bottom,
            selected ? SystemColors.HighlightText : EditorTheme.Muted, flags | TextFormatFlags.PathEllipsis);
        e.DrawFocusRectangle();
    }

    private void OpenSelectedRecentProject()
    {
        if (_recentProjectsList.SelectedItem is RecentEntry entry) OpenRecentEntry(entry);
    }
}
