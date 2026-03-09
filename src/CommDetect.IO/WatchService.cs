using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CommDetect.Core;
using CommDetect.Analysis;
using Microsoft.Extensions.Logging;

namespace CommDetect.IO;

/// <summary>
/// Monitors directories for new media files and automatically processes them
/// for commercial detection. Designed for integration with Emby Server and 
/// similar DVR systems.
///
/// Features:
/// - Watches multiple directories with configurable extensions
/// - Waits for file stability (recording completion) before processing
/// - Skips already-processed files
/// - Concurrent job limiting to avoid overloading NAS hardware
/// - Graceful shutdown with in-progress job completion
/// </summary>
public class WatchService : IDisposable
{
    private readonly DetectionPipeline _pipeline;
    private readonly DetectionConfig _detectionConfig;
    private readonly WatchConfig _watchConfig;
    private readonly ILogger<WatchService>? _logger;

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Channel<string> _fileQueue;
    private readonly ConcurrentDictionary<string, DateTime> _pendingFiles = new();
    private readonly ConcurrentDictionary<string, byte> _processingFiles = new();
    private readonly ConcurrentDictionary<string, byte> _completedFiles = new();

    private CancellationTokenSource? _cts;
    private Task? _processingTask;
    private Task? _stabilityCheckTask;
    private int _totalProcessed;
    private int _totalFailed;

    public WatchService(
        DetectionPipeline pipeline,
        DetectionConfig detectionConfig,
        WatchConfig watchConfig,
        ILogger<WatchService>? logger = null)
    {
        _pipeline = pipeline;
        _detectionConfig = detectionConfig;
        _watchConfig = watchConfig;
        _logger = logger;

        _fileQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
    }

    /// <summary>
    /// Start watching configured directories.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _logger?.LogInformation("CommDetect Watch Service starting");
        _logger?.LogInformation("Monitoring {Count} directory(ies) for new recordings",
            _watchConfig.WatchDirectories.Count);

        // Validate directories
        foreach (var dir in _watchConfig.WatchDirectories)
        {
            if (!Directory.Exists(dir))
            {
                _logger?.LogWarning("Watch directory does not exist: {Dir} — will retry when available", dir);
            }
        }

        // Start filesystem watchers
        foreach (var dir in _watchConfig.WatchDirectories)
        {
            if (Directory.Exists(dir))
            {
                StartWatcher(dir);
            }
        }

        // Start the stability checker (waits for files to stop changing)
        _stabilityCheckTask = Task.Run(() => StabilityCheckLoop(_cts.Token), _cts.Token);

        // Start processing workers
        _processingTask = Task.Run(() => ProcessingLoop(_cts.Token), _cts.Token);

        // Optionally scan existing files
        if (_watchConfig.ProcessExistingOnStartup)
        {
            await ScanExistingFilesAsync();
        }

        _logger?.LogInformation("Watch service running. Press Ctrl+C to stop.");
    }

    /// <summary>
    /// Stop watching and wait for in-progress jobs to complete.
    /// </summary>
    public async Task StopAsync()
    {
        _logger?.LogInformation("Watch service stopping...");

        _cts?.Cancel();

        // Dispose filesystem watchers
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        // Wait for in-progress work to complete
        _fileQueue.Writer.TryComplete();

        if (_processingTask != null)
        {
            try { await _processingTask; } catch (OperationCanceledException) { }
        }
        if (_stabilityCheckTask != null)
        {
            try { await _stabilityCheckTask; } catch (OperationCanceledException) { }
        }

        _logger?.LogInformation(
            "Watch service stopped. Processed: {Processed}, Failed: {Failed}",
            _totalProcessed, _totalFailed);
    }

    /// <summary>
    /// Run the watch service as a blocking call (for CLI usage).
    /// Runs until cancellation is requested.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken);

        try
        {
            // Block until cancelled
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await StopAsync();
        }
    }

    // ── Filesystem Watching ─────────────────────────────────────────────

    private void StartWatcher(string directory)
    {
        foreach (var ext in _watchConfig.FileExtensions)
        {
            var watcher = new FileSystemWatcher
            {
                Path = directory,
                Filter = $"*{ext}",
                IncludeSubdirectories = _watchConfig.Recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            watcher.Created += OnFileCreated;
            watcher.Changed += OnFileChanged;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnWatcherError;

            _watchers.Add(watcher);
        }

        _logger?.LogInformation("Watching: {Dir} (recursive: {Recursive})",
            directory, _watchConfig.Recursive);
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        _logger?.LogDebug("File created: {Path}", e.FullPath);
        TrackFile(e.FullPath);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Update the "last seen" time — the file is still being written
        TrackFile(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        _logger?.LogDebug("File renamed: {Old} → {New}", e.OldFullPath, e.FullPath);

        // Remove old name, track new name
        _pendingFiles.TryRemove(e.OldFullPath, out _);
        TrackFile(e.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger?.LogError(e.GetException(), "Filesystem watcher error");

        // Attempt to restart the watcher
        if (sender is FileSystemWatcher watcher)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.EnableRaisingEvents = true;
                _logger?.LogInformation("Watcher restarted for: {Dir}", watcher.Path);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to restart watcher for: {Dir}", watcher.Path);
            }
        }
    }

    private void TrackFile(string filePath)
    {
        if (ShouldSkipFile(filePath)) return;
        _pendingFiles[filePath] = DateTime.UtcNow;
    }

    // ── Stability Checking ──────────────────────────────────────────────

    /// <summary>
    /// Periodically checks pending files. Once a file hasn't changed for
    /// the configured stability delay, it's queued for processing.
    /// This prevents analyzing a file while Emby is still recording.
    /// </summary>
    private async Task StabilityCheckLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);

                var stableThreshold = DateTime.UtcNow.AddSeconds(-_watchConfig.StabilityDelaySeconds);

                foreach (var (filePath, lastSeen) in _pendingFiles.ToArray())
                {
                    if (lastSeen > stableThreshold) continue; // Still changing
                    if (!_pendingFiles.TryRemove(filePath, out _)) continue; // Already removed

                    // Verify the file still exists and meets size requirements
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        if (!fileInfo.Exists) continue;

                        double sizeMB = fileInfo.Length / (1024.0 * 1024.0);
                        if (sizeMB < _watchConfig.MinFileSizeMB)
                        {
                            _logger?.LogDebug("Skipping small file ({Size:F1}MB): {Path}", sizeMB, filePath);
                            continue;
                        }
                    }
                    catch (IOException)
                    {
                        // File may be locked — re-queue and try again later
                        _pendingFiles[filePath] = DateTime.UtcNow;
                        continue;
                    }

                    _logger?.LogInformation("File stable, queuing for analysis: {Path}", filePath);
                    await _fileQueue.Writer.WriteAsync(filePath, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in stability check loop");
            }
        }
    }

    // ── Processing Loop ─────────────────────────────────────────────────

    private async Task ProcessingLoop(CancellationToken ct)
    {
        // Use a SemaphoreSlim for concurrent job limiting
        using var semaphore = new SemaphoreSlim(_watchConfig.MaxConcurrentJobs);
        var activeTasks = new List<Task>();

        await foreach (var filePath in _fileQueue.Reader.ReadAllAsync(ct))
        {
            if (ct.IsCancellationRequested) break;

            await semaphore.WaitAsync(ct);

            var task = Task.Run(async () =>
            {
                try
                {
                    await ProcessFileAsync(filePath, ct);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct);

            activeTasks.Add(task);

            // Clean up completed tasks periodically
            activeTasks.RemoveAll(t => t.IsCompleted);
        }

        // Wait for remaining tasks
        await Task.WhenAll(activeTasks);
    }

    private async Task ProcessFileAsync(string filePath, CancellationToken ct)
    {
        // Prevent duplicate processing
        if (!_processingFiles.TryAdd(filePath, 0))
        {
            _logger?.LogDebug("File already being processed: {Path}", filePath);
            return;
        }

        try
        {
            _logger?.LogInformation("Processing: {Path}", filePath);
            var startTime = DateTime.UtcNow;

            var progress = new WatchModeProgress(filePath, _logger);
            var result = await _pipeline.ProcessAsync(filePath, _detectionConfig, progress, ct);

            var commercials = result.Segments.Where(s => s.Type == SegmentType.Commercial).ToList();
            double commercialTime = commercials.Sum(s => s.DurationSeconds);

            Interlocked.Increment(ref _totalProcessed);
            _completedFiles[filePath] = 0;

            _logger?.LogInformation(
                "Completed: {Path} — {Count} commercial(s), {Time:F0}s total, analyzed in {Elapsed:F1}s",
                Path.GetFileName(filePath),
                commercials.Count,
                commercialTime,
                result.AnalysisDuration.TotalSeconds);

            if (_watchConfig.NotifyOnComplete)
            {
                Console.WriteLine(
                    $"[COMPLETE] {Path.GetFileName(filePath)} | " +
                    $"{commercials.Count} commercial(s) | " +
                    $"{commercialTime:F0}s commercial time | " +
                    $"{result.AnalysisDuration.TotalSeconds:F1}s analysis time");
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Processing cancelled: {Path}", filePath);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _totalFailed);
            _logger?.LogError(ex, "Failed to process: {Path}", filePath);

            if (_watchConfig.NotifyOnComplete)
            {
                Console.Error.WriteLine($"[FAILED] {Path.GetFileName(filePath)} | {ex.Message}");
            }
        }
        finally
        {
            _processingFiles.TryRemove(filePath, out _);
        }
    }

    // ── Existing File Scan ──────────────────────────────────────────────

    private async Task ScanExistingFilesAsync()
    {
        _logger?.LogInformation("Scanning existing files...");
        int queued = 0;

        foreach (var dir in _watchConfig.WatchDirectories)
        {
            if (!Directory.Exists(dir)) continue;

            var searchOption = _watchConfig.Recursive
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            foreach (var ext in _watchConfig.FileExtensions)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, $"*{ext}", searchOption))
                    {
                        if (ShouldSkipFile(file)) continue;

                        var fileInfo = new FileInfo(file);
                        if (fileInfo.Length / (1024.0 * 1024.0) < _watchConfig.MinFileSizeMB)
                            continue;

                        await _fileQueue.Writer.WriteAsync(file);
                        queued++;
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger?.LogWarning("Access denied scanning {Dir}: {Error}", dir, ex.Message);
                }
            }
        }

        _logger?.LogInformation("Queued {Count} existing file(s) for processing", queued);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private bool ShouldSkipFile(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        string extension = Path.GetExtension(filePath);

        // Check extension
        if (!_watchConfig.FileExtensions.Any(ext =>
            extension.Equals(ext, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Check exclude patterns
        foreach (var pattern in _watchConfig.ExcludePatterns)
        {
            if (MatchesGlob(fileName, pattern))
            {
                _logger?.LogDebug("Excluded by pattern '{Pattern}': {Path}", pattern, filePath);
                return true;
            }
        }

        // Check if already processed
        if (_watchConfig.SkipAlreadyProcessed && HasExistingOutput(filePath))
        {
            _logger?.LogDebug("Already processed (output exists): {Path}", filePath);
            return true;
        }

        // Check if already completed or in progress
        if (_completedFiles.ContainsKey(filePath) || _processingFiles.ContainsKey(filePath))
            return true;

        return false;
    }

    private bool HasExistingOutput(string filePath)
    {
        string basePath = Path.ChangeExtension(filePath, null);

        // Check for any existing output files
        foreach (var format in _watchConfig.OutputFormats)
        {
            string ext = format switch
            {
                OutputFormat.Edl => ".edl",
                OutputFormat.ComskipTxt => ".txt",
                OutputFormat.MkvChapters => ".chapters.xml",
                OutputFormat.Json => ".commdetect.json",
                OutputFormat.FFMetadata => ".ffmeta",
                _ => ".edl"
            };

            if (File.Exists(basePath + ext)) return true;
        }

        // Also check for Comskip-style output
        if (File.Exists(Path.ChangeExtension(filePath, ".edl"))) return true;
        if (File.Exists(Path.ChangeExtension(filePath, ".txt")))
        {
            // Verify it's a Comskip TXT file, not just any .txt
            try
            {
                string firstLine = File.ReadLines(Path.ChangeExtension(filePath, ".txt")).FirstOrDefault() ?? "";
                if (firstLine.Contains("FILE PROCESSING COMPLETE")) return true;
            }
            catch { /* ignore read errors */ }
        }

        return false;
    }

    private static bool MatchesGlob(string fileName, string pattern)
    {
        // Simple glob matching — supports * wildcard
        if (pattern == "*") return true;

        string regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(
            fileName, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        _cts?.Dispose();
    }
}

/// <summary>
/// Progress reporter for watch-mode — logs rather than printing to console.
/// </summary>
internal class WatchModeProgress : IAnalysisProgress
{
    private readonly string _fileName;
    private readonly ILogger? _logger;

    public WatchModeProgress(string filePath, ILogger? logger)
    {
        _fileName = Path.GetFileName(filePath);
        _logger = logger;
    }

    public void ReportPhase(string phaseName)
    {
        _logger?.LogDebug("[{File}] {Phase}", _fileName, phaseName);
    }

    public void ReportProgress(double percentComplete, string? message = null)
    {
        if (percentComplete % 25 < 1) // Log at 0%, 25%, 50%, 75%, 100%
        {
            _logger?.LogDebug("[{File}] {Pct:F0}% {Msg}", _fileName, percentComplete, message ?? "");
        }
    }

    public void ReportComplete(AnalysisResult result)
    {
        _logger?.LogDebug("[{File}] Analysis complete", _fileName);
    }

    public void ReportError(Exception exception)
    {
        _logger?.LogError(exception, "[{File}] Analysis error", _fileName);
    }
}
