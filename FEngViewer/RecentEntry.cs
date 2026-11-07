using System;
using System.IO;

namespace FEngViewer;

internal enum RecentEntryKind
{
    Project,
    Container,
    LooseFng
}

internal sealed record RecentEntry(RecentEntryKind Kind, string Path, DateTimeOffset LastUsedUtc)
{
    public string DisplayName
    {
        get
        {
            var name = System.IO.Path.GetFileName(Path);
            return string.IsNullOrWhiteSpace(name) ? Path : name;
        }
    }

    public string KindLabel => Kind switch
    {
        RecentEntryKind.Project => "工程",
        RecentEntryKind.Container => "容器",
        _ => "FNG",
    };

    public override string ToString() => $"{KindLabel} · {DisplayName}";
}
