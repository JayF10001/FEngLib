using System;
using System.IO;
using System.Text.Json;

namespace FEngViewer;

/// <summary>User layout preferences. Kept deliberately small: only what the split layout needs.</summary>
internal sealed class ViewerLayoutState
{
    public int LeftWidth { get; set; } = ViewerSettings.DefaultLeftWidth;
    public int RightWidth { get; set; } = ViewerSettings.DefaultRightWidth;
    public bool RightCollapsed { get; set; }
    public string PreviewAspect { get; set; } = "4:3";
    public string LeftMode { get; set; } = "tree";
    public bool SimpleTree { get; set; } = true;
    public bool DarkTheme { get; set; }
    public string[] RecentProjects { get; set; } = Array.Empty<string>();
    public string[] RecentContainers { get; set; } = Array.Empty<string>();
    public string[] RecentFiles { get; set; } = Array.Empty<string>();

    public ViewerLayoutState Clone() => new()
    {
        LeftWidth = LeftWidth,
        RightWidth = RightWidth,
        RightCollapsed = RightCollapsed,
        PreviewAspect = PreviewAspect,
        LeftMode = LeftMode
        ,SimpleTree = SimpleTree, DarkTheme = DarkTheme,
        RecentProjects = RecentProjects ?? Array.Empty<string>(),
        RecentContainers = RecentContainers ?? Array.Empty<string>(),
        RecentFiles = RecentFiles ?? Array.Empty<string>()
    };
}

/// <summary>
/// JSON settings beside the user's local app data. Every operation is best-effort: a missing,
/// unreadable or corrupt file falls back to defaults instead of breaking startup.
/// </summary>
internal static class ViewerSettings
{
    public const int DefaultLeftWidth = 270;
    public const int DefaultRightWidth = 330;

    public const int MinLeftWidth = 180;
    public const int MinRightWidth = 240;
    public const int MinCenterWidth = 320;
    public const int SplitterWidth = 6;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FEngViewer", "layout.json");

    public static ViewerLayoutState Load(string path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            if (!File.Exists(file)) return new ViewerLayoutState();
            var state = JsonSerializer.Deserialize<ViewerLayoutState>(File.ReadAllText(file));
            return Normalize(state, 0);
        }
        catch (Exception)
        {
            return new ViewerLayoutState();
        }
    }

    public static bool Save(ViewerLayoutState state, string path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(file, JsonSerializer.Serialize(state,
                new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Rejects nonsense values (corrupt file, DPI or resolution change).</summary>
    public static ViewerLayoutState Normalize(ViewerLayoutState state, int clientWidth)
    {
        var result = (state ?? new ViewerLayoutState()).Clone();
        result.LeftWidth = Math.Clamp(result.LeftWidth, MinLeftWidth, clientWidth > 0 ? clientWidth : 4000);
        result.RightWidth = Math.Clamp(result.RightWidth, MinRightWidth, clientWidth > 0 ? clientWidth : 4000);
        result.PreviewAspect = PreviewLayout.ModeName(PreviewLayout.ParseMode(result.PreviewAspect));
        result.LeftMode = string.Equals(result.LeftMode, "files", StringComparison.OrdinalIgnoreCase) ? "files" : "tree";
        if (clientWidth > 0) return Clamp(result, clientWidth);
        return result;
    }

    /// <summary>
    /// Makes the widths fit the given window width while keeping the centre usable. Pure so it can
    /// be checked without a window; the SplitContainer still clamps as a last resort.
    /// </summary>
    public static ViewerLayoutState Clamp(ViewerLayoutState state, int clientWidth)
    {
        var result = (state ?? new ViewerLayoutState()).Clone();
        var reserved = 2 * SplitterWidth;
        var available = Math.Max(MinLeftWidth + MinRightWidth + MinCenterWidth, clientWidth - reserved);

        var left = Math.Clamp(result.LeftWidth, MinLeftWidth, available);
        var right = result.RightCollapsed
            ? 0
            : Math.Clamp(result.RightWidth, MinRightWidth, available);

        // Keep the centre at least at its minimum width; give up side width, right first.
        var overflow = left + right + MinCenterWidth - available;
        if (overflow > 0 && !result.RightCollapsed)
        {
            var shrinkRight = Math.Min(overflow, Math.Max(0, right - MinRightWidth));
            right -= shrinkRight;
            overflow -= shrinkRight;
        }

        if (overflow > 0)
        {
            var shrinkLeft = Math.Min(overflow, Math.Max(0, left - MinLeftWidth));
            left -= shrinkLeft;
            overflow -= shrinkLeft;
        }

        result.LeftWidth = left;
        // Preserve the width used when expanded so un-collapsing restores something sensible.
        result.RightWidth = result.RightCollapsed
            ? Math.Max(MinRightWidth, Math.Clamp(result.RightWidth, MinRightWidth, available))
            : right;
        return result;
    }
}
