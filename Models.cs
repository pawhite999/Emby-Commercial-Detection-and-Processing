using System;
using System.Collections.Generic;

namespace CommDetect.Core;

/// <summary>Metadata probed from a media file.</summary>
public class MediaInfo
{
    public string FilePath { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }
    public string VideoCodec { get; set; } = "";
    public string AudioCodec { get; set; } = "";
    public int AudioSampleRate { get; set; }
    public int AudioChannels { get; set; }
}

/// <summary>A single decoded video frame passed to signal detectors.</summary>
public class VideoFrame
{
    public TimeSpan Timestamp { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>A window of decoded audio samples passed to signal detectors.</summary>
public class AudioWindow
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public float[] Samples { get; set; } = Array.Empty<float>();
    public int SampleRate { get; set; }
}

/// <summary>A scored analysis window produced by the classifier.</summary>
public class AnalysisWindow
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public SegmentType Classification { get; set; }
    public double CommercialProbability { get; set; }
    public Dictionary<SignalType, double> SignalScores { get; set; } = new();
}

/// <summary>A contiguous segment of content or commercials.</summary>
public class ContentSegment
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public SegmentType Type { get; set; }
    public double Confidence { get; set; }
    public long StartFrame { get; set; }
    public long EndFrame { get; set; }
    public double DurationSeconds => (End - Start).TotalSeconds;
}

/// <summary>Full result returned by the detection pipeline.</summary>
public class AnalysisResult
{
    public string SourceFile { get; set; } = "";
    public TimeSpan TotalDuration { get; set; }
    public double FrameRate { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public List<AnalysisWindow> Windows { get; set; } = new();
    public List<ContentSegment> Segments { get; set; } = new();
    public DateTime AnalysisStartedUtc { get; set; }
    public DateTime AnalysisCompletedUtc { get; set; }
    public TimeSpan AnalysisDuration => AnalysisCompletedUtc - AnalysisStartedUtc;
}

/// <summary>Algorithm settings that control the detection pipeline.</summary>
public class DetectionConfig
{
    public double FrameSampleRate { get; set; } = 2.0;
    public double WindowSizeSeconds { get; set; } = 1.0;
    public double CommercialThreshold { get; set; } = 0.5;
    public int SmoothingRadius { get; set; } = 3;

    // Signal weights.
    // BlackFrame and Silence are "boundary" signals: they fire only at the few
    // windows where a transition occurs (typically 1–3 s per boundary).
    // SceneChange and LogoAbsence are "content" signals: once a commercial break
    // begins they remain active throughout the break.  The content signals must
    // therefore carry enough weight that their combined score exceeds the
    // CommercialThreshold even without the boundary signals firing.
    public double BlackFrameWeight { get; set; } = 0.25;
    public double SceneChangeWeight { get; set; } = 0.35;
    public double SilenceWeight { get; set; } = 0.15;
    public double LogoAbsenceWeight { get; set; } = 0.25;
    public double AspectRatioWeight { get; set; } = 0.05;
    public double AudioRepetitionWeight { get; set; } = 0.05;

    // Detector toggles
    public double LogoLearnDurationSeconds { get; set; } = 120.0;
    public bool EnableLogoDetection { get; set; } = true;
    public bool EnableAspectRatioDetection { get; set; } = true;
    public bool EnableAudioFingerprinting { get; set; } = false;

    // Logo detector tuning
    // scene_threshold: how much a corner frame must change to count as an event.
    //   Lower = more sensitive (useful for slow-moving local content).
    public double LogoSceneThreshold { get; set; } = 0.20;
    // cluster_min_events: minimum logo-change events needed to form a commercial segment.
    //   Lower = catches sparse-cut commercials (local daytime); higher = fewer false positives.
    public int LogoClusterMinEventCount { get; set; } = 5;
    // cluster_max_gap: max seconds between events before a new cluster is started.
    public double LogoClusterMaxGapSeconds { get; set; } = 35.0;
    // cluster_min_duration: minimum seconds a cluster must span to be kept.
    public double LogoClusterMinDurationSeconds { get; set; } = 30.0;
    /// <summary>SSIM score below this → logo absent (commercial). Range 0–1; default 0.5.</summary>
    public double LogoSsimThreshold { get; set; } = 0.50;
    /// <summary>
    /// Corners with Phase 1 rate > (min active corner rate × this ratio) are excluded.
    /// Filters noisy corners that fire more than the stable logo corner(s).
    /// 1.5 = keep corners within 50% of the quietest corner's rate.
    /// </summary>
    public double LogoCornerFilterRatio { get; set; } = 1.5;

    // Scene change detector tuning
    // threshold: scene-score a frame must exceed to be counted as a cut (0–1).
    //   Lower = more sensitive; higher = only hard cuts.
    public double SceneChangeThreshold { get; set; } = 0.35;

    // Duration constraints
    public double MinCommercialDurationSeconds { get; set; } = 30.0;
    public double MaxCommercialDurationSeconds { get; set; } = 600.0;

    /// <summary>
    /// Ignore commercial detections that start within the first N seconds of the recording.
    /// Protects cold opens and opening credits from being misclassified as commercials.
    /// Set to 0 to disable (default).
    /// </summary>
    public double SkipStartSeconds { get; set; } = 0.0;

    /// <summary>
    /// Ignore commercial detections that end within the last N seconds of the recording.
    /// Protects end-of-show content from being cut when the show ends before the recording does.
    /// Set to 0 to disable (default).
    /// </summary>
    public double SkipEndSeconds { get; set; } = 0.0;

    // Emby Server API integration
    // When server_url and api_key are set, CommDetect queries Emby for the recording's
    // pre/post padding and uses them to automatically set SkipStartSeconds/SkipEndSeconds.
    public string EmbyServerUrl { get; set; } = "";
    public string EmbyApiKey    { get; set; } = "";

    // Output
    public List<OutputFormat> OutputFormats { get; set; } = new() { OutputFormat.Edl };

    public static DetectionConfig Fast() => new()
    {
        FrameSampleRate = 1.0,
        EnableLogoDetection = false,
        EnableAspectRatioDetection = false,
        EnableAudioFingerprinting = false
    };

    public static DetectionConfig Accurate() => new()
    {
        FrameSampleRate = 4.0,
        EnableLogoDetection = true,
        EnableAspectRatioDetection = true,
        EnableAudioFingerprinting = true,
        CommercialThreshold = 0.45
    };
}

/// <summary>Settings for the directory-watching service.</summary>
public class WatchConfig
{
    public List<string> WatchDirectories { get; set; } = new();
    public List<string> FileExtensions { get; set; } = new() { ".ts", ".mpg", ".mpeg", ".mp4", ".mkv" };
    public bool Recursive { get; set; } = true;
    public int StabilityDelaySeconds { get; set; } = 30;
    public double MinFileSizeMB { get; set; } = 10.0;
    public int MaxConcurrentJobs { get; set; } = 1;
    public bool SkipAlreadyProcessed { get; set; } = true;
    public bool ProcessExistingOnStartup { get; set; } = false;
    public bool NotifyOnComplete { get; set; } = true;
    public List<string> ExcludePatterns { get; set; } = new();
    public List<OutputFormat> OutputFormats { get; set; } = new() { OutputFormat.Edl };
}
