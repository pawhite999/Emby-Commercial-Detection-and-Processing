using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CommDetect.Core;
using Microsoft.Extensions.Logging;

namespace CommDetect.IO;

/// <summary>
/// Locates FFmpeg and FFprobe binaries across all supported platforms.
/// Search order: config override → bundled → Emby/media server → PATH → common install locations.
/// 
/// Emby-aware: automatically discovers FFmpeg bundled with Emby Server on all
/// supported platforms, so users with Emby don't need a separate FFmpeg install.
/// </summary>
public class CrossPlatformFFmpegLocator : IFFmpegLocator
{
    private readonly ILogger<CrossPlatformFFmpegLocator>? _logger;
    private readonly string? _configuredPath;
    private readonly bool _preferEmbyFFmpeg;

    // Cache resolved paths to avoid repeated filesystem scans
    private string? _cachedFFmpegPath;
    private string? _cachedFFprobePath;
    private bool _ffmpegSearched;
    private bool _ffprobeSearched;

    public CrossPlatformFFmpegLocator(
        string? configuredPath = null,
        bool preferEmbyFFmpeg = false,
        ILogger<CrossPlatformFFmpegLocator>? logger = null)
    {
        _configuredPath = configuredPath;
        _preferEmbyFFmpeg = preferEmbyFFmpeg;
        _logger = logger;
    }

    public string? FindFFmpeg()
    {
        if (!_ffmpegSearched)
        {
            _cachedFFmpegPath = FindExecutable("ffmpeg");
            _ffmpegSearched = true;
        }
        return _cachedFFmpegPath;
    }

    public string? FindFFprobe()
    {
        if (!_ffprobeSearched)
        {
            _cachedFFprobePath = FindExecutable("ffprobe");
            _ffprobeSearched = true;
        }
        return _cachedFFprobePath;
    }

    /// <summary>
    /// Returns information about which FFmpeg was discovered and its source.
    /// Useful for diagnostics and user feedback.
    /// </summary>
    public (string? path, string source) FindFFmpegWithSource()
    {
        string execName = GetExecutableName("ffmpeg");

        // Walk the search order and report which stage found it
        if (TryFindConfigured(execName, out var p)) return (p, "user configuration");
        if (TryFindBundled(execName, out p)) return (p, "bundled with CommDetect");
        if (_preferEmbyFFmpeg && TryFindEmby(execName, out p)) return (p, $"Emby Server ({p})");
        if (TryFindInPath(execName, out p)) return (p, "system PATH");
        if (!_preferEmbyFFmpeg && TryFindEmby(execName, out p)) return (p, $"Emby Server ({p})");
        if (TryFindMediaServer(execName, out p)) return (p, $"media server ({p})");
        if (TryFindCommon(execName, out p)) return (p, $"common location ({p})");

        return (null, "not found");
    }

    private string? FindExecutable(string name)
    {
        string execName = GetExecutableName(name);

        // 1. Check configured path
        if (TryFindConfigured(execName, out var result))
        {
            _logger?.LogDebug("Found {Name} at configured path: {Path}", name, result);
            return result;
        }

        // 2. Check alongside our own executable (bundled)
        if (TryFindBundled(execName, out result))
        {
            _logger?.LogDebug("Found bundled {Name}: {Path}", name, result);
            return result;
        }

        // 3. If preferEmbyFFmpeg, check Emby before PATH
        if (_preferEmbyFFmpeg && TryFindEmby(execName, out result))
        {
            _logger?.LogInformation("Found {Name} in Emby Server: {Path}", name, result);
            return result;
        }

        // 4. Search PATH
        if (TryFindInPath(execName, out result))
        {
            _logger?.LogDebug("Found {Name} in PATH: {Path}", name, result);
            return result;
        }

        // 5. Check Emby / media server bundled FFmpeg
        if (!_preferEmbyFFmpeg && TryFindEmby(execName, out result))
        {
            _logger?.LogInformation("Found {Name} in Emby Server: {Path}", name, result);
            return result;
        }

        if (TryFindMediaServer(execName, out result))
        {
            _logger?.LogInformation("Found {Name} in media server: {Path}", name, result);
            return result;
        }

        // 6. Check platform-specific common locations
        if (TryFindCommon(execName, out result))
        {
            _logger?.LogDebug("Found {Name} at common location: {Path}", name, result);
            return result;
        }

        _logger?.LogWarning("{Name} not found on this system. Install FFmpeg or use --ffmpeg-path", name);
        return null;
    }

    private static string GetExecutableName(string name) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{name}.exe" : name;

    private bool TryFindConfigured(string execName, out string? path)
    {
        path = null;
        if (string.IsNullOrEmpty(_configuredPath)) return false;

        // Check if the configured path IS the executable directly
        if (File.Exists(_configuredPath) && _configuredPath.EndsWith(execName, StringComparison.OrdinalIgnoreCase))
        {
            path = _configuredPath;
            return true;
        }

        // Check if it's a directory containing the executable
        string candidate = Path.Combine(_configuredPath, execName);
        if (File.Exists(candidate))
        {
            path = candidate;
            return true;
        }

        return false;
    }

    private static bool TryFindBundled(string execName, out string? path)
    {
        path = null;
        string? appDir = Path.GetDirectoryName(AppContext.BaseDirectory);
        if (appDir == null) return false;

        string candidate = Path.Combine(appDir, execName);
        if (File.Exists(candidate))
        {
            path = candidate;
            return true;
        }
        return false;
    }

    private static bool TryFindInPath(string execName, out string? path)
    {
        path = null;
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return false;

        char separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';

        foreach (string dir in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir.Trim(), execName);
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }
        }
        return false;
    }

    // ── Emby Server FFmpeg Discovery ────────────────────────────────────

    private static bool TryFindEmby(string execName, out string? path)
    {
        path = null;
        foreach (var location in GetEmbyFFmpegLocations(execName))
        {
            if (File.Exists(location))
            {
                path = location;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns all known Emby Server FFmpeg installation paths per platform.
    /// Emby bundles its own FFmpeg and places it in predictable locations.
    /// </summary>
    private static IEnumerable<string> GetEmbyFFmpegLocations(string execName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Emby Server for Windows — typical install paths
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            // Emby stores ffmpeg in its system directory
            yield return Path.Combine(appData, "Emby-Server", "system", execName);
            yield return Path.Combine(localAppData, "Emby-Server", "system", execName);
            yield return Path.Combine(programFiles, "Emby-Server", "system", execName);

            // Some installations use a programdata path
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            yield return Path.Combine(programData, "Emby-Server", "system", execName);

            // Docker volume mount convention on Windows
            yield return Path.Combine(@"C:\EmbyServer", "system", execName);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Emby Server for macOS
            yield return Path.Combine("/Applications/Emby Server.app/Contents/MacOS", execName);
            yield return Path.Combine("/Applications/EmbyServer.app/Contents/MacOS", execName);

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, "Library/Application Support/Emby-Server/system", execName);

            // Homebrew cask install
            yield return Path.Combine("/usr/local/opt/emby-server/bin", execName);
            yield return Path.Combine("/opt/homebrew/opt/emby-server/bin", execName);
        }
        else
        {
            // ── Linux ────────────────────────────────────────
            // Emby .deb / .rpm package installs
            yield return Path.Combine("/opt/emby-server/bin", execName);
            yield return Path.Combine("/opt/emby-server/lib", execName);
            yield return Path.Combine("/usr/lib/emby-server", execName);
            yield return Path.Combine("/usr/lib/emby-server/bin", execName);
            yield return Path.Combine("/usr/share/emby-server", execName);

            // Emby snap
            yield return Path.Combine("/snap/emby-server/current/bin", execName);

            // Emby Docker — common mount points and internal paths
            yield return Path.Combine("/config/emby/system", execName);
            yield return Path.Combine("/app/emby/system", execName);
            yield return Path.Combine("/opt/emby/system", execName);

            // ── FreeBSD / TrueNAS ────────────────────────────
            // FreeBSD pkg install
            yield return Path.Combine("/usr/local/lib/emby-server", execName);
            yield return Path.Combine("/usr/local/share/emby-server/bin", execName);
            yield return Path.Combine("/usr/local/opt/emby-server/bin", execName);

            // TrueNAS jail — Emby plugin
            yield return Path.Combine("/usr/local/emby-server/bin", execName);
            yield return Path.Combine("/usr/pbi/emby-amd64/bin", execName);

            // iocage jail paths (TrueNAS CORE)
            yield return Path.Combine("/mnt/iocage/jails/emby/root/usr/local/bin", execName);

            // TrueNAS SCALE — Emby app via Kubernetes/Docker
            yield return Path.Combine("/mnt/pool/ix-applications/releases/emby/volumes", execName);

            // ── Synology / QNAP NAS ─────────────────────────
            yield return Path.Combine("/var/packages/EmbyServer/target/bin", execName);
            yield return Path.Combine("/share/CACHEDEV1_DATA/.qpkg/EmbyServer/bin", execName);

            // ── Generic: Emby running from extracted tarball ─
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, "emby-server/bin", execName);
                yield return Path.Combine(home, ".emby-server/bin", execName);
            }
        }
    }

    // ── Other Media Server FFmpeg Discovery ─────────────────────────────

    private static bool TryFindMediaServer(string execName, out string? path)
    {
        path = null;
        foreach (var location in GetOtherMediaServerLocations(execName))
        {
            if (File.Exists(location))
            {
                path = location;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// FFmpeg locations bundled with other media servers (Jellyfin, Plex).
    /// Checked as a fallback if Emby's FFmpeg is not found.
    /// </summary>
    private static IEnumerable<string> GetOtherMediaServerLocations(string execName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            // Jellyfin
            yield return Path.Combine(programFiles, "Jellyfin", "Server", execName);
            yield return Path.Combine(localAppData, "jellyfin", "ffmpeg", execName);

            // Plex
            yield return Path.Combine(programFiles, "Plex", "Plex Media Server", execName);
            yield return Path.Combine(localAppData, "Plex Media Server", execName);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Jellyfin
            yield return Path.Combine("/Applications/Jellyfin.app/Contents/MacOS", execName);
            // Plex
            yield return Path.Combine("/Applications/Plex Media Server.app/Contents/MacOS", execName);
        }
        else
        {
            // Jellyfin
            yield return Path.Combine("/usr/lib/jellyfin-ffmpeg", execName);
            yield return Path.Combine("/usr/lib/jellyfin-ffmpeg/ffmpeg", execName);
            yield return Path.Combine("/usr/share/jellyfin-ffmpeg", execName);

            // Plex
            yield return Path.Combine("/usr/lib/plexmediaserver", execName);
            yield return Path.Combine("/usr/lib/plexmediaserver/lib", execName);

            // FreeBSD Plex/Jellyfin
            yield return Path.Combine("/usr/local/share/jellyfin-ffmpeg", execName);
            yield return Path.Combine("/usr/local/share/plexmediaserver", execName);
        }
    }

    // ── Common System Locations ─────────────────────────────────────────

    private static bool TryFindCommon(string execName, out string? path)
    {
        path = null;
        foreach (var location in GetCommonLocations(execName))
        {
            if (File.Exists(location))
            {
                path = location;
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<string> GetCommonLocations(string execName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "FFmpeg", "bin", execName);
            yield return Path.Combine(@"C:\ffmpeg\bin", execName);
            yield return Path.Combine(@"C:\Tools\ffmpeg\bin", execName);

            // Chocolatey
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "chocolatey", "bin", execName);

            // Scoop
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
                yield return Path.Combine(userProfile, "scoop", "shims", execName);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return Path.Combine("/usr/local/bin", execName);         // Homebrew Intel
            yield return Path.Combine("/opt/homebrew/bin", execName);      // Homebrew Apple Silicon
            yield return Path.Combine("/usr/bin", execName);
            yield return Path.Combine("/opt/local/bin", execName);         // MacPorts
        }
        else // Linux, FreeBSD, etc.
        {
            yield return Path.Combine("/usr/bin", execName);
            yield return Path.Combine("/usr/local/bin", execName);
            yield return Path.Combine("/snap/bin", execName);
            yield return Path.Combine("/usr/local/share/ffmpeg", execName);

            // FreeBSD-specific
            yield return Path.Combine("/usr/local/libexec", execName);
        }
    }
}
