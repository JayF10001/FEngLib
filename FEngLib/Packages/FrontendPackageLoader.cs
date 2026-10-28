using System.IO;
using FEngLib.Chunks;

namespace FEngLib.Packages;

/// <summary>
///     Responsible for loading <see cref="Package" /> instances
/// </summary>
public class FrontendPackageLoader
{
	public FrontendPackageLoader(HashResolver hashResolver)
	{
		HashResolver = hashResolver;
	}

	public HashResolver HashResolver { get; }

	/// <summary>
	///     Loads a data stream as a <see cref="Package" />.
	/// </summary>
	/// <param name="br">The <see cref="BinaryReader" /> for the data stream.</param>
	/// <returns>A new <see cref="Package" /> instance.</returns>
	public Package Load(BinaryReader br)
    {
        var start = br.BaseStream.Position;
        var package = new Package();
        var chunkReader = new FrontendChunkReader(package, br, HashResolver);

        foreach (var chunk in chunkReader.ReadMainChunks())
            switch (chunk)
            {
                case PackageHeaderChunk packageHeaderChunk:
                    ProcessPackageHeaderChunk(package, packageHeaderChunk);
                    break;
                case MessageDefinitionsChunk messageNamesChunk:
                    package.MessageDefinitions.AddRange(messageNamesChunk.Definitions);
                    break;
            }

        var end = br.BaseStream.Position;
        if (br.BaseStream.CanSeek)
        {
            br.BaseStream.Position = start;
            var body = br.ReadBytes(checked((int)(end - start)));
            br.BaseStream.Position = end;
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            new FrontendChunkWriter(package).WriteCanonical(writer);
            package.EncodingSnapshot = new FngEncodingSnapshot(body, stream.ToArray());
        }
        return package;
    }

    private void ProcessPackageHeaderChunk(Package package, PackageHeaderChunk packageHeaderChunk)
    {
        package.Filename = packageHeaderChunk.Filename;
        package.Name = packageHeaderChunk.Name;
    }
}
