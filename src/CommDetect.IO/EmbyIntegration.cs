using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using CommDetect.Core;
using Microsoft.Extensions.Logging;

namespace CommDetect.IO;

/// <summary>
/// Discovers Emby Server configuration and recording paths.
/// Enables zero-configuration setup when CommDetect is installed 
/// alongside an existing Emby Server installation.
/// 
/// Auto-detects:
/// - Emby's data directory (where config lives)
/// - Recording directories configured in Emby's Live TV settings
/// - Emby's bundled FFmpeg path
/// </summary>
public class EmbyIntegration
{
    private readonly ILogger<EmbyIntegration>? _logger;

    public EmbyIntegration(ILogger<EmbyIntegration>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Attempt to discover Emby Server's data directory.
    /// </summary>
    public string? FindEmbyDataDirectory()
    {
        foreach (var candidate in GetEmbyDataPaths())
        {
            if (Directory.Exists(candidate))
            {
                _logger?.LogInformation("Found Emby data directory: {Path}", candidate);
                return candidate;
            }
        }

        _logger?.LogDebug("Emby data directory not found");
        return null;
    }

    /// <summary>
    /// Discover Emby's configured recording directories by reading its config.
    /// Returns the paths where Emby stores DVR recordings.
    /// </summary>
    public List<string> FindRecordingDirectories(string? embyDataDir = null)
    {
        embyDataDir ??= FindEmbyDataDirectory();
        if (embyDataDir == null) return new List<string>();

        var recordingDirs = new List<string>();

        // Try reading Emby's system.xml for recording path configuration
        string systemXml = Path.Combine(embyDataDir, "config", "system.xml");
        if (File.Exists(systemXml))
        {
            try
            {
                var content = File.ReadAllText(systemXml);
                var paths = ExtractRecordingPaths(content);
                recordingDirs.AddRange(paths);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Could not read Emby system.xml: {Error}", ex.Message);
            }
        }

        // Also check the live TV configuration
        string liveTvConfig = Path.Combine(embyDataDir, "config", "livetv.xml");
        if (File.Exists(liveTvConfig))
        {
            try
            {
                var content = File.ReadAllText(liveTvConfig);
                var paths = ExtractRecordingPaths(content);
                recordingDirs.AddRange(paths);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Could not read Emby livetv.xml: {Error}", ex.Message);
            }
        }

        // Check for common recording directory patterns
        var defaultRecDirs = GetDefaultRecordingPaths(embyDataDir);
        foreach (var dir in defaultRecDirs)
        {
            if (Directory.Exists(dir) && !recordingDirs.Contains(dir))
            {
                recordingDirs.Add(dir);
            }
        }

        // Deduplicate
        recordingDirs = recordingDirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (recordingDirs.Count > 0)
        {
            _logger?.LogInformation("Discovered {Count} Emby recording directory(ies):", recordingDirs.Count);
            foreach (var dir in recordingDirs)
                _logger?.LogInformation("  {Dir}", dir);
        }

        return recordingDirs;
    }

    /// <summary>
    /// Generate a WatchConfig pre-configured for Emby integration.
    /// </summary>
    public WatchConfig CreateEmbyWatchConfig(string? embyDataDir = null)
    {
        var recordingDirs = FindRecordingDirectories(embyDataDir);

        return new WatchConfig
        {
            WatchDirectories = recordingDirs,
            Recursive = true,
            StabilityDelaySeconds = 60, // Emby recordings may take time to finalize
            MinFileSizeMB = 50.0,
            SkipAlreadyProcessed = true,
            ProcessExistingOnStartup = false,
            NotifyOnComplete = true,
            FileExtensions = new List<string>
            {
                ".ts",      // MPEG Transport Stream (most common DVR format)
                ".mpg",     // MPEG
                ".mpeg",    // MPEG
                ".mp4",     // MP4
                ".mkv",     // Matroska
                ".wtv",     // Windows TV recording
                ".m4v",     // MPEG-4
            }
        };
    }

    /// <summary>
    /// Generate a post-processing script that Emby can call after recording.
    /// Returns the script content for the current platform.
    /// </summary>
    public string GenerateEmbyPostProcessScript(string commdetectPath, DetectionConfig? config = null)
    {
        string configArg = "";
        if (config != null)
        {
            // Suggest saving config next to the executable
            string configPath = Path.ChangeExtension(commdetectPath, ".json");
            configArg = $" --config \"{configPath}\"";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return $"""
                @echo off
                REM CommDetect post-processing script for Emby Server (Windows)
                REM Configure in Emby: Dashboard > Live TV > Recording Post Processing
                REM Set this script as the post-processing command.
                REM Emby passes the recording path as the first argument.

                set RECORDING=%1

                REM Remove surrounding quotes if present
                set RECORDING=%RECORDING:"=%

                echo [CommDetect] Processing: %RECORDING%
                "{commdetectPath}" process "%RECORDING%" --preset default --format edl{configArg}

                if %ERRORLEVEL% EQU 0 (
                    echo [CommDetect] Success: %RECORDING%
                ) else (
                    echo [CommDetect] Failed with exit code %ERRORLEVEL%: %RECORDING%
                )
                """;
        }
        else
        {
            return $"""
                #!/bin/bash
                # CommDetect post-processing script for Emby Server (Linux/macOS/FreeBSD)
                # Configure in Emby: Dashboard > Live TV > Recording Post Processing
                # Set this script as the post-processing command.
                # Emby passes the recording path as the first argument.

                RECORDING="$1"

                if [ -z "$RECORDING" ]; then
                    echo "[CommDetect] Error: No recording path provided"
                    exit 1
                fi

                echo "[CommDetect] Processing: $RECORDING"
                "{commdetectPath}" process "$RECORDING" --preset default --format edl{configArg}

                EXIT_CODE=$?
                if [ $EXIT_CODE -eq 0 ]; then
                    echo "[CommDetect] Success: $RECORDING"
                else
                    echo "[CommDetect] Failed with exit code $EXIT_CODE: $RECORDING"
                fi

                exit $EXIT_CODE
                """;
        }
    }

    // ── Emby Data Directory Discovery ───────────────────────────────────

    private static IEnumerable<string> GetEmbyDataPaths()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            yield return Path.Combine(appData, "Emby-Server");
            yield return Path.Combine(localAppData, "Emby-Server");
            yield return Path.Combine(programData, "Emby-Server");
            yield return Path.Combine(programData, "Emby");

            // Docker on Windows
            yield return @"C:\EmbyServer\data";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, "Library/Application Support/Emby-Server/data");
            yield return "/var/lib/emby";
        }
        else // Linux, FreeBSD
        {
            yield return "/var/lib/emby";
            yield return "/var/lib/emby-server";
            yield return "/opt/emby-server/data";

            // Docker common mounts
            yield return "/config";
            yield return "/config/data";

            // TrueNAS jail
            yield return "/usr/local/emby-server/data";
            yield return "/var/db/emby";

            // Synology
            yield return "/var/packages/EmbyServer/var/data";

            // User home directory installs
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, ".emby-server/data");
                yield return Path.Combine(home, "emby-server/data");
            }
        }
    }

    private static IEnumerable<string> GetDefaultRecordingPaths(string embyDataDir)
    {
        // Emby's default recording location is typically inside its data dir
        yield return Path.Combine(embyDataDir, "recordings");
        yield return Path.Combine(embyDataDir, "tv-recordings");
        yield return Path.Combine(embyDataDir, "data", "recordings");

        // Common user-configured locations
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Recordings");
            yield return @"D:\Recordings";
            yield return @"E:\Recordings";
        }
        else
        {
            yield return "/mnt/recordings";
            yield return "/mnt/media/recordings";
            yield return "/mnt/data/recordings";
            yield return "/media/recordings";
            yield return "/storage/recordings";

            // TrueNAS common pool paths
            yield return "/mnt/pool/recordings";
            yield return "/mnt/tank/recordings";
            yield return "/mnt/data/media/recordings";
        }
    }

    /// <summary>
    /// Parse recording paths from Emby XML config files.
    /// Handles both system.xml and livetv.xml formats.
    /// </summary>
    private List<string> ExtractRecordingPaths(string xmlContent)
    {
        var paths = new List<string>();

        // Simple XML parsing — look for recording path elements
        // Emby uses elements like:
        //   <RecordingPath>/path/to/recordings</RecordingPath>
        //   <RecordingPostProcessorPath>...</RecordingPostProcessorPath>
        //   <MovieRecordingPath>...</MovieRecordingPath>
        //   <SeriesRecordingPath>...</SeriesRecordingPath>

        var pathElements = new[]
        {
            "RecordingPath",
            "MovieRecordingPath",
            "SeriesRecordingPath",
            "DefaultRecordingPath"
        };

        foreach (var element in pathElements)
        {
            string startTag = $"<{element}>";
            string endTag = $"</{element}>";

            int startIdx = 0;
            while (true)
            {
                startIdx = xmlContent.IndexOf(startTag, startIdx, StringComparison.OrdinalIgnoreCase);
                if (startIdx < 0) break;

                startIdx += startTag.Length;
                int endIdx = xmlContent.IndexOf(endTag, startIdx, StringComparison.OrdinalIgnoreCase);
                if (endIdx < 0) break;

                string path = xmlContent.Substring(startIdx, endIdx - startIdx).Trim();
                if (!string.IsNullOrEmpty(path) && (Directory.Exists(path) || Path.IsPathRooted(path)))
                {
                    paths.Add(path);
                    _logger?.LogDebug("Found Emby recording path in config: {Path}", path);
                }

                startIdx = endIdx;
            }
        }

        return paths;
    }
}
