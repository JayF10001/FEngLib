using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using FEngLib.Assets;

namespace FEngViewer;

/// <summary>
/// Container selection and extraction. Extraction only writes working copies; the imported FNGs
/// are handed to the workspace as an index, so no tabs are created here.
/// </summary>
internal sealed class ContainerBrowser : Form
{
    private readonly Func<string, IReadOnlyList<string>, string> _importFiles;
    private string _sourcePath;
    private FngContainer _container;
    private bool _busy;
    private bool _outputEdited;

    private readonly TextBox _containerPath = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly Button _browseContainer = new() { Text = "Browse...", AutoSize = true };
    private readonly TextBox _filter = new() { Dock = DockStyle.Fill, PlaceholderText = "按名称过滤 FNG..." };
    private readonly Label _filterCount = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly ListView _entries = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = true,
        HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable
    };
    private readonly TextBox _output = new() { Dock = DockStyle.Fill };
    private readonly Button _browseOutput = new() { Text = "Browse...", AutoSize = true };
    private readonly ComboBox _existing = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly Label _policyHint = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true, MaximumSize = new Size(980, 0) };
    private readonly Button _extractSelected = new() { Text = "Extract Selected & Open", AutoSize = true, Enabled = false };
    private readonly Button _extractAll = new() { Text = "Extract All & Open Tabs", AutoSize = true, Enabled = false };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

    public string ExtractionSummary { get; private set; }

    public ContainerBrowser(string sourcePath, Func<string, IReadOnlyList<string>, string> importFiles)
    {
        _sourcePath = sourcePath;
        _importFiles = importFiles ?? throw new ArgumentNullException(nameof(importFiles));
        Text = "Container FNGs — " + Path.GetFileName(sourcePath);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1000, 620);
        MinimumSize = new Size(760, 460);
        ShowInTaskbar = false;

        _entries.Columns.Add("Name", 280);
        _entries.Columns.Add("Type", 100);
        _entries.Columns.Add("Size", 90);
        _entries.Columns.Add("Status", 300);

        _existing.Items.AddRange(new object[] { "停止提取（不覆盖）", "跳过已有文件", "覆盖已有文件" });
        _existing.SelectedIndexChanged += (_, _) =>
        {
            _policyHint.Text = _existing.SelectedIndex switch
            {
                1 => "保留输出目录中已有的 FNG 与来源记录，并把已有 FNG 一起加入工作区。",
                2 => "用容器中的原始 FNG 替换输出文件及来源记录，已有编辑会被覆盖。",
                _ => "发现同名 FNG 或来源记录时，停止整批提取，不写入任何文件。"
            };
        };
        _existing.SelectedIndex = 1;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(12) };
        for (var i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles[3] = new RowStyle(SizeType.Percent, 100);

        layout.Controls.Add(BuildPathRow("Container:", _containerPath, _browseContainer), 0, 0);
        layout.Controls.Add(BuildFilterRow(), 0, 1);
        layout.Controls.Add(_status, 0, 2);
        layout.Controls.Add(_entries, 0, 3);
        layout.Controls.Add(BuildPathRow("Output folder:", _output, _browseOutput), 0, 4);
        layout.Controls.Add(BuildPolicyRow(), 0, 5);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(960, 0),
            ForeColor = SystemColors.GrayText,
            Text = "提取只写入工作副本，不会写回容器或游戏目录。"
        }, 0, 6);
        layout.Controls.Add(BuildButtonRow(), 0, 7);
        Controls.Add(layout);

        CancelButton = _cancel;
        _filter.TextChanged += (_, _) => Populate();
        _entries.SelectedIndexChanged += (_, _) => UpdateButtons();
        _entries.DoubleClick += async (_, _) => await Extract(allEntries: false);
        _extractSelected.Click += async (_, _) => await Extract(allEntries: false);
        _extractAll.Click += async (_, _) => await Extract(allEntries: true);
        _browseContainer.Click += async (_, _) => await BrowseContainer();
        _browseOutput.Click += (_, _) => BrowseOutput();
        _output.TextChanged += (_, _) =>
        {
            if (!_settingOutput) _outputEdited = true;
        };
        FormClosing += (_, e) =>
        {
            if (_busy) e.Cancel = true;
        };
        Shown += async (_, _) => await Scan();
    }

    private static Control BuildPathRow(string label, TextBox textBox, Button browse)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, AutoSize = true, Margin = new Padding(0, 2, 0, 4) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) }, 0, 0);
        panel.Controls.Add(textBox, 1, 0);
        panel.Controls.Add(browse, 2, 0);
        return panel;
    }

    private Control BuildFilterRow()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.Controls.Add(_filter, 0, 0);
        panel.Controls.Add(_filterCount, 1, 0);
        return panel;
    }

    private Control BuildPolicyRow()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 2, 0, 4) };
        panel.Controls.Add(new Label { Text = "If file exists:", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        panel.Controls.Add(_existing);
        panel.Controls.Add(_policyHint);
        return panel;
    }

    private Control BuildButtonRow()
    {
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 6, 0, 0) };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_extractAll);
        buttons.Controls.Add(_extractSelected);
        return buttons;
    }

    private bool _settingOutput;

    private void SetOutput(string value)
    {
        _settingOutput = true;
        try { _output.Text = value; }
        finally { _settingOutput = false; }
    }

    private async Task BrowseContainer()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "MW05 containers (*.bun, *.lzc)|*.bun;*.lzc",
            CheckFileExists = true,
            FileName = _sourcePath
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _sourcePath = dialog.FileName;
        Text = "Container FNGs — " + Path.GetFileName(_sourcePath);
        await Scan();
    }

    private void BrowseOutput()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择提取输出的目标文件夹（已存在的同名文件按右侧策略处理）",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_output.Text) ? _output.Text : Path.GetDirectoryName(_sourcePath)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) SetOutput(dialog.SelectedPath);
    }

    private async Task Scan()
    {
        SetBusy(true);
        _status.Text = "正在扫描容器（只读）...";
        _container = null;
        _entries.Items.Clear();
        UpdateButtons();
        try
        {
            var container = await Task.Run(() => FngContainer.Open(_sourcePath));
            if (IsDisposed) return;
            _container = container;
            _containerPath.Text = _sourcePath;
            if (!_outputEdited || string.IsNullOrWhiteSpace(_output.Text)) SetOutput(DefaultOutput(container));
            Populate();
            _status.Text = $"{container.Entries.Count} 个 FNG 块，{container.Entries.Count(entry => entry.CanExtract)} 个可原样提取。" +
                           "压缩块需要解压实现，本轮不支持；损坏条目会在结果中说明原因。";
        }
        catch (Exception error)
        {
            if (!IsDisposed) _status.Text = "容器扫描失败：" + error.Message;
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private static string DefaultOutput(FngContainer container)
    {
        var directory = Path.GetDirectoryName(container.SourcePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(container.SourcePath);
        var hash = container.SourceSha256.Length >= 12 ? container.SourceSha256[..12] : container.SourceSha256;
        return Path.Combine(directory, $"{name}-{hash}");
    }

    private void Populate()
    {
        _entries.BeginUpdate();
        try
        {
            _entries.Items.Clear();
            if (_container == null) return;
            var filter = _filter.Text?.Trim() ?? "";
            var shown = 0;
            foreach (var entry in _container.Entries)
            {
                var name = entry.Name ?? "(名称不可用)";
                if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                var item = new ListViewItem(new[]
                {
                    name,
                    entry.Storage switch
                    {
                        FngStorageKind.Uncompressed => "Uncompressed",
                        FngStorageKind.Compressed => "Compressed",
                        _ => "Invalid"
                    },
                    FormatSize(entry.StoredLength),
                    entry.Storage switch
                    {
                        FngStorageKind.Uncompressed => "OK",
                        FngStorageKind.Compressed => entry.Diagnostic ?? "压缩：暂不支持",
                        _ => entry.Diagnostic ?? "损坏"
                    }
                })
                {
                    Tag = entry,
                    ForeColor = entry.CanExtract ? SystemColors.ControlText
                        : entry.Storage == FngStorageKind.Compressed ? SystemColors.GrayText : Color.FromArgb(186, 40, 30)
                };

                item.ToolTipText = $"0x{entry.Offset:X}  {entry.StoredLength:N0} 字节" +
                                   (string.IsNullOrEmpty(entry.Diagnostic) ? "" : "\n" + entry.Diagnostic);
                _entries.Items.Add(item);
                shown++;
            }

            _filterCount.Text = $"共 {_container.Entries.Count}，显示 {shown}";
        }
        finally
        {
            _entries.EndUpdate();
        }

        UpdateButtons();
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1048576.0:0.0} MB"
        : $"{Math.Max(1, bytes / 1024.0):0} KB";

    private void UpdateButtons()
    {
        if (_busy)
        {
            _extractSelected.Enabled = false;
            _extractAll.Enabled = false;
            return;
        }

        var selection = _entries.SelectedItems.Cast<ListViewItem>().Select(item => (FngContainerEntry)item.Tag).ToArray();
        _extractSelected.Enabled = selection.Length > 0 && selection.All(entry => entry.CanExtract);
        _extractAll.Enabled = _container != null && _container.Entries.Any(entry => entry.CanExtract);
    }

    private ExistingFilePolicy Policy => _existing.SelectedIndex switch
    {
        1 => ExistingFilePolicy.Skip,
        2 => ExistingFilePolicy.Overwrite,
        _ => ExistingFilePolicy.Fail
    };

    private async Task Extract(bool allEntries)
    {
        if (_container == null) return;
        var selection = allEntries
            ? _container.Entries.Where(entry => entry.CanExtract).ToArray()
            : _entries.SelectedItems.Cast<ListViewItem>().Select(item => (FngContainerEntry)item.Tag)
                .Where(entry => entry.CanExtract).ToArray();
        if (selection.Length == 0)
        {
            MessageBox.Show(this, allEntries ? "容器中没有可原样提取的 FNG。" : "请选择至少一个可提取的 FNG。",
                "Extract", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var directory = _output.Text?.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            MessageBox.Show(this, "请先选择输出文件夹。", "Extract", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            directory = Path.GetFullPath(directory);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Extract", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            using var progress = new ExtractionProgressDialog(_container, selection, directory, Policy,
                Path.GetFileNameWithoutExtension(_sourcePath), allEntries, _importFiles);
            progress.ShowDialog(this);
            ExtractionSummary = progress.SummaryText;
            if (progress.Succeeded)
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _entries.Enabled = !busy;
        _filter.Enabled = !busy;
        _existing.Enabled = !busy;
        _output.Enabled = !busy;
        _browseContainer.Enabled = !busy;
        _browseOutput.Enabled = !busy;
        _cancel.Enabled = !busy;
        UpdateButtons();
    }
}
