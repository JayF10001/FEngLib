using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using FEngLib.Assets;

namespace FEngViewer;

/// <summary>
/// Structured confirmation surface for a container copy. The technical data stays available under
/// "技术详细信息"; the main view follows the user's decision order: output, counts, items, warnings.
/// </summary>
internal sealed class ContainerExportPreviewDialog : Form
{
    private readonly ContainerExportPlan plan;
    private readonly Label warningLabel;
    private readonly LinkLabel technicalLink;
    private readonly Panel technicalPanel;
    private readonly TableLayoutPanel technicalContent;
    private readonly ToolTip technicalToolTip = new();
    private readonly ListView detailList;
    private readonly Label detailCountLabel;
    private readonly Button exportButton;
    private readonly Button cancelButton;
    private readonly Panel contentScroll;
    private readonly int collapsedHeight;
    private bool technicalExpanded;

    public ContainerExportPreviewDialog(ContainerExportPlan plan)
    {
        this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
        var executable = plan.IsExecutable;

        Text = executable ? "导出容器" : "导出容器 — 存在阻断项";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(LogicalToDeviceUnits(780), LogicalToDeviceUnits(560));
        MinimumSize = new Size(LogicalToDeviceUnits(680), LogicalToDeviceUnits(480));
        collapsedHeight = ClientSize.Height;

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = Padding.Empty
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var contentScroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Margin = new Padding(0),
            Padding = Padding.Empty
        };
        this.contentScroll = contentScroll;
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 8,
            Padding = new Padding(24, 16, 24, 8)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 4; row++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, LogicalToDeviceUnits(130)));
        for (var row = 5; row < 8; row++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 10)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.Controls.Add(new Label
        {
            Text = executable ? "确认导出内容" : "导出被阻断",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 15f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        }, 0, 0);
        header.Controls.Add(new Label
        {
            Text = executable
                ? "请检查输出位置和资源修改内容，确认后生成容器文件。"
                : "请先处理以下阻断项；技术详细信息中保留了完整检查结果。",
            AutoSize = true,
            ForeColor = EditorTheme.Muted,
            Margin = new Padding(0, 0, 0, 0)
        }, 0, 1);
        root.Controls.Add(header, 0, 0);

        var outputSection = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 10)
        };
        outputSection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outputSection.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outputSection.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outputSection.Controls.Add(new Label
        {
            Text = "输出文件",
            AutoSize = true,
            ForeColor = EditorTheme.Muted,
            Margin = new Padding(0, 0, 0, 4)
        }, 0, 0);

        var outputCard = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = EditorTheme.Input,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(14, 12, 14, 10),
            Margin = new Padding(0)
        };
        var outputRows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 3
        };
        outputRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outputRows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outputRows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outputRows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outputRows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var filename = new Label
        {
            Text = Path.GetFileName(plan.OutputPath),
            AutoSize = false,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Height = LogicalToDeviceUnits(24),
            Font = new Font(Font.FontFamily, 11f, FontStyle.Bold),
            Margin = new Padding(0, 0, 8, 2)
        };
        var copyPath = new Button
        {
            Text = "复制路径",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 0)
        };
        copyPath.Click += (_, _) => CopyOutputPath();
        outputRows.Controls.Add(filename, 0, 0);
        outputRows.Controls.Add(copyPath, 1, 0);
        var directory = new Label
        {
            Text = Path.GetDirectoryName(plan.OutputPath) ?? "",
            AutoSize = false,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            ForeColor = EditorTheme.Muted,
            Height = LogicalToDeviceUnits(20),
            Margin = new Padding(0, 0, 0, 6)
        };
        outputRows.Controls.Add(directory, 0, 1);
        outputRows.SetColumnSpan(directory, 2);
        var outputHint = new Label
        {
            Text = "ⓘ 将生成容器副本，不会修改游戏目录或基线容器。",
            AutoSize = true,
            ForeColor = EditorTheme.Muted,
            Margin = new Padding(0, 0, 0, 0)
        };
        outputRows.Controls.Add(outputHint, 0, 2);
        outputRows.SetColumnSpan(outputHint, 2);
        outputCard.Controls.Add(outputRows);
        outputSection.Controls.Add(outputCard, 0, 1);
        root.Controls.Add(outputSection, 0, 1);

        var overviewSection = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 8)
        };
        overviewSection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        overviewSection.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        overviewSection.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        overviewSection.Controls.Add(new Label
        {
            Text = "修改概览",
            AutoSize = true,
            ForeColor = EditorTheme.Muted,
            Margin = new Padding(0, 0, 0, 4)
        }, 0, 0);
        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1
        };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var fngCount = plan.FngPlan?.Items.Count ?? 0;
        cards.Controls.Add(BuildCountCard(fngCount, "FNG 修改", new Padding(0, 0, 8, 0)), 0, 0);
        cards.Controls.Add(BuildCountCard(plan.Textures.Count, "贴图替换", new Padding(8, 0, 0, 0)), 1, 0);
        overviewSection.Controls.Add(cards, 0, 1);
        root.Controls.Add(overviewSection, 0, 2);

        var detailHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 4)
        };
        detailHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detailHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        detailHeader.Controls.Add(new Label
        {
            Text = "修改明细",
            AutoSize = true,
            ForeColor = EditorTheme.Muted,
            Margin = new Padding(0, 0, 0, 0)
        }, 0, 0);
        detailCountLabel = new Label
        {
            Text = $"共 {fngCount + plan.Textures.Count} 项",
            AutoSize = true,
            ForeColor = EditorTheme.Muted,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(0, 0, 0, 0)
        };
        detailHeader.Controls.Add(detailCountLabel, 1, 0);
        root.Controls.Add(detailHeader, 0, 3);

        detailList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.FixedSingle,
            ShowItemToolTips = true,
            MinimumSize = new Size(0, LogicalToDeviceUnits(96)),
            Margin = new Padding(0, 0, 0, 8)
        };
        detailList.Columns.Add("名称", LogicalToDeviceUnits(280));
        detailList.Columns.Add("修改类型", LogicalToDeviceUnits(120));
        detailList.Columns.Add("大小 / 信息", LogicalToDeviceUnits(240));
        AddDetailRows();
        root.Controls.Add(detailList, 0, 4);

        warningLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(700), 0),
            Visible = false,
            Margin = new Padding(0, 0, 0, 6)
        };
        root.Controls.Add(warningLabel, 0, 5);

        technicalLink = new LinkLabel
        {
            Text = "▸ 技术详细信息",
            AutoSize = true,
            LinkBehavior = LinkBehavior.NeverUnderline,
            LinkColor = EditorTheme.Accent,
            ActiveLinkColor = EditorTheme.Accent,
            Margin = new Padding(0, 0, 0, 6)
        };
        technicalLink.LinkClicked += (_, _) => ToggleTechnicalDetails();
        root.Controls.Add(technicalLink, 0, 6);

        technicalPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Height = LogicalToDeviceUnits(120),
            Visible = false,
            AutoScroll = true,
            Margin = new Padding(0, 0, 0, 6)
        };
        root.Controls.Add(technicalPanel, 0, 7);
        contentScroll.Controls.Add(root);
        shell.Controls.Add(contentScroll, 0, 0);

        var footer = new Panel
        {
            Dock = DockStyle.Fill,
            Height = LogicalToDeviceUnits(54),
            Padding = new Padding(16, 8, 16, 12),
            Margin = new Padding(0)
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(0)
        };
        exportButton = new Button
        {
            Text = "导出容器",
            AutoSize = true,
            Enabled = executable,
            DialogResult = DialogResult.OK,
            Margin = new Padding(8, 0, 0, 0)
        };
        cancelButton = new Button
        {
            Text = "取消",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Margin = new Padding(0)
        };
        buttons.Controls.Add(exportButton);
        buttons.Controls.Add(cancelButton);
        footer.Controls.Add(buttons);
        shell.Controls.Add(footer, 0, 1);

        Controls.Add(shell);
        AcceptButton = exportButton;
        CancelButton = cancelButton;
        Disposed += (_, _) => technicalToolTip.Dispose();
        Shown += (_, _) =>
        {
            contentScroll.AutoScrollPosition = new Point(0, 0);
            if (executable) exportButton.Focus();
            else cancelButton.Focus();
        };

        EditorTheme.Apply(this);
        outputCard.BackColor = EditorTheme.Input;
        foreach (Control card in cards.Controls)
        {
            card.BackColor = EditorTheme.Input;
        }
        detailList.BackColor = EditorTheme.Input;
        detailList.ForeColor = EditorTheme.Text;
        technicalContent = BuildTechnicalContent(plan);
        technicalPanel.Controls.Add(technicalContent);
        technicalPanel.BackColor = EditorTheme.Surface;
        EditorTheme.Apply(technicalContent);
        StyleTechnicalContent(technicalContent);
        exportButton.BackColor = executable ? EditorTheme.Accent : EditorTheme.Input;
        exportButton.ForeColor = executable ? Color.White : EditorTheme.Muted;
        exportButton.FlatAppearance.BorderColor = executable ? EditorTheme.Accent : EditorTheme.Border;
        exportButton.FlatAppearance.MouseOverBackColor = executable
            ? Color.FromArgb(0, 110, 180)
            : EditorTheme.Input;
        UpdateWarning();
    }

    private static Panel BuildCountCard(int count, string caption, Padding margin)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            Height = 76,
            BackColor = EditorTheme.Input,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = margin
        };
        card.Height = card.LogicalToDeviceUnits(76);
        card.Controls.Add(new Label
        {
            Text = count.ToString(),
            AutoSize = true,
            Font = new Font("Segoe UI", 20f, FontStyle.Bold),
            Location = new Point(card.LogicalToDeviceUnits(14), card.LogicalToDeviceUnits(5)),
            Margin = new Padding(0)
        });
        card.Controls.Add(new Label
        {
            Text = caption,
            AutoSize = true,
            ForeColor = EditorTheme.Muted,
            Location = new Point(card.LogicalToDeviceUnits(14), card.LogicalToDeviceUnits(46)),
            Margin = new Padding(0)
        });
        return card;
    }

    private void AddDetailRows()
    {
        if (plan.FngPlan != null)
        {
            foreach (var item in plan.FngPlan.Items)
            {
                var row = new ListViewItem(string.IsNullOrWhiteSpace(item.PackageName)
                    ? $"FNG 0x{item.BlockOffset:X}"
                    : item.PackageName);
                row.SubItems.Add(item.IsRunnable
                    ? plan.FngPlan.RebuildWithBinary ? "Binary 重建" : "等长替换"
                    : "FNG 阻断");
                row.SubItems.Add($"{item.StoredLength:N0} 字节 · 块 0x{item.BlockOffset:X}");
                row.ToolTipText = $"块 0x{item.BlockOffset:X} · {item.StoredLength:N0} 字节";
                detailList.Items.Add(row);
            }
        }
        foreach (var texture in plan.Textures)
        {
            var name = string.IsNullOrWhiteSpace(texture.RequestName)
                ? texture.RequestId.ToString("X8")
                : texture.RequestName;
            var row = new ListViewItem(name);
            row.SubItems.Add("贴图替换");
            row.SubItems.Add($"{texture.Width} × {texture.Height} · {texture.PngData.Length:N0} 字节");
            row.ToolTipText = $"{texture.TargetTpkName} / {texture.TargetTextureName} · {texture.TargetCompression}";
            detailList.Items.Add(row);
        }
    }

    private void ToggleTechnicalDetails()
    {
        technicalExpanded = !technicalExpanded;
        technicalPanel.Visible = technicalExpanded;
        technicalLink.Text = technicalExpanded ? "▾ 技术详细信息" : "▸ 技术详细信息";
        if (technicalExpanded)
        {
            var width = Math.Max(LogicalToDeviceUnits(200),
                ClientSize.Width - LogicalToDeviceUnits(48) - SystemInformation.VerticalScrollBarWidth - 4);
            technicalContent.Width = width;
            technicalContent.PerformLayout();
            var preferred = technicalContent.GetPreferredSize(new Size(width, 0)).Height;
            technicalPanel.Height = Math.Min(Math.Max(preferred, LogicalToDeviceUnits(80)),
                LogicalToDeviceUnits(320));
            var desired = collapsedHeight + technicalPanel.Height + LogicalToDeviceUnits(24);
            var workingArea = Screen.FromControl(this).WorkingArea;
            var maxClientHeight = workingArea.Height - LogicalToDeviceUnits(80);
            ClientSize = new Size(ClientSize.Width, Math.Min(desired, maxClientHeight));
            var maxTop = workingArea.Bottom - Height - LogicalToDeviceUnits(20);
            if (Top > maxTop) Top = Math.Max(workingArea.Top + LogicalToDeviceUnits(20), maxTop);
        }
        else
        {
            ClientSize = new Size(ClientSize.Width, collapsedHeight);
        }
        PerformLayout();
        if (technicalExpanded) contentScroll.ScrollControlIntoView(technicalPanel);
        else contentScroll.AutoScrollPosition = new Point(0, 0);
    }

    private void UpdateWarning()
    {
        if (!plan.IsExecutable)
        {
            var blockers = plan.Blockers
                .Concat(plan.FngPlan?.Blockers ?? new System.Collections.Generic.List<string>())
                .Concat(plan.FngPlan?.Items.SelectMany(item => item.Blockers) ??
                        Array.Empty<string>())
                .Where(blocker => !string.IsNullOrWhiteSpace(blocker))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            warningLabel.Text = blockers.Length == 0
                ? "无法导出：计划中存在未处理的阻断项。"
                : "无法导出：" + string.Join("；", blockers.Take(3)) +
                  (blockers.Length > 3 ? "；更多信息见技术详细信息。" : "");
            warningLabel.ForeColor = Color.Firebrick;
            warningLabel.Visible = true;
            exportButton.Enabled = false;
            return;
        }

        if (plan.Skipped.Count > 0)
        {
            var names = plan.Skipped.Take(3).Select(item => item.Name).ToArray();
            warningLabel.Text = $"有 {plan.Skipped.Count} 个贴图未包含在本次导出中：" +
                                string.Join("、", names) +
                                (plan.Skipped.Count > 3 ? " 等" : "") +
                                "。完整原因见技术详细信息。";
            warningLabel.ForeColor = Color.FromArgb(180, 90, 0);
            warningLabel.Visible = true;
        }
        else
        {
            warningLabel.Visible = false;
        }
    }

    private void CopyOutputPath()
    {
        try
        {
            Clipboard.SetText(plan.OutputPath);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, "复制输出路径失败：" + error.Message, "导出容器",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private TableLayoutPanel BuildTechnicalContent(ContainerExportPlan plan)
    {
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            BackColor = EditorTheme.Surface
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var rowIndex = 0;

        void AddGroup(string title, Action<TableLayoutPanel> addRows)
        {
            content.RowCount = rowIndex + 1;
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.Controls.Add(new Label
            {
                Text = title,
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
                ForeColor = EditorTheme.Text,
                Tag = "group",
                Margin = new Padding(0, rowIndex == 0 ? 0 : LogicalToDeviceUnits(10), 0, 6)
            }, 0, rowIndex++);

            var table = CreateTechnicalTable();
            addRows(table);
            content.RowCount = rowIndex + 1;
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.Controls.Add(table, 0, rowIndex++);
        }

        AddGroup("输出信息", table =>
        {
            AddTechnicalRow(table, "输出文件", Path.GetFileName(plan.OutputPath));
            AddTechnicalRow(table, "输出目录", Path.GetDirectoryName(plan.OutputPath), copy: true, mono: true);
        });

        AddGroup("基线信息", table =>
        {
            AddTechnicalRow(table, "基线容器", Path.GetFileName(plan.SourcePath));
            AddTechnicalRow(table, "基线路径", plan.SourcePath, copy: true, mono: true);
            AddTechnicalRow(table, "基线大小", $"{plan.SourceLength:N0} 字节");
            AddTechnicalRow(table, "SHA256", plan.SourceSha256, copy: true, mono: true);
        });

        if (!string.IsNullOrWhiteSpace(plan.FngPlan?.BackupPath) ||
            !string.IsNullOrWhiteSpace(plan.LedgerDirectory))
        {
            AddGroup("备份与写回", table =>
            {
                if (!string.IsNullOrWhiteSpace(plan.FngPlan?.BackupPath))
                    AddTechnicalRow(table, "FNG 阶段备份", plan.FngPlan.BackupPath, copy: true, mono: true);
                if (!string.IsNullOrWhiteSpace(plan.LedgerDirectory))
                {
                    AddTechnicalRow(table, "写回账本", plan.LedgerDirectory, copy: true, mono: true);
                    AddTechnicalRow(table, "写回版本", Path.GetFileName(plan.LedgerDirectory));
                }
            });
        }

        var hasModificationDetails = (plan.FngPlan?.Items.Count ?? 0) > 0 || plan.Textures.Count > 0;
        if (hasModificationDetails)
        {
            AddGroup("修改技术信息", table =>
            {
                if (plan.FngPlan != null)
                {
                    foreach (var item in plan.FngPlan.Items)
                    {
                        var name = string.IsNullOrWhiteSpace(item.PackageName)
                            ? $"FNG 0x{item.BlockOffset:X}"
                            : item.PackageName;
                        AddTechnicalRow(table, name + " 替换摘要", item.ReplacementSha256, copy: true, mono: true);
                        AddTechnicalRow(table, name + " 块内摘要", item.CurrentBlockSha256, copy: true, mono: true);
                        AddTechnicalRow(table, name + " 写入方式",
                            $"{(plan.FngPlan.RebuildWithBinary ? "Binary 重建" : "等长替换")} · " +
                            $"{item.StoredLength:N0} → {item.ReplacementStoredLength:N0} 字节");
                        foreach (var blocker in item.Blockers)
                            AddTechnicalRow(table, name + " 阻断", blocker);
                    }
                }
                foreach (var texture in plan.Textures)
                {
                    var name = string.IsNullOrWhiteSpace(texture.RequestName)
                        ? texture.RequestId.ToString("X8")
                        : texture.RequestName;
                    AddTechnicalRow(table, name + " 目标",
                        $"{texture.TargetTpkName} / {texture.TargetTextureName} · {texture.TargetCompression}");
                    AddTechnicalRow(table, name + " PNG SHA256", texture.Sha256, copy: true, mono: true);
                }
            });
        }

        var hasDiagnostics = plan.Skipped.Count > 0 || plan.Blockers.Count > 0 ||
                             plan.FngPlan?.Blockers.Count > 0 || plan.Warnings.Count > 0;
        if (hasDiagnostics)
        {
            AddGroup("未包含与警告", table =>
            {
                foreach (var skipped in plan.Skipped)
                    AddTechnicalRow(table, "未包含", $"{skipped.Name}：{skipped.Reason}");
                foreach (var blocker in plan.Blockers)
                    AddTechnicalRow(table, "阻断", blocker, copy: true);
                if (plan.FngPlan != null)
                    foreach (var blocker in plan.FngPlan.Blockers)
                        AddTechnicalRow(table, "FNG 阻断", blocker, copy: true);
                foreach (var warning in plan.Warnings)
                    AddTechnicalRow(table, "警告", warning, copy: true);
            });
        }

        return content;
    }

    private TableLayoutPanel CreateTechnicalTable()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            BackColor = EditorTheme.Surface
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(150)));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(62)));
        return table;
    }

    private void AddTechnicalRow(TableLayoutPanel table, string label, string value,
        bool copy = false, bool mono = false)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, LogicalToDeviceUnits(30)));
        var displayValue = SingleLine(value);
        var field = new EllipsisLabel
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = EditorTheme.Muted,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular),
            Tag = "field",
            Margin = new Padding(0, 0, LogicalToDeviceUnits(8), 0)
        };
        var valueLabel = new EllipsisLabel
        {
            Text = displayValue,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = EditorTheme.Text,
            Font = mono
                ? new Font("Consolas", 9f, FontStyle.Regular)
                : new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular),
            Tag = "value",
            Margin = new Padding(0, 0, LogicalToDeviceUnits(4), 0)
        };
        technicalToolTip.SetToolTip(valueLabel, displayValue);
        table.Controls.Add(field, 0, row);
        table.Controls.Add(valueLabel, 1, row);
        if (copy && !string.IsNullOrWhiteSpace(value))
        {
            var button = new Button
            {
                Text = "复制",
                AutoSize = false,
                Width = LogicalToDeviceUnits(58),
                Height = LogicalToDeviceUnits(24),
                Dock = DockStyle.Fill,
                Margin = new Padding(0, LogicalToDeviceUnits(3), 0, LogicalToDeviceUnits(3)),
                Tag = "copy"
            };
            button.Click += (_, _) => CopyTechnicalValue(value);
            table.Controls.Add(button, 2, row);
        }
        else
        {
            table.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }, 2, row);
        }
    }

    private static string SingleLine(string value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Replace("\r", " ").Replace("\n", " ");

    private static void StyleTechnicalContent(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is Label label && string.Equals(label.Tag as string, "field", StringComparison.Ordinal))
                label.ForeColor = EditorTheme.Muted;
            if (child.HasChildren) StyleTechnicalContent(child);
        }
    }

    private void CopyTechnicalValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            Clipboard.SetText(value);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, "复制失败：" + error.Message, "导出容器",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Single-line label that always ellipsizes instead of wrapping long paths.</summary>
    private sealed class EllipsisLabel : Label
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            using var background = new SolidBrush(BackColor);
            e.Graphics.FillRectangle(background, ClientRectangle);
            const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                                          TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter |
                                          TextFormatFlags.Left;
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, BackColor, flags);
        }
    }
}
