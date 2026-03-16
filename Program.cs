using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommDetect.Core;
using CommDetect.Analysis;
using CommDetect.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CommDetect.CLI;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("CommDetect — Cross-platform commercial detection tool")
        {
            CreateProcessCommand(),
            CreateWatchCommand(),
            CreateEmbyCommand(),
            CreateProbeCommand(),
            CreateConfigCommand()
        };

        return await rootCommand.InvokeAsync(args);
    }

    // ── process ─────────────────────────────────────────────────────────

    private static Command CreateProcessCommand()
    {
        var inputArg = new Argument<FileInfo>("input", "Path to the media file to analyze");
        var configOpt = new Option<FileInfo?>("--config",
            "Path to commdetect.ini (or JSON) detection config");
        var processConfigOpt = new Option<FileInfo?>("--process-config",
            "Path to comprocess.ini — controls cut/chapter mode after detection");
        var outputOpt = new Option<string?>("--output-dir", "Output directory (default: same as input)");
        var presetOpt = new Option<string>("--preset", () => "default",
            "Detection preset: fast, default, accurate");
        var formatOpt = new Option<OutputFormat[]>("--format",
            () => new[] { OutputFormat.Edl },
            "Output format(s): edl, comskiptxt, mkvchapters, json, ffmetadata");
        var ffmpegOpt = new Option<string?>("--ffmpeg-path", "Path to FFmpeg/FFprobe directory");
        var verboseOpt = new Option<bool>("--verbose", "Enable verbose logging");
        var forceOpt   = new Option<bool>("--force", "Delete existing output files before analyzing");

        var cmd = new Command("process", "Analyze a media file for commercials")
        {
            inputArg, configOpt, processConfigOpt, outputOpt, presetOpt, formatOpt, ffmpegOpt, verboseOpt, forceOpt
        };

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var input           = ctx.ParseResult.GetValueForArgument(inputArg);
            var configFile      = ctx.ParseResult.GetValueForOption(configOpt);
            var processConfigFile = ctx.ParseResult.GetValueForOption(processConfigOpt);
            var preset          = ctx.ParseResult.GetValueForOption(presetOpt);
            var formats         = ctx.ParseResult.GetValueForOption(formatOpt);
            var ffmpegPath      = ctx.ParseResult.GetValueForOption(ffmpegOpt);
            var verbose         = ctx.ParseResult.GetValueForOption(verboseOpt);
            var force           = ctx.ParseResult.GetValueForOption(forceOpt);

            if (!input!.Exists)
            {
                Console.Error.WriteLine($"Error: File not found: {input.FullName}");
                ctx.ExitCode = 1;
                return;
            }

            var config = LoadDetectionConfig(preset!, configFile);
            if (formats != null && formats.Length > 0)
                config.OutputFormats = new List<OutputFormat>(formats);

            if (force)
            {
                string[] outputExts = { ".edl", ".txt", ".xml", ".json", ".ffmetadata" };
                foreach (var ext in outputExts)
                {
                    string candidate = Path.ChangeExtension(input.FullName, ext);
                    if (File.Exists(candidate))
                    {
                        File.Delete(candidate);
                        Console.WriteLine($"Deleted: {Path.GetFileName(candidate)}");
                    }
                }
            }

            var processConfig = LoadProcessingConfig(processConfigFile);
            if (force)
                processConfig.OverwriteExisting = true;

            var services = BuildServices(ffmpegPath, verbose);
            var pipeline = services.GetRequiredService<DetectionPipeline>();
            var progress = new ConsoleProgress();

            // Report which FFmpeg we're using
            var locator = services.GetRequiredService<IFFmpegLocator>() as CrossPlatformFFmpegLocator;
            if (locator != null)
            {
                var (path, source) = locator.FindFFmpegWithSource();
                Console.WriteLine($"FFmpeg: {path ?? "NOT FOUND"} ({source})");
            }

            try
            {
                Console.WriteLine($"CommDetect v1.0.0 — Analyzing: {input.Name}");
                Console.WriteLine($"Preset: {preset} | Formats: {string.Join(", ", config.OutputFormats)}");

                // EPG lookup — override skip_start/skip_end from Emby recording padding
                if (!string.IsNullOrEmpty(config.EmbyServerUrl) && !string.IsNullOrEmpty(config.EmbyApiKey))
                {
                    var embyLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Emby");
                    using var embyClient = new EmbyApiClient(
                        config.EmbyServerUrl, config.EmbyApiKey, embyLogger);
                    var recording = await embyClient.GetRecordingByFilenameAsync(
                        input.Name, input.FullName, ctx.GetCancellationToken());
                    if (recording != null)
                    {
                        if (recording.PrePaddingSeconds > 0)
                            config.SkipStartSeconds = Math.Max(config.SkipStartSeconds, recording.PrePaddingSeconds);
                        if (recording.PostPaddingSeconds > 0)
                            config.SkipEndSeconds = Math.Max(config.SkipEndSeconds, recording.PostPaddingSeconds);
                        Console.WriteLine(
                            $"EPG: {recording.StartDate:HH:mm}–{recording.EndDate:HH:mm} UTC | " +
                            $"pre={recording.PrePaddingSeconds}s post={recording.PostPaddingSeconds}s");
                    }
                }

                Console.WriteLine($"Config: {FormatConfigSummary(config)}");
                if (processConfig.Mode != ProcessingMode.Skip)
                    Console.WriteLine($"Post-processing: {processConfig.Mode} → {processConfig.ContainerFormat}");
                Console.WriteLine();

                var result = await pipeline.ProcessAsync(
                    input.FullName, config, progress, ctx.GetCancellationToken());

                var commercials = result.Segments.FindAll(s => s.Type == SegmentType.Commercial);
                Console.WriteLine();
                Console.WriteLine($"Results: {commercials.Count} commercial break(s) detected");
                Console.WriteLine($"Total commercial time: {commercials.Sum(s => s.DurationSeconds):F1}s");
                Console.WriteLine($"Analysis time: {result.AnalysisDuration.TotalSeconds:F1}s");

                // Post-processing: cut or chapter-mark the file
                if (processConfig.Mode != ProcessingMode.Skip && commercials.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"► Post-processing ({processConfig.Mode} mode)...");

                    var processor = new CommercialProcessor(
                        services.GetRequiredService<IFFmpegLocator>(),
                        services.GetRequiredService<ILoggerFactory>().CreateLogger<CommercialProcessor>());

                    var processingResult = await processor.ProcessAsync(
                        result, processConfig, ctx.GetCancellationToken());

                    if (processingResult.Success)
                    {
                        foreach (var outFile in processingResult.OutputFiles)
                            Console.WriteLine($"Output: {outFile}");
                        Console.WriteLine($"Processing time: {processingResult.Duration.TotalSeconds:F1}s");
                    }
                    else
                    {
                        Console.Error.WriteLine($"Post-processing failed: {processingResult.ErrorMessage}");
                        ctx.ExitCode = 1;
                        return;
                    }
                }
                else if (processConfig.Mode != ProcessingMode.Skip && commercials.Count == 0)
                {
                    Console.WriteLine("No commercials detected — skipping post-processing.");
                }

                ctx.ExitCode = 0;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Analysis cancelled.");
                ctx.ExitCode = 130;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                if (verbose) Console.Error.WriteLine(ex.StackTrace);
                ctx.ExitCode = 1;
            }
        });

        return cmd;
    }

    // ── watch ───────────────────────────────────────────────────────────

    private static Command CreateWatchCommand()
    {
        var dirsArg = new Argument<string[]>("directories",
            "Directories to watch for new recordings")
        { Arity = ArgumentArity.OneOrMore };

        var configOpt = new Option<FileInfo?>("--config", "Path to detection config JSON");
        var watchConfigOpt = new Option<FileInfo?>("--watch-config", "Path to watch-mode config JSON");
        var presetOpt = new Option<string>("--preset", () => "default", "Detection preset");
        var formatOpt = new Option<OutputFormat[]>("--format",
            () => new[] { OutputFormat.Edl }, "Output format(s)");
        var ffmpegOpt = new Option<string?>("--ffmpeg-path", "Path to FFmpeg directory");
        var existingOpt = new Option<bool>("--process-existing",
            "Also process existing files on startup");
        var concurrencyOpt = new Option<int>("--concurrency", () => 1,
            "Maximum concurrent processing jobs");
        var stabilityOpt = new Option<int>("--stability-delay", () => 30,
            "Seconds to wait after file stops changing before processing");
        var verboseOpt = new Option<bool>("--verbose", "Enable verbose logging");

        var cmd = new Command("watch",
            "Monitor directories for new recordings and auto-process them")
        {
            dirsArg, configOpt, watchConfigOpt, presetOpt, formatOpt, ffmpegOpt,
            existingOpt, concurrencyOpt, stabilityOpt, verboseOpt
        };

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var dirs = ctx.ParseResult.GetValueForArgument(dirsArg);
            var configFile = ctx.ParseResult.GetValueForOption(configOpt);
            var watchConfigFile = ctx.ParseResult.GetValueForOption(watchConfigOpt);
            var preset = ctx.ParseResult.GetValueForOption(presetOpt);
            var formats = ctx.ParseResult.GetValueForOption(formatOpt);
            var ffmpegPath = ctx.ParseResult.GetValueForOption(ffmpegOpt);
            var processExisting = ctx.ParseResult.GetValueForOption(existingOpt);
            var concurrency = ctx.ParseResult.GetValueForOption(concurrencyOpt);
            var stabilityDelay = ctx.ParseResult.GetValueForOption(stabilityOpt);
            var verbose = ctx.ParseResult.GetValueForOption(verboseOpt);

            // Build watch config
            WatchConfig watchConfig;
            if (watchConfigFile?.Exists == true)
            {
                string json = await File.ReadAllTextAsync(watchConfigFile.FullName);
                watchConfig = JsonSerializer.Deserialize<WatchConfig>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new WatchConfig();
            }
            else
            {
                watchConfig = new WatchConfig();
            }

            // CLI overrides
            watchConfig.WatchDirectories = new List<string>(dirs!);
            if (formats != null && formats.Length > 0)
                watchConfig.OutputFormats = new List<OutputFormat>(formats);
            watchConfig.ProcessExistingOnStartup = processExisting;
            watchConfig.MaxConcurrentJobs = Math.Max(1, concurrency);
            watchConfig.StabilityDelaySeconds = Math.Max(5, stabilityDelay);

            // Validate directories
            foreach (var dir in watchConfig.WatchDirectories)
            {
                if (!Directory.Exists(dir))
                    Console.Error.WriteLine($"Warning: Directory not found: {dir}");
            }

            var detectionConfig = LoadDetectionConfig(preset!, configFile);
            var services = BuildServices(ffmpegPath, verbose);
            var pipeline = services.GetRequiredService<DetectionPipeline>();
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<WatchService>();

            // Report FFmpeg source
            var locator = services.GetRequiredService<IFFmpegLocator>() as CrossPlatformFFmpegLocator;
            if (locator != null)
            {
                var (path, source) = locator.FindFFmpegWithSource();
                Console.WriteLine($"FFmpeg: {path ?? "NOT FOUND"} ({source})");
            }

            Console.WriteLine($"CommDetect Watch Mode v1.0.0");
            Console.WriteLine($"Watching: {string.Join(", ", watchConfig.WatchDirectories)}");
            Console.WriteLine($"Formats: {string.Join(", ", watchConfig.OutputFormats)}");
            Console.WriteLine($"Concurrency: {watchConfig.MaxConcurrentJobs} | " +
                              $"Stability delay: {watchConfig.StabilityDelaySeconds}s");
            Console.WriteLine($"Config: {FormatConfigSummary(detectionConfig)}");
            Console.WriteLine();

            using var watchService = new WatchService(pipeline, detectionConfig, watchConfig, logger);

            // Handle Ctrl+C gracefully
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.GetCancellationToken());
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\nShutting down gracefully (waiting for in-progress jobs)...");
                cts.Cancel();
            };

            try
            {
                await watchService.RunAsync(cts.Token);
            }
            catch (OperationCanceledException) { }

            ctx.ExitCode = 0;
        });

        return cmd;
    }

    // ── emby ────────────────────────────────────────────────────────────

    private static Command CreateEmbyCommand()
    {
        var embyCmd = new Command("emby", "Emby Server integration tools");

        // ── emby detect ─────────────────────────────────────
        var detectCmd = new Command("detect",
            "Auto-detect Emby Server installation and configuration");

        detectCmd.SetHandler(() =>
        {
            var emby = new EmbyIntegration();
            var locator = new CrossPlatformFFmpegLocator(preferEmbyFFmpeg: true);

            Console.WriteLine("CommDetect — Emby Server Detection");
            Console.WriteLine("══════════════════════════════════════════");

            // Find Emby data directory
            string? dataDir = emby.FindEmbyDataDirectory();
            Console.WriteLine($"Emby data directory: {dataDir ?? "Not found"}");

            // Find recording directories
            var recordingDirs = emby.FindRecordingDirectories(dataDir);
            Console.WriteLine($"\nRecording directories ({recordingDirs.Count}):");
            if (recordingDirs.Count == 0)
            {
                Console.WriteLine("  (none found — configure manually or check Emby settings)");
            }
            else
            {
                foreach (var dir in recordingDirs)
                {
                    bool exists = Directory.Exists(dir);
                    int fileCount = 0;
                    if (exists)
                    {
                        try
                        {
                            fileCount = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                                .Count(f => IsMediaFile(f));
                        }
                        catch { /* access denied, etc. */ }
                    }
                    Console.WriteLine(exists
                        ? $"  {dir} ({fileCount} media files)"
                        : $"  {dir} (NOT ACCESSIBLE)");
                }
            }

            // Find FFmpeg
            var (ffmpegPath, ffmpegSource) = locator.FindFFmpegWithSource();
            Console.WriteLine($"\nFFmpeg: {ffmpegPath ?? "Not found"} ({ffmpegSource})");

            // Provide guidance
            Console.WriteLine();
            Console.WriteLine("── Next Steps ──────────────────────────────");
            if (recordingDirs.Count > 0)
            {
                Console.WriteLine("Option 1 — Watch mode (recommended, runs as a service):");
                Console.WriteLine("  commdetect emby watch");
                Console.WriteLine();
                Console.WriteLine("Option 2 — Post-processing script (Emby calls after each recording):");
                Console.WriteLine("  commdetect emby generate-script --output postprocess.sh");
                Console.WriteLine("  Then configure in Emby: Dashboard > DVR > Recording Post Processing");
            }
            else
            {
                Console.WriteLine("Emby recording directories were not auto-detected.");
                Console.WriteLine("Specify directories manually:");
                Console.WriteLine("  commdetect watch /path/to/your/recordings");
            }
        });

        // ── emby watch ──────────────────────────────────────
        var embyWatchCmd = new Command("watch",
            "Auto-detect Emby recording directories and start watching");

        var presetOpt = new Option<string>("--preset", () => "default", "Detection preset");
        var formatOpt = new Option<OutputFormat[]>("--format",
            () => new[] { OutputFormat.Edl }, "Output format(s)");
        var configOpt = new Option<FileInfo?>("--config", "Detection config JSON");
        var existingOpt = new Option<bool>("--process-existing",
            "Process existing recordings on startup");
        var verboseOpt = new Option<bool>("--verbose", "Verbose logging");

        embyWatchCmd.AddOption(presetOpt);
        embyWatchCmd.AddOption(formatOpt);
        embyWatchCmd.AddOption(configOpt);
        embyWatchCmd.AddOption(existingOpt);
        embyWatchCmd.AddOption(verboseOpt);

        embyWatchCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var preset = ctx.ParseResult.GetValueForOption(presetOpt);
            var formats = ctx.ParseResult.GetValueForOption(formatOpt);
            var configFile = ctx.ParseResult.GetValueForOption(configOpt);
            var processExisting = ctx.ParseResult.GetValueForOption(existingOpt);
            var verbose = ctx.ParseResult.GetValueForOption(verboseOpt);

            var emby = new EmbyIntegration();
            var watchConfig = emby.CreateEmbyWatchConfig();

            if (watchConfig.WatchDirectories.Count == 0)
            {
                Console.Error.WriteLine("Error: Could not auto-detect Emby recording directories.");
                Console.Error.WriteLine("Run 'commdetect emby detect' to diagnose, or specify directories manually:");
                Console.Error.WriteLine("  commdetect watch /path/to/recordings");
                ctx.ExitCode = 1;
                return;
            }

            if (formats != null && formats.Length > 0)
                watchConfig.OutputFormats = new List<OutputFormat>(formats);
            watchConfig.ProcessExistingOnStartup = processExisting;

            var detectionConfig = LoadDetectionConfig(preset!, configFile);

            // Prefer Emby's FFmpeg
            var services = BuildServices(null, verbose, preferEmbyFFmpeg: true);
            var pipeline = services.GetRequiredService<DetectionPipeline>();
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<WatchService>();

            var locator = services.GetRequiredService<IFFmpegLocator>() as CrossPlatformFFmpegLocator;
            if (locator != null)
            {
                var (path, source) = locator.FindFFmpegWithSource();
                Console.WriteLine($"FFmpeg: {path ?? "NOT FOUND"} ({source})");
            }

            Console.WriteLine("CommDetect — Emby Watch Mode v1.0.0");
            Console.WriteLine("Watching Emby recording directories:");
            foreach (var dir in watchConfig.WatchDirectories)
                Console.WriteLine($"  {dir}");
            Console.WriteLine($"Stability delay: {watchConfig.StabilityDelaySeconds}s | " +
                              $"Formats: {string.Join(", ", watchConfig.OutputFormats)}");
            Console.WriteLine($"Config: {FormatConfigSummary(detectionConfig)}");
            Console.WriteLine();

            using var watchService = new WatchService(
                pipeline, detectionConfig, watchConfig, logger);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.GetCancellationToken());
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\nShutting down gracefully...");
                cts.Cancel();
            };

            try
            {
                await watchService.RunAsync(cts.Token);
            }
            catch (OperationCanceledException) { }

            ctx.ExitCode = 0;
        });

        // ── emby generate-script ────────────────────────────
        var scriptCmd = new Command("generate-script",
            "Generate an Emby post-processing script for the current platform");

        var outputOpt = new Option<FileInfo?>("--output", "Output path for the script");
        var commdetectPathOpt = new Option<string?>("--commdetect-path",
            "Path to the commdetect executable (default: auto-detect)");
        scriptCmd.AddOption(outputOpt);
        scriptCmd.AddOption(commdetectPathOpt);

        scriptCmd.SetHandler(async (FileInfo? output, string? commdetectPath) =>
        {
            // Auto-detect our own path if not specified
            commdetectPath ??= System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                               ?? "commdetect";

            var emby = new EmbyIntegration();
            string script = emby.GenerateEmbyPostProcessScript(commdetectPath);

            if (output != null)
            {
                await File.WriteAllTextAsync(output.FullName, script);

                // Make executable on Unix
                if (!System.Runtime.InteropServices.RuntimeInformation
                        .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    try
                    {
                        System.Diagnostics.Process.Start("chmod", $"+x \"{output.FullName}\"")
                            ?.WaitForExit();
                    }
                    catch { /* best effort */ }
                }

                Console.WriteLine($"Post-processing script written to: {output.FullName}");
                Console.WriteLine();
                Console.WriteLine("To configure in Emby:");
                Console.WriteLine("  1. Open Emby Dashboard → Live TV → DVR");
                Console.WriteLine($"  2. Set Recording Post Processor to: {output.FullName}");
                Console.WriteLine("  3. Emby will call this script after each recording completes");
            }
            else
            {
                Console.WriteLine(script);
            }
        }, outputOpt, commdetectPathOpt);

        embyCmd.AddCommand(detectCmd);
        embyCmd.AddCommand(embyWatchCmd);
        embyCmd.AddCommand(scriptCmd);

        return embyCmd;
    }

    // ── probe ───────────────────────────────────────────────────────────

    private static Command CreateProbeCommand()
    {
        var inputArg = new Argument<FileInfo>("input", "Path to the media file to probe");
        var ffmpegOpt = new Option<string?>("--ffmpeg-path", "Path to FFmpeg/FFprobe directory");

        var cmd = new Command("probe", "Show media file information") { inputArg, ffmpegOpt };

        cmd.SetHandler(async (FileInfo input, string? ffmpegPath) =>
        {
            var locator = new CrossPlatformFFmpegLocator(ffmpegPath, preferEmbyFFmpeg: true);
            var probe = new FFprobeMediaProbe(locator);

            var info = await probe.ProbeAsync(input.FullName);

            Console.WriteLine($"File:        {info.FilePath}");
            Console.WriteLine($"Duration:    {info.Duration}");
            Console.WriteLine($"Resolution:  {info.Width}x{info.Height}");
            Console.WriteLine($"Frame Rate:  {info.FrameRate:F3} fps");
            Console.WriteLine($"Video Codec: {info.VideoCodec}");
            Console.WriteLine($"Audio Codec: {info.AudioCodec}");
            Console.WriteLine($"Audio:       {info.AudioSampleRate}Hz, {info.AudioChannels}ch");

            var (ffmpegUsed, source) = locator.FindFFmpegWithSource();
            Console.WriteLine($"\nFFmpeg:      {ffmpegUsed} ({source})");
        }, inputArg, ffmpegOpt);

        return cmd;
    }

    // ── config ──────────────────────────────────────────────────────────

    private static Command CreateConfigCommand()
    {
        var configCmd = new Command("config", "Generate configuration files");

        // config detection
        var detectionCmd = new Command("detection", "Generate a detection configuration file");
        var outputOpt = new Option<FileInfo?>("--output", "Output path (default: stdout)");
        var presetOpt = new Option<string>("--preset", () => "default", "Preset to export");
        detectionCmd.AddOption(outputOpt);
        detectionCmd.AddOption(presetOpt);

        detectionCmd.SetHandler(async (FileInfo? output, string preset) =>
        {
            var config = preset switch
            {
                "fast" => DetectionConfig.Fast(),
                "accurate" => DetectionConfig.Accurate(),
                _ => new DetectionConfig()
            };

            string json = SerializeConfig(config);
            await OutputOrPrint(output, json, "Detection config");
        }, outputOpt, presetOpt);

        // config watch
        var watchCmd = new Command("watch", "Generate a watch-mode configuration file");
        var watchOutputOpt = new Option<FileInfo?>("--output", "Output path (default: stdout)");
        var embyOpt = new Option<bool>("--emby", "Pre-configure for Emby Server");
        watchCmd.AddOption(watchOutputOpt);
        watchCmd.AddOption(embyOpt);

        watchCmd.SetHandler(async (FileInfo? output, bool emby) =>
        {
            WatchConfig watchConfig;
            if (emby)
            {
                var embyIntegration = new EmbyIntegration();
                watchConfig = embyIntegration.CreateEmbyWatchConfig();
                if (watchConfig.WatchDirectories.Count == 0)
                    watchConfig.WatchDirectories.Add("/path/to/emby/recordings");
            }
            else
            {
                watchConfig = new WatchConfig
                {
                    WatchDirectories = new List<string> { "/path/to/recordings" }
                };
            }

            string json = SerializeConfig(watchConfig);
            await OutputOrPrint(output, json, "Watch config");
        }, watchOutputOpt, embyOpt);

        configCmd.AddCommand(detectionCmd);
        configCmd.AddCommand(watchCmd);

        return configCmd;
    }

    // ── Shared Helpers ──────────────────────────────────────────────────

    private static DetectionConfig LoadDetectionConfig(string preset, FileInfo? configFile)
    {
        DetectionConfig config = preset switch
        {
            "fast" => DetectionConfig.Fast(),
            "accurate" => DetectionConfig.Accurate(),
            _ => new DetectionConfig()
        };

        // Explicit --config path supplied
        if (configFile?.Exists == true)
        {
            if (configFile.Extension.Equals(".ini", StringComparison.OrdinalIgnoreCase))
            {
                var ini = IniFile.Load(configFile.FullName);
                config = DetectionIniMapper.LoadDetectionConfig(ini, config);
                Console.WriteLine($"Detection config: {configFile.FullName}");
            }
            else
            {
                string json = File.ReadAllText(configFile.FullName);
                config = JsonSerializer.Deserialize<DetectionConfig>(json,
                             new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                         ?? config;
                Console.WriteLine($"Detection config: {configFile.FullName}");
            }
            return config;
        }

        // Auto-discover commdetect.ini
        string? autoIni = GetConfigSearchPaths("commdetect.ini").FirstOrDefault(File.Exists);
        if (autoIni != null)
        {
            var ini = IniFile.Load(autoIni);
            config = DetectionIniMapper.LoadDetectionConfig(ini, config);
            Console.WriteLine($"Detection config: {autoIni} (auto-discovered)");
        }

        return config;
    }

    private static ProcessingConfig LoadProcessingConfig(FileInfo? configFile)
    {
        // Explicit --process-config path supplied
        if (configFile?.Exists == true &&
            configFile.Extension.Equals(".ini", StringComparison.OrdinalIgnoreCase))
        {
            var ini = IniFile.Load(configFile.FullName);
            var cfg = ProcessingIniMapper.LoadProcessingConfig(ini);
            Console.WriteLine($"Processing config: {configFile.FullName}");
            return cfg;
        }

        // Auto-discover comprocess.ini
        string? autoIni = GetConfigSearchPaths("comprocess.ini").FirstOrDefault(File.Exists);
        if (autoIni != null)
        {
            var ini = IniFile.Load(autoIni);
            var cfg = ProcessingIniMapper.LoadProcessingConfig(ini);
            Console.WriteLine($"Processing config: {autoIni} (auto-discovered)");
            return cfg;
        }

        return new ProcessingConfig(); // defaults to Skip mode
    }

    /// <summary>Candidate paths to search for a named config file, in priority order.</summary>
    private static IEnumerable<string> GetConfigSearchPaths(string filename)
    {
        // Current working directory
        yield return Path.Combine(Environment.CurrentDirectory, filename);
        yield return Path.Combine(Environment.CurrentDirectory, "config", filename);

        // Next to the actual executable (works for self-contained single-file builds)
        string? exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            string exeDir = Path.GetDirectoryName(exePath) ?? "";
            if (!string.IsNullOrEmpty(exeDir))
            {
                yield return Path.Combine(exeDir, filename);
                yield return Path.Combine(exeDir, "config", filename);
            }
        }

        // System-wide config directory
        yield return Path.Combine("/etc/commdetect", filename);

        // AppContext base (fallback — may be temp dir for single-file builds)
        yield return Path.Combine(AppContext.BaseDirectory, filename);
        yield return Path.Combine(AppContext.BaseDirectory, "config", filename);
    }

    private static ServiceProvider BuildServices(
        string? ffmpegPath, bool verbose, bool preferEmbyFFmpeg = false)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
        });

        // FFmpeg — Emby-aware locator
        services.AddSingleton<IFFmpegLocator>(sp =>
            new CrossPlatformFFmpegLocator(
                ffmpegPath,
                preferEmbyFFmpeg,
                sp.GetService<ILogger<CrossPlatformFFmpegLocator>>()));

        services.AddSingleton<IMediaProbe, FFprobeMediaProbe>();
        services.AddSingleton<IFrameExtractor, FFmpegFrameExtractor>();
        services.AddSingleton<IAudioExtractor, FFmpegAudioExtractor>();

        services.AddSingleton<ISignalDetector, BlackFrameDetector>();
        services.AddSingleton<ISignalDetector, SceneChangeDetector>();
        services.AddSingleton<ISignalDetector, SilenceDetector>();
        services.AddSingleton<ISignalDetector, LogoDetector>();

        services.AddSingleton<ICommercialClassifier, CommercialClassifier>();

        services.AddSingleton<IResultWriter, EdlWriter>();
        services.AddSingleton<IResultWriter, ComskipTxtWriter>();
        services.AddSingleton<IResultWriter, MkvChapterWriter>();
        services.AddSingleton<IResultWriter, JsonResultWriter>();
        services.AddSingleton<IResultWriter, FFMetadataWriter>();

        services.AddSingleton<DetectionPipeline>();
        services.AddSingleton<EmbyIntegration>();

        return services.BuildServiceProvider();
    }

    private static string FormatConfigSummary(DetectionConfig config)
    {
        var logo = config.EnableLogoDetection
            ? $"Lo={config.LogoAbsenceWeight:F2}[ssim={config.LogoSsimThreshold:F2}]"
            : "Lo=off";

        var summary = $"threshold={config.CommercialThreshold:F2} | " +
                      $"BF={config.BlackFrameWeight:F2} SC={config.SceneChangeWeight:F2} " +
                      $"Si={config.SilenceWeight:F2} {logo} AR={config.AspectRatioWeight:F2} | " +
                      $"comm={config.MinCommercialDurationSeconds:F0}–{config.MaxCommercialDurationSeconds:F0}s";

        if (config.SkipStartSeconds > 0)
            summary += $" skip-start={config.SkipStartSeconds:F0}s";
        if (config.SkipEndSeconds > 0)
            summary += $" skip-end={config.SkipEndSeconds:F0}s";

        return summary;
    }

    private static string SerializeConfig<T>(T config) =>
        JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

    private static async Task OutputOrPrint(FileInfo? output, string content, string label)
    {
        if (output != null)
        {
            await File.WriteAllTextAsync(output.FullName, content);
            Console.WriteLine($"{label} written to: {output.FullName}");
        }
        else
        {
            Console.WriteLine(content);
        }
    }

    private static bool IsMediaFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".ts" or ".mpg" or ".mpeg" or ".mp4" or ".mkv"
            or ".avi" or ".wtv" or ".m4v" or ".mov";
    }
}

// ── Console Helpers ─────────────────────────────────────────────────────

internal class ConsoleProgress : IAnalysisProgress
{
    public void ReportPhase(string phaseName)
        => Console.WriteLine($"► {phaseName}");

    public void ReportProgress(double percentComplete, string? message = null)
    {
        string bar = new string('█', (int)(percentComplete / 5)) +
                     new string('░', 20 - (int)(percentComplete / 5));
        string msg = message != null ? $" — {message}" : "";
        Console.Write($"\r  [{bar}] {percentComplete:F0}%{msg}          ");
        if (percentComplete >= 100) Console.WriteLine();
    }

    public void ReportComplete(AnalysisResult result)
        => Console.WriteLine("✓ Analysis complete");

    public void ReportError(Exception exception)
        => Console.Error.WriteLine($"✗ Error: {exception.Message}");
}

internal static class ListExtensions
{
    public static double Sum(this List<ContentSegment> segments, Func<ContentSegment, double> selector)
    {
        double total = 0;
        foreach (var s in segments) total += selector(s);
        return total;
    }
}
