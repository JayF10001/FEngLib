using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

namespace FEngLib.Assets;

/// <summary>Optional adapter for the installed Binary 2.9 / Nikki 1.6.5 runtime.
/// No Binary binaries or research directories are part of the application build.</summary>
public static class BinaryRuntime
{
    private static readonly object Gate = new();
    private static Assembly nikki;
    private static string loadedDirectory;
    public static string WorkerHostPath { get; set; }
    private static string SettingsFile => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "FEngViewer", "binary-runtime.json");

    public static string DirectoryPath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("FENGLIB_BINARY_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(path);
            return File.Exists(SettingsFile) ? JsonSerializer.Deserialize<string>(File.ReadAllText(SettingsFile)) : null;
        }
    }

    public static void Configure(string directory)
    {
        directory = Path.GetFullPath(directory);
        Validate(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
        AtomicJsonFile.Write(SettingsFile, directory);
    }

    private static void Validate(string directory)
    {
        if (directory == null) throw new NotSupportedException(
            "请先在容器窗口配置 Binary 2.9 的安装目录（需要 Nikki.dll 和 LZCompressLib.dll）。");
        foreach (var name in new[] { "Nikki.dll", "CoreExtensions.dll", "LZCompressLib.dll" })
            if (!File.Exists(Path.Combine(directory, name))) throw new FileNotFoundException("Binary 运行库缺少 " + name);
        if (AssemblyName.GetAssemblyName(Path.Combine(directory, "Nikki.dll")).Version != new Version(1, 6, 5, 0))
            throw new NotSupportedException("当前适配器验证的是 Binary 2.9 / Nikki 1.6.5.0，请选择相应安装目录。");
    }

    private static Assembly Runtime
    {
        get
        {
            lock (Gate)
            {
                var directory = DirectoryPath;
                Validate(directory);
                if (nikki != null)
                {
                    if (!ContainerSession.PathsEqual(directory, loadedDirectory))
                        throw new NotSupportedException("Binary 运行库目录已改变，请重启编辑器后使用新目录。");
                    return nikki;
                }
                loadedDirectory = directory;
                var context = new RuntimeContext(directory);
                nikki = context.LoadFromAssemblyPath(Path.Combine(directory, "Nikki.dll"));
                NativeLibrary.SetDllImportResolver(nikki, (name, _, _) =>
                {
                    var path = Path.Combine(directory, name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll");
                    return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
                });
                return nikki;
            }
        }
    }

    private sealed class RuntimeContext : AssemblyLoadContext
    {
        private readonly string directory;
        internal RuntimeContext(string directory) : base("FEng.Binary") => this.directory = directory;
        protected override Assembly Load(AssemblyName name)
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    internal static byte[] Decompress(byte[] bytes)
    {
        if (bytes.Length < 16) throw new InvalidDataException("压缩头不足 16 字节。");
        var storedLength = BitConverter.ToUInt32(bytes, 12);
        if (storedLength < 16 || storedLength > bytes.Length)
            throw new InvalidDataException("压缩头的存储长度超出输入边界。");
        var size = BitConverter.ToUInt32(bytes, 8);
        if (size == 0 || size > 512 * 1024 * 1024) throw new InvalidDataException("压缩头的解压长度超出支持范围。");
        var result = (byte[])Call(Runtime.GetType("Nikki.Utils.Interop"), null, "Decompress", bytes);
        if (result == null || result.Length != size) throw new InvalidDataException("Binary 解压结果长度不符。");
        return result;
    }

    internal static byte[] CompressJdlz(byte[] bytes)
    {
        var codec = Enum.Parse(Runtime.GetType("Nikki.Reflection.Enum.LZCompressionType"), "JDLZ");
        var result = (byte[])Call(Runtime.GetType("Nikki.Utils.Interop"), null, "Compress", bytes, codec);
        if (!Decompress(result).SequenceEqual(bytes)) throw new InvalidDataException("JDLZ 重压缩校验失败。");
        return result;
    }

    internal sealed class RebuildRequest
    {
        public string Source { get; set; }
        public string Candidate { get; set; }
        public string RuntimeDirectory { get; set; }
        public List<WriteBackPlanItem> Items { get; set; }
    }

    internal static void Rebuild(string source, string candidate, ContainerWritePlan plan)
    {
        Validate(DirectoryPath);
        if (WorkerHostPath == null) throw new NotSupportedException("当前宿主没有注册 Binary 重建进程入口。");
        var workspace = candidate + ".binary-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(workspace);
        try
        {
            var request = Path.Combine(workspace, "request.json");
            File.WriteAllText(request, JsonSerializer.Serialize(new RebuildRequest
            { Source = source, Candidate = candidate, RuntimeDirectory = DirectoryPath, Items = plan.Items }));
            var host = Path.GetFullPath(WorkerHostPath);
            var start = new ProcessStartInfo(host.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : host)
            { WorkingDirectory = workspace, CreateNoWindow = true, UseShellExecute = false };
            if (host.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(host);
            start.ArgumentList.Add("--feng-binary-worker"); start.ArgumentList.Add(request);
            using var process = Process.Start(start);
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidDataException(File.Exists(request + ".error")
                ? File.ReadAllText(request + ".error") : "Binary 重建进程失败：" + process.ExitCode);
        }
        finally { Directory.Delete(workspace, true); }
    }

    /// <summary>Call before starting the host's normal command parser or WinForms loop.</summary>
    public static bool TryRunWorker(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length != 2 || args[0] != "--feng-binary-worker") return false;
        try
        {
            var request = JsonSerializer.Deserialize<RebuildRequest>(File.ReadAllText(args[1]));
            Environment.SetEnvironmentVariable("FENGLIB_BINARY_DIRECTORY", request.RuntimeDirectory);
            RebuildInWorker(request);
        }
        catch (Exception error)
        {
            File.WriteAllText(args[1] + ".error", error.ToString()); exitCode = 1;
        }
        return true;
    }

    private static void RebuildInWorker(RebuildRequest request)
    {
        var asm = Runtime;
        object Options(string path) => Activator.CreateInstance(asm.GetType("Nikki.Core.Options"), path, "", false, false);
        var db = Activator.CreateInstance(asm.GetType("Nikki.Support.MostWanted.Datamap"));
        Call(db.GetType(), db, "Load", Options(request.Source));
        var texturesBefore = TextureFingerprint(db);
        var manager = db.GetType().GetProperty("FNGroups").GetValue(db);
        var mode = Enum.Parse(asm.GetType("Nikki.Reflection.Enum.SerializeType"), "Override");
        foreach (var item in request.Items)
        {
            using var reader = new BinaryReader(File.OpenRead(item.ReplacementPath));
            Call(manager.GetType(), manager, "Import", mode, reader);
        }
        File.Copy(request.Source, request.Candidate, false);
        // Nikki's >64 MiB stream path uses the executable directory for temporary files.
        // Reject that unverified path rather than let it write outside this worker directory.
        if (new FileInfo(request.Source).Length > (1 << 26))
            throw new NotSupportedException("Binary 重建暂支持不超过 64 MiB 的容器；较大容器仍可使用等长写回。");
        Call(db.GetType(), db, "Save", Options(request.Candidate));
        using (var original = File.OpenRead(request.Source))
        using (var reader = new BinaryReader(original))
            if (reader.ReadUInt32() == 0x5A4C444A)
                File.WriteAllBytes(request.Candidate, CompressJdlz(File.ReadAllBytes(request.Candidate)));
        var verify = Activator.CreateInstance(db.GetType());
        Call(verify.GetType(), verify, "Load", Options(request.Candidate));
        var texturesAfter = TextureFingerprint(verify);
        var changed = texturesBefore.Keys.Union(texturesAfter.Keys).FirstOrDefault(k =>
            !texturesBefore.TryGetValue(k, out var before) || !texturesAfter.TryGetValue(k, out var after) || before != after);
        if (changed != null)
            throw new InvalidDataException("重建后贴图像素或属性发生变化，拒绝发布：" + changed);
    }

    private static Dictionary<string, string> TextureFingerprint(object db)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var tpks = (IEnumerable)db.GetType().GetProperty("TPKBlocks").GetValue(db);
        foreach (var tpk in tpks)
        {
            var packageName = (string)tpk.GetType().GetProperty("CollectionName").GetValue(tpk);
            result.Add(packageName, "TPK");
            var textures = (IEnumerable)tpk.GetType().GetProperty("Textures").GetValue(tpk);
            var textureIndex = 0;
            foreach (var texture in textures)
            {
                var ordinal = textureIndex++;
                foreach (var prop in texture.GetType().GetProperties().OrderBy(p => p.Name))
                {
                    // Physical positions are regenerated by TPK assembly. Data includes both palette and pixels.
                    if (prop.GetIndexParameters().Length != 0 || prop.Name is "Offset" or "PaletteOffset") continue;
                    var type = prop.PropertyType;
                    if (!(type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(byte[]))) continue;
                    var value = prop.GetValue(texture);
                    var textureName = (string)texture.GetType().GetProperty("CollectionName").GetValue(texture);
                    var key = packageName + "/" + ordinal + ":" + textureName + "/" + prop.DeclaringType.FullName + "." + prop.Name;
                    result.Add(key, value is byte[] data ? Convert.ToHexString(SHA256.HashData(data)) :
                        Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
                }
            }
        }
        return result;
    }

    private static object Call(Type type, object instance, string method, params object[] args)
    {
        try { return type.InvokeMember(method, BindingFlags.Public | BindingFlags.Static |
            BindingFlags.Instance | BindingFlags.InvokeMethod, null, instance, args); }
        catch (TargetInvocationException e) { throw new InvalidDataException("Binary: " + e.InnerException?.Message, e.InnerException); }
    }
}
