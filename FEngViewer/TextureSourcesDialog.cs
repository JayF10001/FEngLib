using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace FEngViewer;

/// <summary>
/// Configures read-only game archives for texture lookup. Paths are stored as an ordered source
/// list; the resolver tries them in this order after loose PNGs.
/// </summary>
internal sealed class TextureSourcesDialog : Form
{
    private readonly ListBox list = new()
    {
        Dock = DockStyle.Fill,
        SelectionMode = SelectionMode.MultiExtended,
        IntegralHeight = false,
    };

    public TextureSourcesDialog(IEnumerable<string> archives)
    {
        Text = "贴图来源";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Width = 820;
        Height = 460;

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        var hint = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 44,
            Text = "按顺序读取游戏档案中的 TPK 贴图；同目录 textures/*.png 仍作为覆盖层。\n" +
                   "支持 FrontB.lzc、GlobalB.lzc、GLOBALA/BUN、GLOBALB.BUN、DYNTEX.BIN 等。",
        };
        body.Controls.Add(list);
        body.Controls.Add(hint);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 6, 8, 6),
        };
        var add = new Button { Text = "添加档案…", AutoSize = true };
        var remove = new Button { Text = "移除", AutoSize = true };
        var clear = new Button { Text = "清空", AutoSize = true };
        add.Click += (_, _) => AddArchives();
        remove.Click += (_, _) => RemoveSelected();
        clear.Click += (_, _) => list.Items.Clear();
        buttons.Controls.AddRange(new Control[] { add, remove, clear });

        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        var okCancel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(8, 8, 8, 8),
        };
        okCancel.Controls.AddRange(new Control[] { cancel, ok });

        Controls.Add(body);
        Controls.Add(buttons);
        Controls.Add(okCancel);
        AcceptButton = ok;
        CancelButton = cancel;

        foreach (var archive in archives ?? Array.Empty<string>()) list.Items.Add(archive);
        EditorTheme.Apply(this);
    }

    public IReadOnlyList<string> Result =>
        list.Items.Cast<string>().Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

    private void AddArchives()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择游戏档案",
            Filter = "游戏档案|*.lzc;*.bun;*.bin;*.tpk|所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        foreach (var file in dialog.FileNames)
        {
            if (list.Items.Cast<string>().Contains(file, StringComparer.OrdinalIgnoreCase)) continue;
            list.Items.Add(file);
        }
    }

    private void RemoveSelected()
    {
        var selected = list.SelectedIndices.Cast<int>().OrderByDescending(i => i).ToList();
        foreach (var index in selected) list.Items.RemoveAt(index);
    }
}
