using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FEngLib.Assets;

/// <summary>One texture override intentionally excluded from the current container copy.</summary>
public sealed record ContainerExportSkip(string Name, string Reason);

/// <summary>
/// One complete "generate container copy" plan. It carries the FNG write-back plan, the texture
/// overrides that target this baseline, skipped overrides, blockers and warnings so the UI can show
/// the final operation before anything is written.
/// </summary>
public sealed class ContainerExportPlan
{
    public string SourcePath { get; init; }
    public long SourceLength { get; init; }
    public string SourceSha256 { get; init; }
    public string OutputPath { get; init; }
    public ContainerWritePlan FngPlan { get; init; }
    public IReadOnlyList<ContainerTextureOverride> Textures { get; init; } = Array.Empty<ContainerTextureOverride>();
    public IReadOnlyList<ContainerExportSkip> Skipped { get; init; } = Array.Empty<ContainerExportSkip>();
    public string LedgerDirectory { get; set; }
    public List<string> Blockers { get; } = new();
    public List<string> Warnings { get; } = new();

    public bool HasFng => FngPlan?.Items.Any(item => item.IsRunnable) == true;
    public bool HasTextures => Textures.Count > 0;
    public bool IsExecutable => Blockers.Count == 0 && HasFngOrTextures &&
        (FngPlan == null || FngPlan.IsExecutable);
    public bool HasFngOrTextures => HasFng || HasTextures;
    public bool TextureFailureLeavesFngCopy => HasFng && HasTextures;

    public static ContainerExportPlan Build(string sourcePath, long sourceLength, string sourceSha256,
        string outputPath, ContainerWritePlan fngPlan, IEnumerable<ContainerTextureOverride> textureOverrides,
        bool binaryReady)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        outputPath = Path.GetFullPath(outputPath);
        var textures = new List<ContainerTextureOverride>();
        var skipped = new List<ContainerExportSkip>();
        var plan = new ContainerExportPlan
        {
            SourcePath = sourcePath,
            SourceLength = sourceLength,
            SourceSha256 = sourceSha256,
            OutputPath = outputPath,
            FngPlan = fngPlan,
            Textures = textures,
            Skipped = skipped
        };

        if (ContainerSession.PathsEqual(sourcePath, outputPath))
            plan.Blockers.Add("输出副本不能与基线容器相同。");
        var extension = Path.GetExtension(sourcePath);
        if (!string.Equals(Path.GetExtension(outputPath), extension, StringComparison.OrdinalIgnoreCase))
            plan.Blockers.Add($"输出副本必须保留容器扩展名 {extension}。");
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
            plan.Blockers.Add("输出位置已存在，请选择新的文件名。");

        if (fngPlan != null)
        {
            if (!ContainerSession.PathsEqual(fngPlan.OriginalPath, sourcePath))
                plan.Blockers.Add("FNG 写回计划使用的基线与当前工程不一致。");
            if (!ContainerSession.PathsEqual(fngPlan.WorkingCopyPath, outputPath))
                plan.Blockers.Add("FNG 写回计划的输出位置与当前选择不一致。");
            if (!string.Equals(fngPlan.WorkingCopySha256, sourceSha256, StringComparison.OrdinalIgnoreCase))
                plan.Blockers.Add("FNG 写回计划的基线摘要与当前工程不一致。");
            if (!fngPlan.IsExecutable)
                plan.Warnings.Add("FNG 写回计划当前不可执行，请先处理上方阻断项。");
        }

        foreach (var texture in textureOverrides ?? Array.Empty<ContainerTextureOverride>())
        {
            if (texture == null) continue;
            var name = string.IsNullOrWhiteSpace(texture.RequestName)
                ? texture.RequestId.ToString("X8")
                : texture.RequestName;
            if (!texture.HasTarget)
            {
                skipped.Add(new ContainerExportSkip(name, "缺少目标 TPK/纹理身份，无法写回。"));
                plan.Warnings.Add($"贴图 {name} 缺少目标身份，已跳过。");
                continue;
            }
            if (!ContainerSession.PathsEqual(texture.TargetArchivePath, sourcePath))
            {
                skipped.Add(new ContainerExportSkip(name,
                    $"目标在外部档案中，本次容器副本不包含：{texture.TargetArchivePath}"));
                plan.Warnings.Add($"贴图 {name} 来自外部档案，已跳过。");
                continue;
            }
            var compression = texture.TargetCompression?.ToUpperInvariant();
            if (compression is not ("TEXCOMP_DXTC1" or "TEXCOMP_DXTC3" or "TEXCOMP_DXTC5"))
            {
                plan.Blockers.Add($"贴图 {name} 的格式 {texture.TargetCompression ?? "(未知)"} 暂不支持写回。");
                continue;
            }
            if (texture.PngData == null || texture.PngData.Length == 0)
            {
                plan.Blockers.Add($"贴图 {name} 的 PNG 数据为空。");
                continue;
            }
            textures.Add(texture);
        }

        if (textures.Count > 0 && !binaryReady)
            plan.Blockers.Add("未配置 Binary 2.9 运行库，无法写回贴图。");
        if (fngPlan == null && textures.Count == 0)
            plan.Blockers.Add("工程没有可导出的 FNG 修改或贴图覆盖。");
        if (plan.TextureFailureLeavesFngCopy)
            plan.Warnings.Add("FNG 与贴图分两阶段执行；如果贴图阶段失败，FNG 修改仍会保留在输出副本中。");
        return plan;
    }
}
