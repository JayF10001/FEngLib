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
/// Extraction progress. It reports each block honestly: skipped collisions keep the existing file,
/// compressed blocks stay unsupported, and a failure rolls the batch back before reporting.
/// </summary>
internal sealed class ExtractionProgressDialog : Form
{
    private static readonly Color ExtractedColor = Color.FromArgb(22, 110, 40);
    private static readonly Color SkippedColor = SystemColors.GrayText;
    private static readonly Color FailedColor = Color.FromArgb(186, 40, 30);
    private static readonly Color PendingColor = SystemColors.ControlText;

    private readonly FngContainer _container;
    private readonly FngContainerEntry[] _selection;
    private readonly string _outputDirectory;
    private readonly ExistingFilePolicy _policy;
    private readonly string _groupName;
    private readonly Func<string, string, string, IReadOnlyList<string>, string> _importFiles;
    private readonly bool _listAllEntries;

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable
    };
    private readonly Label _headline = new() { AutoSize = true, Text = "Preparing..." };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous };
    private readonly Label _totals = new() { AutoSize = true, Text = "总计: 0" };
    private readonly Label _opened = new() { AutoSize = true, MaximumSize = new Size(700, 0) };
    private readonly Label _error = new() { AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = FailedColor };
    private readonly Button _close = new() { Text = "Close", AutoSize = true, Enabled = false };
    private readonly Dictionary<long, ListViewItem> _rows = new();

    private bool _busy;
    private bool _succeeded;

    public ExtractionProgressDialog(FngContainer container, FngContainerEntry[] selection, string outputDirectory,
        ExistingFilePolicy policy, string groupName, bool listAllEntries,
        Func<string, string, string, IReadOnlyList<string>, string> importFiles)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _outputDirectory = outputDirectory;
        _policy = policy;
        _groupName = groupName;
        _listAllEntries = listAllEntries;
        _importFiles = importFiles;

        Text = "Extracting and Opening...";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 560);
        MinimumSize = new Size(620, 420);
        ShowInTaskbar = false;
        Shown += (_,_) => EditorTheme.Apply(this);
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MaximizeBox = false;
        MinimizeBox = false;

        _list.Columns.Add("状态", 70);
        _list.Columns.Add("文件", 260);
        _list.Columns.Add("大小", 90);
        _list.Columns.Add("说明", 290);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(12) };
        for (var i = 0; i < 7; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles[3] = new RowStyle(SizeType.Percent, 100);

        var progressPanel = new Panel { Dock = DockStyle.Fill, Height = 20, Padding = new Padding(0, 2, 0, 2) };
        progressPanel.Controls.Add(_progress);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(_close);

        layout.Controls.Add(_headline, 0, 0);
        layout.Controls.Add(progressPanel, 0, 1);
        layout.Controls.Add(_totals, 0, 2);
        layout.Controls.Add(_list, 0, 3);
        layout.Controls.Add(_error, 0, 4);
        layout.Controls.Add(_opened, 0, 5);
        layout.Controls.Add(buttons, 0, 6);
        Controls.Add(layout);

        _close.Click += (_, _) => Close();
        AcceptButton = _close;
        FormClosing += (_, e) =>
        {
            if (_busy) e.Cancel = true;
        };
        Shown += async (_, _) => await RunAsync();
    }

    public bool Succeeded => _succeeded;

    /// <summary>One-line result for the container browser's status bar.</summary>
    public string SummaryText => string.Join("；", new[] { _totals.Text, _opened.Text, _error.Text }
        .Where(text => !string.IsNullOrWhiteSpace(text)));

    private void PopulateRows()
    {
        var entries = _listAllEntries ? _container.Entries : _selection;
        foreach (var entry in entries)
        {
            var row = new ListViewItem(new[]
            {
                "-", entry.Name ?? "(名称不可用)", FormatSize(entry.StoredLength), DescribeEntry(entry)
            })
            {
                Tag = entry,
                ForeColor = PendingColor
            };
            if (!entry.CanExtract) row.ForeColor = entry.Storage == FngStorageKind.Compressed ? SkippedColor : FailedColor;
            _list.Items.Add(row);
            if (!_rows.ContainsKey(entry.Offset)) _rows.Add(entry.Offset, row);
        }
    }

    private static string DescribeEntry(FngContainerEntry entry) => entry.Storage switch
    {
        FngStorageKind.Compressed => entry.Diagnostic ?? "压缩块：暂不支持解压",
        FngStorageKind.Invalid => entry.Diagnostic ?? "块头损坏，无法提取",
        _ => "可以提取"
    };

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1048576.0:0.0} MB"
        : $"{Math.Max(1, bytes / 1024.0):0} KB";

    private async Task RunAsync()
    {
        _busy = true;
        _progress.Maximum = Math.Max(1, _selection.Length);
        _progress.Value = 0;
        _headline.Text = $"Processing {_selection.Length} entries...";
        PopulateRows();
        Application.DoEvents();

        try
        {
            var progress = new Progress<FngExtractionProgress>(report => OnBlockExtracted(report));
            var batch = await Task.Run(() => _container.Extract(_selection, _outputDirectory, _policy, progress));
            ApplyBatch(batch);
            _succeeded = true;

            var paths = OpenablePaths(batch);
            if (paths.Count == 0)
            {
                _opened.Text = "没有可加入工作区的 FNG：跳过项只有来源记录，或没有可提取的块。";
                _opened.ForeColor = FailedColor;
            }
            else
            {
                var summary = _importFiles(_container.SourcePath, _outputDirectory, _groupName, paths);
                _opened.Text = summary;
                _opened.ForeColor = ExtractedColor;
            }
        }
        catch (Exception error)
        {
            foreach (ListViewItem row in _list.Items)
                if (row.SubItems[0].Text == "提取")
                {
                    row.SubItems[0].Text = "已回滚";
                    row.SubItems[3].Text = "整批失败，未写入任何文件";
                    row.ForeColor = FailedColor;
                }

            _error.Text = "提取失败，未写入任何文件：" + error.Message;
            _headline.Text = "Failed";
            _totals.Text = "总计: " + _list.Items.Count + "　提取: 0";
        }
        finally
        {
            _busy = false;
            _close.Enabled = true;
            _progress.Style = ProgressBarStyle.Continuous;
        }
    }

    private void OnBlockExtracted(FngExtractionProgress report)
    {
        _progress.Value = Math.Min(_progress.Maximum, report.Index);
        _headline.Text = $"Processing {_selection.Length} entries... {report.Index}/{_selection.Length}";
        if (!_rows.TryGetValue(report.Entry.Offset, out var row)) return;
        row.SubItems[0].Text = "提取";
        row.SubItems[3].Text = "已写入 " + Path.GetFileName(report.OutputPath);
        row.ForeColor = ExtractedColor;
    }

    private void ApplyBatch(FngExtractionBatch batch)
    {
        foreach (var skip in batch.Skipped)
        {
            if (!_rows.TryGetValue(skip.Entry.Offset, out var row)) continue;
            var exists = File.Exists(skip.OutputPath);
            row.SubItems[0].Text = exists ? "跳过" : "跳过";
            row.SubItems[3].Text = exists ? "已存在，保留原文件" : "只有来源记录，缺少 FNG";
            row.ForeColor = exists ? SkippedColor : FailedColor;
        }

        foreach (var extraction in batch.Extracted)
        {
            if (!_rows.TryGetValue(extraction.Offset, out var row)) continue;
            row.SubItems[0].Text = "提取";
            row.SubItems[3].Text = "已写入 " + Path.GetFileName(extraction.OutputPath);
            row.ForeColor = ExtractedColor;
        }

        var listed = _list.Items.Count;
        var failed = _listAllEntries
            ? _container.Entries.Count(entry => entry.Storage != FngStorageKind.Uncompressed)
            : 0;
        var totals = $"总计: {listed}　提取: {batch.Extracted.Count}　跳过: {batch.Skipped.Count}";
        if (failed > 0) totals += $"　损坏/不支持: {failed}";
        if (batch.OverwrittenCount > 0) totals += $"　覆盖: {batch.OverwrittenCount}";
        _totals.Text = totals;
        _headline.Text = "Processing " + _selection.Length + " entries... 100%";
        _progress.Value = _progress.Maximum;
    }

    private List<string> OpenablePaths(FngExtractionBatch batch)
    {
        var paths = new Dictionary<long, string>();
        foreach (var extraction in batch.Extracted) paths[extraction.Offset] = extraction.OutputPath;
        foreach (var skip in batch.Skipped)
            if (File.Exists(skip.OutputPath)) paths[skip.Entry.Offset] = skip.OutputPath;

        var ordered = new List<string>();
        foreach (var entry in _selection)
            if (paths.TryGetValue(entry.Offset, out var path)) ordered.Add(path);
        return ordered;
    }
}
