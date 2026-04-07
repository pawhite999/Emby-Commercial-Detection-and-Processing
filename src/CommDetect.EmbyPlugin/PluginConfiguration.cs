using MediaBrowser.Model.Plugins;

namespace CommDetect.EmbyPlugin;

/// <summary>
/// Stores all CommDetect and ComProcess settings.
/// Properties map 1:1 to commdetect.ini and comprocess.ini keys.
/// When saved, the plugin writes updated ini files to the same directory as the binary.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ── Plugin-specific ───────────────────────────────────────────────────────

    /// <summary>Full path to the commdetect binary (e.g. /usr/local/bin/commdetect).</summary>
    public string CommdetectBinaryPath { get; set; } = "/usr/local/bin/commdetect";

    // ══════════════════════════════════════════════════════════════════════════
    // commdetect.ini
    // ══════════════════════════════════════════════════════════════════════════

    // ── [General] ─────────────────────────────────────────────────────────────
    public string Preset { get; set; } = "default";
    public double WindowSize { get; set; } = 2.0;
    public double FrameSampleRate { get; set; } = 3.0;
    public int MaxParallelism { get; set; } = 0;

    // ── [Detection] ───────────────────────────────────────────────────────────
    public double CommercialThreshold { get; set; } = 0.38;
    public int SmoothingRadius { get; set; } = 3;
    public int SkipStartSeconds { get; set; } = 0;
    public int SkipEndSeconds { get; set; } = 0;
    public int MinCommercialSeconds { get; set; } = 60;
    public int MaxCommercialSeconds { get; set; } = 600;
    public double PaddingSeconds { get; set; } = 0.5;

    // ── [BlackFrame] ──────────────────────────────────────────────────────────
    public bool BlackFrameEnabled { get; set; } = true;
    public int BlackFrameLumaThreshold { get; set; } = 12;
    public int BlackFrameMinCount { get; set; } = 1;
    public double BlackFrameWeight { get; set; } = 0.60;
    public bool BlackFrameUseClustering { get; set; } = true;
    public int BlackFrameClusterMaxGap { get; set; } = 220;
    public int BlackFrameClusterMinDuration { get; set; } = 60;
    public int BlackFrameClusterMinEvents { get; set; } = 2;

    // ── [SceneChange] ─────────────────────────────────────────────────────────
    public bool SceneChangeEnabled { get; set; } = true;
    public double SceneChangeThreshold { get; set; } = 0.40;
    public double SceneChangeRateThreshold { get; set; } = 12.0;
    public double SceneChangeWeight { get; set; } = 0.05;

    // ── [Silence] ─────────────────────────────────────────────────────────────
    public bool SilenceEnabled { get; set; } = true;
    public double SilenceRmsThreshold { get; set; } = 0.01;
    public double SilenceMinDuration { get; set; } = 0.3;
    public double SilenceWeight { get; set; } = 0.15;

    // ── [Logo] ────────────────────────────────────────────────────────────────
    public bool LogoEnabled { get; set; } = true;
    public int LogoLearnDuration { get; set; } = 180;
    public int LogoClusterMinEvents { get; set; } = 3;
    public double LogoWeight { get; set; } = 0.40;
    public double LogoSsimThreshold { get; set; } = 0.70;
    public double LogoCornerFilterRatio { get; set; } = 1.5;

    // ── [Letterbox] ───────────────────────────────────────────────────────────
    public bool LetterboxEnabled { get; set; } = false;
    public double LetterboxWeight { get; set; } = 0.50;
    public double LetterboxMinDuration { get; set; } = 5.0;
    public double LetterboxPicThreshold { get; set; } = 0.85;

    // ── [AspectRatio] ─────────────────────────────────────────────────────────
    public bool AspectRatioEnabled { get; set; } = true;
    public double AspectRatioWeight { get; set; } = 0.05;

    // ── [AudioFingerprint] ────────────────────────────────────────────────────
    public bool AudioFingerprintEnabled { get; set; } = false;
    public int AudioFingerprintMinRepetitions { get; set; } = 2;
    public double AudioFingerprintWeight { get; set; } = 0.30;

    // ── [Emby] ────────────────────────────────────────────────────────────────
    public string EmbyServerUrl { get; set; } = "";
    public string EmbyApiKey { get; set; } = "";

    // ── [Psip] ────────────────────────────────────────────────────────────────
    public bool PsipEnabled { get; set; } = true;

    // ── [Xds] ─────────────────────────────────────────────────────────────────
    public bool XdsEnabled { get; set; } = true;

    // ── [Logging] ─────────────────────────────────────────────────────────────
    public bool LoggingEnabled { get; set; } = true;
    public string LogDir { get; set; } = "/tmp/logs/log";
    public string EdlDir { get; set; } = "/tmp/logs/edl";
    public int RetentionDays { get; set; } = 3;
    public string VideoDir { get; set; } = "";

    // ── [Output] ──────────────────────────────────────────────────────────────
    public string OutputFormats { get; set; } = "edl";
    public string OutputLocation { get; set; } = "alongside";

    // ── [Watch] ───────────────────────────────────────────────────────────────
    public int WatchStabilityDelay { get; set; } = 30;
    public int WatchMinFileSizeMb { get; set; } = 50;
    public bool WatchSkipAlreadyProcessed { get; set; } = true;
    public int WatchMaxConcurrentJobs { get; set; } = 1;
    public string WatchFileExtensions { get; set; } = ".ts,.mpg,.mpeg,.mp4,.mkv,.avi,.wtv,.m4v";
    public string WatchExcludePatterns { get; set; } = "*.partial.*,*temp*,*.tmp";
    public bool WatchProcessExistingOnStartup { get; set; } = false;

    // ══════════════════════════════════════════════════════════════════════════
    // comprocess.ini
    // ══════════════════════════════════════════════════════════════════════════

    // ── [General] ─────────────────────────────────────────────────────────────
    public string ProcessingMode { get; set; } = "cut";
    public string OriginalAction { get; set; } = "keep";
    public string OriginalArchiveDir { get; set; } = "/mnt/archive/originals";
    public string OutputNamePattern { get; set; } = "{name}.{mode}.{ext}";
    public bool OverwriteExisting { get; set; } = false;

    // ── [Container] ───────────────────────────────────────────────────────────
    public string ContainerFormat { get; set; } = "mkv";
    public bool RemuxOnly { get; set; } = true;

    // ── [ChapterMarking] ──────────────────────────────────────────────────────
    public string ProgramChapterLabel { get; set; } = "Part {number}";
    public string CommercialChapterLabel { get; set; } = "Commercial Break {number}";
    public bool HideCommercialChapters { get; set; } = true;
    public bool SetDefaultEdition { get; set; } = true;
    public string ChapterTool { get; set; } = "auto";

    // ── [Cut] ─────────────────────────────────────────────────────────────────
    public string CutMethod { get; set; } = "concat";
    public double SmartReencodemargin { get; set; } = 2.0;
    public bool FadeAtCuts { get; set; } = false;
    public double FadeDuration { get; set; } = 0.5;

    // ── [VideoEncode] ─────────────────────────────────────────────────────────
    public string VideoCodec { get; set; } = "libx264";
    public string VideoPreset { get; set; } = "faster";
    public int VideoCrf { get; set; } = 20;
    public string VideoResolution { get; set; } = "";
    public string VideoPixelFormat { get; set; } = "yuv420p";
    public string VideoHwAccel { get; set; } = "none";
    public string VideoDeinterlace { get; set; } = "bwdif=mode=1";
    public string VideoExtraFilters { get; set; } = "";
    public string VideoExtraArgs { get; set; } = "";

    // ── [AudioEncode] ─────────────────────────────────────────────────────────
    public string AudioCodec { get; set; } = "aac";
    public string AudioBitrate { get; set; } = "192k";
    public string AudioSampleRate { get; set; } = "";
    public string AudioChannels { get; set; } = "";
    public bool AudioNormalize { get; set; } = false;
    public int AudioNormalizeTarget { get; set; } = -16;

    // ── [SubtitleHandling] ────────────────────────────────────────────────────
    public string CopySubtitles { get; set; } = "all";
    public string PreferredLanguages { get; set; } = "eng";
    public bool AdjustTiming { get; set; } = true;

    // ── [Metadata] ────────────────────────────────────────────────────────────
    public bool CopyMetadata { get; set; } = true;
    public bool AddProcessingInfo { get; set; } = true;
    public string CustomMetadata { get; set; } = "";

    // ── [Advanced] ────────────────────────────────────────────────────────────
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
    public string MkvmergePath { get; set; } = "";
    public string TempDir { get; set; } = "";
    public bool KeepTempFiles { get; set; } = false;
    public string ProcessPriority { get; set; } = "below_normal";
    public string FfmpegLogLevel { get; set; } = "error";
    public int ProcessingTimeoutMinutes { get; set; } = 0;
}
