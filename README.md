# CommDetect

A cross-platform commercial detection tool written in C# targeting .NET 6/8. Analyzes TV recordings to identify commercial breaks using multiple signal detection methods, producing output files compatible with media servers like Plex, Jellyfin, Kodi, and more.

## Features

- **Multi-signal detection** — Combines black frame, scene change, silence, logo absence, and aspect ratio analysis for high accuracy
- **Cross-platform** — Runs on Windows, macOS (Intel & Apple Silicon), Linux (x64/ARM64/Alpine), and FreeBSD
- **Multiple output formats** — EDL, Comskip TXT, MKV chapters, JSON, FFmetadata
- **Configurable** — JSON-based configuration with fast/default/accurate presets
- **Docker support** — Ready-to-use container for TrueNAS, Unraid, and other server environments

## Quick Start

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download) (for building)
- [FFmpeg](https://ffmpeg.org/download.html) (must be installed and in PATH)

### Build & Run

```bash
# Clone and build
git clone https://github.com/yourname/CommDetect.git
cd CommDetect
dotnet build -c Release

# Analyze a recording
dotnet run --project src/CommDetect.CLI -- process /path/to/recording.ts

# Use the fast preset
dotnet run --project src/CommDetect.CLI -- process recording.ts --preset fast

# Output multiple formats
dotnet run --project src/CommDetect.CLI -- process recording.ts --format edl json mkvchapters

# Probe a media file
dotnet run --project src/CommDetect.CLI -- probe recording.ts

# Generate a config file to customize
dotnet run --project src/CommDetect.CLI -- config --output myconfig.json
dotnet run --project src/CommDetect.CLI -- process recording.ts --config myconfig.json
```

### Docker

```bash
# Build the image
docker build -t commdetect -f docker/Dockerfile .

# Run analysis
docker run -v /mnt/media:/media:ro -v ./output:/output \
    commdetect process /media/recording.ts --output-dir /output

# Or use docker-compose
MEDIA_DIR=/mnt/media OUTPUT_DIR=./output docker-compose -f docker/docker-compose.yml up
```

## Platform Support

| Platform | Status | Notes |
|----------|--------|-------|
| Windows x64 | ✅ Fully supported | |
| Windows ARM64 | ✅ Fully supported | |
| macOS x64 (Intel) | ✅ Fully supported | |
| macOS ARM64 (Apple Silicon) | ✅ Fully supported | .NET 6+ |
| Linux x64 (Debian/Ubuntu/Fedora/etc.) | ✅ Fully supported | |
| Linux ARM64 | ✅ Fully supported | Raspberry Pi 4+, etc. |
| Alpine Linux (musl) | ✅ Fully supported | Docker-friendly |
| FreeBSD/TrueNAS | ⚠️ Community support | Use Docker or jail with Linux base |

### FreeBSD / TrueNAS

The recommended approach for FreeBSD is to run CommDetect inside a Docker container or a jail with a Debian/Ubuntu base. See the Docker section above.

## Architecture

```
CommDetect.sln
├── src/
│   ├── CommDetect.Core         # Models, interfaces, scoring engine (netstandard2.0+)
│   ├── CommDetect.Analysis     # Signal detectors and pipeline orchestration
│   ├── CommDetect.IO           # FFmpeg integration and output writers
│   └── CommDetect.CLI          # Command-line interface
├── tests/
│   └── CommDetect.Tests        # Unit tests (xUnit)
├── docker/                     # Dockerfile and docker-compose
└── .github/workflows/          # CI/CD pipeline
```

### Detection Pipeline

1. **Probe** — Extract media metadata via FFprobe
2. **Extract** — Decode frames and audio via FFmpeg (streamed, not loaded into memory)
3. **Detect** — Run enabled signal detectors in parallel:
   - Black Frame — finds dark frame sequences at break boundaries
   - Scene Change — measures cut frequency (commercials have faster cuts)
   - Silence — detects audio silence at break boundaries
   - Logo Absence — learns the network bug and flags when it disappears
   - Aspect Ratio — detects letterboxing changes
4. **Classify** — Weighted scoring with temporal smoothing
5. **Output** — Write results in selected format(s)

### Configuration

Generate a default config and customize it:

```bash
commdetect config --output config.json
commdetect config --preset accurate --output config.json
```

Key settings to tune:

- `blackFrameLumaThreshold` — How dark is "black"? (default: 12, range 5-20)
- `sceneChangeRateThreshold` — Scene changes/minute for commercial (default: 12)
- `commercialThreshold` — Combined score cutoff (default: 0.45, lower = more sensitive)
- `minCommercialDurationSeconds` — Ignore short segments (default: 15s)
- Signal weights — Adjust per-detector influence on final score

## Cross-Platform Build

```bash
# Build for all platforms at once
chmod +x build-all.sh
./build-all.sh

# Or build for a specific platform
dotnet publish src/CommDetect.CLI/CommDetect.CLI.csproj \
    -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true

# Output: a single executable with no dependencies (except FFmpeg)
```

## Integration

### Plex / Jellyfin
CommDetect outputs Comskip-compatible `.txt` and `.edl` files. Configure your media server to look for these alongside recordings.

### Kodi / mpv
EDL files are automatically detected when placed next to the media file with the same base name.

### MKV Chapter Embedding
```bash
# Generate chapters
commdetect process recording.ts --format mkvchapters

# Embed into MKV
mkvmerge -o output.mkv --chapters recording.chapters.xml recording.mkv
```

## License

MIT
