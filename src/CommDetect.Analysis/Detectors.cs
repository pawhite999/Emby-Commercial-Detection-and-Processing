using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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
        string threshold = config.SceneChangeThreshold.ToString("F2", CultureInfo.InvariantCulture);
        string filter = $"select='gt(scene,{threshold})',showinfo";
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

        // Cluster nearby cuts into contiguous segments.  BuildWindowedScores then
        // gives every window inside a segment a score of 1.0.
        var segments = DetectorHelpers.ClusterIntoSegments(timestamps,
            config.LogoClusterMaxGapSeconds,
            config.LogoClusterMinDurationSeconds,
            config.LogoClusterMinEventCount);
        foreach (var (s, e) in segments)
            _logger?.LogDebug("  scene segment: {S:F1}s → {E:F1}s", s.TotalSeconds, e.TotalSeconds);

        return DetectorHelpers.BuildWindowedScores(segments, mediaInfo.Duration, config.WindowSizeSeconds);
    }
}

// ── LogoDetector ─────────────────────────────────────────────────────────────

/// <summary>
/// Detects network-logo absence by monitoring all four corners of the frame.
///
/// Most US broadcast networks display a semi-transparent "bug" logo in one corner
/// during program content.  During commercial breaks the logo is absent, so that
/// corner region shows much higher frame-to-frame variation.
///
/// Two-phase approach:
///
///   Phase 1 — Learning (first LogoLearnDurationSeconds):
///     All four corners and two full-width ticker bands are probed in parallel.
///     Corners with activity > 0.5 events/s are classified as ticker-affected
///     (e.g. live weather/news crawl) and excluded from full detection.
///     Full-width bands with activity > 1.5 events/s trigger a warning log.
///
///   Phase 2 — Detection (full recording):
///     Active (non-ticker) corners are analysed in parallel.  All timestamps
///     from every active corner are merged, then clustered and scored exactly as
///     before, so that an entire commercial break scores 1.0 across its windows.
///
/// Corner crop filters (10 % × 10 % patches):
///   top-left     crop=iw/10:ih/10:0:0
///   top-right    crop=iw/10:ih/10:9*iw/10:0
///   bottom-left  crop=iw/10:ih/10:0:9*ih/10
///   bottom-right crop=iw/10:ih/10:9*iw/10:9*ih/10
///
/// Ticker-band crop filters (full-width, 1/12 height):
///   top-band     crop=iw:ih/12:0:0
///   bottom-band  crop=iw:ih/12:0:11*ih/12
/// </summary>
public class LogoDetector : ISignalDetector
{
    private readonly IFFmpegLocator _ffmpegLocator;
    private readonly ILogger<LogoDetector>? _logger;

    // 10 % × 10 % corner patches
    private static readonly (string Name, string Crop)[] Corners =
    [
        ("top-left",     "crop=iw/10:ih/10:0:0"),
        ("top-right",    "crop=iw/10:ih/10:9*iw/10:0"),
        ("bottom-left",  "crop=iw/10:ih/10:0:9*ih/10"),
        ("bottom-right", "crop=iw/10:ih/10:9*iw/10:9*ih/10"),
    ];

    // Full-width ticker bands (1/12 height)
    private static readonly (string Name, string Crop)[] TickerBands =
    [
        ("top-band",    "crop=iw:ih/12:0:0"),
        ("bottom-band", "crop=iw:ih/12:0:11*ih/12"),
    ];

    // Full-width band activity above this (events/s) → warn that a news/weather crawl is present
    private const double BandTickerThreshold = 1.5;

    // Parses FFmpeg SSIM stats lines: "n:1 Y:0.998 U:0.999 V:0.997 All:0.998 (47.4)"
    private static readonly Regex SsimLineRx = new(
        @"n:(\d+)\s+\S+\s+\S+\s+\S+\s+All:([\d.]+)",
        RegexOptions.Compiled);

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

        if (config.LogoAbsenceWeight <= 0)
        {
            _logger?.LogInformation("LogoDetector: weight is 0 — skipping.");
            return Array.Empty<(TimeSpan, TimeSpan, double)>();
        }

        // ── Phase 1: Learning ─────────────────────────────────────────────────
        // Start learning AFTER skip_start_seconds so the reference frame is captured
        // from actual program content (with the network logo visible), not from DVR
        // pre-padding where the logo may be absent, which would invert the SSIM signal.
        double learnStart = Math.Min(config.SkipStartSeconds, mediaInfo.Duration.TotalSeconds * 0.5);
        double learnSecs  = Math.Min(config.LogoLearnDurationSeconds,
            mediaInfo.Duration.TotalSeconds - learnStart);
        _logger?.LogDebug("LogoDetector: learning phase {Start:F0}s–{End:F0}s — probing {N} corners + {B} bands",
            learnStart, learnStart + learnSecs, Corners.Length, TickerBands.Length);

        // Probe corners and bands in parallel over the learning window
        var learnTasks = Corners
            .Concat(TickerBands)
            .Select(r => MeasureActivityAsync(ffmpegPath, mediaInfo.FilePath, r.Crop, r.Name,
                learnStart, learnSecs, config.LogoSceneThreshold, cancellationToken))
            .ToArray();

        var learnResults = await Task.WhenAll(learnTasks);

        // Classify corners — exclude those with ticker-level activity
        var activeCorners = new List<(string Name, string Crop)>();
        var activeRates   = new List<double>();
        for (int i = 0; i < Corners.Length; i++)
        {
            double eps = learnResults[i].EventsPerSec;
            bool isTicker = eps > config.LogoCornerTickerThreshold;
            _logger?.LogInformation(
                "LogoDetector: corner {Corner} → {Eps:F2} events/s {Status}",
                Corners[i].Name, eps,
                isTicker ? "(TICKER — excluded)" : "(active)");

            if (!isTicker)
            {
                activeCorners.Add(Corners[i]);
                activeRates.Add(eps);
            }
        }

        // Filter high-noise corners: keep only corners within LogoCornerFilterRatio
        // of the quietest corner's rate. This prevents corners without a logo from
        // polluting the consensus with scene-change noise.
        if (activeCorners.Count > 1 && config.LogoCornerFilterRatio > 0)
        {
            double minRate  = activeRates.Min();
            double rateLimit = minRate * config.LogoCornerFilterRatio;
            var filtered = activeCorners
                .Zip(activeRates, (c, r) => (Corner: c, Rate: r))
                .Where(x => x.Rate <= rateLimit)
                .ToList();

            if (filtered.Count > 0 && filtered.Count < activeCorners.Count)
            {
                foreach (var (c, r) in activeCorners.Zip(activeRates, (c, r) => (c, r))
                    .Where(x => x.r > rateLimit))
                    _logger?.LogInformation(
                        "LogoDetector: corner {Corner} excluded by noise filter ({Eps:F2} events/s > {Limit:F2})",
                        c.Name, r, rateLimit);

                activeCorners = filtered.Select(x => x.Corner).ToList();
            }
        }

        // Log ticker-band results
        for (int i = 0; i < TickerBands.Length; i++)
        {
            double eps = learnResults[Corners.Length + i].EventsPerSec;
            if (eps > BandTickerThreshold)
                _logger?.LogWarning(
                    "LogoDetector: {Band} has high activity ({Eps:F2} events/s) — weather/news crawl likely",
                    TickerBands[i].Name, eps);
            else
                _logger?.LogDebug("LogoDetector: {Band} → {Eps:F2} events/s", TickerBands[i].Name, eps);
        }

        if (activeCorners.Count == 0)
        {
            _logger?.LogWarning("LogoDetector: all corners are ticker-affected; logo detection suppressed.");
            return Array.Empty<(TimeSpan, TimeSpan, double)>();
        }

        // Extract reference frames for SSIM comparison from the middle of the learn window,
        // which is now offset past skip_start_seconds into actual program content.
        double refTime = learnStart + learnSecs / 2.0;
        var refTasks = activeCorners
            .Select(c => ExtractReferenceFrameAsync(ffmpegPath, mediaInfo.FilePath, c.Crop, c.Name, refTime, cancellationToken))
            .ToArray();
        var refPaths = await Task.WhenAll(refTasks);

        // ── Phase 2: Full detection on active corners in parallel ─────────────
        _logger?.LogDebug("LogoDetector: detecting on {N} corner(s): {Names}",
            activeCorners.Count,
            string.Join(", ", activeCorners.Select(c => c.Name)));

        var detectTasks = activeCorners.Select((c, i) =>
            refPaths[i] != null
                ? SsimDetectCornerAsync(ffmpegPath, mediaInfo.FilePath, c.Crop, c.Name,
                      refPaths[i]!, config.LogoSsimThreshold,
                      mediaInfo.Duration.TotalSeconds, cancellationToken)
                : DetectCornerAsync(ffmpegPath, mediaInfo.FilePath, c.Crop, c.Name,
                      config.LogoSceneThreshold, cancellationToken)
        ).ToArray();

        var cornerTimestamps = await Task.WhenAll(detectTasks);

        // Cleanup reference files
        foreach (var p in refPaths)
            if (p != null) try { System.IO.File.Delete(p); } catch { /* ignore */ }

        // Log per-corner event counts
        for (int i = 0; i < activeCorners.Count; i++)
            _logger?.LogInformation(
                "LogoDetector: corner {Corner} → {Count} event(s)",
                activeCorners[i].Name, cornerTimestamps[i].Count);

        // Merge timestamps, requiring consensus from ≥2 corners when possible.
        // This prevents false positives when the network bug temporarily moves to a
        // different corner (e.g. during a live performance) — in that case only one
        // corner loses its logo while the others remain stable.
        var allTimestamps = MergeWithConsensus(
            cornerTimestamps, mediaInfo.Duration, activeCorners.Count);

        _logger?.LogInformation(
            "LogoDetector: {Total} consensus event(s) from {N} corner(s) in {File}",
            allTimestamps.Count, activeCorners.Count,
            System.IO.Path.GetFileName(mediaInfo.FilePath));

        // Cluster and score
        var segments = DetectorHelpers.ClusterIntoSegments(allTimestamps,
            config.LogoClusterMaxGapSeconds,
            config.LogoClusterMinDurationSeconds,
            config.LogoClusterMinEventCount);
        foreach (var (s, e) in segments)
            _logger?.LogDebug("  logo-absent segment: {S:F1}s → {E:F1}s", s.TotalSeconds, e.TotalSeconds);

        return DetectorHelpers.BuildWindowedScores(segments, mediaInfo.Duration, config.WindowSizeSeconds);
    }

    /// <summary>
    /// Probes <paramref name="learnSecs"/> of <paramref name="filePath"/> through the
    /// <paramref name="crop"/> filter and returns the events-per-second activity rate.
    /// </summary>
    private async Task<(string Name, double EventsPerSec)> MeasureActivityAsync(
        string ffmpegPath, string filePath, string crop, string name,
        double learnStart, double learnSecs, double sceneThreshold, CancellationToken ct)
    {
        string thresh = sceneThreshold.ToString("F2", CultureInfo.InvariantCulture);
        string filter = $"{crop},select='gt(scene,{thresh})',showinfo";
        // -ss before -i for fast keyframe seek to learnStart, then -t limits duration
        string args   = $"-ss {learnStart.ToString(CultureInfo.InvariantCulture)}" +
                        $" -i \"{filePath}\" -t {learnSecs.ToString(CultureInfo.InvariantCulture)}" +
                        $" -vf \"{filter}\" -vsync vfr -an -f null -";

        string stderr = await DetectorHelpers.RunFFmpegForStderrAsync(ffmpegPath, args, ct);
        int    count  = DetectorHelpers.ShowInfoPtsTime.Matches(stderr).Count;
        double eps    = learnSecs > 0 ? count / learnSecs : 0.0;

        return (name, eps);
    }

    /// <summary>
    /// Merges per-corner timestamp lists, requiring at least 2 corners to agree
    /// within a short bucket window before including their timestamps.
    ///
    /// When only 1 active corner exists, consensus is impossible and all
    /// timestamps are returned as-is (original behaviour).
    ///
    /// The 5-second bucket size is deliberately coarser than the 1-second
    /// analysis window so that near-simultaneous events from different corners
    /// (which rarely land on exactly the same frame) are treated as agreeing.
    /// </summary>
    private static List<TimeSpan> MergeWithConsensus(
        List<TimeSpan>[] cornerTimestamps, TimeSpan totalDuration, int activeCount)
    {
        if (activeCount < 2)
        {
            // Only one corner — no consensus possible; return its timestamps directly
            var single = new List<TimeSpan>();
            if (cornerTimestamps.Length > 0) single.AddRange(cornerTimestamps[0]);
            return single;
        }

        const double BucketSecs = 5.0;
        int bucketCount = (int)Math.Ceiling(totalDuration.TotalSeconds / BucketSecs) + 1;
        var votes = new int[bucketCount];

        // First pass: count how many corners fired in each bucket
        foreach (var cornerList in cornerTimestamps)
            foreach (var ts in cornerList)
            {
                int b = (int)(ts.TotalSeconds / BucketSecs);
                if (b < votes.Length) votes[b]++;
            }

        // Second pass: collect timestamps from buckets where ≥2 corners agreed
        var result = new List<TimeSpan>();
        foreach (var cornerList in cornerTimestamps)
            foreach (var ts in cornerList)
            {
                int b = (int)(ts.TotalSeconds / BucketSecs);
                if (b < votes.Length && votes[b] >= 2)
                    result.Add(ts);
            }

        return result;
    }

    private async Task<string?> ExtractReferenceFrameAsync(
        string ffmpegPath, string filePath, string crop, string cornerName,
        double atSeconds, CancellationToken ct)
    {
        // Safe temp path: no spaces, no special chars
        string safeName   = Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(filePath), @"[^\w]", "_");
        string safeCorner = cornerName.Replace("-", "_");
        string refPath    = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"commdetect_ref_{safeCorner}_{safeName}.png");

        string args = $"-y -ss {atSeconds.ToString("F1", CultureInfo.InvariantCulture)}" +
                      $" -i \"{filePath}\" -vf \"{crop}\" -frames:v 1 \"{refPath}\"";
        try
        {
            await DetectorHelpers.RunFFmpegForStderrAsync(ffmpegPath, args, ct);
            if (System.IO.File.Exists(refPath) && new System.IO.FileInfo(refPath).Length > 0)
            {
                _logger?.LogDebug("LogoDetector: reference frame extracted for {Corner} at {T:F1}s → {Path}",
                    cornerName, atSeconds, refPath);
                return refPath;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                "LogoDetector: reference extraction failed for {Corner}: {Msg} — falling back to scene-change detection",
                cornerName, ex.Message);
        }
        return null;
    }

    private async Task<List<TimeSpan>> SsimDetectCornerAsync(
        string ffmpegPath, string filePath, string crop, string cornerName,
        string refPath, double ssimThreshold, double totalSeconds, CancellationToken ct)
    {
        string statsFile = System.IO.Path.GetTempFileName();
        try
        {
            // fps=1/5 → one frame every 5 seconds (sufficient for commercial break detection)
            // [out] + explicit -map tell FFmpeg exactly what stream to write
            // -t {totalSeconds} hard-caps the output at the video duration;
            //   without this, -loop 1 on the reference image causes FFmpeg to run forever
            string filter = $"[0:v]fps=1/5,{crop}[probe];[probe][1:v]ssim=stats_file='{statsFile}'[out]";
            string dur    = totalSeconds.ToString("F1", CultureInfo.InvariantCulture);
            string args   = $"-hide_banner -loglevel error -i \"{filePath}\" -loop 1 -i \"{refPath}\"" +
                            $" -filter_complex \"{filter}\" -map \"[out]\" -t {dur} -f null -";

            _logger?.LogDebug("LogoDetector SSIM ({Corner}): threshold={T:F2}", cornerName, ssimThreshold);

            await DetectorHelpers.RunFFmpegForStderrAsync(ffmpegPath, args, ct);

            string stats  = await System.IO.File.ReadAllTextAsync(statsFile, ct);
            var events    = ParseSsimEvents(stats, ssimThreshold);

            _logger?.LogInformation(
                "LogoDetector SSIM ({Corner}): {Count} logo-absent second(s) below threshold {T:F2}",
                cornerName, events.Count, ssimThreshold);

            return events;
        }
        finally
        {
            if (System.IO.File.Exists(statsFile)) try { System.IO.File.Delete(statsFile); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Parses the FFmpeg SSIM stats file and returns timestamps (seconds) where
    /// the SSIM All score falls below <paramref name="threshold"/>, indicating
    /// the corner looks different from the reference (logo absent).
    /// Frame n is 1-indexed; with fps=1 sampling, t = (n-1) seconds.
    /// </summary>
    private List<TimeSpan> ParseSsimEvents(string stats, double threshold)
    {
        var events = new List<TimeSpan>();
        foreach (Match m in SsimLineRx.Matches(stats))
        {
            int    frame = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            double ssim  = DetectorHelpers.ParseDouble(m.Groups[2].Value);
            if (ssim < threshold)
            {
                double t = (frame - 1) * 5.0; // n is 1-indexed; fps=1/5 → each frame is 5s apart
                events.Add(TimeSpan.FromSeconds(t));
                _logger?.LogDebug("  logo absent at {T:F0}s (SSIM={S:F3})", t, ssim);
            }
        }
        return events;
    }

    /// <summary>
    /// Runs the full-file logo-absence scan for a single <paramref name="crop"/> region
    /// and returns all detected event timestamps.
    /// </summary>
    private async Task<List<TimeSpan>> DetectCornerAsync(
        string ffmpegPath, string filePath, string crop, string name,
        double sceneThreshold, CancellationToken ct)
    {
        string thresh = sceneThreshold.ToString("F2", CultureInfo.InvariantCulture);
        string filter = $"{crop},select='gt(scene,{thresh})',showinfo";
        string args   = $"-i \"{filePath}\" -vf \"{filter}\" -vsync vfr -an -f null -";

        _logger?.LogDebug("logodetect ({Corner}): {FFmpeg} {Args}", name, ffmpegPath, args);

        string stderr = await DetectorHelpers.RunFFmpegForStderrAsync(ffmpegPath, args, ct);

        var timestamps = new List<TimeSpan>();
        foreach (Match m in DetectorHelpers.ShowInfoPtsTime.Matches(stderr))
        {
            double t = DetectorHelpers.ParseDouble(m.Groups[1].Value);
            timestamps.Add(TimeSpan.FromSeconds(t));
        }

        return timestamps;
    }
}
