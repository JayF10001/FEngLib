using System;
using System.Windows.Forms;

namespace FEngViewer;

/// <summary>ComboBox that ignores the mouse wheel so inspecting a value cannot change it.</summary>
internal sealed class NoWheelComboBox : ComboBox
{
    private const int WmMouseWheel = 0x020A;

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        WheelScroll.Forward(this, e.Delta);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseWheel)
        {
            var delta = (short)((long)m.WParam >> 16);
            WheelScroll.Forward(this, delta);
            return;
        }
        base.WndProc(ref m);
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
