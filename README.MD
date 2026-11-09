# FEngEditor

**A graphical FNG and container editor for EA Black Box Need for Speed games.**

FEngEditor provides a visual workflow for inspecting and editing FNG interface resources, browsing LZC/BUN containers, replacing textures, and exporting modified container copies.

The project builds upon the original FEngLib tooling and is under active development.

## Features

### Visual FNG Editing

- Interactive FNG preview and object hierarchy
- Object selection, positioning, and property editing
- Editing support for supported interface properties
- Animation and layout preview tools
- Standalone FNG editing and saving

### Container and Project Workflow

- Browse LZC/BUN containers and their FNG resources
- Edit container FNGs through `.fengproj` project files
- Preserve original container baselines for verification and recovery
- Preview changes before exporting a modified container copy
- Backup, recovery, and file integrity checks

### Texture Editing

- Inspect texture references and source archives
- Preview and replace textures using PNG or supported DDS files
- Support compatible DXT1, DXT3, and DXT5 DDS textures
- Validate texture dimensions and formats
- Include supported texture replacements in container exports

Some operations require external Binary/Nikki and DevIL components. Feature availability depends on the resource format and target archive.

## Getting Started

### Editing a Standalone FNG

1. Launch FEngEditor.
2. Choose **Open FNG File**.
3. Select and edit objects in the visual editor.
4. Save the FNG or use **Save As** to create another file.

Standalone file saves include backup and recovery support.

### Editing an LZC/BUN Container

1. Choose **Open Container or Project**.
2. Select an LZC/BUN container.
3. Open the desired FNG resources and make your changes.
4. Save your work as a `.fengproj` project.
5. Export a modified container copy after reviewing the changes.

**Saving a project does not modify the original game container.** Exporting is a separate operation.

## Requirements

- Windows
- .NET 6 Windows Desktop Runtime for the current FEngEditor target
- .NET 6 SDK for building from source
- Optional external Binary/Nikki runtime for supported container reconstruction and texture operations

Compatibility may vary between games, versions, and resource formats. Always test exported files before using them in an important game installation.

## Building from Source

Clone the repository and build the solution:

```bash
git clone https://github.com/JayF10001/FEngLib.git
cd FEngLib
dotnet build FEngLib.sln -c Release
```

To launch the graphical editor from source on Windows:

```bash
dotnet run --project FEngViewer/FEngViewer.csproj -c Release
```

## Project Structure

| Project | Purpose |
|---|---|
| `FEngViewer` | FEngEditor graphical application |
| `FEngLib` | FNG parsing, serialization, and container services |
| `FEngCli` | Command-line FNG utilities |
| `FEngRender` | Rendering support |
| `FEngRender.GL` | OpenGL rendering components |
| `FEngTestBench` | Tests and validation utilities |

## Credits and Acknowledgements

FEngEditor builds upon the original [NFSTools/FEngLib](https://github.com/NFSTools/FEngLib) project and work by its original authors and contributors.

The project also uses third-party components, including SharpGL, Newtonsoft.Json, CommandLineParser, JetBrains.Annotations, and image-processing libraries.

See **Help → About FEngEditor** for additional credits and component information.

## License and Disclaimer

The current project distribution is marked **All rights reserved**. An explicit public redistribution license has not yet been established for this repository.

Original upstream code and third-party components remain subject to their respective copyrights and licenses.

This is an independent community project and is not affiliated with or endorsed by Electronic Arts or Black Box. Need for Speed and related trademarks belong to their respective owners.

No original game assets are distributed with this project. Users should back up their files and verify all exported content before deployment.

## Feedback

For bug reports, feature requests, and technical discussions, visit the [GitHub Issues page](https://github.com/JayF10001/FEngLib/issues).
