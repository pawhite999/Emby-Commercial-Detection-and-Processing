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
    // When true, dense BF events are clustered into pod-spanning segments before
    // scoring (same approach as Logo/SC detectors).  Use for shows where logo
    // detection is disabled but commercial pods contain many BFs at regular
    // intervals (e.g. one BF per 15s at each ad boundary within the pod).
    // Isolated single BFs at program transitions are discarded by the cluster
    // filter, leaving only true commercial-pod clusters with high scores.
    public bool   BfUseClustering            { get; set; } = false;
    public int    BfMinCount                 { get; set; } = 1;
    public double BfClusterMaxGapSeconds     { get; set; } = 20.0;
    public double BfClusterMinDurationSeconds{ get; set; } = 60.0;
    public int    BfClusterMinEventCount     { get; set; } = 4;
    public double SceneChangeWeight { get; set; } = 0.35;
    public double SilenceWeight { get; set; } = 0.15;
    public double LogoAbsenceWeight { get; set; } = 0.25;
    public double AspectRatioWeight { get; set; } = 0.05;
    public double AudioRepetitionWeight { get; set; } = 0.05;

    // Detector toggles
    public double LogoLearnDurationSeconds { get; set; } = 120.0;

    /// <summary>
    /// When >= 0, overrides SkipStartSeconds as the start of the logo learning window.
    /// Use when skip_start_seconds is set early (e.g. past pre-show graphics only) but
    /// the first commercial break immediately follows — pushing the learning window past
    /// that break prevents capturing a logo-absent reference frame.
    /// Default -1 = use SkipStartSeconds (existing behavior).
    /// </summary>
    public double LogoLearnStartSeconds { get; set; } = -1.0;

    /// <summary>
    /// When >= 0, directly sets the timestamp (seconds) at which the logo reference
    /// frame is extracted, overriding the default midpoint of the learn window.
    /// Use when the default midpoint lands on a black frame or pre-show content.
    /// Default -1 = use learnStart + learnDuration / 2 (existing behavior).
    /// </summary>
    public double LogoReferenceSeconds { get; set; } = -1.0;

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

    /// <summary>
    /// Corner activity rate (events/s) above which a corner is classified as ticker-affected
    /// and excluded from logo detection. Raise this for channels where the network bug
    /// corners have moderate activity that falls just above the default 0.5 threshold.
    /// </summary>
    public double LogoCornerTickerThreshold { get; set; } = 0.5;

    /// <summary>
    /// Fractional size of each corner crop (width and height as a fraction of frame size).
    /// Default 0.10 = 10% × 10% patch. Increase to 0.15 or 0.20 for logos that sit
    /// slightly inside the frame edge (e.g. CBS eye on Colbert at ~12.9% from bottom).
    /// </summary>
    public double LogoCropSize { get; set; } = 0.10;

    /// <summary>
    /// Horizontal inset for left/right corner crops as a fraction of frame width.
    /// Default 0.0 = crop from the frame edge. Use to shift crops past pillarbox
    /// bars or to centre them on a logo that sits away from the horizontal edges.
    /// </summary>
    public double LogoXInset { get; set; } = 0.0;

    /// <summary>
    /// Vertical inset for top/bottom corner crops as a fraction of frame height.
    /// Default 0.0 = crop from the frame edge. Use when the logo sits above the
    /// bottom edge or below the top edge — e.g. logo_y_inset=0.09 centres the
    /// bottom crops 14% up from the bottom of the frame.
    /// </summary>
    public double LogoYInset { get; set; } = 0.0;

    // Scene change detector tuning
    // threshold: scene-score a frame must exceed to be counted as a cut (0–1).
    //   Lower = more sensitive; higher = only hard cuts.
    public double SceneChangeThreshold { get; set; } = 0.35;
    // When true, sparse-cut regions score 1.0 and dense-cut segments score 0.0.
    // Use for shows where commercial breaks are quiet (few cuts) and program
    // content is action-heavy (many cuts), e.g. Saturday Night Live.
    public bool SceneChangeInvert { get; set; } = false;

    // Duration constraints
    public double MinCommercialDurationSeconds { get; set; } = 30.0;
    public double MaxCommercialDurationSeconds { get; set; } = 600.0;

    /// <summary>
    /// If > 0, adjacent commercial segments separated by a program gap shorter than
    /// this many seconds are merged into a single commercial segment.  Bridges
    /// individual ads within a pod that scored just below threshold (e.g. sponsored
    /// content with logo visible, or drug ads at the start of a break).
    /// Default 0 = disabled.
    /// </summary>
    public double CommercialMergeGapSeconds { get; set; } = 0.0;

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

    // ATSC PSIP auto-detection
    // When enabled, CommDetect reads the STT and EIT tables embedded in the TS stream
    // to determine exact program boundaries — no external EPG source required.
    // Overrides skip_start_seconds/skip_end_seconds when successful.
    // Falls back to ini values if the file has no PSIP data (e.g. non-TS sources).
    public bool PsipEnabled { get; set; } = true;

    // XDS (Extended Data Services / EIA-608) program boundary detection.
    // Reads Class 1 (Current Program) packets from the EIA-608 closed-caption stream
    // embedded in the video track. Unlike PSIP, XDS survives Emby/HDHomeRun DVR recording
    // because the video track is preserved. Provides minute-precision program boundaries
    // from the broadcast signal with no external EPG required.
    // Used as a fallback when PSIP is unavailable or disabled.
    // Overrides skip_start_seconds/skip_end_seconds when successful.
    public bool XdsEnabled { get; set; } = true;

    // Letterbox detector
    // Detects sustained darkness in the center-top strip of the frame (above the active
    // 4:3 picture in pillarboxed content). When all four borders go dark during commercial
    // breaks (as on Great TV / Story TV), the center-top strip turns black for the entire
    // break duration, giving a clean binary signal.
    public bool   EnableLetterboxDetection { get; set; } = false;
    public double LetterboxWeight          { get; set; } = 0.50;
    /// <summary>Minimum continuous darkness (seconds) to count as a letterbox segment. Default 5s filters scene-cut flashes.</summary>
    public double LetterboxMinDuration     { get; set; } = 5.0;
    /// <summary>Fraction of pixels in the strip that must be below pix_th to count as black (0–1). Default 0.85.</summary>
    public double LetterboxPicThreshold    { get; set; } = 0.85;

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

/// <summary>Settings for the run-log and EDL archiving system.</summary>
public class LoggingConfig
{
    /// <summary>When true, each run writes a log file and EDL copy to the archive directories.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Directory where log files are written. Created automatically if missing.</summary>
    public string LogDirectory { get; set; } = "/tmp/logs/log";

    /// <summary>Directory where EDL copies are written. Created automatically if missing.</summary>
    public string EdlDirectory { get; set; } = "/tmp/logs/edl";

    /// <summary>Log and EDL files older than this many days are deleted on startup.</summary>
    public int RetentionDays { get; set; } = 3;
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
