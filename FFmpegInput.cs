using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommDetect.Core;
using Microsoft.Extensions.Logging;

namespace CommDetect.IO;

/// <summary>
/// Runs ffprobe on a media file and parses its JSON output into a MediaInfo object.
/// Extracts duration, resolution, codec names, frame rate, and audio properties.
/// </summary>
public class FFprobeMediaProbe : IMediaProbe
{
    private readonly IFFmpegLocator _locator;
    private readonly ILogger<FFprobeMediaProbe>? _logger;

    public FFprobeMediaProbe(IFFmpegLocator locator,
        ILogger<FFprobeMediaProbe>? logger = null)
    {
        _locator = locator;
        _logger = logger;
    }

    public async Task<MediaInfo> ProbeAsync(string filePath,
        CancellationToken cancellationToken = default)
    {
        string ffprobePath = _locator.FindFFprobe()
            ?? throw new InvalidOperationException(
                "FFprobe not found. Install FFmpeg or use --ffmpeg-path.");

        // -show_streams  → per-stream details (codec, resolution, frame rate)
        // -show_format   → container details (duration, size)
        // -print_format json → machine-readable output
        string args = $"-v quiet -print_format json -show_streams -show_format \"{filePath}\"";

        _logger?.LogDebug("Running: {FFprobe} {Args}", ffprobePath, args);

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffprobe.");

        try
        {
            // Read stdout and stderr concurrently to avoid buffer-fill deadlocks.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            string json = await stdoutTask;
            string stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"FFprobe failed (exit code {process.ExitCode}): {stderr.Trim()}");
            }

            _logger?.LogDebug("FFprobe succeeded for {File}", filePath);
            return ParseOutput(filePath, json);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        finally
        {
            process.Dispose();
        }
    }

    // ── JSON Parsing ────────────────────────────────────────────────────

    private MediaInfo ParseOutput(string filePath, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var info = new MediaInfo { FilePath = filePath };

        // ── Streams ──────────────────────────────────────────────────────
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                string codecType = stream.GetStringOrEmpty("codec_type");

                if (codecType == "video" && string.IsNullOrEmpty(info.VideoCodec))
                {
                    info.VideoCodec = stream.GetStringOrEmpty("codec_name");
                    if (stream.TryGetProperty("width", out var w))  info.Width  = w.GetInt32();
                    if (stream.TryGetProperty("height", out var h)) info.Height = h.GetInt32();

                    // avg_frame_rate is more accurate for interlaced / VFR content.
                    // Fall back to r_frame_rate if avg is absent or "0/0".
                    string fps = stream.GetStringOrEmpty("avg_frame_rate");
                    if (string.IsNullOrEmpty(fps) || fps == "0/0")
                        fps = stream.GetStringOrEmpty("r_frame_rate");

                    info.FrameRate = ParseFraction(fps);

                    // Stream-level duration (used if format block is absent)
                    double streamDur = ParseDouble(stream.GetStringOrEmpty("duration"));
                    if (streamDur > 0 && info.Duration == TimeSpan.Zero)
                        info.Duration = TimeSpan.FromSeconds(streamDur);
                }
                else if (codecType == "audio" && string.IsNullOrEmpty(info.AudioCodec))
                {
                    info.AudioCodec = stream.GetStringOrEmpty("codec_name");

                    if (int.TryParse(stream.GetStringOrEmpty("sample_rate"),
                            NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out int sr))
                        info.AudioSampleRate = sr;

                    if (stream.TryGetProperty("channels", out var ch))
                        info.AudioChannels = ch.GetInt32();
                }
            }
        }

        // ── Format ───────────────────────────────────────────────────────
        // The format-level duration is more reliable than the stream-level one.
        if (root.TryGetProperty("format", out var format))
        {
            double dur = ParseDouble(format.GetStringOrEmpty("duration"));
            if (dur > 0)
                info.Duration = TimeSpan.FromSeconds(dur);
        }

        _logger?.LogInformation(
            "Probed: {W}x{H} @ {Fps:F3}fps, {Dur}, video={V}, audio={A}",
            info.Width, info.Height, info.FrameRate,
            info.Duration, info.VideoCodec, info.AudioCodec);

        return info;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a frame-rate fraction such as "30000/1001" or "25/1".
    /// Returns 0 on any parse failure or divide-by-zero.
    /// </summary>
    private static double ParseFraction(string value)
    {
        if (string.IsNullOrEmpty(value)) return 0;

        int slash = value.IndexOf('/');
        if (slash > 0)
        {
            if (double.TryParse(value[..slash],
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
             && double.TryParse(value[(slash + 1)..],
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double den)
             && den != 0)
                return num / den;
        }

        return double.TryParse(value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double plain) ? plain : 0;
    }

    private static double ParseDouble(string value)
        => double.TryParse(value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double d) ? d : 0;
}

// ── Stubs (to be implemented) ───────────────────────────────────────────────

public class FFmpegFrameExtractor : IFrameExtractor
{
    private readonly IFFmpegLocator _locator;
    private readonly ILogger<FFmpegFrameExtractor>? _logger;

    public FFmpegFrameExtractor(IFFmpegLocator locator,
        ILogger<FFmpegFrameExtractor>? logger = null)
    {
        _locator = locator;
        _logger = logger;
    }

    public IAsyncEnumerable<VideoFrame> ExtractFramesAsync(
        string filePath, double frameRate,
        CancellationToken cancellationToken)
        => throw new NotImplementedException(
            "FFmpegFrameExtractor is not yet implemented.");
}

public class FFmpegAudioExtractor : IAudioExtractor
{
    private readonly IFFmpegLocator _locator;
    private readonly ILogger<FFmpegAudioExtractor>? _logger;

    public FFmpegAudioExtractor(IFFmpegLocator locator,
        ILogger<FFmpegAudioExtractor>? logger = null)
    {
        _locator = locator;
        _logger = logger;
    }

    public IAsyncEnumerable<AudioWindow> ExtractAudioAsync(
        string filePath, double windowSeconds,
        CancellationToken cancellationToken)
        => throw new NotImplementedException(
            "FFmpegAudioExtractor is not yet implemented.");
}

// ── JsonElement extension used only in this file ────────────────────────────
internal static class JsonElementExtensions
{
    internal static string GetStringOrEmpty(this JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var prop) ? prop.GetString() ?? "" : "";
}
