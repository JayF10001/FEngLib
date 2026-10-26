using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using FEngLib.Assets;

namespace FEngViewer;

/// <summary>
/// The write-back manifest: what will be replaced in which copy, where the backup goes, and every
/// blocker that currently prevents execution. OK is only offered when the plan is executable.
/// </summary>
internal sealed class WriteBackPreviewDialog : Form
{
    public WriteBackPreviewDialog(ContainerWritePlan plan)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        var executable = plan.IsExecutable;

        Text = executable ? "写回清单 — 确认发布" : "写回被阻断";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MaximizeBox = false;
        ClientSize = new Size(760, 460);
        MinimumSize = new Size(560, 340);

        var text = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            Text = BuildManifest(plan)
        };

        var execute = new Button
        {
            Text = executable ? "执行写回" : "关闭",
            DialogResult = executable ? DialogResult.OK : DialogResult.Cancel,
            AutoSize = true
        };
        var cancel = new Button
        {
            Text = executable ? "取消" : "关闭",
            DialogResult = DialogResult.Cancel,
            AutoSize = true
        };
        if (!executable) cancel.Visible = false;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(execute);
        CancelButton = cancel;
        AcceptButton = execute;

        Controls.Add(text);
        Controls.Add(buttons);
    }

    private static string BuildManifest(ContainerWritePlan plan)
    {
        var lines = new System.Collections.Generic.List<string>();
        if (!plan.IsExecutable)
        {
            lines.Add("当前无法写回，以下问题必须先解决：");
            lines.Add("");
            foreach (var blocker in plan.Blockers) lines.Add("  ✕ " + blocker);
            foreach (var item in plan.Items.Where(i => !i.IsRunnable))
            {
                lines.Add($"  ✕ {item.PackageName}（块 0x{item.BlockOffset:X}）：");
                foreach (var blocker in item.Blockers) lines.Add("      " + blocker);
            }
            lines.Add("");
        }

        lines.Add($"原始容器（只读基线）: {plan.OriginalPath}");
        lines.Add($"工作副本（写回目标）: {plan.WorkingCopyPath ?? "(尚未创建)"}");
        lines.Add($"输入副本 SHA256    : {plan.WorkingCopySha256 ?? "—"}");
        lines.Add($"备份位置           : {plan.BackupPath ?? "—"}");
        lines.Add($"容器块             : {plan.BlockCount} 个，本次替换 {plan.Items.Count(i => i.IsRunnable)} 个");
        lines.Add("");
        foreach (var item in plan.Items.Where(i => i.IsRunnable))
        {
            lines.Add($"  ▸ {item.PackageName}　块 0x{item.BlockOffset:X}　{item.StoredLength} 字节（等长替换）");
            lines.Add($"      替换文件 : {item.ReplacementPath}");
            lines.Add($"      替换摘要 : {item.ReplacementSha256}");
            lines.Add($"      块内现有 : {item.CurrentBlockSha256}");
            lines.Add("");
        }

        if (plan.Items.Any(i => i.IsRunnable))
        {
            lines.Add("非目标块（含填充与压缩块）逐字节保留，所有块偏移不变。");
            lines.Add("原始容器与游戏目录不会被修改；未通过候选验证不会发布。");
        }
        foreach (var note in plan.Notes) lines.Add("  · " + note);
        return string.Join(Environment.NewLine, lines);
    }
}
