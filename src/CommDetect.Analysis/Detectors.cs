using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommDetect.Core;
using Microsoft.Extensions.Logging;

namespace CommDetect.Analysis;

// ── Shared helpers ───────────────────────────────────────────────────────────

/// <summary>
/// Utility methods shared by all FFmpeg-based signal detectors.
/// </summary>
internal static class DetectorHelpers
{
    /// <summary>
    /// Divides the video into equal-width windows and assigns 1.0 to any window
    /// that overlaps a detected segment, 0.0 otherwise.
    /// </summary>
    internal static List<(TimeSpan start, TimeSpan end, double score)> BuildWindowedScores(
        List<(TimeSpan start, TimeSpan end)> segments,
        TimeSpan totalDuration,
        double windowSeconds)
    {
        if (totalDuration == TimeSpan.Zero || windowSeconds <= 0)
            return new List<(TimeSpan, TimeSpan, double)>();

        int windowCount = (int)Math.Ceiling(totalDuration.TotalSeconds / windowSeconds);
        var windows = new List<(TimeSpan, TimeSpan, double)>(windowCount);

        for (int i = 0; i < windowCount; i++)
        {
            var wStart = TimeSpan.FromSeconds(i * windowSeconds);
            var wEnd   = TimeSpan.FromSeconds(
                Math.Min((i + 1) * windowSeconds, totalDuration.TotalSeconds));

            double score = 0.0;
            foreach (var (sStart, sEnd) in segments)
            {
                if (sStart < wEnd && sEnd > wStart)
                {
                    score = 1.0;
                    break;
                }
            }

            windows.Add((wStart, wEnd, score));
        }

        return windows;
    }

    /// <summary>
    /// Starts an FFmpeg process, drains stdout + stderr concurrently, and
    /// returns stderr (where FFmpeg writes its filter log output).
    /// Kills the process on cancellation.
    /// </summary>
    internal static async Task<string> RunFFmpegForStderrAsync(
        string ffmpegPath, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = ffmpegPath,
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg.");

        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            string stderr = await stderrTask;
            await stdoutTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"FFmpeg failed (exit code {process.ExitCode})");

            return stderr;
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

    /// <summary>
    /// Groups a sorted list of point-in-time events into contiguous segments by
    /// merging events that are no more than <paramref name="maxGapSeconds"/> apart.
    /// Segments shorter than <paramref name="minDurationSeconds"/> or containing
    /// fewer than <paramref name="minEventCount"/> events are discarded.
    ///
    /// This turns sparse scene-change or logo-absence timestamps into the same
    /// start/end segment format used by BuildWindowedScores, so that an entire
    /// commercial break (e.g. 636–877 s) scores 1.0 across all its windows rather
    /// than scoring near-zero in each individual 1-second window.
    /// </summary>
    internal static List<(TimeSpan start, TimeSpan end)> ClusterIntoSegments(
        List<TimeSpan> events,
        double maxGapSeconds  = 35.0,
        double minDurationSeconds = 30.0,
        int    minEventCount  = 5)
    {
        if (events.Count == 0) return new List<(TimeSpan, TimeSpan)>();

        events.Sort();

        var segments   = new List<(TimeSpan start, TimeSpan end)>();
        var segStart   = events[0];
        var segEnd     = events[0];
        int eventCount = 1;

        for (int i = 1; i < events.Count; i++)
        {
            if ((events[i] - segEnd).TotalSeconds <= maxGapSeconds)
            {
                segEnd = events[i];
                eventCount++;
            }
            else
            {
                if (eventCount >= minEventCount &&
                    (segEnd - segStart).TotalSeconds >= minDurationSeconds)
                    segments.Add((segStart, segEnd));

                segStart   = events[i];
                segEnd     = events[i];
                eventCount = 1;
            }
        }

        // Flush the last in-progress segment
        if (eventCount >= minEventCount &&
            (segEnd - segStart).TotalSeconds >= minDurationSeconds)
            segments.Add((segStart, segEnd));

        return segments;
    }

    // pts_time:X.XXXXX is written by FFmpeg's showinfo filter to stderr
    internal static readonly Regex ShowInfoPtsTime = new(
        @"pts_time:([\d.]+)",
        RegexOptions.Compiled);

    internal static double ParseDouble(string value)
        => double.TryParse(value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double d) ? d : 0;
}

// ── BlackFrameDetector ───────────────────────────────────────────────────────

/// <summary>
/// Detects black frames using FFmpeg's blackdetect filter.
///
/// FFmpeg writes lines like:
///   [blackdetect @ 0x...] black_start:10.5 black_end:11.2 black_duration:0.7
/// Score: 1.0 for any window overlapping a black segment, 0.0 otherwise.
/// </summary>
public class BlackFrameDetector : ISignalDetector
{
    private readonly IFFmpegLocator _ffmpegLocator;
    private readonly ILogger<BlackFrameDetector>? _logger;

    private static readonly Regex BlackdetectLine = new(
        @"black_start:(\d+(?:\.\d+)?)\s+black_end:(\d+(?:\.\d+)?)",
        RegexOptions.Compiled);

    public BlackFrameDetector(IFFmpegLocator ffmpegLocator,
        ILogger<BlackFrameDetector>? logger = null)
    {
        _ffmpegLocator = ffmpegLocator;
        _logger = logger;
    }

    public string Name          => "Black Frame";
    public SignalType SignalType => SignalType.BlackFrame;
    public bool RequiresVideo   => false; // runs FFmpeg directly; does not consume the VideoFrame stream
    public bool RequiresAudio   => false;

    public async Task<IReadOnlyList<(TimeSpan start, TimeSpan end, double score)>> AnalyzeAsync(
        IAsyncEnumerable<VideoFrame> frames,
        IAsyncEnumerable<AudioWindow> audio,
        MediaInfo mediaInfo,
        DetectionConfig config,
        CancellationToken cancellationToken)
    {
        string ffmpegPath = _ffmpegLocator.FindFFmpeg()
            ?? throw new InvalidOperationException("FFmpeg not found.");

        // d=0.05  → minimum 50 ms of black (catches brief inter-segment blacks)
        // pix_th  → per-pixel brightness threshold (0–1); below this = black pixel
        // pic_th  → fraction of pixels that must be black; 0.98 = almost entirely black
        const string filter = "blackdetect=d=0.05:pix_th=0.10:pic_th=0.98";
        string args = $"-i \"{mediaInfo.FilePath}\" -vf \"{filter}\" -an -f null -";

        _logger?.LogDebug("blackdetect: {FFmpeg} {Args}", ffmpegPath, args);

        string stderr = await DetectorHelpers.RunFFmpegForStderrAsync(ffmpegPath, args, cancellationToken);

        var segments = new List<(TimeSpan, TimeSpan)>();
        foreach (Match m in BlackdetectLine.Matches(stderr))
        {
            double start = DetectorHelpers.ParseDouble(m.Groups[1].Value);
            double end   = DetectorHelpers.ParseDouble(m.Groups[2].Value);
            segments.Add((TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end)));
            _logger?.LogDebug("  black: {Start:F3}s → {End:F3}s", start, end);
        }

        _logger?.LogInformation("BlackFrameDetector: {Count} black segment(s) in {File}",
            segments.Count, System.IO.Path.GetFileName(mediaInfo.FilePath));

        return DetectorHelpers.BuildWindowedScores(segments, mediaInfo.Duration, config.WindowSizeSeconds);
    }
}

// ── SilenceDetector ──────────────────────────────────────────────────────────

/// <summary>
/// Detects audio silence using FFmpeg's silencedetect filter.
///
/// FFmpeg writes to stderr:
///   [silencedetect @ 0x...] silence_start: 10.523
///   [silencedetect @ 0x...] silence_end: 11.823 | silence_duration: 1.3
///
/// Note: start and end appear on separate lines, so they are collected
/// independently then paired in order.
///
/// Score: 1.0 for any window overlapping a silence segment, 0.0 otherwise.
/// Broadcast commercials almost always have a brief audio gap (~0.5–1.5 s)
/// at the transition point, making this a strong corroborating signal
/// when combined with black frames.
/// </summary>
public class SilenceDetector : ISignalDetector
{
    private readonly IFFmpegLocator _ffmpegLocator;
    private readonly ILogger<SilenceDetector>? _logger;

    private static readonly Regex SilenceStartRx = new(
        @"silence_start:\s*(\d+(?:\.\d+)?)",
        RegexOptions.Compiled);

    private static readonly Regex SilenceEndRx = new(
        @"silence_end:\s*(\d+(?:\.\d+)?)",
        RegexOptions.Compiled);

    public SilenceDetector(IFFmpegLocator ffmpegLocator,
        ILogger<SilenceDetector>? logger = null)
    {
        _ffmpegLocator = ffmpegLocator;
        _logger = logger;
    }

    public string Name          => "Silence";
    public SignalType SignalType => SignalType.Silence;
    public bool RequiresVideo   => false;
    public bool RequiresAudio   => false; // runs FFmpeg directly; does not consume the AudioWindow stream

    public async Task<IReadOnlyList<(TimeSpan start, TimeSpan end, double score)>> AnalyzeAsync(
        IAsyncEnumerable<VideoFrame> frames,
        IAsyncEnumerable<AudioWindow> audio,
        MediaInfo mediaInfo,
        DetectionConfig config,
        CancellationToken cancellationToken)
    {
        string ffmpegPath = _ffmpegLocator.FindFFmpeg()
            ?? throw new InvalidOperationException("FFmpeg not found.");

        // noise = amplitude threshold below which a sample is considered silent.
        //   -50 dB is more permissive than FFmpeg's default (-60 dB), which helps
        //   with broadcast recordings that have light background hiss.
        // d = minimum duration (seconds) a silent stretch must last to be reported.
        //   0.3 s catches the brief audio gaps typical at commercial transitions.
        const string filter = "silencedetect=noise=-50dB:d=0.3";
        string args = $"-i \"{mediaInfo.FilePath}\" -af \"{filter}\" -vn -f null -";

        _logger?.LogDebug("silencedetect: {FFmpeg} {Args}", ffmpegPath, args);

        string stderr = await DetectorHelpers.RunFFmpegForStderrAsync(ffmpegPath, args, cancellationToken);

        var segments = ParseSilenceSegments(stderr, mediaInfo.Duration);

        _logger?.LogInformation("SilenceDetector: {Count} silence segment(s) in {File}",
            segments.Count, System.IO.Path.GetFileName(mediaInfo.FilePath));

        return DetectorHelpers.BuildWindowedScores(segments, mediaInfo.Duration, config.WindowSizeSeconds);
    }

    private List<(TimeSpan start, TimeSpan end)> ParseSilenceSegments(
        string stderr, TimeSpan totalDuration)
    {
        // Collect starts and ends in order — they appear on separate lines.
        var starts = new List<double>();
        var ends   = new List<double>();

        foreach (Match m in SilenceStartRx.Matches(stderr))
            starts.Add(DetectorHelpers.ParseDouble(m.Groups[1].Value));

        foreach (Match m in SilenceEndRx.Matches(stderr))
            ends.Add(DetectorHelpers.ParseDouble(m.Groups[1].Value));

        var segments = new List<(TimeSpan, TimeSpan)>();
        int count = Math.Min(starts.Count, ends.Count);

        for (int i = 0; i < count; i++)
        {
            _logger?.LogDebug("  silence: {Start:F3}s → {End:F3}s", starts[i], ends[i]);
            segments.Add((TimeSpan.FromSeconds(starts[i]), TimeSpan.FromSeconds(ends[i])));
        }

        // If the recording ends mid-silence there will be a start with no matching end.
        if (starts.Count > ends.Count)
        {
            double openStart = starts[^1];
            _logger?.LogDebug("  silence: {Start:F3}s → EOF", openStart);
            segments.Add((TimeSpan.FromSeconds(openStart), totalDuration));
        }

        return segments;
    }
}

// ── SceneChangeDetector ──────────────────────────────────────────────────────

/// <summary>
/// Detects scene changes using FFmpeg's built-in scene-score calculation.
///
/// FFmpeg command:
///   ffmpeg -i {file} -vf "select='gt(scene,0.35)',showinfo" -vsync vfr -an -f null -
///
/// Only frames whose scene-change score exceeds 0.35 are passed to showinfo,
/// which logs each one to stderr with a pts_time field.  Collecting those
/// timestamps and counting them per window gives a cut-density signal:
///   high density  → rapid editing  → likely commercial
///   low density   → steady footage → likely program content
///
/// Saturation at 3 cuts / window so that a single busy window scores 1.0.
/// </summary>
public class SceneChangeDetector : ISignalDetector
{
    private readonly IFFmpegLocator _ffmpegLocator;
    private readonly ILogger<SceneChangeDetector>? _logger;

    public SceneChangeDetector(IFFmpegLocator ffmpegLocator,
        ILogger<SceneChangeDetector>? logger = null)
    {
        _ffmpegLocator = ffmpegLocator;
        _logger = logger;
    }

    public string Name          => "Scene Change";
    public SignalType SignalType => SignalType.SceneChange;
    public bool RequiresVideo   => false; // runs FFmpeg directly
    public bool RequiresAudio   => false;

    public async Task<IReadOnlyList<(TimeSpan start, TimeSpan end, double score)>> AnalyzeAsync(
        IAsyncEnumerable<VideoFrame> frames,
        IAsyncEnumerable<AudioWindow> audio,
        MediaInfo mediaInfo,
        DetectionConfig config,
        CancellationToken cancellationToken)
    {
        string ffmpegPath = _ffmpegLocator.FindFFmpeg()
            ?? throw new InvalidOperationException("FFmpeg not found.");

        // select passes only frames whose built-in scene score exceeds the
        // threshold; showinfo logs each such frame (including pts_time) to stderr.
        // -vsync vfr keeps the selected frames at their original timestamps.
        const string filter = "select='gt(scene,0.35)',showinfo";
        string args = $"-i \"{mediaInfo.FilePath}\" -vf \"{filter}\" -vsync vfr -an -f null -";

        _logger?.LogDebug("scenechange: {FFmpeg} {Args}", ffmpegPath, args);

        string stderr = await DetectorHelpers.RunFFmpegForStderrAsync(ffmpegPath, args, cancellationToken);

        var timestamps = new List<TimeSpan>();
        foreach (Match m in DetectorHelpers.ShowInfoPtsTime.Matches(stderr))
        {
            double t = DetectorHelpers.ParseDouble(m.Groups[1].Value);
            timestamps.Add(TimeSpan.FromSeconds(t));
            _logger?.LogDebug("  scene change at {T:F3}s", t);
        }

        _logger?.LogInformation("SceneChangeDetector: {Count} cut(s) in {File}",
            timestamps.Count, System.IO.Path.GetFileName(mediaInfo.FilePath));

        // Cluster nearby cuts into contiguous segments (≥5 cuts, ≥30 s long,
        // with gaps no wider than 35 s).  BuildWindowedScores then gives every
        // window inside a segment a score of 1.0.
        var segments = DetectorHelpers.ClusterIntoSegments(timestamps);
        foreach (var (s, e) in segments)
            _logger?.LogDebug("  scene segment: {S:F1}s → {E:F1}s", s.TotalSeconds, e.TotalSeconds);

        return DetectorHelpers.BuildWindowedScores(segments, mediaInfo.Duration, config.WindowSizeSeconds);
    }
}

// ── LogoDetector ─────────────────────────────────────────────────────────────

/// <summary>
/// Detects network-logo absence by monitoring the bottom-right corner of the frame.
///
/// Most US broadcast networks display a semi-transparent "bug" logo in the
/// bottom-right corner during program content.  During commercial breaks the
/// logo is absent, so that corner region exhibits much higher frame-to-frame
/// variation.
///
/// FFmpeg command:
///   ffmpeg -i {file}
///     -vf "crop=iw/10:ih/10:9*iw/10:9*ih/10,select='gt(scene,0.2)',showinfo"
///     -vsync vfr -an -f null -
///
/// The crop isolates the bottom-right 10 × 10 % patch; select then passes only
/// frames where that patch changes more than 20 % (scene score > 0.2).  High
/// corner-change density → logo absent → commercial break.
///
/// Limitation: assumes bottom-right corner.  Networks that place their logo
/// elsewhere (e.g. top-right, bottom-left) will produce weaker signals.
/// </summary>
public class LogoDetector : ISignalDetector
{
    private readonly IFFmpegLocator _ffmpegLocator;
    private readonly ILogger<LogoDetector>? _logger;

    public LogoDetector(IFFmpegLocator ffmpegLocator,
        ILogger<LogoDetector>? logger = null)
    {
        _ffmpegLocator = ffmpegLocator;
        _logger = logger;
    }

    public string Name          => "Logo";
    public SignalType SignalType => SignalType.LogoAbsence;
    public bool RequiresVideo   => false; // runs FFmpeg directly
    public bool RequiresAudio   => false;

    public async Task<IReadOnlyList<(TimeSpan start, TimeSpan end, double score)>> AnalyzeAsync(
        IAsyncEnumerable<VideoFrame> frames,
        IAsyncEnumerable<AudioWindow> audio,
        MediaInfo mediaInfo,
        DetectionConfig config,
        CancellationToken cancellationToken)
    {
        string ffmpegPath = _ffmpegLocator.FindFFmpeg()
            ?? throw new InvalidOperationException("FFmpeg not found.");

        // Crop to bottom-right 10 % × 10 % patch; select frames where the
        // patch scene score > 0.2 (lower threshold than full-frame detection
        // because the patch is tiny and the logo is subtle).
        const string filter =
            "crop=iw/10:ih/10:9*iw/10:9*ih/10,select='gt(scene,0.2)',showinfo";
        string args = $"-i \"{mediaInfo.FilePath}\" -vf \"{filter}\" -vsync vfr -an -f null -";

        _logger?.LogDebug("logodetect: {FFmpeg} {Args}", ffmpegPath, args);

        string stderr = await DetectorHelpers.RunFFmpegForStderrAsync(ffmpegPath, args, cancellationToken);

        var timestamps = new List<TimeSpan>();
        foreach (Match m in DetectorHelpers.ShowInfoPtsTime.Matches(stderr))
        {
            double t = DetectorHelpers.ParseDouble(m.Groups[1].Value);
            timestamps.Add(TimeSpan.FromSeconds(t));
            _logger?.LogDebug("  logo absent at {T:F3}s", t);
        }

        _logger?.LogInformation("LogoDetector: {Count} corner-change event(s) in {File}",
            timestamps.Count, System.IO.Path.GetFileName(mediaInfo.FilePath));

        // Same clustering as SceneChangeDetector: group bursts of corner-change
        // events into segments and score every window in the segment 1.0.
        var segments = DetectorHelpers.ClusterIntoSegments(timestamps);
        foreach (var (s, e) in segments)
            _logger?.LogDebug("  logo-absent segment: {S:F1}s → {E:F1}s", s.TotalSeconds, e.TotalSeconds);

        return DetectorHelpers.BuildWindowedScores(segments, mediaInfo.Duration, config.WindowSizeSeconds);
    }
}
