using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using FEngLib.Assets;

namespace FEngViewer;

/// <summary>
/// Confirms an explicit restore from the automatic standalone-FNG backup. Ordinary saves never
/// show a dialog; this one does because it replaces the current file with older content.
/// </summary>
internal sealed class RestoreBackupDialog : Form
{
    public RestoreBackupDialog(StandaloneBackupInfo backup, string currentPath, bool hasUnsavedChanges)
    {
        if (backup == null) throw new ArgumentNullException(nameof(backup));

        Text = "从备份恢复 — 确认";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(720, 470);

        var title = new Label
        {
            Text = "将用原始备份替换当前文件",
            Font = new Font(Font.FontFamily, 11f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        };
        var hint = new Label
        {
            Text = "恢复会用备份内容覆盖磁盘上的当前文件。恢复前，当前版本会先另存为备份快照。",
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            Margin = new Padding(0, 0, 0, 12)
        };

        var file = new FileInfo(currentPath);
        var currentState = file.Exists
            ? $"{file.Length:N0} 字节，修改于 {file.LastWriteTime:yyyy-MM-dd HH:mm:ss}"
            : "当前文件不存在（恢复会重新创建）";

        var rows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 12,
            Padding = new Padding(16, 14, 16, 6)
        };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddLabel(string text, int topMargin)
        {
            rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rows.Controls.Add(new Label
            {
                Text = text,
                AutoSize = true,
                Margin = new Padding(0, topMargin, 0, 4)
            });
        }

        void AddBox(string text)
        {
            rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            var box = MakePathBox(text);
            rows.Controls.Add(box);
            box.SelectionStart = 0;
            box.SelectionLength = 0;
        }

        rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rows.Controls.Add(title);
        rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rows.Controls.Add(hint);
        AddLabel("当前文件", 0);
        AddBox(currentPath);
        AddLabel("当前磁盘内容", 10);
        AddLabel(currentState, 0);
        AddLabel("备份创建时间", 10);
        AddLabel(backup.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), 0);
        AddLabel("备份 SHA256", 10);
        AddBox(backup.OriginalSha256);
        AddLabel("备份文件", 10);
        AddBox(backup.OriginalFilePath);
        rows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rows.Controls.Add(new Label { Text = "", AutoSize = false, Dock = DockStyle.Fill });

        var warning = new Label
        {
            Text = hasUnsavedChanges
                ? "编辑器中有未保存的修改，恢复后这些修改会丢失。"
                : "",
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            ForeColor = Color.Firebrick,
            Margin = new Padding(0, 10, 0, 0),
            Visible = hasUnsavedChanges
        };
        rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rows.Controls.Add(warning);

        var restore = new Button
        {
            Text = "从备份恢复",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Margin = new Padding(0)
        };
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Margin = new Padding(8, 0, 0, 0)
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(16, 8, 16, 12)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(restore);

        Controls.Add(rows);
        Controls.Add(buttons);
        AcceptButton = cancel;
        CancelButton = cancel;
        EditorTheme.Apply(this);
    }

    private static TextBox MakePathBox(string text) => new()
    {
        Text = text,
        ReadOnly = true,
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = EditorTheme.Input,
        ForeColor = EditorTheme.Text,
        Margin = new Padding(0)
    };
}
