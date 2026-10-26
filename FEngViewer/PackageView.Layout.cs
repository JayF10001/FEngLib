using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace FEngViewer;

/// <summary>
/// Split layout shell: left navigation, centre preview and right property panel are all resizable,
/// and the widths are remembered between sessions. The GL control is created once by the designer
/// and only ever re-parented here during construction, so no render context is rebuilt later.
/// </summary>
public partial class PackageView
{
    private Panel _host;
    private SplitContainer _splitOuter;
    private SplitContainer _innerSplit;
    private TableLayoutPanel _leftToolbar;
    private TableLayoutPanel _searchRow;
    private FlowLayoutPanel _fileExtras;
    private FlowLayoutPanel _treeExtras;
    private ComboBox _fileSortCombo;
    private Button _expandTreeButton;
    private Panel _propertyHeader;
    private Button _collapseRightButton;
    private Button _expandRightButton;
    private Label _propertyEmptyLabel;
    private Label _previewScaleLabel;
    private ComboBox _previewScaleCombo;
    private TableLayoutPanel _canvasFooter;
    private ToolTip _layoutToolTip;

    private bool _layoutRestored;
    private bool _rightCollapsed;
    private int _rightWidth = ViewerSettings.DefaultRightWidth;
    private int _leftWidth = ViewerSettings.DefaultLeftWidth;
    private bool _applyingLayout;

    private void BuildShell()
    {
        SuspendLayout();

        _layoutToolTip = new ToolTip { ShowAlways = true, InitialDelay = 400, ReshowDelay = 200 };

        _host = new Panel { Dock = DockStyle.Fill, Name = "workspaceHost" };
        Controls.Add(_host);
        _host.BringToFront();

        _splitOuter = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = ViewerSettings.SplitterWidth,
            Name = "splitOuter",
            TabStop = false
        };
        _innerSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel2,
            SplitterWidth = ViewerSettings.SplitterWidth,
            Name = "splitInner",
            TabStop = false
        };
        _splitOuter.Panel2.Controls.Add(_innerSplit);
        _host.Controls.Add(_splitOuter);
        ApplyMinimumSizes();
        // SplitterMoving only fires for real drags, so programmatic clamps never overwrite the preference.
        _splitOuter.SplitterMoving += (_, e) =>
        {
            if (!_applyingLayout && e.SplitX > 0) _leftWidth = e.SplitX;
        };
        _innerSplit.SplitterMoving += (_, e) =>
        {
            if (_applyingLayout || _innerSplit.Panel2Collapsed || e.SplitX <= 0) return;
            _rightWidth = Math.Max(ViewerSettings.MinRightWidth,
                _innerSplit.Width - _innerSplit.SplitterWidth - e.SplitX);
        };

        BuildCanvasArea();
        BuildPropertyArea();

        ResumeLayout();
    }

    /// <summary>
    /// Panel minimums can only be applied once the split has a usable width; WinForms throws when
    /// the current splitter distance would fall outside the new bounds. Retried on load.
    /// </summary>
    private void ApplyMinimumSizes()
    {
        if (_splitOuter == null || _innerSplit == null) return;
        var leftMinimum = DesiredLeftMinimum();
        try
        {
            _innerSplit.Panel1MinSize = ViewerSettings.MinCenterWidth;
            _innerSplit.Panel2MinSize = ViewerSettings.MinRightWidth;
        }
        catch (Exception)
        {
            // Layout not settled yet.
        }

        try
        {
            _splitOuter.Panel1MinSize = leftMinimum;
            _splitOuter.Panel2MinSize = ViewerSettings.MinCenterWidth + ViewerSettings.MinRightWidth + ViewerSettings.SplitterWidth;
        }
        catch (Exception)
        {
            // Layout not settled yet.
        }

        var minimumWidth = leftMinimum + ViewerSettings.MinCenterWidth + ViewerSettings.MinRightWidth
                           + 2 * ViewerSettings.SplitterWidth + 48;
        MinimumSize = new Size(Math.Max(820, minimumWidth), Math.Max(560, MinimumSize.Height));
    }

    /// <summary>
    /// The mode buttons sit side by side, so the left column must be wide enough for the longest
    /// label at the current font (a DPI-scaled UI needs more room than 180px).
    /// </summary>
    private int DesiredLeftMinimum()
    {
        var widest = Math.Max(
            TextRenderer.MeasureText("FNG 文件", Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix).Width,
            TextRenderer.MeasureText("当前对象树", Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix).Width);
        var button = widest + Math.Max(20, LineHeight + 6);
        return Math.Clamp(button * 2 + 10, ViewerSettings.MinLeftWidth, 460);
    }

    private int LineHeight => Math.Max(12, Font.Height);

    /// <summary>
    /// Heights that are not driven by an AutoSize layout follow the font, so a scaled UI does not
    /// clip the tab strip, footer, status bar or the property header.
    /// </summary>
    private void ApplyMetricHeights()
    {
        if (_tabBar != null) _tabBar.Height = _tabBar.PreferredHeight;
        if (_statusBar != null) _statusBar.Height = LineHeight + 9;
        if (_canvasFooter != null) _canvasFooter.Height = LineHeight + 11;
        if (_propertyHeader != null) _propertyHeader.Height = LineHeight + 11;
        if (_expandRightButton != null) _expandRightButton.Width = Math.Max(22, LineHeight + 8);
        if (_collapseRightButton != null) _collapseRightButton.Width = Math.Max(24, LineHeight + 12);
        if (_previewScaleCombo != null) _previewScaleCombo.Width = Math.Max(120, LineHeight * 9);
        if (_fileSortCombo != null) _fileSortCombo.Width = Math.Max(90, LineHeight * 6);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ApplyMetricHeights();
        ApplyMinimumSizes();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyMetricHeights();
        ApplyMinimumSizes();
    }

    private void BuildCanvasArea()
    {
        // The fixed-ratio canvas is centred inside this surface by GLRenderControl; the GL control
        // itself stays in place when the ratio changes.
        viewOutput.Dock = DockStyle.Fill;
        viewOutput.MinimumSize = Size.Empty;
        viewOutput.MaximumSize = Size.Empty;
        viewOutput.Margin = Padding.Empty;
        viewOutput.AutoSize = false;
        viewOutput.PreviewViewportChanged += (_, _) => UpdatePreviewScaleLabel();

        labelCoordDisplay.AutoSize = false;
        labelCoordDisplay.Dock = DockStyle.Fill;
        labelCoordDisplay.TextAlign = ContentAlignment.MiddleLeft;
        labelCoordDisplay.Margin = Padding.Empty;
        labelCoordDisplay.Text = "FE: (—, —)";

        _previewScaleLabel = new Label
        {
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(8, 0, 8, 0),
            Text = "640×480"
        };
        _previewScaleCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0),
            Anchor = AnchorStyles.Right
        };
        _previewScaleCombo.Items.AddRange(new object[] { "4:3", "16:9", "21:9" });
        _previewScaleCombo.SelectedIndex = 0;
        _previewScaleCombo.SelectedIndexChanged += (_, _) => ApplyPreviewAspect();

        _canvasFooter = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(4, 0, 4, 0),
            Margin = Padding.Empty
        };
        _canvasFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _canvasFooter.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _canvasFooter.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _canvasFooter.Controls.Add(labelCoordDisplay, 0, 0);
        _canvasFooter.Controls.Add(_previewScaleLabel, 1, 0);
        _canvasFooter.Controls.Add(_previewScaleCombo, 2, 0);

        _expandRightButton = new Button
        {
            Dock = DockStyle.Right,
            Text = "◀",
            FlatStyle = FlatStyle.Flat,
            Visible = false,
            TabStop = false,
            Margin = Padding.Empty
        };
        _expandRightButton.FlatAppearance.BorderSize = 0;
        _expandRightButton.Click += (_, _) => SetRightCollapsed(false);

        _innerSplit.Panel1.Controls.Add(viewOutput);
        _innerSplit.Panel1.Controls.Add(_canvasFooter);
        _innerSplit.Panel1.Controls.Add(_expandRightButton);
        _layoutToolTip.SetToolTip(_previewScaleCombo, "画布比例：保持元素比例，宽屏向左右扩展；画布整体等比缩放并居中显示");
        _layoutToolTip.SetToolTip(_expandRightButton, "展开属性面板");
    }

    private void BuildPropertyArea()
    {
        _propertyHeader = new Panel { Dock = DockStyle.Top, Margin = Padding.Empty };
        var propertyTitle = new Label
        {
            Text = "Properties",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            Font = new Font(Font, FontStyle.Bold)
        };
        _collapseRightButton = new Button
        {
            Dock = DockStyle.Right,
            Text = "▶",
            FlatStyle = FlatStyle.Flat,
            TabStop = false
        };
        _collapseRightButton.FlatAppearance.BorderSize = 0;
        _collapseRightButton.Click += (_, _) => SetRightCollapsed(true);
        _propertyHeader.Controls.Add(propertyTitle);
        _propertyHeader.Controls.Add(_collapseRightButton);
        _propertyHeader.Paint += (_, e) =>
            e.Graphics.DrawLine(SystemPens.ControlDark, 0, _propertyHeader.Height - 1, _propertyHeader.Width, _propertyHeader.Height - 1);

        objectPropertyGrid.Dock = DockStyle.Fill;
        objectPropertyGrid.Margin = Padding.Empty;

        _propertyEmptyLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "未选中对象\r\n在左侧「当前对象树」中选择节点以编辑属性",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SystemColors.GrayText,
            BackColor = SystemColors.Window
        };

        groupBox1.Dock = DockStyle.Bottom;
        groupBox1.Margin = new Padding(0, 2, 0, 0);

        _innerSplit.Panel2.Controls.Add(objectPropertyGrid);
        _innerSplit.Panel2.Controls.Add(_propertyEmptyLabel);
        _innerSplit.Panel2.Controls.Add(_propertyHeader);
        _innerSplit.Panel2.Controls.Add(groupBox1);
        _propertyEmptyLabel.BringToFront();

        _layoutToolTip.SetToolTip(_collapseRightButton, "折叠属性面板，把空间让给预览");
    }

    /// <summary>Called after the left panel exists; adds the mode/status marker legend.</summary>
    private void FinishLayoutToolTips()
    {
        if (_layoutToolTip == null) return;
        if (_filesModeButton != null)
            _layoutToolTip.SetToolTip(_filesModeButton,
                "工作区 FNG 列表。状态点：灰=未打开，绿=已打开，蓝=当前文档，红=文件缺失");
        if (_treeModeButton != null)
            _layoutToolTip.SetToolTip(_treeModeButton, "当前文档的对象树");
    }

    #region Preview aspect ratio

    private void ApplyPreviewAspect()
    {
        if (_previewScaleCombo == null) return;
        viewOutput.PreviewAspect = _previewScaleCombo.SelectedIndex switch
        {
            1 => PreviewAspectRatio.Widescreen16x9,
            2 => PreviewAspectRatio.Ultrawide21x9,
            _ => PreviewAspectRatio.Standard4x3
        };
        UpdatePreviewScaleLabel();
    }

    private void UpdatePreviewScaleLabel()
    {
        if (_previewScaleLabel == null) return;
        _previewScaleLabel.Text = PreviewLayout.Describe(viewOutput.PreviewViewport, viewOutput.PreviewAspect);
    }

    private void ApplyPreviewAspectFromState(string name)
    {
        var mode = PreviewLayout.ParseMode(name);
        if (_previewScaleCombo != null)
            _previewScaleCombo.SelectedIndex = (int)mode;
        viewOutput.PreviewAspect = mode;
        UpdatePreviewScaleLabel();
    }

    #endregion

    #region Right panel collapse

    private void SetRightCollapsed(bool collapsed, bool rememberWidth = true)
    {
        if (_innerSplit == null || _rightCollapsed == collapsed) return;
        if (collapsed)
        {
            if (rememberWidth && !_innerSplit.Panel2Collapsed)
                _rightWidth = Math.Max(ViewerSettings.MinRightWidth, CurrentRightWidth());
            _innerSplit.Panel2Collapsed = true;
            _expandRightButton.Visible = true;
        }
        else
        {
            _innerSplit.Panel2Collapsed = false;
            _expandRightButton.Visible = false;
            SetRightWidth(_rightWidth);
        }

        _rightCollapsed = collapsed;
        ClampSplittersToWidth();
    }

    /// <summary>
    /// WinForms keeps the panel sizes when a SplitContainer shrinks, so the right panel can be pushed
    /// outside the window. Shrink the splitters back to their preferred widths whenever they fit.
    /// </summary>
    private void ClampSplittersToWidth()
    {
        if (_applyingLayout || _splitOuter == null || _innerSplit == null) return;
        _applyingLayout = true;
        try
        {
            var width = _splitOuter.Width;
            if (width > 0)
            {
                var maximum = width - _splitOuter.Panel2MinSize - _splitOuter.SplitterWidth;
                if (maximum < _splitOuter.Panel1MinSize) maximum = _splitOuter.Panel1MinSize;
                var target = Math.Clamp(_leftWidth, _splitOuter.Panel1MinSize, maximum);
                if (_splitOuter.SplitterDistance != target) _splitOuter.SplitterDistance = target;
            }

            if (!_innerSplit.Panel2Collapsed) SetRightWidth(_rightWidth);
        }
        catch (Exception)
        {
            // A size that cannot be settled yet is retried on the next resize.
        }
        finally
        {
            _applyingLayout = false;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ClampSplittersToWidth();
    }

    private int CurrentRightWidth()
    {
        var available = _innerSplit.Width - _innerSplit.SplitterWidth;
        return Math.Max(0, available - _innerSplit.SplitterDistance);
    }

    private void SetRightWidth(int width)
    {
        if (_innerSplit == null || _innerSplit.Panel2Collapsed) return;
        var available = _innerSplit.Width - _innerSplit.SplitterWidth;
        if (available <= 0) return;
        var minimum = _innerSplit.Panel2MinSize;
        var maximum = available - _innerSplit.Panel1MinSize;
        if (maximum < minimum) return;
        var target = Math.Clamp(width, minimum, maximum);
        try
        {
            _innerSplit.SplitterDistance = available - target;
        }
        catch (InvalidOperationException)
        {
            // Size not settled yet; a later restore pass applies it.
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    #endregion

    #region Layout persistence

    protected override void OnLoad(EventArgs e)
    {
        ApplyMinimumSizes();
        if (_splitOuter != null && _splitOuter.Width > 0)
            ApplySavedLayout();
        base.OnLoad(e);
        if (!_layoutRestored) Shown += (_, _) => ApplySavedLayout();
    }

    private void ApplySavedLayout()
    {
        if (_layoutRestored || _splitOuter == null) return;
        ApplyMetricHeights();
        ApplyMinimumSizes();
        if (_splitOuter.Width <= 0 || _innerSplit.Width <= 0) return;
        _layoutRestored = true;

        try
        {
            var state = ViewerSettings.Clamp(ViewerSettings.Load(), _splitOuter.Width);

            _leftWidth = Math.Max(state.LeftWidth, DesiredLeftMinimum());
            _rightWidth = state.RightWidth;
            ClampSplittersToWidth();
            if (state.RightCollapsed) SetRightCollapsed(true, rememberWidth: false);

            ApplyPreviewAspectFromState(state.PreviewAspect);
            SetLeftMode(state.LeftMode == "tree" ? WorkspaceLeftMode.ObjectTree : WorkspaceLeftMode.Files);
        }
        catch (Exception)
        {
            // A broken or impossible saved layout must never stop the editor from opening.
        }

        UpdatePreviewScaleLabel();
    }

    private ViewerLayoutState CaptureLayoutState()
    {
        var state = new ViewerLayoutState
        {
            LeftWidth = _leftWidth,
            RightCollapsed = _rightCollapsed,
            RightWidth = _rightWidth,
            PreviewAspect = PreviewLayout.ModeName(viewOutput.PreviewAspect),
            LeftMode = _leftMode == WorkspaceLeftMode.Files ? "files" : "tree"
        };
        // Save the preferred widths, not the ones clamped by the current (possibly small) window; the
        // restore path clamps them again to whatever window the next session opens with.
        return ViewerSettings.Normalize(state, 0);
    }

    private void SaveLayoutState()
    {
        try
        {
            ViewerSettings.Save(CaptureLayoutState());
        }
        catch (Exception)
        {
            // Saving preferences is best-effort.
        }
    }

    #endregion

    /// <summary>Selects the property target and shows/hides the empty-state overlay.</summary>
    private void SetPropertyTarget(object target)
    {
        objectPropertyGrid.SelectedObject = target;
        if (_propertyEmptyLabel != null) _propertyEmptyLabel.Visible = target == null;
    }
}
