using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace FEngViewer;

/// <summary>
/// Compact About dialog. Credentials and legal text are deliberately kept as plain text; the
/// project is currently distributed as "All rights reserved" until upstream licensing is settled.
/// </summary>
internal sealed class AboutDialog : Form
{
    private readonly List<Label> mutedLabels = new();

    public AboutDialog(Icon ownerIcon = null)
    {
        Text = "关于 FEngEditor";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(620));
        MinimumSize = new Size(LogicalToDeviceUnits(680), LogicalToDeviceUnits(520));

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

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = Padding.Empty
        };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 0,
            Padding = new Padding(24, 20, 24, 8)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var assembly = typeof(AboutDialog).Assembly;
        var version = assembly.GetName().Version;
        var versionText = version == null
            ? "开发版"
            : $"{version.Major}.{version.Minor}.{version.Build}";
        var buildText = BuildDate(assembly);
        var icon = ownerIcon ?? Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        AddHeader(root, icon, versionText, buildText);
        AddSeparator(root);
        AddSection(root, "简介");
        AddBody(root,
            "FEngEditor 用于查看和修改 FNG 界面资源、容器内的 FNG 与贴图，并在不修改原始游戏文件的前提下生成容器副本。\n" +
            "工程通过 .fengproj 保存，基线容器以只读缓存方式管理；导出结果需要由用户自行验证。");
        AddSeparator(root);
        AddSection(root, "项目与支持");
        AddLinks(root);
        AddSeparator(root);
        AddSection(root, "第三方组件与致谢");
        AddThirdPartyTable(root);
        AddMuted(root, "各组件按自身许可证发布；版本、版权和许可证全文以各项目页面为准。", 0);
        AddSeparator(root);
        AddRow(root, new Panel { Height = LogicalToDeviceUnits(8), Dock = DockStyle.Top, Margin = Padding.Empty });

        var footer = new Panel
        {
            Dock = DockStyle.Fill,
            Height = LogicalToDeviceUnits(54),
            Padding = new Padding(16, 8, 16, 12)
        };
        var footerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = Padding.Empty
        };
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var legalLink = new LinkLabel
        {
            Text = "免责声明与许可信息 · All rights reserved",
            AutoSize = true,
            LinkBehavior = LinkBehavior.NeverUnderline,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0)
        };
        legalLink.LinkClicked += (_, _) => ShowLegalNotice();
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0)
        };
        var close = new Button
        {
            Text = "关闭",
            AutoSize = true,
            DialogResult = DialogResult.OK
        };
        buttons.Controls.Add(close);
        footerLayout.Controls.Add(legalLink, 0, 0);
        footerLayout.Controls.Add(buttons, 1, 0);
        footer.Controls.Add(footerLayout);

        content.Controls.Add(root);
        shell.Controls.Add(content, 0, 0);
        shell.Controls.Add(footer, 0, 1);
        Controls.Add(shell);
        AcceptButton = close;
        CancelButton = close;

        EditorTheme.Apply(this);
        foreach (var label in mutedLabels) label.ForeColor = EditorTheme.Muted;
        SetLinkColors(this);
    }

    private void AddHeader(TableLayoutPanel root, Icon icon, string versionText, string buildText)
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 12)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(94)));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var iconBox = new PictureBox
        {
            Size = new Size(LogicalToDeviceUnits(76), LogicalToDeviceUnits(76)),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = EditorTheme.Surface,
            Margin = new Padding(0, 0, 16, 0)
        };
        if (icon != null)
        {
            iconBox.Image = icon.ToBitmap();
            iconBox.Disposed += (_, _) => iconBox.Image?.Dispose();
        }
        header.Controls.Add(iconBox, 0, 0);

        var text = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 4
        };
        text.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) text.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        text.Controls.Add(new Label
        {
            Text = "FEngEditor",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 2)
        }, 0, 0);
        text.Controls.Add(new Label
        {
            Text = "面向 EA Black Box《极品飞车》系列的 FNG 与容器编辑器",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4)
        }, 0, 1);
        var buildLine = string.IsNullOrWhiteSpace(buildText)
            ? $"版本 {versionText}"
            : $"版本 {versionText} · 构建 {buildText}";
        text.Controls.Add(new Label
        {
            Text = buildLine,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2)
        }, 0, 2);
        var maintainer = new Label
        {
            Text = "维护：JayF / 贡献者",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 0)
        };
        mutedLabels.Add(maintainer);
        text.Controls.Add(maintainer, 0, 3);
        header.Controls.Add(text, 1, 0);
        AddRow(root, header);
    }

    private void AddLinks(TableLayoutPanel root)
    {
        var links = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 2)
        };
        links.Controls.Add(MakeLink("项目主页", "https://github.com/JayF10001/FEngLib"));
        links.Controls.Add(MakeLink("问题反馈", "https://github.com/JayF10001/FEngLib/issues"));
        AddRow(root, links);
    }

    private void AddThirdPartyTable(TableLayoutPanel root)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 0,
            Margin = new Padding(0, 0, 0, 4)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(225)));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(88)));

        AddThirdParty(table, "FEngLib / FEngViewer", "上游基础 · LeoCodes21、fridtjof 及贡献者",
            "https://github.com/NFSTools/FEngLib");
        AddThirdParty(table, "SharpGL", "Dave Kerr", "https://github.com/dwmkerr/sharpgl");
        AddThirdParty(table, "SixLabors.ImageSharp / Fonts", "Six Labors", "https://github.com/SixLabors/ImageSharp");
        AddThirdParty(table, "Newtonsoft.Json", "James Newton-King", "https://github.com/JamesNK/Newtonsoft.Json");
        AddThirdParty(table, "CommandLineParser", "CommandLineParser contributors",
            "https://github.com/commandlineparser/commandline");
        AddThirdParty(table, "JetBrains.Annotations", "JetBrains", "https://github.com/JetBrains/Annotations");
        AddThirdParty(table, "DevIL / ILWrapper", "外部运行库，不随程序分发", null);
        AddThirdParty(table, "Binary / Nikki", "外部运行库，不随程序分发", null);
        AddRow(root, table);
    }

    private void AddThirdParty(TableLayoutPanel table, string name, string author, string url)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, LogicalToDeviceUnits(30)));
        var nameLabel = new Label
        {
            Text = name,
            AutoSize = false,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 8, 0)
        };
        var authorLabel = new Label
        {
            Text = author,
            AutoSize = false,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 8, 0)
        };
        mutedLabels.Add(authorLabel);
        table.Controls.Add(nameLabel, 0, row);
        table.Controls.Add(authorLabel, 1, row);
        if (!string.IsNullOrWhiteSpace(url))
        {
            var link = MakeLink("项目链接", url);
            link.Anchor = AnchorStyles.Left;
            table.Controls.Add(link, 2, row);
        }
        else
        {
            table.Controls.Add(new Label { Text = "—", AutoSize = true, Margin = new Padding(0) }, 2, row);
        }
    }

    private void ShowLegalNotice()
    {
        MessageBox.Show(this,
            "许可证：All rights reserved（当前未授予公开再分发许可）。\n\n" +
            "上游 FEngLib / FEngViewer 代码版权归其原作者及贡献者所有。\n" +
            "第三方组件仍受其各自许可证约束。\n\n" +
            "本程序与 Electronic Arts、Black Box 无隶属关系；\n" +
            "Need for Speed 及相关商标归 EA 所有。\n\n" +
            "本程序不附带游戏资源；请自行备份并验证输出副本。",
            "免责声明与许可信息", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void AddSection(TableLayoutPanel root, string text)
    {
        AddRow(root, new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        });
    }

    private void AddBody(TableLayoutPanel root, string text)
    {
        AddRow(root, new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(660), 0),
            Margin = new Padding(0, 0, 0, 8)
        });
    }

    private void AddMuted(TableLayoutPanel root, string text, int top)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(660), 0),
            Margin = new Padding(0, top, 0, 6)
        };
        mutedLabels.Add(label);
        AddRow(root, label);
    }

    private void AddSeparator(TableLayoutPanel root)
    {
        AddRow(root, new Panel
        {
            Dock = DockStyle.Top,
            Height = LogicalToDeviceUnits(1),
            BackColor = EditorTheme.Border,
            Margin = new Padding(0, 10, 0, 10)
        });
    }

    private static void AddRow(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(control, 0, row);
    }

    private LinkLabel MakeLink(string text, string url)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            LinkBehavior = LinkBehavior.NeverUnderline,
            Margin = new Padding(0, 0, 16, 0)
        };
        link.LinkClicked += (_, _) => OpenLink(url);
        return link;
    }

    private static string BuildDate(Assembly assembly)
    {
        try
        {
            return File.Exists(assembly.Location)
                ? File.GetLastWriteTime(assembly.Location).ToString("yyyy-MM-dd")
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            MessageBox.Show(this, "无法打开链接：" + error.Message, "关于 FEngEditor",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void SetLinkColors(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is LinkLabel link) link.LinkColor = EditorTheme.Accent;
            if (child.HasChildren) SetLinkColors(child);
        }
    }
}
