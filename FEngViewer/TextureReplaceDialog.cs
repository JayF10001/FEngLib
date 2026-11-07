using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using FEngLib.Assets;
using FEngLib.Packages;

namespace FEngViewer;

/// <summary>
/// Compact replacement view: current texture summary, candidate PNG validation and a project-level
/// override. Nothing is written to the container or the game directory; the PNG is stored in the
/// .fengproj by the caller. A verified container copy can be generated from the applied override.
/// </summary>
internal sealed class TextureReplaceDialog : Form
{
    private readonly string _objectName;
    private readonly string _documentName;
    private readonly ResourceRequest _request;
    private readonly string _projectSourcePath;
    private readonly Func<ContainerTextureOverride, string, string> _writeBack;
    private readonly Func<TextureLookupResult> _resolveCurrent;
    private readonly Func<byte[], string, ContainerTextureOverride> _createOverride;
    private readonly Func<ContainerTextureOverride, string> _applyOverride;
    private readonly Func<string> _removeOverride;

    private readonly Label _headline = new()
    {
        AutoSize = true,
        Font = new Font(DefaultFont, FontStyle.Bold),
        Margin = new Padding(0, 0, 0, 6)
    };
    private readonly Label _summary = new()
    {
        AutoSize = true,
        MaximumSize = new Size(940, 0),
        Margin = new Padding(0, 0, 0, 8)
    };
    private readonly PictureBox _currentPreview = NewPreview();
    private readonly PictureBox _candidatePreview = NewPreview();
    private readonly Label _currentDetails = NewDetailsLabel();
    private readonly Label _candidateDetails = NewDetailsLabel();
    private readonly Label _status = new()
    {
        AutoSize = true,
        MaximumSize = new Size(940, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 8, 0, 6)
    };
    private readonly Button _select = new() { Text = "选择 PNG…", AutoSize = true };
    private readonly Button _apply = new() { Text = "应用覆盖", AutoSize = true, Enabled = false };
    private readonly Button _remove = new() { Text = "移除覆盖", AutoSize = true, Enabled = false };
    private readonly Button _writeBackButton = new() { Text = "写入档案副本…", AutoSize = true, Enabled = false };
    private readonly Button _close = new() { Text = "关闭", AutoSize = true, DialogResult = DialogResult.Cancel };

    private TextureLookupResult _current;
    private ContainerTextureOverride _existing;
    private byte[] _candidateBytes;
    private string _candidatePath;
    private (int Width, int Height)? _candidateSize;

    public TextureReplaceDialog(string objectName, string documentName, ResourceRequest request,
        TextureLookupResult current, ContainerTextureOverride existing,
        string projectSourcePath, Func<ContainerTextureOverride, string, string> writeBack,
        Func<TextureLookupResult> resolveCurrent,
        Func<byte[], string, ContainerTextureOverride> createOverride,
        Func<ContainerTextureOverride, string> applyOverride,
        Func<string> removeOverride)
    {
        _objectName = objectName;
        _documentName = documentName;
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _current = current ?? new TextureLookupResult
        {
            Status = TextureLookupStatus.NotFound,
            RequestedName = request.Name,
            RequestedId = request.ID,
        };
        _existing = existing;
        _projectSourcePath = projectSourcePath ?? "";
        _writeBack = writeBack;
        _resolveCurrent = resolveCurrent ?? throw new ArgumentNullException(nameof(resolveCurrent));
        _createOverride = createOverride ?? throw new ArgumentNullException(nameof(createOverride));
        _applyOverride = applyOverride ?? throw new ArgumentNullException(nameof(applyOverride));
        _removeOverride = removeOverride ?? throw new ArgumentNullException(nameof(removeOverride));

        Text = "替换贴图 · " + (request.Name ?? $"0x{request.ID:X8}");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = false;
        MinimizeBox = false;
        ClientSize = new Size(1000, 700);
        MinimumSize = new Size(860, 580);
        KeyPreview = true;

        BuildLayout();
        EditorTheme.Apply(this);
        _currentPreview.BackgroundImage = PreviewBackground.Checkerboard;
        _candidatePreview.BackgroundImage = PreviewBackground.Checkerboard;
        _currentDetails.ForeColor = EditorTheme.Text;
        _candidateDetails.ForeColor = EditorTheme.Text;
        _status.ForeColor = EditorTheme.Muted;
        RefreshCurrent();
        RefreshCandidate();
        UpdateButtons();
    }

    private static PictureBox NewPreview() => new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.FromArgb(210, 210, 210),
        BackgroundImageLayout = ImageLayout.Tile,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 0, 0, 4)
    };

    private static Label NewDetailsLabel() => new()
    {
        AutoSize = true,
        MaximumSize = new Size(420, 0),
        Margin = new Padding(0, 2, 0, 0)
    };

    private void BuildLayout()
    {
        _headline.Text = "替换贴图 · " + (_request.Name ?? $"0x{_request.ID:X8}");
        _summary.Text = $"对象：{_objectName}\r\n" +
                        $"FNG：{_documentName}\r\n" +
                        $"FNG 资源请求：{_request.Name ?? "(未命名)"} · 0x{_request.ID:X8}";

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        header.Controls.Add(_headline, 0, 0);
        header.Controls.Add(_summary, 0, 1);

        var previews = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previews.Controls.Add(BuildPreviewGroup("当前贴图", _currentPreview, _currentDetails), 0, 0);
        previews.Controls.Add(BuildPreviewGroup("替换候选 PNG", _candidatePreview, _candidateDetails), 1, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 0)
        };
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_writeBackButton);
        buttons.Controls.Add(_remove);
        buttons.Controls.Add(_apply);
        buttons.Controls.Add(_select);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10),
            Margin = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(previews, 0, 1);
        root.Controls.Add(_status, 0, 2);
        root.Controls.Add(buttons, 0, 3);
        Controls.Add(root);
        CancelButton = _close;

        _select.Click += (_, _) => SelectCandidate();
        _apply.Click += (_, _) => ApplyCandidate();
        _remove.Click += (_, _) => RemoveCandidate();
        _writeBackButton.Click += (_, _) => WriteBackCopy();
    }

    private static GroupBox BuildPreviewGroup(string title, PictureBox preview, Label details)
    {
        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Margin = new Padding(4)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(preview, 0, 0);
        layout.Controls.Add(details, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private void RefreshCurrent()
    {
        _currentDetails.Text = DescribeCurrent(_current);
        var error = SetPreview(_currentPreview, _current.IsFound ? _current.PngData : null);
        if (error != null) _currentDetails.Text += "\r\n预览失败：" + error;
        UpdateButtons();
    }

    private string DescribeCurrent(TextureLookupResult current)
    {
        var lines = new List<string>();
        if (current.IsProjectOverride)
        {
            lines.Add("来源：工程贴图覆盖");
            lines.Add("覆盖：" + current.Source);
            lines.Add("贴图：" + (current.SourceTexture ?? _request.Name));
            lines.Add($"尺寸：{current.Width} × {current.Height}");
            lines.Add("格式：PNG（覆盖层）");
            if (_existing != null && _existing.HasTarget)
            {
                lines.Add($"目标：{_existing.TargetTpkName} / {_existing.TargetTextureName} · {_existing.TargetCompression}");
                if (!File.Exists(_existing.TargetArchivePath))
                    lines.Add("写回：目标档案不存在。");
                else if (ContainerSession.PathsEqual(_existing.TargetArchivePath, _projectSourcePath))
                    lines.Add("写回：可写入当前容器副本。");
                else
                    lines.Add("写回：外部共享包；将生成该档案的副本。");
            }
            else
            {
                lines.Add("写回：缺少 TPK 目标信息。");
            }
        }
        else if (current.Entry is { } entry)
        {
            lines.Add("来源：TPK 档案");
            lines.Add("档案：" + entry.ArchivePath);
            lines.Add("TPK：" + entry.TpkName);
            lines.Add("贴图：" + entry.Name);
            lines.Add($"尺寸：{entry.Width} × {entry.Height}");
            lines.Add("格式：" + entry.Compression);
        }
        else if (current.IsFound)
        {
            lines.Add("来源：松散 PNG");
            lines.Add("文件：" + current.Source);
            lines.Add("贴图：" + (current.SourceTexture ?? _request.Name));
            var size = TryGetPngSize(current.PngData);
            if (size.HasValue) lines.Add($"尺寸：{size.Value.Width} × {size.Value.Height}");
            lines.Add("格式：PNG");
        }
        else
        {
            lines.Add("来源：未找到");
            lines.Add("请求：" + (_request.Name ?? "(未命名)"));
            if (!string.IsNullOrWhiteSpace(current.Message)) lines.Add("说明：" + current.Message);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private (int Width, int Height)? CurrentSize()
    {
        if (_current.Entry is { } entry) return (entry.Width, entry.Height);
        if (_current.IsProjectOverride && _current.Width > 0 && _current.Height > 0)
            return (_current.Width, _current.Height);
        if (_current.IsFound) return TryGetPngSize(_current.PngData);
        return null;
    }

    private void SelectCandidate()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择替换 PNG",
            Filter = "PNG 图片 (*.png)|*.png",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            SetCandidate(File.ReadAllBytes(dialog.FileName), dialog.FileName);
        }
        catch (Exception error)
        {
            _status.Text = "无法读取 PNG：" + error.Message;
            _status.ForeColor = Color.Firebrick;
        }
    }

    private void SetCandidate(byte[] bytes, string path)
    {
        var size = TryGetPngSize(bytes) ?? throw new InvalidDataException("不是可解码的 PNG 文件。");
        _candidateBytes = bytes;
        _candidatePath = path;
        _candidateSize = size;
        RefreshCandidate();
    }

    private void RefreshCandidate()
    {
        if (_candidateBytes == null || _candidateSize == null)
        {
            _candidateDetails.Text = "尚未选择 PNG。\r\n选择后会显示尺寸、格式和与当前贴图的尺寸校验。";
            SetPreview(_candidatePreview, null);
            UpdateButtons();
            return;
        }

        var size = _candidateSize.Value;
        var currentSize = CurrentSize();
        var lines = new List<string>
        {
            "文件：" + _candidatePath,
            $"尺寸：{size.Width} × {size.Height}",
            "格式：PNG",
            $"大小：{_candidateBytes.Length:N0} 字节"
        };
        if (currentSize == null)
        {
            lines.Add("尺寸校验：当前贴图缺失，无法校验");
            _status.Text = "当前贴图缺失，无法校验尺寸；仍可作为覆盖层应用。";
            _status.ForeColor = Color.FromArgb(190, 100, 20);
        }
        else if (currentSize.Value == size)
        {
            lines.Add($"尺寸校验：匹配（{currentSize.Value.Width} × {currentSize.Value.Height}）");
            _status.Text = "候选 PNG 尺寸匹配，可以应用覆盖。";
            _status.ForeColor = Color.ForestGreen;
        }
        else
        {
            lines.Add($"尺寸校验：不一致（当前 {currentSize.Value.Width} × {currentSize.Value.Height}）");
            _status.Text = "候选 PNG 尺寸与当前贴图不一致，已禁用应用。";
            _status.ForeColor = Color.Firebrick;
        }
        _candidateDetails.Text = string.Join(Environment.NewLine, lines);
        var error = SetPreview(_candidatePreview, _candidateBytes);
        if (error != null) _candidateDetails.Text += "\r\n预览失败：" + error;
        UpdateButtons();
    }

    private void ApplyCandidate()
    {
        if (_candidateBytes == null || _candidateSize == null) return;
        try
        {
            var replacement = _createOverride(_candidateBytes, Path.GetFileName(_candidatePath));
            var error = _applyOverride(replacement);
            if (!string.IsNullOrWhiteSpace(error))
            {
                _status.Text = error;
                _status.ForeColor = Color.Firebrick;
                return;
            }
            _existing = replacement;
            _current = _resolveCurrent();
            RefreshCurrent();
            RefreshCandidate();
            _status.Text = "已应用工程贴图覆盖（未保存工程）。";
            _status.ForeColor = Color.ForestGreen;
        }
        catch (Exception error)
        {
            _status.Text = "应用失败：" + error.Message;
            _status.ForeColor = Color.Firebrick;
        }
    }

    private void RemoveCandidate()
    {
        try
        {
            var error = _removeOverride();
            if (!string.IsNullOrWhiteSpace(error))
            {
                _status.Text = error;
                _status.ForeColor = Color.Firebrick;
                return;
            }
            _existing = null;
            _current = _resolveCurrent();
            RefreshCurrent();
            RefreshCandidate();
            _status.Text = "已移除工程贴图覆盖（未保存工程）。";
            _status.ForeColor = Color.ForestGreen;
        }
        catch (Exception error)
        {
            _status.Text = "移除失败：" + error.Message;
            _status.ForeColor = Color.Firebrick;
        }
    }

    private void WriteBackCopy()
    {
        if (_existing == null || !_existing.HasTarget || _writeBack == null) return;
        var targetArchive = _existing.TargetArchivePath;
        if (string.IsNullOrWhiteSpace(targetArchive) || !File.Exists(targetArchive))
        {
            _status.Text = "目标档案不存在，无法写回。";
            _status.ForeColor = Color.Firebrick;
            return;
        }
        var extension = Path.GetExtension(targetArchive);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".lzc";
        using var dialog = new SaveFileDialog
        {
            Title = "写入档案副本",
            Filter = $"档案副本 (*{extension})|*{extension}",
            DefaultExt = extension.TrimStart('.'),
            FileName = Path.GetFileNameWithoutExtension(targetArchive) + ".texture" + extension,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (ContainerSession.PathsEqual(dialog.FileName, targetArchive))
        {
            _status.Text = "输出副本不能覆盖目标档案。";
            _status.ForeColor = Color.Firebrick;
            return;
        }
        UseWaitCursor = true;
        Cursor = Cursors.WaitCursor;
        try
        {
            var error = _writeBack(_existing, dialog.FileName);
            if (!string.IsNullOrWhiteSpace(error))
            {
                _status.Text = error;
                _status.ForeColor = Color.Firebrick;
                return;
            }
            _status.Text = "已生成档案副本：" + dialog.FileName;
            _status.ForeColor = Color.ForestGreen;
        }
        catch (Exception error)
        {
            _status.Text = "写入失败：" + error.Message;
            _status.ForeColor = Color.Firebrick;
        }
        finally
        {
            UseWaitCursor = false;
            Cursor = Cursors.Default;
        }
    }

    private void UpdateButtons()
    {
        var currentSize = CurrentSize();
        var matches = _candidateBytes != null && _candidateSize.HasValue &&
                      (currentSize == null || currentSize.Value == _candidateSize.Value);
        _apply.Enabled = matches;
        _remove.Enabled = _existing != null || _current.IsProjectOverride;
        var compression = _existing?.TargetCompression?.ToUpperInvariant();
        var targetArchive = _existing?.TargetArchivePath;
        _writeBackButton.Enabled = _writeBack != null && _existing != null && _existing.HasTarget &&
            !string.IsNullOrWhiteSpace(targetArchive) && File.Exists(targetArchive) &&
            compression is "TEXCOMP_DXTC1" or "TEXCOMP_DXTC3" or "TEXCOMP_DXTC5";
    }

    private string SetPreview(PictureBox box, byte[] png)
    {
        var old = box.Image;
        box.Image = null;
        old?.Dispose();
        if (png == null || png.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            using var image = Image.FromStream(stream);
            box.Image = new Bitmap(image);
            return null;
        }
        catch (Exception error)
        {
            return error.Message;
        }
    }

    private static (int Width, int Height)? TryGetPngSize(byte[] png)
    {
        if (png == null || png.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            using var image = Image.FromStream(stream);
            return (image.Width, image.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var current = _currentPreview.Image;
            _currentPreview.Image = null;
            current?.Dispose();
            var candidate = _candidatePreview.Image;
            _candidatePreview.Image = null;
            candidate?.Dispose();
        }
        base.Dispose(disposing);
    }
}
