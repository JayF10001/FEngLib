using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using FEngLib.Assets;

namespace FEngViewer;

/// <summary>Container selection and extraction; editing continues in the existing PackageView.</summary>
internal sealed class ContainerBrowser : Form
{
    private readonly string _sourcePath;
    private FngContainer _container;
    private readonly ListView _entries = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        MultiSelect = false, HideSelection = false
    };
    private readonly TextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Filter FNG names..." };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Button _open = new() { Text = "Extract and open", AutoSize = true, Enabled = false };
    private readonly Button _extractAll = new() { Text = "Extract all supported", AutoSize = true, Enabled = false };

    public string ExtractedPath { get; private set; }

    public ContainerBrowser(string sourcePath)
    {
        _sourcePath = sourcePath;
        Text = "Container FNGs — " + Path.GetFileName(sourcePath);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(960, 560);
        MinimumSize = new Size(760, 420);
        _entries.Columns.Add("FNG name", 270);
        _entries.Columns.Add("Offset", 100);
        _entries.Columns.Add("Bytes", 95);
        _entries.Columns.Add("Storage / support", 125);
        _entries.Columns.Add("Diagnostic", 330);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange(new Control[] { _open, _extractAll, close });
        layout.Controls.Add(_search, 0, 0);
        layout.Controls.Add(_entries, 0, 1);
        layout.Controls.Add(_status, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        CancelButton = close;
        _status.Text = "Scanning container (read-only)...";
        _search.TextChanged += (_, _) => Populate();
        _entries.SelectedIndexChanged += (_, _) => UpdateButtons();
        _entries.DoubleClick += async (_, _) => { if (_open.Enabled) await Extract(true); };
        _open.Click += async (_, _) => await Extract(true);
        _extractAll.Click += async (_, _) => await Extract(false);
        Shown += async (_, _) => await Scan();
    }

    private async Task Scan()
    {
        try
        {
            var container = await Task.Run(() => FngContainer.Open(_sourcePath));
            if (IsDisposed) return;
            _container = container;
            Populate();
            _status.Text = $"{container.Entries.Count} FNG blocks; {container.Entries.Count(e => e.CanExtract)} extractable. " +
                "Compressed blocks need a future decoder.\r\nSelect an output folder; preview PNGs still belong in its textures folder.";
        }
        catch (Exception error)
        {
            if (!IsDisposed) _status.Text = "Container scan failed: " + error.Message;
        }
    }

    private void Populate()
    {
        _entries.BeginUpdate();
        _entries.Items.Clear();
        if (_container != null)
            foreach (var entry in _container.Entries.Where(e => string.IsNullOrWhiteSpace(_search.Text) ||
                         (e.Name ?? "(compressed / invalid)").Contains(_search.Text, StringComparison.OrdinalIgnoreCase)))
            {
                var item = new ListViewItem(new[]
                {
                    entry.Name ?? "(name unavailable)", $"0x{entry.Offset:X}", entry.StoredLength.ToString("N0"),
                    entry.Storage.ToString(), entry.Diagnostic ?? "Exact block extraction"
                }) { Tag = entry };
                if (!entry.CanExtract) item.ForeColor = SystemColors.GrayText;
                _entries.Items.Add(item);
            }
        _entries.EndUpdate();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _open.Enabled = _entries.SelectedItems.Count == 1 &&
            ((FngContainerEntry)_entries.SelectedItems[0].Tag).CanExtract;
        _extractAll.Enabled = _container?.Entries.Any(e => e.CanExtract) == true;
    }

    private async Task Extract(bool openSelected)
    {
        using var folder = new FolderBrowserDialog { Description = "Choose the parent folder for extracted working files.", UseDescriptionForTitle = true };
        if (folder.ShowDialog(this) != DialogResult.OK) return;
        var selection = openSelected
            ? new[] { (FngContainerEntry)_entries.SelectedItems[0].Tag }
            : _container.Entries.Where(e => e.CanExtract).ToArray();
        var output = Path.Combine(folder.SelectedPath,
            Path.GetFileNameWithoutExtension(_sourcePath) + "-" + _container.SourceSha256.Substring(0, 12));
        _open.Enabled = _extractAll.Enabled = _entries.Enabled = _search.Enabled = false;
        _status.Text = "Verifying source and extracting FNG blocks...";
        try
        {
            var results = await Task.Run(() => _container.Extract(selection, output));
            if (IsDisposed) return;
            if (openSelected)
            {
                ExtractedPath = results[0].OutputPath;
                DialogResult = DialogResult.OK;
                Close();
            }
            else
                _status.Text = $"Extracted and verified {results.Count} FNGs with source manifests.\r\n{output}";
        }
        catch (Exception error)
        {
            if (!IsDisposed) MessageBox.Show(this, error.Message, "Extraction failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed)
            {
                _entries.Enabled = _search.Enabled = true;
                UpdateButtons();
            }
        }
    }
}
