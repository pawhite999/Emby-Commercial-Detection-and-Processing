using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CommDetect.Core;

public interface IFFmpegLocator
{
    string? FindFFmpeg();
    string? FindFFprobe();
}

public interface IMediaProbe
{
    Task<MediaInfo> ProbeAsync(string filePath, CancellationToken cancellationToken = default);
}

public interface IFrameExtractor
{
    IAsyncEnumerable<VideoFrame> ExtractFramesAsync(
        string filePath, double frameRate, CancellationToken cancellationToken);
}

public interface IAudioExtractor
{
    IAsyncEnumerable<AudioWindow> ExtractAudioAsync(
        string filePath, double windowSeconds, CancellationToken cancellationToken);
}

public interface ISignalDetector
{
    string Name { get; }
    SignalType SignalType { get; }
    bool RequiresVideo { get; }
    bool RequiresAudio { get; }

    Task<IReadOnlyList<(TimeSpan start, TimeSpan end, double score)>> AnalyzeAsync(
        IAsyncEnumerable<VideoFrame> frames,
        IAsyncEnumerable<AudioWindow> audio,
        MediaInfo mediaInfo,
        DetectionConfig config,
        CancellationToken cancellationToken);
}

public interface ICommercialClassifier
{
    AnalysisResult Classify(
        MediaInfo mediaInfo,
        Dictionary<SignalType, IReadOnlyList<(TimeSpan start, TimeSpan end, double score)>> signals,
        DetectionConfig config);
}

public interface IResultWriter
{
    OutputFormat Format { get; }
    string FileExtension { get; }
    Task WriteAsync(AnalysisResult result, string outputPath,
        CancellationToken cancellationToken = default);
}

public interface IAnalysisProgress
{
    void ReportPhase(string phaseName);
    void ReportProgress(double percentComplete, string? message = null);
    void ReportComplete(AnalysisResult result);
    void ReportError(Exception exception);
}
