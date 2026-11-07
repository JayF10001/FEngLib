using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FEngLib.Assets;

public sealed class ContainerTextureReplacement
{
    public string TpkName { get; init; }
    public int TpkOrdinal { get; init; } = -1;
    public int TextureOrdinal { get; init; } = -1;
    public string TextureName { get; init; }
    public uint BinKey { get; init; }
    public string Compression { get; init; }
    public byte[] PngData { get; init; }
}

public sealed class ContainerTextureWriteBackReport
{
    public string SourcePath { get; init; }
    public string OutputPath { get; init; }
    public string SourceSha256 { get; init; }
    public string OutputSha256 { get; init; }
    public int ReplacedCount { get; init; }
    public int VerifiedCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Worker-backed TPK texture replacement. The parent process writes PNG files and a request; the
/// worker loads the container with the Binary/Nikki runtime, imports a DXT DDS into the exact TPK
/// entry, saves a candidate, re-reads it and publishes a verified copy. The source is never written.
/// </summary>
public static class ContainerTextureWriteBack
{
    private const uint JdlzMarker = 0x5A4C444A;

    public static ContainerTextureWriteBackReport Write(string sourcePath, string outputPath,
        IReadOnlyList<ContainerTextureReplacement> replacements)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        outputPath = Path.GetFullPath(outputPath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("源容器不存在。", sourcePath);
        if (ContainerSession.PathsEqual(sourcePath, outputPath))
            throw new IOException("输出副本不能覆盖源容器。");
        if (replacements == null || replacements.Count == 0)
            throw new InvalidOperationException("没有可写回的贴图替换。");
        if (BinaryRuntime.DirectoryPath == null)
            throw new NotSupportedException("请先配置 Binary 2.9 运行库。");
        if (string.IsNullOrWhiteSpace(BinaryRuntime.WorkerHostPath))
            throw new NotSupportedException("当前宿主没有注册 Binary 工作进程入口。");
        foreach (var item in replacements)
        {
            if (item == null || item.PngData == null || item.PngData.Length == 0 ||
                string.IsNullOrWhiteSpace(item.TpkName) || string.IsNullOrWhiteSpace(item.TextureName))
                throw new InvalidDataException("贴图写回项缺少目标或 PNG 数据。");
        }
        using (var source = File.OpenRead(sourcePath))
        using (var reader = new BinaryReader(source))
        {
            if (source.Length >= 4)
            {
                var marker = reader.ReadUInt32();
                if (FngContainer.IsWholeFileWrapper(marker) && marker != JdlzMarker)
                    throw new NotSupportedException("贴图写回目前只支持未压缩或整文件 JDLZ 容器。");
            }
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);
        var sourceHashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)));
        var workspace = outputPath + ".textures-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(workspace);
        try
        {
            var request = new TextureWriteRequest
            {
                Source = sourcePath,
                Candidate = Path.Combine(workspace, "candidate.lzc"),
                Output = outputPath,
                RuntimeDirectory = BinaryRuntime.DirectoryPath,
            };
            for (var i = 0; i < replacements.Count; i++)
            {
                var item = replacements[i];
                var pngPath = Path.Combine(workspace, $"texture-{i}.png");
                File.WriteAllBytes(pngPath, item.PngData);
                request.Items.Add(new TextureWriteItem
                {
                    TpkName = item.TpkName,
                    TpkOrdinal = item.TpkOrdinal,
                    TextureOrdinal = item.TextureOrdinal,
                    TextureName = item.TextureName,
                    BinKey = item.BinKey,
                    Compression = item.Compression,
                    PngPath = pngPath,
                });
            }
            var requestPath = Path.Combine(workspace, "request.json");
            File.WriteAllText(requestPath, JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }));
            var host = Path.GetFullPath(BinaryRuntime.WorkerHostPath);
            var start = new ProcessStartInfo(host.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : host)
            {
                WorkingDirectory = workspace,
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            if (host.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(host);
            start.ArgumentList.Add("--feng-texture-worker");
            start.ArgumentList.Add(requestPath);
            using var process = Process.Start(start);
            if (process == null) throw new InvalidDataException("无法启动 Binary 贴图写回进程。");
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidDataException(File.Exists(requestPath + ".error")
                    ? File.ReadAllText(requestPath + ".error")
                    : "Binary 贴图写回进程失败：" + process.ExitCode);
            var reportPath = requestPath + ".report.json";
            if (!File.Exists(reportPath))
                throw new InvalidDataException("Binary 贴图写回进程没有返回报告。");
            var workerReport = JsonSerializer.Deserialize<TextureWriteReport>(File.ReadAllText(reportPath));
            if (workerReport == null || !File.Exists(outputPath))
                throw new InvalidDataException("Binary 贴图写回没有发布输出副本。");
            var sourceHashAfter = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)));
            if (!string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(sourceHashBefore, workerReport.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("源容器在贴图写回期间发生了变化。");
            var outputHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(outputPath)));
            if (!string.Equals(outputHash, workerReport.OutputSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("输出副本的 SHA256 与工作进程报告不符。");
            return new ContainerTextureWriteBackReport
            {
                SourcePath = sourcePath,
                OutputPath = outputPath,
                SourceSha256 = workerReport.SourceSha256,
                OutputSha256 = outputHash,
                ReplacedCount = workerReport.ReplacedCount,
                VerifiedCount = workerReport.VerifiedCount,
                Warnings = workerReport.Warnings ?? new List<string>(),
            };
        }
        finally
        {
            try { Directory.Delete(workspace, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static bool TryRunWorker(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length != 2 || args[0] != "--feng-texture-worker") return false;
        try
        {
            var request = JsonSerializer.Deserialize<TextureWriteRequest>(File.ReadAllText(args[1]))
                          ?? throw new InvalidDataException("贴图写回请求无法解析。");
            Environment.SetEnvironmentVariable("FENGLIB_BINARY_DIRECTORY", request.RuntimeDirectory);
            RunWorker(request, args[1]);
        }
        catch (Exception error)
        {
            File.WriteAllText(args[1] + ".error", error.ToString());
            exitCode = 1;
        }
        return true;
    }

    private static void RunWorker(TextureWriteRequest request, string requestPath)
    {
        var asm = BinaryRuntime.RuntimeAssembly;
        var dbType = asm.GetType("Nikki.Support.MostWanted.Datamap")
                     ?? throw new InvalidDataException("Binary 运行库缺少 MostWanted Datamap。");
        var db = Activator.CreateInstance(dbType)
                 ?? throw new InvalidDataException("无法创建 Binary Datamap。");
        BinaryRuntime.Call(dbType, db, "Load", BinaryRuntime.CreateOptions(request.Source));
        var manager = dbType.GetProperty("TPKBlocks")?.GetValue(db)
                      ?? throw new InvalidDataException("Binary Datamap 没有 TPKBlocks。");
        var before = Fingerprint(manager);
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replaced = 0;
        foreach (var item in request.Items)
        {
            var target = Find(manager, item);
            var oldCompression = Convert.ToString(target.Texture.GetType().GetProperty("Compression")?.GetValue(target.Texture));
            if (!string.Equals(oldCompression, item.Compression, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"贴图目标格式不符：{item.TpkName}/{item.TextureName} 期望 {item.Compression}，实际 {oldCompression}。");
            var dds = BinaryRuntime.EncodeTextureDds(File.ReadAllBytes(item.PngPath), item.Compression);
            var ddsPath = item.PngPath + ".dds";
            File.WriteAllBytes(ddsPath, dds);
            var textureType = asm.GetType("Nikki.Support.MostWanted.Class.Texture")
                              ?? throw new InvalidDataException("Binary 运行库缺少 MostWanted Texture。");
            var replacement = Activator.CreateInstance(textureType, item.TextureName, ddsPath, target.Tpk)
                              ?? throw new InvalidDataException("无法创建替换 Texture。");
            var width = Convert.ToInt32(replacement.GetType().GetProperty("Width")?.GetValue(replacement));
            var height = Convert.ToInt32(replacement.GetType().GetProperty("Height")?.GetValue(replacement));
            var oldWidth = Convert.ToInt32(target.Texture.GetType().GetProperty("Width")?.GetValue(target.Texture));
            var oldHeight = Convert.ToInt32(target.Texture.GetType().GetProperty("Height")?.GetValue(target.Texture));
            if (width != oldWidth || height != oldHeight)
                throw new InvalidDataException(
                    $"贴图尺寸不符：{item.TpkName}/{item.TextureName} 期望 {oldWidth}x{oldHeight}，实际 {width}x{height}。");
            target.Textures[target.Index] = replacement;
            expected.Add(target.Key);
            replaced++;
        }

        BinaryRuntime.Call(dbType, db, "Save", BinaryRuntime.CreateOptions(request.Candidate));
        using (var source = File.OpenRead(request.Source))
        using (var reader = new BinaryReader(source))
        {
            if (source.Length >= 4 && reader.ReadUInt32() == JdlzMarker)
                File.WriteAllBytes(request.Candidate, BinaryRuntime.CompressJdlz(File.ReadAllBytes(request.Candidate)));
        }

        var verify = Activator.CreateInstance(dbType)
                     ?? throw new InvalidDataException("无法创建验证 Datamap。");
        BinaryRuntime.Call(dbType, verify, "Load", BinaryRuntime.CreateOptions(request.Candidate));
        var verifyManager = dbType.GetProperty("TPKBlocks")?.GetValue(verify)
                            ?? throw new InvalidDataException("验证 Datamap 没有 TPKBlocks。");
        var after = Fingerprint(verifyManager);
        var changed = before.Keys.Union(after.Keys)
            .Where(key => !before.TryGetValue(key, out var a) || !after.TryGetValue(key, out var b) || a != b)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!changed.SetEquals(expected))
            throw new InvalidDataException("贴图写回后的指纹变化与目标不一致：" + string.Join(", ", changed));
        foreach (var item in request.Items)
        {
            var target = Find(verifyManager, item);
            var compression = Convert.ToString(target.Texture.GetType().GetProperty("Compression")?.GetValue(target.Texture));
            var dds = (byte[])BinaryRuntime.Call(target.Texture.GetType(), target.Texture, "GetDDSArray", true);
            var fourCc = Encoding.ASCII.GetString(dds, 84, 4);
            var expectedFourCc = item.Compression.ToUpperInvariant() switch
            {
                "TEXCOMP_DXTC1" => "DXT1",
                "TEXCOMP_DXTC3" => "DXT3",
                "TEXCOMP_DXTC5" => "DXT5",
                _ => throw new NotSupportedException("不支持写回的格式：" + item.Compression),
            };
            if (!string.Equals(compression, item.Compression, StringComparison.OrdinalIgnoreCase) ||
                fourCc != expectedFourCc)
                throw new InvalidDataException($"贴图写回验证失败：{item.TpkName}/{item.TextureName}。");
            var data = (byte[])target.Texture.GetType().GetProperty("Data")?.GetValue(target.Texture);
            var inputDds = File.ReadAllBytes(item.PngPath + ".dds");
            if (data == null || inputDds.Length < 128 || !data.AsSpan().SequenceEqual(inputDds.AsSpan(128)))
                throw new InvalidDataException($"贴图写回后的像素数据不一致：{item.TpkName}/{item.TextureName}。");
        }

        File.Move(request.Candidate, request.Output, overwrite: true);
        var report = new TextureWriteReport
        {
            SourcePath = request.Source,
            OutputPath = request.Output,
            SourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(request.Source))),
            OutputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(request.Output))),
            ReplacedCount = replaced,
            VerifiedCount = request.Items.Count,
            Warnings = new List<string>(),
        };
        File.WriteAllText(requestPath + ".report.json",
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static (object Tpk, IList Textures, int Index, object Texture, string Key) Find(object manager,
        TextureWriteItem item)
    {
        var tpks = ((IEnumerable)manager).Cast<object>().ToList();
        object tpk = null;
        if (item.TpkOrdinal >= 0 && item.TpkOrdinal < tpks.Count)
        {
            var candidate = tpks[item.TpkOrdinal];
            var name = Convert.ToString(candidate.GetType().GetProperty("CollectionName")?.GetValue(candidate));
            if (string.Equals(name, item.TpkName, StringComparison.OrdinalIgnoreCase)) tpk = candidate;
        }
        tpk ??= tpks.FirstOrDefault(candidate => string.Equals(
            Convert.ToString(candidate.GetType().GetProperty("CollectionName")?.GetValue(candidate)),
            item.TpkName, StringComparison.OrdinalIgnoreCase));
        if (tpk == null) throw new InvalidDataException("找不到 TPK：" + item.TpkName);
        var textures = (IList)tpk.GetType().GetProperty("Textures")!.GetValue(tpk)!;
        var index = -1;
        if (item.TextureOrdinal >= 0 && item.TextureOrdinal < textures.Count &&
            Matches(textures[item.TextureOrdinal], item))
            index = item.TextureOrdinal;
        if (index < 0)
        {
            for (var i = 0; i < textures.Count; i++)
                if (Matches(textures[i], item))
                {
                    index = i;
                    break;
                }
        }
        if (index < 0) throw new InvalidDataException("找不到贴图：" + item.TpkName + "/" + item.TextureName);
        var texture = textures[index]!;
        var actualTpkName = Convert.ToString(tpk.GetType().GetProperty("CollectionName")?.GetValue(tpk));
        return (tpk, textures, index, texture,
            actualTpkName + "/" + Convert.ToString(texture.GetType().GetProperty("CollectionName")?.GetValue(texture)));
    }

    private static bool Matches(object texture, TextureWriteItem item)
    {
        var name = Convert.ToString(texture.GetType().GetProperty("CollectionName")?.GetValue(texture));
        var binKey = Convert.ToUInt32(texture.GetType().GetProperty("BinKey")?.GetValue(texture));
        return string.Equals(name, item.TextureName, StringComparison.OrdinalIgnoreCase) &&
               (item.BinKey == 0 || binKey == item.BinKey);
    }

    private static Dictionary<string, string> Fingerprint(object manager)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tpk in (IEnumerable)manager)
        {
            var tpkName = Convert.ToString(tpk.GetType().GetProperty("CollectionName")?.GetValue(tpk))!;
            foreach (var texture in (IEnumerable)tpk.GetType().GetProperty("Textures")!.GetValue(tpk)!)
            {
                var name = Convert.ToString(texture.GetType().GetProperty("CollectionName")?.GetValue(texture))!;
                var dds = (byte[])BinaryRuntime.Call(texture.GetType(), texture, "GetDDSArray", true);
                result[tpkName + "/" + name] = Convert.ToHexString(SHA256.HashData(dds));
            }
        }
        return result;
    }

    private sealed class TextureWriteRequest
    {
        public string Source { get; set; }
        public string Candidate { get; set; }
        public string Output { get; set; }
        public string RuntimeDirectory { get; set; }
        public List<TextureWriteItem> Items { get; set; } = new();
    }

    private sealed class TextureWriteItem
    {
        public string TpkName { get; set; }
        public int TpkOrdinal { get; set; }
        public int TextureOrdinal { get; set; }
        public string TextureName { get; set; }
        public uint BinKey { get; set; }
        public string Compression { get; set; }
        public string PngPath { get; set; }
    }

    private sealed class TextureWriteReport
    {
        public string SourcePath { get; set; }
        public string OutputPath { get; set; }
        public string SourceSha256 { get; set; }
        public string OutputSha256 { get; set; }
        public int ReplacedCount { get; set; }
        public int VerifiedCount { get; set; }
        public List<string> Warnings { get; set; }
    }
}
