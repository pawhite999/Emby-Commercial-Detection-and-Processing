using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CommDetect.Core;

namespace CommDetect.IO;

/// <summary>
/// Translates ProcessingConfig settings into FFmpeg command-line arguments.
/// Handles video/audio codec selection, hardware acceleration, filters,
/// container format, and all the myriad FFmpeg options users might want.
/// </summary>
public class FFmpegCommandBuilder
{
    private readonly ProcessingConfig _config;
    private readonly string _ffmpegPath;

    public FFmpegCommandBuilder(ProcessingConfig config, string ffmpegPath)
    {
        _config = config;
        _ffmpegPath = ffmpegPath;
    }

    /// <summary>
    /// Build FFmpeg arguments for a simple remux (container change, no re-encoding).
    /// </summary>
    public string BuildRemuxArgs(string inputPath, string outputPath)
    {
        var args = new List<string>
        {
            "-hide_banner",
            $"-loglevel {_config.FFmpegLogLevel}",
            "-y",
            $"-i \"{inputPath}\"",
            "-map 0",        // Copy all streams
            "-c copy",       // Stream copy everything
        };

        AddMetadataArgs(args, inputPath);
        AddSubtitleArgs(args);
        args.Add($"\"{outputPath}\"");

        return string.Join(" ", args);
    }

    /// <summary>
    /// Build FFmpeg arguments for concatenating program segments (cut mode=concat).
    /// Uses the concat demuxer with a file list.
    /// </summary>
    public string BuildConcatArgs(string concatListPath, string outputPath)
    {
        var args = new List<string>
        {
            "-hide_banner",
            $"-loglevel {_config.FFmpegLogLevel}",
            "-y",
            "-f concat",
            "-safe 0",
            $"-i \"{concatListPath}\"",
            "-c copy",
        };

        AddSubtitleArgs(args);
        args.Add($"\"{outputPath}\"");

        return string.Join(" ", args);
    }

    /// <summary>
    /// Build FFmpeg arguments for extracting a single segment via stream copy (same format, no re-encode).
    /// </summary>
    public string BuildSegmentExtractArgs(
        string inputPath, string outputPath, TimeSpan start, TimeSpan duration)
    {
        var args = new List<string>
        {
            "-hide_banner",
            $"-loglevel {_config.FFmpegLogLevel}",
            "-y",
            $"-ss {FormatTime(start)}",
            $"-i \"{inputPath}\"",
            $"-t {FormatTime(duration)}",
            "-c copy",
            "-avoid_negative_ts make_zero",
            $"\"{outputPath}\""
        };

        return string.Join(" ", args);
    }

    /// <summary>
    /// Build FFmpeg arguments for extracting and re-encoding a single segment.
    /// Used by the concat cut method when the output format or codecs differ from the source.
    /// Each segment is encoded independently; the final concat step can then use stream copy.
    /// </summary>
    public string BuildSegmentEncodeArgs(
        string inputPath, string outputPath, TimeSpan start, TimeSpan duration)
    {
        var args = new List<string>
        {
            "-hide_banner",
            $"-loglevel {_config.FFmpegLogLevel}",
            "-y",
            $"-ss {FormatTime(start)}",
            $"-i \"{inputPath}\"",
            $"-t {FormatTime(duration)}",
            "-avoid_negative_ts make_zero",
        };

        AddVideoEncodeArgs(args);
        AddAudioEncodeArgs(args);
        args.Add($"\"{outputPath}\"");

        return string.Join(" ", args);
    }

    /// <summary>
    /// Build FFmpeg arguments for smart cut (re-encode around cut points, copy the rest).
    /// This uses complex filtergraph to splice segments.
    /// </summary>
    public string BuildSmartCutArgs(
        string inputPath,
        string outputPath,
        List<(TimeSpan start, TimeSpan end, bool needsReencode)> segments)
    {
        // For smart cut, we build a complex filter that:
        // 1. Trims each segment
        // 2. Re-encodes segments near cut points
        // 3. Stream-copies segments in the middle of program content
        // This is implemented as multiple FFmpeg passes joined by concat

        // For now, build as a single-pass re-encode of selected segments
        // (the actual multi-pass optimization would be more complex)
        var args = new List<string>
        {
            "-hide_banner",
            $"-loglevel {_config.FFmpegLogLevel}",
            "-y",
            $"-i \"{inputPath}\"",
        };

        // Build select/trim filter
        var videoFilters = new List<string>();
        var audioFilters = new List<string>();

        // Build trim filters for each program segment
        for (int i = 0; i < segments.Count; i++)
        {
            var (start, end, _) = segments[i];
            videoFilters.Add($"[0:v]trim=start={FormatSeconds(start)}:end={FormatSeconds(end)},setpts=PTS-STARTPTS[v{i}]");
            audioFilters.Add($"[0:a]atrim=start={FormatSeconds(start)}:end={FormatSeconds(end)},asetpts=PTS-STARTPTS[a{i}]");
        }

        // Concat filter — inputs must be interleaved [v0][a0][v1][a1]... per segment
        string segInputs = string.Join("", Enumerable.Range(0, segments.Count).Select(i => $"[v{i}][a{i}]"));
        string concatFilter = $"{segInputs}concat=n={segments.Count}:v=1:a=1[outv][outa]";

        var allFilters = videoFilters.Concat(audioFilters).Append(concatFilter);
        args.Add($"-filter_complex \"{string.Join(";", allFilters)}\"");
        args.Add("-map \"[outv]\"");
        args.Add("-map \"[outa]\"");

        // Video encoding
        AddVideoEncodeArgs(args);

        // Audio encoding
        AddAudioEncodeArgs(args);

        AddMetadataArgs(args, inputPath);
        args.Add($"\"{outputPath}\"");

        return string.Join(" ", args);
    }

    /// <summary>
    /// Build FFmpeg arguments for full re-encode of program segments.
    /// </summary>
    public string BuildReencodeArgs(
        string inputPath, string outputPath,
        List<ContentSegment> programSegments)
    {
        var args = new List<string>
        {
            "-hide_banner",
            $"-loglevel {_config.FFmpegLogLevel}",
            "-y",
            $"-i \"{inputPath}\"",
        };

        // Build segment selection filter
        var videoFilters = new List<string>();
        var audioFilters = new List<string>();

        for (int i = 0; i < programSegments.Count; i++)
        {
            var seg = programSegments[i];
            videoFilters.Add($"[0:v]trim=start={FormatSeconds(seg.Start)}:end={FormatSeconds(seg.End)},setpts=PTS-STARTPTS[v{i}]");
            audioFilters.Add($"[0:a]atrim=start={FormatSeconds(seg.Start)}:end={FormatSeconds(seg.End)},asetpts=PTS-STARTPTS[a{i}]");
        }

        // Concat filter — inputs must be interleaved [v0][a0][v1][a1]... per segment
        string segInputs = string.Join("", Enumerable.Range(0, programSegments.Count).Select(i => $"[v{i}][a{i}]"));
        string concatFilter = $"{segInputs}concat=n={programSegments.Count}:v=1:a=1[outv][outa]";

        var allFilters = videoFilters.Concat(audioFilters).Append(concatFilter);

        // Add deinterlace and extra filters
        string? extraFilters = BuildVideoFilterChain();
        if (!string.IsNullOrEmpty(extraFilters))
        {
            allFilters = allFilters.Append($"[outv]{extraFilters}[outv2]");
            args.Add($"-filter_complex \"{string.Join(";", allFilters)}\"");
            args.Add("-map \"[outv2]\"");
        }
        else
        {
            args.Add($"-filter_complex \"{string.Join(";", allFilters)}\"");
            args.Add("-map \"[outv]\"");
        }

        args.Add("-map \"[outa]\"");

        // Add fade at cuts if configured
        // (would need to be integrated into the filter graph above for production use)

        AddVideoEncodeArgs(args);
        AddAudioEncodeArgs(args);
        AddMetadataArgs(args, inputPath);
        args.Add($"\"{outputPath}\"");

        return string.Join(" ", args);
    }

    /// <summary>
    /// Build FFmpeg arguments for adding chapter metadata to a file.
    /// </summary>
    public string BuildChapterEmbedArgs(string inputPath, string metadataPath, string outputPath)
    {
        var args = new List<string>
        {
            "-hide_banner",
            $"-loglevel {_config.FFmpegLogLevel}",
            "-y",
            $"-i \"{inputPath}\"",
            $"-i \"{metadataPath}\"",
            "-map_metadata 1",
            "-map_chapters 1",
            "-c copy",
            $"\"{outputPath}\""
        };

        return string.Join(" ", args);
    }

    // ── Private Helpers ─────────────────────────────────────────────────

    private void AddVideoEncodeArgs(List<string> args)
    {
        string codec = _config.VideoCodec;

        // Map hardware acceleration to actual codec names
        if (_config.HwAccel != HardwareAccel.None)
        {
            codec = _config.HwAccel switch
            {
                HardwareAccel.Nvenc => _config.VideoCodec.Contains("265") || _config.VideoCodec.Contains("hevc")
                    ? "hevc_nvenc" : "h264_nvenc",
                HardwareAccel.Qsv => _config.VideoCodec.Contains("265") || _config.VideoCodec.Contains("hevc")
                    ? "hevc_qsv" : "h264_qsv",
                HardwareAccel.Vaapi => _config.VideoCodec.Contains("265") || _config.VideoCodec.Contains("hevc")
                    ? "hevc_vaapi" : "h264_vaapi",
                HardwareAccel.VideoToolbox => _config.VideoCodec.Contains("265") || _config.VideoCodec.Contains("hevc")
                    ? "hevc_videotoolbox" : "h264_videotoolbox",
                HardwareAccel.Amf => _config.VideoCodec.Contains("265") || _config.VideoCodec.Contains("hevc")
                    ? "hevc_amf" : "h264_amf",
                _ => codec
            };

            // Add hwaccel init for VAAPI
            if (_config.HwAccel == HardwareAccel.Vaapi)
            {
                args.Insert(1, "-vaapi_device /dev/dri/renderD128");
                args.Add("-vf 'format=nv12,hwupload'");
            }
        }

        args.Add($"-c:v {codec}");

        if (codec != "copy")
        {
            // Preset (not applicable to all codecs)
            if (!string.IsNullOrEmpty(_config.VideoPreset) &&
                (codec.Contains("264") || codec.Contains("265") || codec.Contains("libsvt")))
            {
                args.Add($"-preset {_config.VideoPreset}");
            }

            // CRF
            if (_config.Crf > 0)
            {
                // NVENC uses -cq instead of -crf
                string crfFlag = codec.Contains("nvenc") ? "-cq" : "-crf";
                args.Add($"{crfFlag} {_config.Crf}");
            }

            // Resolution
            if (!string.IsNullOrEmpty(_config.Resolution))
            {
                args.Add($"-s {_config.Resolution}");
            }

            // Pixel format
            if (!string.IsNullOrEmpty(_config.PixelFormat))
            {
                args.Add($"-pix_fmt {_config.PixelFormat}");
            }
        }

        // Extra raw args
        if (!string.IsNullOrEmpty(_config.ExtraArgs))
        {
            args.Add(_config.ExtraArgs);
        }
    }

    private void AddAudioEncodeArgs(List<string> args)
    {
        args.Add($"-c:a {_config.AudioCodec}");

        if (_config.AudioCodec != "copy")
        {
            if (!string.IsNullOrEmpty(_config.AudioBitrate))
                args.Add($"-b:a {_config.AudioBitrate}");

            if (!string.IsNullOrEmpty(_config.AudioSampleRate))
                args.Add($"-ar {_config.AudioSampleRate}");

            if (!string.IsNullOrEmpty(_config.AudioChannels))
                args.Add($"-ac {_config.AudioChannels}");
        }

        if (_config.NormalizeAudio && _config.AudioCodec != "copy")
        {
            args.Add($"-af loudnorm=I={_config.NormalizeTarget}:TP=-1.5:LRA=11");
        }
    }

    private void AddSubtitleArgs(List<string> args)
    {
        switch (_config.CopySubtitles.ToLowerInvariant())
        {
            case "all":
                args.Add("-c:s copy");
                break;
            case "none":
                args.Add("-sn");
                break;
            case "first":
                args.Add("-map 0:s:0?");
                args.Add("-c:s copy");
                break;
            case "by_language":
                // This would require probing the file to find subtitle streams by language
                // For now, copy all and let FFmpeg handle it
                args.Add("-c:s copy");
                break;
        }
    }

    private void AddMetadataArgs(List<string> args, string inputPath)
    {
        if (_config.CopyMetadata)
        {
            args.Add("-map_metadata 0");
        }

        if (_config.AddProcessingInfo)
        {
            args.Add($"-metadata comment=\"Processed by CommDetect on {DateTime.Now:yyyy-MM-dd}\"");
        }
    }

    private string? BuildVideoFilterChain()
    {
        var filters = new List<string>();

        // Deinterlace
        if (_config.Deinterlace != DeinterlaceMode.None && _config.Deinterlace != DeinterlaceMode.Auto)
        {
            filters.Add(_config.Deinterlace switch
            {
                DeinterlaceMode.Yadif => "yadif",
                DeinterlaceMode.Bwdif => "bwdif",
                _ => ""
            });
        }
        else if (_config.Deinterlace == DeinterlaceMode.Auto)
        {
            // Use idet to detect interlaced content, then bwdif if needed
            filters.Add("bwdif=mode=send_field:parity=auto:deint=interlaced");
        }

        // Extra user-specified filters
        if (!string.IsNullOrEmpty(_config.ExtraVideoFilters))
        {
            filters.AddRange(_config.ExtraVideoFilters.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        filters.RemoveAll(string.IsNullOrEmpty);
        return filters.Count > 0 ? string.Join(",", filters) : null;
    }

    /// <summary>Get the file extension for the configured container format.</summary>
    public string GetOutputExtension(string originalPath)
    {
        return _config.ContainerFormat switch
        {
            ContainerFormat.Mkv => ".mkv",
            ContainerFormat.Mp4 => ".mp4",
            ContainerFormat.Ts => ".ts",
            ContainerFormat.Original => Path.GetExtension(originalPath),
            _ => ".mkv"
        };
    }

    /// <summary>Build the output filename from the pattern and config.</summary>
    public string BuildOutputPath(string inputPath)
    {
        string dir = Path.GetDirectoryName(inputPath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(inputPath);
        string ext = GetOutputExtension(inputPath).TrimStart('.');
        string mode = _config.Mode.ToString().ToLowerInvariant();

        string outputName = _config.OutputNamePattern
            .Replace("{name}", name)
            .Replace("{ext}", ext)
            .Replace("{mode}", mode)
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"));

        return Path.Combine(dir, outputName);
    }

    private static string FormatTime(TimeSpan ts) =>
        $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";

    private static string FormatSeconds(TimeSpan ts) =>
        ts.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
}
