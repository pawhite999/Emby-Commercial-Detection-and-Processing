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
        config.MinCommercialDurationSeconds = ini.GetDouble("Detection", "min_commercial_seconds", config.MinCommercialDurationSeconds);
        config.MaxCommercialDurationSeconds = ini.GetDouble("Detection", "max_commercial_seconds", config.MaxCommercialDurationSeconds);
        config.SkipStartSeconds            = ini.GetDouble("Detection", "skip_start_seconds",     config.SkipStartSeconds);
        config.SkipEndSeconds              = ini.GetDouble("Detection", "skip_end_seconds",       config.SkipEndSeconds);

        // [BlackFrame]
        config.BlackFrameWeight = ini.GetDouble("BlackFrame", "weight", config.BlackFrameWeight);

        // [SceneChange]
        config.SceneChangeWeight = ini.GetDouble("SceneChange", "weight", config.SceneChangeWeight);

        // [Silence]
        config.SilenceWeight = ini.GetDouble("Silence", "weight", config.SilenceWeight);

        // [Logo]
        config.EnableLogoDetection        = ini.GetBool  ("Logo", "enabled",              config.EnableLogoDetection);
        config.LogoAbsenceWeight          = ini.GetDouble("Logo", "weight",                config.LogoAbsenceWeight);
        config.LogoLearnDurationSeconds   = ini.GetDouble("Logo", "learn_duration",        config.LogoLearnDurationSeconds);
        config.LogoSceneThreshold         = ini.GetDouble("Logo", "scene_threshold",       config.LogoSceneThreshold);
        config.LogoClusterMinEventCount   = ini.GetInt   ("Logo", "cluster_min_events",    config.LogoClusterMinEventCount);
        config.LogoClusterMaxGapSeconds   = ini.GetDouble("Logo", "cluster_max_gap",       config.LogoClusterMaxGapSeconds);
        config.LogoClusterMinDurationSeconds = ini.GetDouble("Logo", "cluster_min_duration", config.LogoClusterMinDurationSeconds);
        config.LogoSsimThreshold          = ini.GetDouble("Logo", "logo_ssim_threshold",         config.LogoSsimThreshold);
        config.LogoCornerFilterRatio      = ini.GetDouble("Logo", "logo_corner_filter_ratio",    config.LogoCornerFilterRatio);
        config.LogoCornerTickerThreshold  = ini.GetDouble("Logo", "corner_ticker_threshold",     config.LogoCornerTickerThreshold);

        // [SceneChange]
        config.SceneChangeThreshold = ini.GetDouble("SceneChange", "threshold", config.SceneChangeThreshold);

        // [AspectRatio]
        config.EnableAspectRatioDetection = ini.GetBool  ("AspectRatio", "enabled", config.EnableAspectRatioDetection);
        config.AspectRatioWeight          = ini.GetDouble("AspectRatio", "weight",  config.AspectRatioWeight);

        // [AudioFingerprint]
        config.EnableAudioFingerprinting = ini.GetBool  ("AudioFingerprint", "enabled", config.EnableAudioFingerprinting);
        config.AudioRepetitionWeight     = ini.GetDouble("AudioFingerprint", "weight",  config.AudioRepetitionWeight);

        // [Emby]
        config.EmbyServerUrl = ini.GetString("Emby", "server_url", config.EmbyServerUrl) ?? "";
        config.EmbyApiKey    = ini.GetString("Emby", "api_key",    config.EmbyApiKey)    ?? "";

        // [Output]
        var formatStrings = ini.GetStringArray("Output", "formats");
        if (formatStrings.Length > 0)
            config.OutputFormats = ParseOutputFormats(formatStrings);

        return config;
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
