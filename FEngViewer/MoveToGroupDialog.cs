using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using FEngLib.Objects;
using FEngLib.Packages;

namespace FEngViewer;

/// <summary>Selects the destination group for an explicit reparent; root stays a first-class choice.</summary>
internal sealed class MoveToGroupDialog : Form
{
    private sealed class Choice
    {
        public IObject<ObjectData> Parent;
        public string Text;
        public override string ToString() => Text;
    }

    private readonly ComboBox groups = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        IntegralHeight = false,
    };

    public MoveToGroupDialog(Package package, IObject<ObjectData> moving)
    {
        Text = "移动到组";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 150);

        var label = new Label
        {
            Text = $"把“{moving?.Name ?? "未命名对象"}”移动到：",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 28,
            Padding = new Padding(0, 3, 0, 0),
        };
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 8, 14, 8) };
        body.Controls.Add(groups);
        body.Controls.Add(label);

        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(14, 8, 14, 8),
            WrapContents = false,
        };
        buttons.Controls.AddRange(new Control[] { cancel, ok });

        Controls.Add(body);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        EditorTheme.Apply(this);

        groups.Items.Add(new Choice { Parent = null, Text = "（根级，不属于任何组）" });
        foreach (var group in package.Objects.OfType<Group>()
                     .Where(g => !ReferenceEquals(g, moving) && !IsDescendant(g, moving)))
        {
            groups.Items.Add(new Choice
            {
                Parent = group,
                Text = $"{DepthPrefix(group, package)}{group.Name ?? "未命名组"}  0x{group.Guid:X8}",
            });
        }

        var selected = groups.Items.Cast<Choice>().FirstOrDefault(c => ReferenceEquals(c.Parent, moving?.Parent));
        groups.SelectedItem = selected ?? groups.Items[0];
    }

    public IObject<ObjectData> SelectedParent => (groups.SelectedItem as Choice)?.Parent;

    private static bool IsDescendant(IObject<ObjectData> candidate, IObject<ObjectData> ancestor)
    {
        for (var parent = candidate.Parent; parent != null; parent = parent.Parent)
            if (ReferenceEquals(parent, ancestor)) return true;
        return false;
    }

    private static string DepthPrefix(IObject<ObjectData> obj, Package package)
    {
        var depth = 0;
        for (var parent = obj.Parent; parent != null; parent = parent.Parent) depth++;
        return new string(' ', Math.Min(depth, 12) * 2);
    }
}
