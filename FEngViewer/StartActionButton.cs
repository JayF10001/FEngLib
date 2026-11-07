using System;
using System.Drawing;
using System.Windows.Forms;

namespace FEngViewer;

/// <summary>Rectangular start-page action: bold title plus smaller description text inside.</summary>
internal sealed class StartActionButton : Button
{
    private bool _hover;

    public StartActionButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var scale = DeviceDpi / 96f;
        var pad = (int)Math.Round(14 * scale);
        var background = !Enabled ? EditorTheme.Surface : _hover ? EditorTheme.Surface : EditorTheme.Input;
        using (var brush = new SolidBrush(background))
            e.Graphics.FillRectangle(brush, ClientRectangle);
        using (var pen = new Pen(Focused || _hover ? EditorTheme.Accent : EditorTheme.Border, 1.4f))
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        using var titleFont = new Font(Font.FontFamily, 12.5f, FontStyle.Bold, GraphicsUnit.Point);
        using var descriptionFont = new Font(Font.FontFamily, 9.25f, FontStyle.Regular, GraphicsUnit.Point);
        var titleFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                         TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        var titleHeight = TextRenderer.MeasureText(Title, titleFont,
            new Size(Math.Max(1, Width - pad * 2), int.MaxValue), titleFlags).Height;
        TextRenderer.DrawText(e.Graphics, Title, titleFont,
            new Rectangle(pad, pad, Math.Max(1, Width - pad * 2), titleHeight),
            Enabled ? EditorTheme.Text : EditorTheme.Muted, titleFlags);

        var descriptionY = pad + titleHeight + (int)Math.Round(7 * scale);
        var descriptionRect = new Rectangle(pad, descriptionY,
            Math.Max(1, Width - pad * 2), Math.Max(1, Height - descriptionY - pad));
        TextRenderer.DrawText(e.Graphics, Description, descriptionFont, descriptionRect, EditorTheme.Muted,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle, EditorTheme.Text, background);
    }
}
