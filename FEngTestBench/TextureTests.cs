using System;
using System.IO;
using System.Linq;
using FEngLib.Assets;
using FEngLib.Packages;
using FEngViewer;

namespace FEngTestBench;

/// <summary>
/// Generated fixtures only: resolver order, status propagation and loose-PNG identity. Real TPK
/// metadata is exercised by the opt-in acceptance probe that needs a configured Binary runtime
/// and game archives.
/// </summary>
internal static class TextureTests
{
    // 1x1 transparent PNG.
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    public static int Run(string outputDirectory)
    {
        var root = Path.Combine(Path.GetFullPath(outputDirectory), "texture-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
        }

        try
        {
            Check(TextureResolver.NormalizeRequestName(@"textures\BasePoly.tga") == "BasePoly", "Backslash path is not normalized.");
            Check(TextureResolver.NormalizeRequestName("folder/BasePoly.png") == "BasePoly", "Slash path is not normalized.");
            Check(TextureResolver.NormalizeRequestName(null) == "", "Null request name is not normalized.");

            var png = Convert.FromBase64String(OnePixelPng);
            var loosePath = Path.Combine(root, "BasePoly.png");
            File.WriteAllBytes(loosePath, png);
            var hashedPath = Path.Combine(root, "Hashed (0x1234ABCD).png");
            File.WriteAllBytes(hashedPath, png);

            var request = new ResourceRequest { ID = 0xA8E2B755, Type = ResourceType.Image, Name = "BasePoly.tga" };
            var archive = new FakeSource("archive", (_, _) => Found(png, "archive :: TPK", "BASEPOLY"));
            var resolver = new TextureResolver(new ITextureSource[] { archive });
            resolver.SetTextureDirectory(root);

            var loose = resolver.Resolve(request);
            Check(loose.Status == TextureLookupStatus.Found, "Loose PNG was not found.");
            Check(loose.Source == loosePath, "Loose PNG did not report the actual file path.");
            Check(loose.SourceTexture == "BasePoly.png", "Loose PNG did not report the actual file name.");
            Check(loose.PngData.SequenceEqual(png), "Loose PNG bytes changed.");
            Check(archive.Calls == 0, "Archive was queried even though a loose PNG matched.");

            var hashedRequest = new ResourceRequest { ID = 0x1234ABCD, Type = ResourceType.Image, Name = "Missing.tga" };
            var hashed = resolver.Resolve(hashedRequest);
            Check(hashed.Status == TextureLookupStatus.Found && hashed.Source == hashedPath,
                "Loose PNG hash suffix was not used as the resource ID fallback.");

            var archiveOnly = new TextureResolver(new ITextureSource[] { archive });
            var entry = new BinaryTextureInfo
            {
                ArchivePath = Path.Combine(root, "FrontB.lzc"),
                TpkName = "FRONTENDTEXTURES",
                TpkOrdinal = 0,
                TpkBlockOffset = 0x12080,
                TextureOrdinal = 4,
                Name = "BASEPOLY",
                BinKey = 0xC6AFDD7E,
                Width = 32,
                Height = 32,
                Compression = "TEXCOMP_DXTC3",
            };
            var archiveHit = archiveOnly.Resolve(request);
            Check(archiveHit.Status == TextureLookupStatus.Found, "Archive hit was not returned.");
            Check(archiveHit.Entry == null, "Fake archive hit unexpectedly had an entry.");
            Check(archiveHit.Source == "archive :: TPK", "Archive hit lost its source label.");

            var metadataSource = new FakeSource("metadata", (_, _) => new TextureLookupResult
            {
                Status = TextureLookupStatus.Found,
                RequestedName = "BasePoly",
                RequestedId = request.ID,
                PngData = png,
                Source = entry.ArchivePath + " :: " + entry.TpkName,
                SourceTexture = entry.Name,
                Entry = entry,
                MatchingEntryCount = 2,
                Note = "same-name entries",
            });
            var metadataResolver = new TextureResolver(new ITextureSource[] { metadataSource });
            var metadata = metadataResolver.Resolve(request);
            Check(metadata.Entry == entry && metadata.Entry.TpkBlockOffset == 0x12080,
                "Entry metadata was not propagated by the resolver.");
            Check(metadata.MatchingEntryCount == 2 && metadata.Note == "same-name entries",
                "Duplicate-name metadata was not propagated by the resolver.");

            var overrideTexture = ContainerTextureOverride.Create(0x1000, request.ID, request.Name,
                "replacement.png", png);
            var overrideArchive = new FakeSource("archive", (_, _) => Found(png, "archive :: TPK", "BASEPOLY"));
            var overrideResolver = new TextureResolver(new ITextureSource[]
            {
                new ProjectTextureOverrideSource(0x1000, new[] { overrideTexture }),
                overrideArchive,
            });
            var overrideResult = overrideResolver.Resolve(request);
            Check(overrideResult.IsFound && overrideResult.IsProjectOverride &&
                overrideResult.Width == 1 && overrideResult.Height == 1 &&
                overrideResult.Compression == "PNG",
                "Project texture override did not win or lost its dimensions.");
            Check(overrideArchive.Calls == 0, "Archive was queried despite a project texture override.");

            var otherFngArchive = new FakeSource("archive", (_, _) => Found(png, "archive :: TPK", "BASEPOLY"));
            var otherFng = new TextureResolver(new ITextureSource[]
            {
                new ProjectTextureOverrideSource(0x2000, new[] { overrideTexture }),
                otherFngArchive,
            }).Resolve(request);
            Check(!otherFng.IsProjectOverride && otherFng.Source == "archive :: TPK",
                "Project texture override leaked to another FNG block.");

            var otherNameArchive = new FakeSource("archive", (_, _) => Found(png, "archive :: TPK", "BASEPOLY"));
            var otherName = new TextureResolver(new ITextureSource[]
            {
                new ProjectTextureOverrideSource(0x1000, new[] { overrideTexture }),
                otherNameArchive,
            }).Resolve(new ResourceRequest { ID = request.ID, Type = ResourceType.Image, Name = "Other.tga" });
            Check(!otherName.IsProjectOverride && otherName.Source == "archive :: TPK",
                "Project texture override ignored the request name.");

            var otherIdArchive = new FakeSource("archive", (_, _) => Found(png, "archive :: TPK", "BASEPOLY"));
            var otherId = new TextureResolver(new ITextureSource[]
            {
                new ProjectTextureOverrideSource(0x1000, new[] { overrideTexture }),
                otherIdArchive,
            }).Resolve(new ResourceRequest { ID = 0xDEADBEEF, Type = ResourceType.Image, Name = request.Name });
            Check(!otherId.IsProjectOverride && otherId.Source == "archive :: TPK",
                "Project texture override ignored the request ID.");

            var ambiguousSource = new FakeSource("ambiguous", (_, _) => new TextureLookupResult
            {
                Status = TextureLookupStatus.Ambiguous,
                RequestedName = "CARSELECT_MANUFACTURER",
                RequestedId = request.ID,
                Candidates = new[]
                {
                    new TextureCandidate { TextureName = "CARSELECT_MANUFACTURER_", BinKey = 1 },
                    new TextureCandidate { TextureName = "CARSELECT_MANUFACTURER_", BinKey = 2 },
                },
            });
            var fallbackSource = new FakeSource("fallback", (_, _) => null);
            var ambiguousResolver = new TextureResolver(new ITextureSource[] { ambiguousSource, fallbackSource });
            var ambiguous = ambiguousResolver.Resolve(request);
            Check(ambiguous.Status == TextureLookupStatus.Ambiguous && ambiguous.Candidates.Count == 2,
                "Ambiguous result was not preserved when no later source matched.");

            var laterFound = new FakeSource("later", (_, _) => Found(png, "later :: TPK", "BASEPOLY"));
            var foundWins = new TextureResolver(new ITextureSource[] { ambiguousSource, laterFound }).Resolve(request);
            Check(foundWins.Status == TextureLookupStatus.Found, "A later exact hit did not win over an earlier ambiguity.");

            var throwing = new FakeSource("throwing", (_, _) => throw new InvalidDataException("broken archive"));
            var errorResolver = new TextureResolver(new ITextureSource[] { throwing });
            var error = errorResolver.Resolve(request);
            Check(error.Status == TextureLookupStatus.Error && error.Message.Contains("broken archive"),
                "Source exception was not reported as an error.");

            var none = new TextureResolver(Array.Empty<ITextureSource>()).Resolve(request);
            Check(none.Status == TextureLookupStatus.NotFound, "Empty resolver did not report NotFound.");
            Check(new TextureResolver(Array.Empty<ITextureSource>()).Resolve(null).Status == TextureLookupStatus.NoRequest,
                "Null request did not report NoRequest.");
            var fontRequest = new ResourceRequest { ID = 1, Type = ResourceType.Font, Name = "FONT.ffn" };
            Check(new TextureResolver(Array.Empty<ITextureSource>()).Resolve(fontRequest).Status == TextureLookupStatus.WrongType,
                "Non-image request did not report WrongType.");

            Console.WriteLine($"Texture tests passed: {checks} checks.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Texture tests failed: " + error);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static TextureLookupResult Found(byte[] png, string source, string texture) => new()
    {
        Status = TextureLookupStatus.Found,
        RequestedName = texture,
        PngData = png,
        Source = source,
        SourceTexture = texture,
    };

    private sealed class FakeSource : ITextureSource
    {
        private readonly Func<string, uint, TextureLookupResult> _handler;

        public FakeSource(string name, Func<string, uint, TextureLookupResult> handler)
        {
            Name = name;
            _handler = handler;
        }

        public string Name { get; }
        public int Calls { get; private set; }

        public void Preload()
        {
        }

        public TextureLookupResult TryResolve(string textureName, uint resourceId)
        {
            Calls++;
            return _handler(textureName, resourceId);
        }
    }
}
