using System;
using System.Drawing;
using System.Windows.Forms;

namespace FEngViewer;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var iconStream = typeof(Program).Assembly.GetManifestResourceStream("FEngViewer.Assets.FEngEditor.ico");
        using var icon = new Icon(iconStream);
        using var window = new PackageView { Icon = icon };
        Application.Run(window);
    }
}
