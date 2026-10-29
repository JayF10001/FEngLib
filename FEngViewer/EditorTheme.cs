using System.Drawing;
using System.Windows.Forms;

namespace FEngViewer;

internal static class EditorTheme
{
    public static bool Dark { get; set; }
    public static Color Surface => Dark?Color.FromArgb(31,34,39):Color.FromArgb(246,247,249);
    public static Color Input => Dark?Color.FromArgb(41,45,51):Color.White;
    public static Color Text => Dark?Color.FromArgb(226,230,235):Color.FromArgb(35,41,49);
    public static Color Muted => Dark?Color.FromArgb(160,170,183):Color.FromArgb(93,104,119);
    public static Color Border => Dark?Color.FromArgb(66,73,83):Color.FromArgb(209,216,224);
    public static Color Accent => Color.FromArgb(0,126,201);
    public static void Apply(Control root)
    {
        if(root is GLRenderControl) return;
        if(root is Label or Button or GroupBox) root.Text=EditorStrings.Translate(root.Text);
        root.BackColor=root is TextBoxBase or NumericUpDown or ComboBox or TreeView or PropertyGrid?Input:Surface;
        root.ForeColor=Text;
        if(root is PropertyGrid grid)
        {
            grid.ViewBackColor=Input; grid.ViewForeColor=Text; grid.LineColor=Border;
            grid.HelpBackColor=Surface; grid.HelpForeColor=Text; grid.CategoryForeColor=Text;
            grid.CommandsBackColor=Surface; grid.CommandsForeColor=Text;
        }
        if(root is Button button) { button.FlatStyle=FlatStyle.Flat; button.FlatAppearance.BorderColor=Border; }
        if(root is ToolStrip strip)
        {
            strip.Renderer=new ToolStripProfessionalRenderer(new Palette());
            foreach(ToolStripItem item in strip.Items) ApplyItem(item);
        }
        if(root is SplitContainer split) split.BackColor=Border;
        foreach(Control child in root.Controls) Apply(child);
        root.Invalidate();
    }
    private static void ApplyItem(ToolStripItem item)
    {
        item.Text=EditorStrings.Translate(item.Text);
        item.BackColor=Surface; item.ForeColor=Text;
        if(item is ToolStripDropDownItem menu) foreach(ToolStripItem child in menu.DropDownItems) ApplyItem(child);
    }
    private sealed class Palette:ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground=>Surface;
        public override Color MenuItemSelected=>Dark?Color.FromArgb(55,73,93):Color.FromArgb(222,237,251);
        public override Color ImageMarginGradientBegin=>Surface;
        public override Color ImageMarginGradientMiddle=>Surface;
        public override Color ImageMarginGradientEnd=>Surface;
        public override Color MenuStripGradientBegin=>Surface;
        public override Color MenuStripGradientEnd=>Surface;
        public override Color ToolStripGradientBegin=>Surface;
        public override Color ToolStripGradientMiddle=>Surface;
        public override Color ToolStripGradientEnd=>Surface;
        public override Color ButtonCheckedGradientBegin=>Dark?Color.FromArgb(45,80,108):Color.FromArgb(201,225,246);
        public override Color ButtonCheckedGradientMiddle=>ButtonCheckedGradientBegin;
        public override Color ButtonCheckedGradientEnd=>ButtonCheckedGradientBegin;
    }
}
