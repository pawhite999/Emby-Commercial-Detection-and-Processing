using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommDetect.Core;
using Microsoft.Extensions.Logging;

namespace CommDetect.Analysis;

/// <summary>
/// Orchestrates the full commercial detection pipeline:
/// 1. Probe media file for metadata
/// 2. Run all enabled signal detectors in parallel
/// 3. Combine signals via the classifier
/// 4. Write output files
/// </summary>
public class DetectionPipeline
{
    private readonly IMediaProbe _probe;
    private readonly IFrameExtractor _frameExtractor;
    private readonly IAudioExtractor _audioExtractor;
    private readonly IEnumerable<ISignalDetector> _detectors;
    private readonly ICommercialClassifier _classifier;
    private readonly IEnumerable<IResultWriter> _writers;
    private readonly ILogger<DetectionPipeline>? _logger;

    public DetectionPipeline(
        IMediaProbe probe,
        IFrameExtractor frameExtractor,
        IAudioExtractor audioExtractor,
        IEnumerable<ISignalDetector> detectors,
        ICommercialClassifier classifier,
        IEnumerable<IResultWriter> writers,
        ILogger<DetectionPipeline>? logger = null)
    {
        _probe = probe;
        _frameExtractor = frameExtractor;
        _audioExtractor = audioExtractor;
        _detectors = detectors;
        _classifier = classifier;
        _writers = writers;
        _logger = logger;
    }

    /// <summary>
    /// Run the full detection pipeline on a media file.
    /// </summary>
    public async Task<AnalysisResult> ProcessAsync(
        string inputPath,
        DetectionConfig config,
        IAnalysisProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        // ── Phase 1: Probe ───────────────────────────────────
        progress?.ReportPhase("Probing media file");
        _logger?.LogInformation("Probing {File}", inputPath);

        var mediaInfo = await _probe.ProbeAsync(inputPath, cancellationToken);
        _logger?.LogInformation(
            "Media: {Width}x{Height} @ {Fps}fps, duration {Duration}",
            mediaInfo.Width, mediaInfo.Height, mediaInfo.FrameRate, mediaInfo.Duration);

        // ── Phase 2: Extract & Analyze ───────────────────────
        progress?.ReportPhase("Analyzing media");

        var activeDetectors = _detectors
            .Where(d => IsDetectorEnabled(d, config))
            .ToList();

        _logger?.LogInformation("Running {Count} detectors: {Names}",
            activeDetectors.Count,
            string.Join(", ", activeDetectors.Select(d => d.Name)));

        var signals = new Dictionary<SignalType, IReadOnlyList<(TimeSpan start, TimeSpan end, double score)>>();

        // Run detectors (could be parallelized further, but each already
        // processes frames in a streaming fashion)
        int completedDetectors = 0;
        foreach (var detector in activeDetectors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger?.LogInformation("Running {Detector}...", detector.Name);

            var frames = detector.RequiresVideo
                ? _frameExtractor.ExtractFramesAsync(inputPath, config.FrameSampleRate, cancellationToken)
                : EmptyAsyncEnumerable<VideoFrame>();

            var audio = detector.RequiresAudio
                ? _audioExtractor.ExtractAudioAsync(inputPath, config.WindowSizeSeconds, cancellationToken)
                : EmptyAsyncEnumerable<AudioWindow>();

            var result = await detector.AnalyzeAsync(frames, audio, mediaInfo, config, cancellationToken);
            signals[detector.SignalType] = result;

            completedDetectors++;
            double pct = (double)completedDetectors / activeDetectors.Count * 80.0;
            progress?.ReportProgress(pct, $"Completed {detector.Name}");
        }

        // ── Phase 3: Classify ────────────────────────────────
        progress?.ReportPhase("Classifying segments");
        _logger?.LogInformation("Classifying with {Count} signal types", signals.Count);

        var analysisResult = _classifier.Classify(mediaInfo, signals, config);
        analysisResult.AnalysisStartedUtc = DateTime.UtcNow - stopwatch.Elapsed;

        // Reclassify any commercial segment that starts within the skip window as Program.
        if (config.SkipStartSeconds > 0)
        {
            var skipThreshold = TimeSpan.FromSeconds(config.SkipStartSeconds);
            int skipped = 0;
            foreach (var seg in analysisResult.Segments)
            {
                if (seg.Type == SegmentType.Commercial && seg.Start < skipThreshold)
                {
                    seg.Type = SegmentType.Program;
                    skipped++;
                }
            }
            if (skipped > 0)
                _logger?.LogInformation(
                    "SkipStart: reclassified {Count} commercial segment(s) within first {Secs:F0}s as Program",
                    skipped, config.SkipStartSeconds);
        }

        // Reclassify any commercial segment that ends within the skip-end window as Program.
        if (config.SkipEndSeconds > 0)
        {
            var skipThreshold = mediaInfo.Duration - TimeSpan.FromSeconds(config.SkipEndSeconds);
            int skipped = 0;
            foreach (var seg in analysisResult.Segments)
            {
                if (seg.Type == SegmentType.Commercial && seg.End > skipThreshold)
                {
                    seg.Type = SegmentType.Program;
                    skipped++;
                }
            }
            if (skipped > 0)
                _logger?.LogInformation(
                    "SkipEnd: reclassified {Count} commercial segment(s) within last {Secs:F0}s as Program",
                    skipped, config.SkipEndSeconds);
        }

        var commercialSegments = analysisResult.Segments
            .Where(s => s.Type == SegmentType.Commercial)
            .ToList();

        _logger?.LogInformation(
            "Found {Count} commercial segments totaling {Duration:F1}s",
            commercialSegments.Count,
            commercialSegments.Sum(s => s.DurationSeconds));

        // ── Phase 4: Write Output ────────────────────────────
        progress?.ReportPhase("Writing output files");

        var enabledWriters = _writers
            .Where(w => config.OutputFormats.Contains(w.Format))
            .ToList();

        foreach (var writer in enabledWriters)
        {
            string outputPath = System.IO.Path.ChangeExtension(inputPath, writer.FileExtension);
            _logger?.LogInformation("Writing {Format} to {Path}", writer.Format, outputPath);
            await writer.WriteAsync(analysisResult, outputPath, cancellationToken);
        }

        // ── Done ─────────────────────────────────────────────
        analysisResult.AnalysisCompletedUtc = DateTime.UtcNow;
        stopwatch.Stop();

        _logger?.LogInformation("Analysis complete in {Elapsed}", stopwatch.Elapsed);
        progress?.ReportProgress(100, "Complete");
        progress?.ReportComplete(analysisResult);

        return analysisResult;
    }

    private static bool IsDetectorEnabled(ISignalDetector detector, DetectionConfig config)
    {
        return detector.SignalType switch
        {
            SignalType.LogoAbsence => config.EnableLogoDetection,
            SignalType.AspectRatioChange => config.EnableAspectRatioDetection,
            SignalType.AudioRepetition => config.EnableAudioFingerprinting,
            _ => true // BlackFrame, SceneChange, Silence are always enabled
        };
    }

    private static async IAsyncEnumerable<T> EmptyAsyncEnumerable<T>()
    {
        await Task.CompletedTask;
        yield break;
    }
}
