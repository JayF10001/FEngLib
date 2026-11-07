using System;
using System.Windows.Forms;

namespace FEngViewer;

/// <summary>
/// Numeric editor that never changes its value from the mouse wheel. Hovering or scrolling over
/// the inspector used to modify values accidentally; the wheel now scrolls the surrounding panel.
/// The up/down buttons, keyboard and typing still work normally.
/// </summary>
internal sealed class NoWheelNumericUpDown : NumericUpDown
{
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        WheelScroll.Forward(this, e.Delta);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        foreach (Control child in Controls)
        {
            child.MouseWheel -= ChildMouseWheel;
            child.MouseWheel += ChildMouseWheel;
        }
    }

    private void ChildMouseWheel(object sender, MouseEventArgs e)
    {
        WheelScroll.Forward(this, e.Delta);
    }
}
