using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace FEngViewer;

/// <summary>
/// Lightweight document tab strip: name plus unsaved marker, close buttons, horizontal scrolling,
/// middle/right click actions and drag reordering.
/// It is only a header; the editor controls stay in one place and are never reparented.
/// </summary>
internal sealed class DocumentTabBar : Control
{
    internal readonly struct Item
    {
        public Item(string caption, string toolTip, bool hasError)
        {
            Caption = caption ?? "";
            ToolTip = toolTip ?? "";
            HasError = hasError;
        }

        public string Caption { get; }
        public string ToolTip { get; }
        public bool HasError { get; }
    }

    // All strip metrics follow the control font so a DPI-scaled UI does not clip the tab text.
    private int LineHeight => Math.Max(9, Font.Height);
    private int TabTop => Math.Max(2, LineHeight / 6);
    private int MinTabWidth => Math.Max(80, LineHeight * 5);
    private int MaxTabWidth => Math.Max(160, LineHeight * 12);
    private int TabPadding => Math.Max(6, LineHeight / 2);
    private int CloseBoxSize => Math.Max(12, LineHeight - 2);
    private int ScrollButtonWidth => Math.Max(16, LineHeight + 2);
    private int TabGap => Math.Max(2, LineHeight / 8);

    /// <summary>Height the strip wants for the current font.</summary>
    public int PreferredHeight => LineHeight + 16;

    private readonly List<Item> _items = new();
    private readonly List<int> _widths = new();
    private readonly List<string> _measuredCaptions = new();
    private int _selectedIndex = -1;
    private int _scrollOffset;
    private int _hoverIndex = -1;
    private bool _hoverClose;
    private int _dragIndex = -1;
    private int _dragTarget = -1;
    private bool _dragging;
    private Point _dragOrigin;
    private ToolTip _toolTip;
    private int _tipIndex = -1;

    private Rectangle _scrollLeftRect;
    private Rectangle _scrollRightRect;
    private bool _overflow;

    public DocumentTabBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        Height = 32;
        BackColor = SystemColors.Control;
    }

    public event Action<int> SelectionRequested;
    public event Action<int> CloseRequested;
    public event Action<int> ContextMenuRequested;
    public event Action<int, int> ReorderRequested;

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _hoverIndex = -1;
        MeasureTabs();
        Invalidate();
    }

    public int ItemCount => _items.Count;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var index = _items.Count == 0 ? -1 : Math.Clamp(value, -1, _items.Count - 1);
            if (_selectedIndex == index) return;
            _selectedIndex = index;
            EnsureVisible(index);
            Invalidate();
        }
    }

    public void SetItems(IReadOnlyList<Item> items)
    {
        _items.Clear();
        if (items != null) _items.AddRange(items);
        if (_selectedIndex >= _items.Count) _selectedIndex = _items.Count - 1;
        _hoverIndex = -1;
        _tipIndex = -1;
        MeasureTabs();
        EnsureVisible(_selectedIndex);
        Invalidate();
    }

    /// <summary>Refresh captions in place, keeping the current scroll position.</summary>
    public void UpdateItems(IReadOnlyList<Item> items)
    {
        var offset = _scrollOffset;
        var sameCaptions = CaptionsUnchanged(items);
        _items.Clear();
        if (items != null) _items.AddRange(items);
        if (_selectedIndex >= _items.Count) _selectedIndex = _items.Count - 1;
        if (sameCaptions) RefreshLayout();
        else MeasureTabs();
        _scrollOffset = Math.Clamp(offset, 0, MaxScrollOffset);
        Invalidate();
    }

    public void EnsureVisible(int index)
    {
        if (index < 0 || index >= _widths.Count) return;
        var left = TabLeft(index);
        var available = AvailableWidth;
        if (left < 0) _scrollOffset = Math.Max(0, _scrollOffset + left);
        else if (left + _widths[index] > available) _scrollOffset = Math.Min(MaxScrollOffset, _scrollOffset + left + _widths[index] - available);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, MaxScrollOffset);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RefreshLayout();
        Invalidate();
    }

    private int AvailableWidth
    {
        get
        {
            var reserved = _overflow ? 2 * ScrollButtonWidth : 0;
            return Math.Max(0, Width - reserved);
        }
    }

    private int ContentWidth => _widths.Sum() + Math.Max(0, _widths.Count - 1) * TabGap;

    private int MaxScrollOffset => Math.Max(0, ContentWidth - AvailableWidth);

    private int TabLeft(int index)
    {
        var x = -_scrollOffset;
        for (var i = 0; i < index && i < _widths.Count; i++) x += _widths[i] + TabGap;
        return x;
    }

    private Rectangle TabRect(int index)
    {
        var left = TabLeft(index);
        return new Rectangle(left, TabTop, _widths[index], Math.Max(18, Height - TabTop));
    }

    private Rectangle CloseRect(int index)
    {
        var tab = TabRect(index);
        return new Rectangle(tab.Right - CloseBoxSize - 6, tab.Top + (tab.Height - CloseBoxSize) / 2, CloseBoxSize, CloseBoxSize);
    }

    private void MeasureTabs()
    {
        _widths.Clear();
        foreach (var item in _items)
        {
            var text = TextRenderer.MeasureText(item.Caption, Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            var width = text.Width + TabPadding * 2 + CloseBoxSize + 6;
            _widths.Add(Math.Clamp(width, MinTabWidth, MaxTabWidth));
        }

        _measuredCaptions.Clear();
        _measuredCaptions.AddRange(_items.Select(item => item.Caption));
        _overflow = ContentWidth > Width;
        _scrollOffset = Math.Clamp(_scrollOffset, 0, MaxScrollOffset);
        PositionButtons();
    }

    /// <summary>Recomputes overflow and button positions without measuring text.</summary>
    private void RefreshLayout()
    {
        if (_widths.Count != _items.Count) MeasureTabs();
        _overflow = ContentWidth > Width;
        _scrollOffset = Math.Clamp(_scrollOffset, 0, MaxScrollOffset);
        PositionButtons();
    }

    private bool CaptionsUnchanged(IReadOnlyList<Item> items)
    {
        if (items == null || items.Count != _measuredCaptions.Count) return false;
        for (var i = 0; i < items.Count; i++)
            if (!string.Equals(items[i].Caption, _measuredCaptions[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private void PositionButtons()
    {
        var height = Math.Max(18, Height - TabTop - 4);
        var top = TabTop;
        if (_overflow)
        {
            var available = AvailableWidth;
            _scrollLeftRect = new Rectangle(available, top, ScrollButtonWidth, height);
            _scrollRightRect = new Rectangle(available + ScrollButtonWidth, top, ScrollButtonWidth, height);
        }
        else
        {
            _scrollLeftRect = Rectangle.Empty;
            _scrollRightRect = Rectangle.Empty;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        RefreshLayout();

        var bottomLine = Height - 1;
        using (var pen = new Pen(EditorTheme.Border))
            g.DrawLine(pen, 0, bottomLine, Width, bottomLine);

        var clip = new Rectangle(0, 0, AvailableWidth, Height);
        var state = g.Save();
        g.SetClip(clip);
        for (var i = 0; i < _items.Count; i++)
        {
            var rect = TabRect(i);
            if (rect.Right < 0 || rect.Left > clip.Width) continue;
            PaintTab(g, i, rect);
        }

        g.Restore(state);

        if (_dragging && _dragTarget >= 0) PaintDropIndicator(g);

        if (_overflow)
        {
            PaintButton(g, _scrollLeftRect, _hoverIndex == -2, ButtonGlyph.Left);
            PaintButton(g, _scrollRightRect, _hoverIndex == -3, ButtonGlyph.Right);
        }
    }

    private void PaintTab(Graphics g, int index, Rectangle rect)
    {
        var selected = index == _selectedIndex;
        var hovered = index == _hoverIndex && !_dragging;
        var item = _items[index];

        using (var background = new SolidBrush(selected || hovered ? EditorTheme.Input : BackColor))
            g.FillRectangle(background, rect);

        using (var border = new Pen(EditorTheme.Border))
        {
            g.DrawLine(border, rect.Left, rect.Top, rect.Right - 1, rect.Top);
            g.DrawLine(border, rect.Left, rect.Top, rect.Left, rect.Bottom - 1);
            g.DrawLine(border, rect.Right - 1, rect.Top, rect.Right - 1, rect.Bottom - 1);
        }

        if (selected)
            using (var accent = new SolidBrush(SystemColors.Highlight))
                g.FillRectangle(accent, rect.Left, rect.Top, rect.Width, 2);

        var close = CloseRect(index);
        var textRect = new Rectangle(rect.Left + TabPadding, rect.Top + 2,
            Math.Max(0, close.Left - rect.Left - TabPadding - TabGap * 2), rect.Height - 4);
        var color = item.HasError ? Color.IndianRed : EditorTheme.Text;
        TextRenderer.DrawText(g, item.Caption, Font, textRect, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        PaintClose(g, close, hovered && _hoverClose);
    }

    private void PaintClose(Graphics g, Rectangle rect, bool hot)
    {
        if (hot)
            using (var background = new SolidBrush(Color.FromArgb(198, 48, 34)))
                g.FillRectangle(background, rect);

        var inset = Math.Max(3, CloseBoxSize / 4);
        using var pen = new Pen(hot ? Color.White : Color.FromArgb(120, 120, 120), 1.4f);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawLine(pen, rect.Left + inset, rect.Top + inset, rect.Right - inset, rect.Bottom - inset);
        g.DrawLine(pen, rect.Right - inset, rect.Top + inset, rect.Left + inset, rect.Bottom - inset);
        g.SmoothingMode = SmoothingMode.Default;
    }

    private enum ButtonGlyph
    {
        Left,
        Right
    }

    private void PaintButton(Graphics g, Rectangle rect, bool hot, ButtonGlyph glyph)
    {
        if (rect.IsEmpty) return;
        if (hot)
            using (var background = new SolidBrush(EditorTheme.Input))
                g.FillRectangle(background, rect);

        var centerX = rect.Left + rect.Width / 2;
        var centerY = rect.Top + rect.Height / 2;
        var arm = Math.Max(4, ScrollButtonWidth / 4);
        using var pen = new Pen(EditorTheme.Text, 1.5f);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        switch (glyph)
        {
            case ButtonGlyph.Left:
                g.DrawLine(pen, centerX + arm / 2, centerY - arm, centerX - arm / 2, centerY);
                g.DrawLine(pen, centerX - arm / 2, centerY, centerX + arm / 2, centerY + arm);
                break;
            case ButtonGlyph.Right:
                g.DrawLine(pen, centerX - arm / 2, centerY - arm, centerX + arm / 2, centerY);
                g.DrawLine(pen, centerX + arm / 2, centerY, centerX - arm / 2, centerY + arm);
                break;
        }

        g.SmoothingMode = SmoothingMode.Default;
    }

    private void PaintDropIndicator(Graphics g)
    {
        var x = _dragTarget >= _items.Count
            ? TabLeft(_items.Count - 1) + (_items.Count > 0 ? _widths[^1] : 0)
            : TabLeft(_dragTarget);
        using var pen = new Pen(SystemColors.Highlight, 2f);
        g.DrawLine(pen, x, TabTop, x, Height - 2);
    }

    private bool PointIn(Rectangle rect, Point point) => !rect.IsEmpty && rect.Contains(point);

    private int HitTest(Point point, out bool onClose)
    {
        onClose = false;
        if (point.X < AvailableWidth)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                var rect = TabRect(i);
                if (rect.Right < 0 || rect.Left > AvailableWidth) continue;
                if (!rect.Contains(point)) continue;
                onClose = CloseRect(i).Contains(point);
                return i;
            }
        }

        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = HitTest(e.Location, out var onClose);
        var overLeft = _overflow && PointIn(_scrollLeftRect, e.Location);
        var overRight = _overflow && PointIn(_scrollRightRect, e.Location);

        var hoverIndex = index >= 0 ? index : overLeft ? -2 : overRight ? -3 : -1;
        if (hoverIndex != _hoverIndex || onClose != _hoverClose)
        {
            _hoverIndex = hoverIndex;
            _hoverClose = onClose;
            Invalidate();
        }

        if (index != _tipIndex)
        {
            _tipIndex = index;
            _toolTip ??= new ToolTip { ShowAlways = true, InitialDelay = 600, ReshowDelay = 200 };
            _toolTip.SetToolTip(this, index >= 0 ? _items[index].ToolTip : null);
        }

        if (_dragIndex < 0 || e.Button != MouseButtons.Left) return;
        if (!_dragging && Math.Abs(e.X - _dragOrigin.X) > 4) _dragging = true;
        if (!_dragging) return;
        _dragTarget = InsertionIndex(e.X);
        Invalidate();
    }

    private int InsertionIndex(int x)
    {
        var position = x + _scrollOffset;
        var cursor = 0;
        for (var i = 0; i < _widths.Count; i++)
        {
            if (position < cursor + _widths[i] / 2) return i;
            cursor += _widths[i] + TabGap;
        }

        return _widths.Count;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_overflow && PointIn(_scrollLeftRect, e.Location))
        {
            _scrollOffset = Math.Max(0, _scrollOffset - 80);
            Invalidate();
            return;
        }

        if (_overflow && PointIn(_scrollRightRect, e.Location))
        {
            _scrollOffset = Math.Min(MaxScrollOffset, _scrollOffset + 80);
            Invalidate();
            return;
        }

        var index = HitTest(e.Location, out var onClose);
        if (index < 0) return;

        if (e.Button == MouseButtons.Middle)
        {
            CloseRequested?.Invoke(index);
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            SelectionRequested?.Invoke(index);
            ContextMenuRequested?.Invoke(index);
            return;
        }

        if (onClose)
        {
            CloseRequested?.Invoke(index);
            return;
        }

        SelectionRequested?.Invoke(index);
        _dragIndex = index;
        _dragOrigin = e.Location;
        _dragging = false;
        _dragTarget = -1;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragIndex >= 0 && _dragging && _dragTarget >= 0)
        {
            var target = Math.Clamp(_dragTarget, 0, _items.Count - 1);
            if (target != _dragIndex) ReorderRequested?.Invoke(_dragIndex, target);
        }

        _dragIndex = -1;
        _dragTarget = -1;
        _dragging = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        _hoverClose = false;
        _tipIndex = -1;
        _toolTip?.SetToolTip(this, null);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_overflow)
        {
            _scrollOffset = Math.Clamp(_scrollOffset - e.Delta / 2, 0, MaxScrollOffset);
            Invalidate();
            return;
        }

        base.OnMouseWheel(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip?.Dispose();
        base.Dispose(disposing);
    }
}
