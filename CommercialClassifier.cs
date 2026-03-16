using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace CommDetect.Core;

/// <summary>
/// Combines signals from multiple detectors, applies temporal smoothing,
/// and produces final commercial/program segment classifications.
/// </summary>
public class CommercialClassifier : ICommercialClassifier
{
    private readonly ILogger<CommercialClassifier>? _logger;

    public CommercialClassifier(ILogger<CommercialClassifier>? logger = null)
    {
        _logger = logger;
    }

    public AnalysisResult Classify(
        MediaInfo mediaInfo,
        Dictionary<SignalType, IReadOnlyList<(TimeSpan start, TimeSpan end, double score)>> signals,
        DetectionConfig config)
    {
        _logger?.LogInformation("Classifying {File} with {Count} signal types",
            mediaInfo.FilePath, signals.Count);

        // 1. Build unified analysis windows
        var windows = BuildWindows(signals, config, mediaInfo);

        // 2. Compute weighted scores
        ScoreWindows(windows, signals, config);

        // 3. Apply temporal smoothing
        SmoothScores(windows, config.SmoothingRadius);

        // 4. Classify each window
        foreach (var w in windows)
        {
            w.Classification = w.CommercialProbability >= config.CommercialThreshold
                ? SegmentType.Commercial
                : SegmentType.Program;
        }

        // 5. Merge adjacent same-type windows into segments
        var segments = MergeIntoSegments(windows, mediaInfo.FrameRate);

        // 6. Apply duration constraints
        segments = ApplyDurationConstraints(segments, config);

        return new AnalysisResult
        {
            SourceFile = mediaInfo.FilePath,
            TotalDuration = mediaInfo.Duration,
            FrameRate = mediaInfo.FrameRate,
            Width = mediaInfo.Width,
            Height = mediaInfo.Height,
            Windows = windows,
            Segments = segments
        };
    }

    private List<AnalysisWindow> BuildWindows(
        Dictionary<SignalType, IReadOnlyList<(TimeSpan start, TimeSpan end, double score)>> signals,
        DetectionConfig config,
        MediaInfo mediaInfo)
    {
        // Use the time grid from whichever signal has the most entries
        var referenceSignal = signals.Values.OrderByDescending(s => s.Count).First();

        return referenceSignal.Select(s => new AnalysisWindow
        {
            Start = s.start,
            End = s.end
        }).ToList();
    }

    private void ScoreWindows(
        List<AnalysisWindow> windows,
        Dictionary<SignalType, IReadOnlyList<(TimeSpan start, TimeSpan end, double score)>> signals,
        DetectionConfig config)
    {
        var weights = new Dictionary<SignalType, double>
        {
            [SignalType.BlackFrame] = config.BlackFrameWeight,
            [SignalType.SceneChange] = config.SceneChangeWeight,
            [SignalType.Silence] = config.SilenceWeight,
            [SignalType.LogoAbsence] = config.LogoAbsenceWeight,
            [SignalType.AspectRatioChange] = config.AspectRatioWeight,
            [SignalType.AudioRepetition] = config.AudioRepetitionWeight
        };

        // Normalize weights to sum to 1.0 based on active signals only
        var activeWeightSum = signals.Keys.Sum(k => weights.GetValueOrDefault(k, 0.0));
        if (activeWeightSum <= 0) activeWeightSum = 1.0;

        for (int i = 0; i < windows.Count; i++)
        {
            double weightedSum = 0;
            foreach (var (signalType, signalData) in signals)
            {
                if (i < signalData.Count)
                {
                    var weight = weights.GetValueOrDefault(signalType, 0.0) / activeWeightSum;
                    var score = signalData[i].score;
                    windows[i].SignalScores[signalType] = score;
                    weightedSum += weight * score;
                }
            }
            windows[i].CommercialProbability = Math.Clamp(weightedSum, 0.0, 1.0);
        }
    }

    private void SmoothScores(List<AnalysisWindow> windows, int radius)
    {
        if (radius <= 0 || windows.Count == 0) return;

        var original = windows.Select(w => w.CommercialProbability).ToArray();

        for (int i = 0; i < windows.Count; i++)
        {
            int start = Math.Max(0, i - radius);
            int end = Math.Min(windows.Count - 1, i + radius);
            double sum = 0;
            int count = 0;
            for (int j = start; j <= end; j++)
            {
                sum += original[j];
                count++;
            }
            windows[i].CommercialProbability = sum / count;
        }
    }

    private List<ContentSegment> MergeIntoSegments(List<AnalysisWindow> windows, double frameRate)
    {
        if (windows.Count == 0) return new List<ContentSegment>();

        var segments = new List<ContentSegment>();
        var currentType = windows[0].Classification;
        var segStart = windows[0].Start;
        double confidenceSum = windows[0].CommercialProbability;
        int windowCount = 1;

        for (int i = 1; i < windows.Count; i++)
        {
            if (windows[i].Classification != currentType)
            {
                segments.Add(new ContentSegment
                {
                    Start = segStart,
                    End = windows[i - 1].End,
                    Type = currentType,
                    Confidence = confidenceSum / windowCount,
                    StartFrame = (long)(segStart.TotalSeconds * frameRate),
                    EndFrame = (long)(windows[i - 1].End.TotalSeconds * frameRate)
                });

                currentType = windows[i].Classification;
                segStart = windows[i].Start;
                confidenceSum = 0;
                windowCount = 0;
            }
            confidenceSum += windows[i].CommercialProbability;
            windowCount++;
        }

        // Final segment
        var last = windows[^1];
        segments.Add(new ContentSegment
        {
            Start = segStart,
            End = last.End,
            Type = currentType,
            Confidence = confidenceSum / windowCount,
            StartFrame = (long)(segStart.TotalSeconds * frameRate),
            EndFrame = (long)(last.End.TotalSeconds * frameRate)
        });

        return segments;
    }

    private List<ContentSegment> ApplyDurationConstraints(
        List<ContentSegment> segments, DetectionConfig config)
    {
        // Reclassify commercial segments that are too short or too long
        foreach (var seg in segments)
        {
            if (seg.Type == SegmentType.Commercial)
            {
                if (seg.DurationSeconds < config.MinCommercialDurationSeconds ||
                    seg.DurationSeconds > config.MaxCommercialDurationSeconds)
                {
                    _logger?.LogDebug(
                        "Reclassifying {Duration:F1}s commercial segment as program (out of range)",
                        seg.DurationSeconds);
                    seg.Type = SegmentType.Program;
                }
            }
        }

        // Re-merge adjacent same-type segments after reclassification
        var merged = new List<ContentSegment>();
        foreach (var seg in segments)
        {
            if (merged.Count > 0 && merged[^1].Type == seg.Type)
            {
                var last = merged[^1];
                last.End = seg.End;
                last.EndFrame = seg.EndFrame;
                last.Confidence = (last.Confidence + seg.Confidence) / 2.0;
            }
            else
            {
                merged.Add(seg);
            }
        }

        return merged;
    }
}
