using System;
using System.Drawing;
using System.Windows.Forms;

namespace FEngViewer;

/// <summary>Forwards a wheel gesture to the surrounding scrollable inspector panel.</summary>
internal static class WheelScroll
{
    public static void Forward(Control control, int delta)
    {
        var parent = control.Parent;
        while (parent != null && parent is not ScrollableControl) parent = parent.Parent;
        if (parent is not ScrollableControl scroll) return;
        var lines = SystemInformation.MouseWheelScrollLines;
        if (lines <= 0) return;
        var step = Math.Max(1, scroll.Font.Height) * lines;
        var change = -Math.Sign(delta) * step;
        var maximum = Math.Max(scroll.VerticalScroll.Minimum,
            scroll.VerticalScroll.Maximum - scroll.VerticalScroll.LargeChange + 1);
        var value = Math.Clamp(scroll.VerticalScroll.Value + change,
            scroll.VerticalScroll.Minimum, maximum);
        if (value == scroll.VerticalScroll.Value) return;
        scroll.VerticalScroll.Value = value;
        scroll.PerformLayout();
        scroll.Invalidate();
    }
}
