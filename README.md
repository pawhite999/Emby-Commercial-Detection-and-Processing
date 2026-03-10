# CommDetect

A cross-platform commercial detection tool written in C# targeting .NET 6/8. Analyzes TV recordings to identify commercial breaks using multiple signal detection methods, producing output files compatible with media servers like Plex, Jellyfin, Kodi, and more.

## Features

- **Multi-signal detection** — Combines black frame, scene change, silence, logo absence, and aspect ratio analysis for high accuracy
- **Cross-platform** — Runs on Windows, macOS (Intel & Apple Silicon), Linux (x64/ARM64/Alpine), and FreeBSD
- **Multiple output formats** — EDL, Comskip TXT, MKV chapters, JSON, FFmetadata
- **Configurable** — JSON-based configuration with fast/default/accurate presets
- **Docker support** — Ready-to-use container for TrueNAS, Unraid, and other server environments

## Installation

[FFmpeg](https://ffmpeg.org/download.html) must be installed and available on your PATH.

### Linux / macOS

```bash
curl -fsSL https://raw.githubusercontent.com/pawhite999/Emby-Commercial-Detection-and-Processing/main/install.sh | bash
```

Installs to `~/.local/bin/commdetect`. Pass `--system` to install to `/usr/local/bin` instead (requires sudo):

```bash
curl -fsSL https://raw.githubusercontent.com/pawhite999/Emby-Commercial-Detection-and-Processing/main/install.sh | bash -s -- --system
```

### Windows (PowerShell)

```powershell
irm https://raw.githubusercontent.com/pawhite999/Emby-Commercial-Detection-and-Processing/main/install.ps1 | iex
```

Installs to `%LOCALAPPDATA%\Programs\commdetect\commdetect.exe` and adds it to your user PATH.

### Manual download

Download the binary for your platform from the [Releases page](https://github.com/pawhite999/Emby-Commercial-Detection-and-Processing/releases/latest), rename it to `commdetect` (or `commdetect.exe` on Windows), and place it somewhere on your PATH.

---

## Quick Start

```bash
# Analyze a recording (produces an EDL file by default)
commdetect process /path/to/recording.ts

# Use the fast preset (skips logo detection)
commdetect process recording.ts --preset fast

# Output multiple formats
commdetect process recording.ts --format edl json mkvchapters

# Probe a media file
commdetect probe recording.ts

# Generate a config file to customize
commdetect config --output commdetect.ini
commdetect process recording.ts
```

### Build from Source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/pawhite999/Emby-Commercial-Detection-and-Processing.git
cd Emby-Commercial-Detection-and-Processing
dotnet build -c Release
dotnet run --project src/CommDetect.CLI -- process /path/to/recording.ts
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
| macOS ARM64 (Apple Silicon) | ✅ Fully supported | |
| Linux x64 (Debian/Ubuntu/Fedora/etc.) | ✅ Fully supported | |
| Linux ARM64 | ✅ Fully supported | Raspberry Pi 4+, etc. |
| Alpine Linux (musl) | ✅ Fully supported | Docker-friendly |
| FreeBSD x64 | ⚙️ Build from source | TrueNAS CORE, FreeBSD 13+ |

### FreeBSD / TrueNAS CORE

The .NET SDK cannot cross-compile a self-contained FreeBSD binary from Linux, so no pre-built binary is provided. Build natively on the FreeBSD machine:

```bash
# Install prerequisites
pkg install dotnet-sdk-8.0 ffmpeg git

# Clone and build
git clone https://github.com/pawhite999/Emby-Commercial-Detection-and-Processing.git
cd Emby-Commercial-Detection-and-Processing
dotnet publish src/CommDetect.CLI/CommDetect.CLI.csproj \
    -c Release -r freebsd-x64 --self-contained true \
    -p:PublishSingleFile=true -o ./out
cp out/commdetect /usr/local/bin/commdetect
chmod +x /usr/local/bin/commdetect
```

**TrueNAS SCALE** is Linux-based — use the `linux-x64` binary instead.

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
