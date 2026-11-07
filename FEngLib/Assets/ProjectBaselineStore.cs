using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace FEngLib.Assets;

public sealed class ProjectBaselineMissingException : IOException
{
    public ProjectBaselineMissingException(string expectedPath, string expectedSha256, long expectedLength,
        string cachePath, Exception inner = null)
        : base($"工程基线缺失或摘要不符：{expectedPath}（期望 SHA256 {expectedSha256}，{expectedLength} 字节；缓存 {cachePath}）", inner)
    {
        ExpectedPath = expectedPath;
        ExpectedSha256 = expectedSha256;
        ExpectedLength = expectedLength;
        CachePath = cachePath;
    }

    public string ExpectedPath { get; }
    public string ExpectedSha256 { get; }
    public long ExpectedLength { get; }
    public string CachePath { get; }
}

/// <summary>
/// Content-addressed, read-only container baselines used by .fengproj files. Keeping the baseline
/// outside the game directory means exporting and replacing the game file cannot invalidate a
/// project.
/// </summary>
public static class ProjectBaselineStore
{
    private const int BufferSize = 1024 * 1024;

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FEngViewer", "bases");

    public static string CachePath(string sha256, string originalFileName)
    {
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64 ||
            !sha256.All(character => Uri.IsHexDigit(character)))
            throw new ArgumentException("基线 SHA256 无效。", nameof(sha256));
        var directory = Path.Combine(Root, sha256.ToUpperInvariant());
        var name = Path.GetFileName(originalFileName ?? "");
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        if (string.IsNullOrWhiteSpace(name)) name = "container.bin";
        return Path.Combine(directory, name);
    }

    public static bool Matches(string path, long length, string sha256)
    {
        if (string.IsNullOrWhiteSpace(path) || length < 0 || string.IsNullOrWhiteSpace(sha256))
            return false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != length) return false;
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return string.Equals(Convert.ToHexString(sha.ComputeHash(stream)), sha256,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies the source into the content-addressed cache and returns the baseline path. If the
    /// source is already a valid cached baseline, it is returned unchanged.
    /// </summary>
    public static string Ensure(string sourcePath, long length, string sha256)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("源容器不存在。", sourcePath);
        if (!Matches(sourcePath, length, sha256))
            throw new InvalidDataException("源容器的大小或 SHA256 与工程记录不符。");
        if (IsUnderRoot(sourcePath)) return sourcePath;

        var target = CachePath(sha256, Path.GetFileName(sourcePath));
        if (Matches(target, length, sha256)) return target;
        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(directory)) throw new IOException("无法确定基线缓存目录。");
        Directory.CreateDirectory(directory);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = File.OpenRead(sourcePath))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(output, BufferSize);
            if (!Matches(temporary, length, sha256))
                throw new InvalidDataException("基线复制后的内容与 SHA256 不符。");
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
            return target;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Resolves a baseline from an explicit path, the cache, or the original source. All candidates
    /// are verified by length and SHA256.
    /// </summary>
    public static string Resolve(string sha256, long length, string originalFileName, string hintPath,
        string fallbackPath = null)
    {
        var candidates = new[]
        {
            hintPath,
            CachePath(sha256, originalFileName),
            fallbackPath,
        };
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var full = Path.GetFullPath(candidate);
            if (Matches(full, length, sha256)) return full;
        }

        var directory = Path.Combine(Root, (sha256 ?? "").ToUpperInvariant());
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                if (Matches(file, length, sha256)) return file;
        }
        var expected = !string.IsNullOrWhiteSpace(hintPath)
            ? Path.GetFullPath(hintPath)
            : CachePath(sha256, originalFileName);
        throw new ProjectBaselineMissingException(expected, sha256 ?? "", length,
            CachePath(sha256, originalFileName));
    }

    private static bool IsUnderRoot(string path)
    {
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
