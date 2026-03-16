using System;

namespace CommDetect.Core;

/// <summary>
/// Processing mode — what to do with detected commercials.
/// </summary>
public enum ProcessingMode
{
    /// <summary>Generate EDL/chapter files only. Player skips during playback.</summary>
    Skip,
    /// <summary>Embed chapter markers into the file container.</summary>
    Chapter,
    /// <summary>Remove commercial segments and produce a new file.</summary>
    Cut,
    /// <summary>Both chapter-mark AND produce a cut version.</summary>
    Both
}

/// <summary>
/// What to do with the original file after processing.
/// </summary>
public enum OriginalAction
{
    Keep,
    Delete,
    Move
}

/// <summary>
/// Container format for output.
/// </summary>
public enum ContainerFormat
{
    Mkv,
    Mp4,
    Ts,
    Original
}

/// <summary>
/// Method for removing commercial segments.
/// </summary>
public enum CutMethod
{
    /// <summary>Concatenate extracted segments. Fast, may glitch at cuts.</summary>
    Concat,
    /// <summary>Re-encode around cut points for seamless transitions.</summary>
    Reencode,
    /// <summary>Re-encode only near cut points, stream-copy the rest.</summary>
    Smart
}

/// <summary>
/// Hardware acceleration backend for encoding.
/// </summary>
public enum HardwareAccel
{
    None,
    Nvenc,
    Qsv,
    Vaapi,
    VideoToolbox,
    Amf
}

/// <summary>
/// Deinterlace mode.
/// </summary>
public enum DeinterlaceMode
{
    None,
    Yadif,
    Bwdif,
    Auto
}

/// <summary>
/// Typed configuration loaded from comprocess.ini.
/// Controls the entire post-detection processing pipeline.
/// </summary>
public class ProcessingConfig
{
    // ── General ──────────────────────────────────────────────
    public ProcessingMode Mode { get; set; } = ProcessingMode.Skip;
    public OriginalAction OriginalAction { get; set; } = OriginalAction.Keep;
    public string OriginalArchiveDir { get; set; } = "/mnt/archive/originals";
    public string OutputNamePattern { get; set; } = "{name}.{mode}.{ext}";
    public bool OverwriteExisting { get; set; } = false;

    // ── Container ────────────────────────────────────────────
    public ContainerFormat ContainerFormat { get; set; } = ContainerFormat.Mkv;
    public bool RemuxOnly { get; set; } = true;

    // ── Chapter Marking ──────────────────────────────────────
    public string ProgramChapterLabel { get; set; } = "Part {number}";
    public string CommercialChapterLabel { get; set; } = "Commercial Break {number}";
    public bool HideCommercialChapters { get; set; } = true;
    public bool SetDefaultEdition { get; set; } = true;
    public string ChapterTool { get; set; } = "auto";

    // ── Cut Settings ─────────────────────────────────────────
    public CutMethod CutMethod { get; set; } = CutMethod.Smart;
    public double SmartReencodeMargin { get; set; } = 2.0;
    public bool FadeAtCuts { get; set; } = false;
    public double FadeDuration { get; set; } = 0.5;

    // ── Video Encode ─────────────────────────────────────────
    public string VideoCodec { get; set; } = "libx264";
    public string VideoPreset { get; set; } = "veryfast";
    public int Crf { get; set; } = 20;
    public string Resolution { get; set; } = "";
    public string PixelFormat { get; set; } = "yuv420p";
    public HardwareAccel HwAccel { get; set; } = HardwareAccel.None;
    public DeinterlaceMode Deinterlace { get; set; } = DeinterlaceMode.Auto;
    public string ExtraVideoFilters { get; set; } = "";
    public string ExtraArgs { get; set; } = "";

    // ── Audio Encode ─────────────────────────────────────────
    public string AudioCodec { get; set; } = "copy";
    public string AudioBitrate { get; set; } = "192k";
    public string AudioSampleRate { get; set; } = "";
    public string AudioChannels { get; set; } = "";
    public bool NormalizeAudio { get; set; } = false;
    public double NormalizeTarget { get; set; } = -16.0;

    // ── Subtitles ────────────────────────────────────────────
    public string CopySubtitles { get; set; } = "all";
    public string PreferredLanguages { get; set; } = "eng";
    public bool AdjustSubtitleTiming { get; set; } = true;

    // ── Metadata ─────────────────────────────────────────────
    public bool CopyMetadata { get; set; } = true;
    public bool AddProcessingInfo { get; set; } = true;
    public string CustomMetadata { get; set; } = "";

    // ── Advanced ─────────────────────────────────────────────
    public string FFmpegPath { get; set; } = "";
    public string FFprobePath { get; set; } = "";
    public string MkvmergePath { get; set; } = "";
    public string TempDir { get; set; } = "";
    public bool KeepTempFiles { get; set; } = false;
    public string ProcessPriority { get; set; } = "below_normal";
    public string FFmpegLogLevel { get; set; } = "error";
    public int ProcessingTimeoutMinutes { get; set; } = 0;
}

/// <summary>
/// Maps comprocess.ini to/from ProcessingConfig.
/// </summary>
public static class ProcessingIniMapper
{
    public static ProcessingConfig LoadProcessingConfig(IniFile ini)
    {
        var config = new ProcessingConfig();

        // General
        config.Mode = ParseEnum(ini.GetString("General", "mode", "skip"), ProcessingMode.Skip);
        config.OriginalAction = ParseEnum(ini.GetString("General", "original_action", "keep"), OriginalAction.Keep);
        config.OriginalArchiveDir = ini.GetString("General", "original_archive_dir", config.OriginalArchiveDir) ?? config.OriginalArchiveDir;
        config.OutputNamePattern = ini.GetString("General", "output_name_pattern", config.OutputNamePattern) ?? config.OutputNamePattern;
        config.OverwriteExisting = ini.GetBool("General", "overwrite_existing", config.OverwriteExisting);

        // Container
        config.ContainerFormat = ParseEnum(ini.GetString("Container", "format", "mkv"), ContainerFormat.Mkv);
        config.RemuxOnly = ini.GetBool("Container", "remux_only", config.RemuxOnly);

        // Chapter Marking
        config.ProgramChapterLabel = ini.GetString("ChapterMarking", "program_chapter_label", config.ProgramChapterLabel) ?? config.ProgramChapterLabel;
        config.CommercialChapterLabel = ini.GetString("ChapterMarking", "commercial_chapter_label", config.CommercialChapterLabel) ?? config.CommercialChapterLabel;
        config.HideCommercialChapters = ini.GetBool("ChapterMarking", "hide_commercial_chapters", config.HideCommercialChapters);
        config.SetDefaultEdition = ini.GetBool("ChapterMarking", "set_default_edition", config.SetDefaultEdition);
        config.ChapterTool = ini.GetString("ChapterMarking", "chapter_tool", config.ChapterTool) ?? config.ChapterTool;

        // Cut
        config.CutMethod = ParseEnum(ini.GetString("Cut", "cut_method", "smart"), CutMethod.Smart);
        config.SmartReencodeMargin = ini.GetDouble("Cut", "smart_reencode_margin", config.SmartReencodeMargin);
        config.FadeAtCuts = ini.GetBool("Cut", "fade_at_cuts", config.FadeAtCuts);
        config.FadeDuration = ini.GetDouble("Cut", "fade_duration", config.FadeDuration);

        // Video Encode
        config.VideoCodec = ini.GetString("VideoEncode", "codec", config.VideoCodec) ?? config.VideoCodec;
        config.VideoPreset = ini.GetString("VideoEncode", "preset", config.VideoPreset) ?? config.VideoPreset;
        config.Crf = ini.GetInt("VideoEncode", "crf", config.Crf);
        config.Resolution = ini.GetString("VideoEncode", "resolution", config.Resolution) ?? config.Resolution;
        config.PixelFormat = ini.GetString("VideoEncode", "pixel_format", config.PixelFormat) ?? config.PixelFormat;
        config.HwAccel = ParseEnum(ini.GetString("VideoEncode", "hw_accel", "none"), HardwareAccel.None);
        config.Deinterlace = ParseEnum(ini.GetString("VideoEncode", "deinterlace", "auto"), DeinterlaceMode.Auto);
        config.ExtraVideoFilters = ini.GetString("VideoEncode", "extra_filters", config.ExtraVideoFilters) ?? config.ExtraVideoFilters;
        config.ExtraArgs = ini.GetString("VideoEncode", "extra_args", config.ExtraArgs) ?? config.ExtraArgs;

        // Audio Encode
        config.AudioCodec = ini.GetString("AudioEncode", "codec", config.AudioCodec) ?? config.AudioCodec;
        config.AudioBitrate = ini.GetString("AudioEncode", "bitrate", config.AudioBitrate) ?? config.AudioBitrate;
        config.AudioSampleRate = ini.GetString("AudioEncode", "sample_rate", config.AudioSampleRate) ?? config.AudioSampleRate;
        config.AudioChannels = ini.GetString("AudioEncode", "channels", config.AudioChannels) ?? config.AudioChannels;
        config.NormalizeAudio = ini.GetBool("AudioEncode", "normalize", config.NormalizeAudio);
        config.NormalizeTarget = ini.GetDouble("AudioEncode", "normalize_target", config.NormalizeTarget);

        // Subtitles
        config.CopySubtitles = ini.GetString("SubtitleHandling", "copy_subtitles", config.CopySubtitles) ?? config.CopySubtitles;
        config.PreferredLanguages = ini.GetString("SubtitleHandling", "preferred_languages", config.PreferredLanguages) ?? config.PreferredLanguages;
        config.AdjustSubtitleTiming = ini.GetBool("SubtitleHandling", "adjust_timing", config.AdjustSubtitleTiming);

        // Metadata
        config.CopyMetadata = ini.GetBool("Metadata", "copy_metadata", config.CopyMetadata);
        config.AddProcessingInfo = ini.GetBool("Metadata", "add_processing_info", config.AddProcessingInfo);
        config.CustomMetadata = ini.GetString("Metadata", "custom_metadata", config.CustomMetadata) ?? config.CustomMetadata;

        // Advanced
        config.FFmpegPath = ini.GetString("Advanced", "ffmpeg_path", config.FFmpegPath) ?? config.FFmpegPath;
        config.FFprobePath = ini.GetString("Advanced", "ffprobe_path", config.FFprobePath) ?? config.FFprobePath;
        config.MkvmergePath = ini.GetString("Advanced", "mkvmerge_path", config.MkvmergePath) ?? config.MkvmergePath;
        config.TempDir = ini.GetString("Advanced", "temp_dir", config.TempDir) ?? config.TempDir;
        config.KeepTempFiles = ini.GetBool("Advanced", "keep_temp_files", config.KeepTempFiles);
        config.ProcessPriority = ini.GetString("Advanced", "process_priority", config.ProcessPriority) ?? config.ProcessPriority;
        config.FFmpegLogLevel = ini.GetString("Advanced", "ffmpeg_log_level", config.FFmpegLogLevel) ?? config.FFmpegLogLevel;
        config.ProcessingTimeoutMinutes = ini.GetInt("Advanced", "processing_timeout_minutes", config.ProcessingTimeoutMinutes);

        return config;
    }

    private static T ParseEnum<T>(string? value, T defaultValue) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        return Enum.TryParse<T>(value.Replace("_", ""), ignoreCase: true, out var result)
            ? result : defaultValue;
    }
}
