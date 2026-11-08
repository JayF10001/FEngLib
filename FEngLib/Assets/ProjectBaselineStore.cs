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

public enum BaselineProbeStatus
{
    Match,
    Missing,
    LengthMismatch,
    Sha256Mismatch,
    Unreadable,
    InvalidPath
}

/// <summary>A non-throwing baseline check result used by recovery UI and diagnostics.</summary>
public sealed record BaselineProbe(string Path, BaselineProbeStatus Status, long? ActualLength,
    string ActualSha256, string Detail)
{
    public bool IsMatch => Status == BaselineProbeStatus.Match;
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

    public static bool IsCanonical(string path, string sha256, string originalFileName)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(originalFileName)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(path), CachePath(sha256, originalFileName),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    /// <summary>Returns a detailed, non-throwing check of one candidate file.</summary>
    public static BaselineProbe Probe(string path, long expectedLength, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new BaselineProbe(null, BaselineProbeStatus.InvalidPath, null, null, "未指定文件。");
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            return new BaselineProbe(path, BaselineProbeStatus.InvalidPath, null, null, "路径无效。");
        }

        long actualLength;
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
                return new BaselineProbe(full, BaselineProbeStatus.Missing, null, null, "文件不存在。");
            actualLength = info.Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or System.Security.SecurityException)
        {
            return new BaselineProbe(full, BaselineProbeStatus.Unreadable, null, null, error.Message);
        }

        if (actualLength != expectedLength)
            return new BaselineProbe(full, BaselineProbeStatus.LengthMismatch, actualLength, null,
                $"文件大小不符：实际 {actualLength:N0} 字节，期望 {expectedLength:N0} 字节。");
        if (string.IsNullOrWhiteSpace(expectedSha256))
            return new BaselineProbe(full, BaselineProbeStatus.Sha256Mismatch, actualLength, null,
                "工程记录缺少基线 SHA256。");

        try
        {
            using var stream = File.Open(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = SHA256.Create();
            var actualSha256 = Convert.ToHexString(sha.ComputeHash(stream));
            return string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase)
                ? new BaselineProbe(full, BaselineProbeStatus.Match, actualLength, actualSha256,
                    "文件与工程记录一致。")
                : new BaselineProbe(full, BaselineProbeStatus.Sha256Mismatch, actualLength, actualSha256,
                    "文件内容与工程记录不一致（SHA256 不同）。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or System.Security.SecurityException)
        {
            return new BaselineProbe(full, BaselineProbeStatus.Unreadable, actualLength, null, error.Message);
        }
    }

    public static bool Matches(string path, long length, string sha256)
    {
        if (string.IsNullOrWhiteSpace(path) || length < 0 || string.IsNullOrWhiteSpace(sha256))
            return false;
        return Probe(path, length, sha256).IsMatch;
    }

    /// <summary>
    /// Copies the source into the canonical content-addressed cache and returns the cache path.
    /// A verified cache entry is preferred so a project never keeps using the original game file
    /// when an independent baseline already exists.
    /// </summary>
    public static string Ensure(string sourcePath, long length, string sha256, string originalFileName = null)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        var target = CachePath(sha256,
            string.IsNullOrWhiteSpace(originalFileName) ? Path.GetFileName(sourcePath) : originalFileName);
        if (Matches(target, length, sha256)) return target;
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("源容器不存在。", sourcePath);
        if (!Matches(sourcePath, length, sha256))
            throw new InvalidDataException("源容器的大小或 SHA256 与工程记录不符。");

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
            string full;
            try { full = Path.GetFullPath(candidate); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
            {
                continue;
            }
            if (Matches(full, length, sha256)) return full;
        }

        var directory = Path.Combine(Root, (sha256 ?? "").ToUpperInvariant());
        try
        {
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    if (Matches(file, length, sha256)) return file;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Treat an unreadable cache directory exactly like a missing one; the caller reports
            // the detailed status through Probe when it opens the recovery dialog.
        }

        var expected = !string.IsNullOrWhiteSpace(hintPath) ? SafeFullPath(hintPath)
            : CachePath(sha256, originalFileName);
        throw new ProjectBaselineMissingException(expected, sha256 ?? "", length,
            CachePath(sha256, originalFileName));
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            return path;
        }
    }
}
