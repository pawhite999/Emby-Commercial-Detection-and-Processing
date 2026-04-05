using System;
using System.Collections.Generic;

namespace CommDetect.Core;

/// <summary>
/// Maps commdetect.ini key/value pairs to a DetectionConfig.
/// All values in the INI are optional — missing keys fall back to the
/// base config (or DetectionConfig defaults if no base is supplied).
/// </summary>
public static class DetectionIniMapper
{
    public static DetectionConfig LoadDetectionConfig(IniFile ini, DetectionConfig? baseConfig = null)
    {
        var config = baseConfig ?? new DetectionConfig();

        // [General]
        config.WindowSizeSeconds  = ini.GetDouble("General", "window_size",       config.WindowSizeSeconds);
        config.FrameSampleRate    = ini.GetDouble("General", "frame_sample_rate",  config.FrameSampleRate);

        // [Detection]
        config.CommercialThreshold         = ini.GetDouble("Detection", "commercial_threshold",    config.CommercialThreshold);
        config.SmoothingRadius             = ini.GetInt   ("Detection", "smoothing_radius",        config.SmoothingRadius);
        config.MinCommercialDurationSeconds = ini.GetDouble("Detection", "min_commercial_seconds",   config.MinCommercialDurationSeconds);
        config.MaxCommercialDurationSeconds = ini.GetDouble("Detection", "max_commercial_seconds",   config.MaxCommercialDurationSeconds);
        config.CommercialMergeGapSeconds    = ini.GetDouble("Detection", "commercial_merge_gap",     config.CommercialMergeGapSeconds);
        config.SkipStartSeconds            = ini.GetDouble("Detection", "skip_start_seconds",     config.SkipStartSeconds);
        config.SkipEndSeconds              = ini.GetDouble("Detection", "skip_end_seconds",       config.SkipEndSeconds);

        // [BlackFrame]
        config.BlackFrameWeight              = ini.GetDouble("BlackFrame", "weight",              config.BlackFrameWeight);
        config.BfMinCount                    = ini.GetInt   ("BlackFrame", "min_count",           config.BfMinCount);
        config.BfUseClustering               = ini.GetBool  ("BlackFrame", "use_clustering",      config.BfUseClustering);
        config.BfClusterMaxGapSeconds        = ini.GetDouble("BlackFrame", "cluster_max_gap",     config.BfClusterMaxGapSeconds);
        config.BfClusterMinDurationSeconds   = ini.GetDouble("BlackFrame", "cluster_min_duration",config.BfClusterMinDurationSeconds);
        config.BfClusterMinEventCount        = ini.GetInt   ("BlackFrame", "cluster_min_events",  config.BfClusterMinEventCount);

        // [SceneChange]
        config.SceneChangeWeight  = ini.GetDouble("SceneChange", "weight",               config.SceneChangeWeight);
        config.SceneChangeInvert  = ini.GetBool  ("SceneChange", "scene_change_invert",  config.SceneChangeInvert);

        // [Silence]
        config.SilenceWeight = ini.GetDouble("Silence", "weight", config.SilenceWeight);

        // [Logo]
        config.EnableLogoDetection        = ini.GetBool  ("Logo", "enabled",              config.EnableLogoDetection);
        config.LogoAbsenceWeight          = ini.GetDouble("Logo", "weight",                config.LogoAbsenceWeight);
        config.LogoLearnDurationSeconds   = ini.GetDouble("Logo", "learn_duration",        config.LogoLearnDurationSeconds);
        config.LogoLearnStartSeconds      = ini.GetDouble("Logo", "learn_start",           config.LogoLearnStartSeconds);
        config.LogoReferenceSeconds       = ini.GetDouble("Logo", "reference_seconds",     config.LogoReferenceSeconds);
        config.LogoSceneThreshold         = ini.GetDouble("Logo", "scene_threshold",       config.LogoSceneThreshold);
        config.LogoClusterMinEventCount   = ini.GetInt   ("Logo", "cluster_min_events",    config.LogoClusterMinEventCount);
        config.LogoClusterMaxGapSeconds   = ini.GetDouble("Logo", "cluster_max_gap",       config.LogoClusterMaxGapSeconds);
        config.LogoClusterMinDurationSeconds = ini.GetDouble("Logo", "cluster_min_duration", config.LogoClusterMinDurationSeconds);
        config.LogoSsimThreshold          = ini.GetDouble("Logo", "logo_ssim_threshold",         config.LogoSsimThreshold);
        config.LogoCornerFilterRatio      = ini.GetDouble("Logo", "logo_corner_filter_ratio",    config.LogoCornerFilterRatio);
        config.LogoCornerTickerThreshold  = ini.GetDouble("Logo", "corner_ticker_threshold",     config.LogoCornerTickerThreshold);
        config.LogoCropSize               = ini.GetDouble("Logo", "logo_crop_size",               config.LogoCropSize);
        config.LogoXInset                 = ini.GetDouble("Logo", "logo_x_inset",                 config.LogoXInset);
        config.LogoYInset                 = ini.GetDouble("Logo", "logo_y_inset",                 config.LogoYInset);

        // [SceneChange]
        config.SceneChangeThreshold = ini.GetDouble("SceneChange", "threshold", config.SceneChangeThreshold);

        // [Letterbox]
        config.EnableLetterboxDetection = ini.GetBool  ("Letterbox", "enabled",       config.EnableLetterboxDetection);
        config.LetterboxWeight          = ini.GetDouble("Letterbox", "weight",         config.LetterboxWeight);
        config.LetterboxMinDuration     = ini.GetDouble("Letterbox", "min_duration",   config.LetterboxMinDuration);
        config.LetterboxPicThreshold    = ini.GetDouble("Letterbox", "pic_threshold",  config.LetterboxPicThreshold);

        // [AspectRatio]
        config.EnableAspectRatioDetection = ini.GetBool  ("AspectRatio", "enabled", config.EnableAspectRatioDetection);
        config.AspectRatioWeight          = ini.GetDouble("AspectRatio", "weight",  config.AspectRatioWeight);

        // [AudioFingerprint]
        config.EnableAudioFingerprinting = ini.GetBool  ("AudioFingerprint", "enabled", config.EnableAudioFingerprinting);
        config.AudioRepetitionWeight     = ini.GetDouble("AudioFingerprint", "weight",  config.AudioRepetitionWeight);

        // [Emby]
        config.EmbyServerUrl = ini.GetString("Emby", "server_url", config.EmbyServerUrl) ?? "";
        config.EmbyApiKey    = ini.GetString("Emby", "api_key",    config.EmbyApiKey)    ?? "";

        // [Psip]
        config.PsipEnabled = ini.GetBool("Psip", "enabled", config.PsipEnabled);

        // [Xds]
        config.XdsEnabled = ini.GetBool("Xds", "enabled", config.XdsEnabled);

        // [Output]
        var formatStrings = ini.GetStringArray("Output", "formats");
        if (formatStrings.Length > 0)
            config.OutputFormats = ParseOutputFormats(formatStrings);

        return config;
    }

    public static LoggingConfig LoadLoggingConfig(IniFile ini)
    {
        var cfg = new LoggingConfig();
        cfg.Enabled       = ini.GetBool  ("Logging", "enabled",        cfg.Enabled);
        cfg.LogDirectory  = ini.GetString("Logging", "log_dir",        cfg.LogDirectory) ?? cfg.LogDirectory;
        cfg.EdlDirectory  = ini.GetString("Logging", "edl_dir",        cfg.EdlDirectory) ?? cfg.EdlDirectory;
        cfg.RetentionDays = ini.GetInt   ("Logging", "retention_days", cfg.RetentionDays);
        return cfg;
    }

    private static List<OutputFormat> ParseOutputFormats(string[] formats)
    {
        var result = new List<OutputFormat>();
        foreach (var f in formats)
        {
            // commdetect.ini uses "comskip_txt"; enum is "ComskipTxt"
            if (Enum.TryParse<OutputFormat>(f.Replace("_", ""), ignoreCase: true, out var fmt))
                result.Add(fmt);
        }
        return result.Count > 0 ? result : new List<OutputFormat> { OutputFormat.Edl };
    }
}
