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
    private static Assembly ilwrapper;
    private static string loadedDirectory;
    private static bool devilResolverSet;
    public static string WorkerHostPath { get; set; }
    private const uint DevIlPng = 0x042A;
    private const uint DevIlDds = 0x0437;
    private const uint DevIlDxtcFormat = 0x705;

    [DllImport("DevIL.dll", EntryPoint = "ilInit", CallingConvention = CallingConvention.StdCall)]
    private static extern void ilInit();
    [DllImport("DevIL.dll", EntryPoint = "ilGenImage", CallingConvention = CallingConvention.StdCall)]
    private static extern uint ilGenImage();
    [DllImport("DevIL.dll", EntryPoint = "ilBindImage", CallingConvention = CallingConvention.StdCall)]
    private static extern void ilBindImage(uint image);
    [DllImport("DevIL.dll", EntryPoint = "ilDeleteImage", CallingConvention = CallingConvention.StdCall)]
    private static extern void ilDeleteImage(uint image);
    [DllImport("DevIL.dll", EntryPoint = "ilLoadL", CallingConvention = CallingConvention.StdCall)]
    private static extern bool ilLoadL(uint type, IntPtr data, uint size);
    [DllImport("DevIL.dll", EntryPoint = "ilSaveL", CallingConvention = CallingConvention.StdCall)]
    private static extern uint ilSaveL(uint type, IntPtr data, uint size);
    [DllImport("DevIL.dll", EntryPoint = "ilSetInteger", CallingConvention = CallingConvention.StdCall)]
    private static extern void ilSetInteger(uint mode, int value);
    [DllImport("DevIL.dll", EntryPoint = "ilGetError", CallingConvention = CallingConvention.StdCall)]
    private static extern int ilGetError();

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

    private static Assembly IlRuntime
    {
        get
        {
            lock (Gate)
            {
                var directory = DirectoryPath;
                Validate(directory);
                if (ilwrapper != null) return ilwrapper;
                var file = Path.Combine(directory, "ILWrapper.dll");
                if (!File.Exists(file))
                    throw new FileNotFoundException("Binary 运行库缺少 ILWrapper.dll", file);
                var context = new RuntimeContext(directory);
                ilwrapper = context.LoadFromAssemblyPath(file);
                NativeLibrary.SetDllImportResolver(ilwrapper, (name, _, _) =>
                {
                    var path = Path.Combine(directory, name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll");
                    return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
                });
                return ilwrapper;
            }
        }
    }

    /// <summary>
    /// Read-only TPK catalog for the texture resolver. Binary objects stay alive through
    /// <see cref="BinaryTextureInfo.Owner"/> and are never written back here.
    /// </summary>
    public static List<BinaryTextureInfo> LoadTextures(string archivePath) =>
        LoadTextures(archivePath, out _);

    public static List<BinaryTextureInfo> LoadTextures(string archivePath, out bool hasOuterCompression)
    {
        archivePath = Path.GetFullPath(archivePath);
        var result = new List<BinaryTextureInfo>();
        var asm = Runtime;
        using var source = File.OpenRead(archivePath);
        using var header = new BinaryReader(source, System.Text.Encoding.UTF8, leaveOpen: true);
        var marker = source.Length >= 4 ? header.ReadUInt32() : 0;
        source.Position = 0;
        var codecType = asm.GetType("Nikki.Reflection.Enum.LZCompressionType");
        hasOuterCompression = Enum.IsDefined(codecType, Enum.ToObject(codecType, marker));
        using var decoded = hasOuterCompression
            ? new MemoryStream(Decompress(header.ReadBytes(checked((int)source.Length))), writable: false)
            : null;
        using var reader = new BinaryReader((Stream)decoded ?? source, System.Text.Encoding.UTF8, leaveOpen: true);
        var index = ContainerBlockIndex.Scan(reader.BaseStream, archivePath, source.Length, null, hashBlocks: false);
        var offsets = index.Blocks
            .Where(block => block.ChunkId is 0xB3300000 or 0x0003A100 or 0x0003BD00)
            .Select(block => block.Offset).ToList();
        if (offsets.Count == 0) return result;

        // Datamap.Load opens a relative MainLog.txt and parses unrelated resources. Invoke
        // its TPK manager with the same ordered block offsets instead.
        var dbType = asm.GetType("Nikki.Support.MostWanted.Datamap");
        var db = Activator.CreateInstance(dbType);
        var manager = dbType.GetProperty("TPKBlocks").GetValue(db);
        var blockType = asm.GetType("Nikki.Core.Block");
        var blockIdType = asm.GetType("Nikki.Reflection.Enum.BinBlockID");
        var tpkBlocks = Activator.CreateInstance(blockType, Enum.Parse(blockIdType, "TPKBlocks"));
        blockType.GetProperty("Offsets").SetValue(tpkBlocks, offsets);
        try
        {
            manager.GetType().GetMethod("Disassemble", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(BinaryReader), blockType }, null)
                .Invoke(manager, new[] { (object)reader, tpkBlocks });
        }
        catch (TargetInvocationException e)
        {
            throw new InvalidDataException("Binary: " + e.InnerException?.Message, e.InnerException);
        }

        // TPKBlockManager processes TPKBlocks offsets in order and skips the texture-page
        // setting blocks. The ordinal mapping is only trustworthy when both counts agree; a
        // mismatch leaves the block offset unknown instead of guessing.
        var tpkBlockOffsets = new List<long>();
        foreach (var offset in offsets)
        {
            reader.BaseStream.Position = offset;
            if (reader.ReadUInt32() == 0xB3300000) tpkBlockOffsets.Add(offset);
        }
        var tpks = ((IEnumerable)manager).Cast<object>().ToList();
        var confirmedOffsets = tpkBlockOffsets.Count == tpks.Count ? tpkBlockOffsets : null;

        for (var tpkOrdinal = 0; tpkOrdinal < tpks.Count; tpkOrdinal++)
        {
            var tpk = tpks[tpkOrdinal];
            var tpkName = Convert.ToString(tpk.GetType().GetProperty("CollectionName").GetValue(tpk));
            var textureOrdinal = 0;
            foreach (var texture in ((IEnumerable)tpk.GetType().GetProperty("Textures").GetValue(tpk)).Cast<object>())
            {
                result.Add(new BinaryTextureInfo
                {
                    Instance = texture,
                    Owner = db,
                    ArchivePath = archivePath,
                    ArchiveHasOuterCompression = hasOuterCompression,
                    TpkName = tpkName,
                    TpkOrdinal = tpkOrdinal,
                    TpkBlockOffset = confirmedOffsets?[tpkOrdinal],
                    TextureOrdinal = textureOrdinal++,
                    Name = Convert.ToString(texture.GetType().GetProperty("CollectionName").GetValue(texture)),
                    BinKey = Convert.ToUInt32(texture.GetType().GetProperty("BinKey").GetValue(texture)),
                    Width = Convert.ToInt32(texture.GetType().GetProperty("Width").GetValue(texture)),
                    Height = Convert.ToInt32(texture.GetType().GetProperty("Height").GetValue(texture)),
                    MipCount = Convert.ToInt32(texture.GetType().GetProperty("Mipmaps").GetValue(texture)),
                    Compression = Convert.ToString(texture.GetType().GetProperty("Compression").GetValue(texture)),
                });
            }
        }
        return result;
    }

    public static byte[] ExportTextureDds(BinaryTextureInfo texture, bool noPalette)
    {
        if (texture?.Instance == null) throw new ArgumentNullException(nameof(texture));
        return (byte[])Call(texture.Instance.GetType(), texture.Instance, "GetDDSArray", noPalette);
    }

    public static byte[] ExportTexturePng(BinaryTextureInfo texture) =>
        DecodeTextureDds(ExportTextureDds(texture, true));

    /// <summary>Decodes a stored DDS payload to PNG through the same DevIL/ILWrapper runtime.</summary>
    public static byte[] DecodeTextureDds(byte[] dds)
    {
        if (dds == null || dds.Length == 0) throw new ArgumentException("DDS 数据为空。", nameof(dds));
        var asm = IlRuntime;
        var imageType = asm.GetType("ILWrapper.Enums.ImageType");
        var pngType = Enum.Parse(imageType, "PNG");
        var image = Activator.CreateInstance(asm.GetType("ILWrapper.Image"), new object[] { dds });
        try
        {
            using var stream = new MemoryStream();
            image.GetType().GetMethod("Save", new[] { typeof(Stream), imageType })
                .Invoke(image, new object[] { stream, pngType });
            return stream.ToArray();
        }
        finally
        {
            if (image is IDisposable disposable) disposable.Dispose();
        }
    }

    /// <summary>
    /// Encodes a PNG as a compressed DDS using the same DevIL build that Binary uses. The caller
    /// must request the original TPK format (DXTC1/DXTC3/DXTC5); uncompressed/palette formats are
    /// intentionally rejected until their write-back path is verified.
    /// </summary>
    public static byte[] EncodeTextureDds(byte[] pngData, string compression)
    {
        if (pngData == null || pngData.Length == 0)
            throw new ArgumentException("PNG 数据为空。", nameof(pngData));
        var normalized = (compression ?? "").ToUpperInvariant();
        var format = normalized switch
        {
            "TEXCOMP_DXTC1" => 0x706,
            "TEXCOMP_DXTC3" => 0x708,
            "TEXCOMP_DXTC5" => 0x70A,
            _ => throw new NotSupportedException(
                $"暂不支持把 PNG 写成 {compression}；目前支持 TEXCOMP_DXTC1/DXTC3/DXTC5。")
        };
        lock (Gate)
        {
            EnsureDevILResolver();
            var directory = DirectoryPath ?? throw new NotSupportedException(
                "请先在容器窗口配置 Binary 2.9 的安装目录（需要 DevIL.dll）。");
            if (!File.Exists(Path.Combine(directory, "DevIL.dll")))
                throw new FileNotFoundException("Binary 运行库缺少 DevIL.dll", Path.Combine(directory, "DevIL.dll"));
            ilInit();
            var image = ilGenImage();
            if (image == 0) throw new InvalidDataException("DevIL 无法创建图像。");
            ilBindImage(image);
            try
            {
                var handle = GCHandle.Alloc(pngData, GCHandleType.Pinned);
                try
                {
                    if (!ilLoadL(DevIlPng, handle.AddrOfPinnedObject(), (uint)pngData.Length))
                        throw new InvalidDataException("DevIL 无法加载 PNG：" + ilGetError());
                }
                finally { handle.Free(); }
                ilSetInteger(DevIlDxtcFormat, format);
                var setError = ilGetError();
                if (setError != 0)
                    throw new InvalidDataException("DevIL 不支持该 DXT 格式：" + setError);
                var size = ilSaveL(DevIlDds, IntPtr.Zero, 0);
                if (size == 0 || size > int.MaxValue)
                    throw new InvalidDataException("DevIL 无法写出 DDS：" + ilGetError());
                var output = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (ilSaveL(DevIlDds, output, size) == 0)
                        throw new InvalidDataException("DevIL 无法写出 DDS：" + ilGetError());
                    var dds = new byte[size];
                    Marshal.Copy(output, dds, 0, (int)size);
                    var expected = normalized switch
                    {
                        "TEXCOMP_DXTC1" => "DXT1",
                        "TEXCOMP_DXTC3" => "DXT3",
                        _ => "DXT5",
                    };
                    var fourCc = System.Text.Encoding.ASCII.GetString(dds, 84, 4);
                    if (fourCc != expected)
                        throw new InvalidDataException($"DDS 格式不符：期望 {expected}，实际 {fourCc}。");
                    return dds;
                }
                finally { Marshal.FreeHGlobal(output); }
            }
            finally { ilDeleteImage(image); }
        }
    }

    private static void EnsureDevILResolver()
    {
        if (devilResolverSet) return;
        NativeLibrary.SetDllImportResolver(typeof(BinaryRuntime).Assembly, (name, assembly, path) =>
        {
            if (!string.Equals(name, "DevIL.dll", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
            var directory = DirectoryPath;
            if (string.IsNullOrWhiteSpace(directory)) return IntPtr.Zero;
            var file = Path.Combine(directory, "DevIL.dll");
            return File.Exists(file) ? NativeLibrary.Load(file) : IntPtr.Zero;
        });
        devilResolverSet = true;
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
        if (ContainerTextureWriteBack.TryRunWorker(args, out exitCode)) return true;
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

    internal static Assembly RuntimeAssembly => Runtime;

    internal static object CreateOptions(string path) =>
        Activator.CreateInstance(Runtime.GetType("Nikki.Core.Options"), path, "", false, false);

    internal static object Call(Type type, object instance, string method, params object[] args)
    {
        try { return type.InvokeMember(method, BindingFlags.Public | BindingFlags.Static |
            BindingFlags.Instance | BindingFlags.InvokeMethod, null, instance, args); }
        catch (TargetInvocationException e) { throw new InvalidDataException("Binary: " + e.InnerException?.Message, e.InnerException); }
    }
}
