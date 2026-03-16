using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommDetect.Core;

namespace CommDetect.IO;

/// <summary>
/// Writes an MPlayer/Kodi EDL (Edit Decision List) file.
///
/// Format — one line per commercial segment:
///   {start_seconds}  {end_seconds}  3
///
/// Action code 3 = commercial break (players skip or prompt the user).
/// Times are in seconds with three decimal places.
///
/// Example:
///   10.500  75.200  3
///   180.000 255.750 3
/// </summary>
public class EdlWriter : IResultWriter
{
    public OutputFormat Format => OutputFormat.Edl;
    public string FileExtension => ".edl";

    public async Task WriteAsync(AnalysisResult result, string outputPath,
        CancellationToken cancellationToken = default)
    {
        var commercials = result.Segments
            .Where(s => s.Type == SegmentType.Commercial)
            .OrderBy(s => s.Start)
            .ToList();

        var sb = new StringBuilder();
        foreach (var seg in commercials)
        {
            string start = seg.Start.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
            string end   = seg.End.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
            sb.AppendLine($"{start} {end} 3");
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), cancellationToken);
    }
}

/// <summary>
/// Writes a Comskip-compatible frame-number file.
///
/// Format:
///   FILE PROCESSING COMPLETE  {filename}
///   {start_frame}\t{end_frame}
///   ...
///
/// Understood by Kodi's ComSkip plugin and several other DVR tools.
/// </summary>
public class ComskipTxtWriter : IResultWriter
{
    public OutputFormat Format => OutputFormat.ComskipTxt;
    public string FileExtension => ".txt";

    public async Task WriteAsync(AnalysisResult result, string outputPath,
        CancellationToken cancellationToken = default)
    {
        var commercials = result.Segments
            .Where(s => s.Type == SegmentType.Commercial)
            .OrderBy(s => s.Start)
            .ToList();

        double fps = result.FrameRate > 0 ? result.FrameRate : 29.97;

        var sb = new StringBuilder();
        sb.AppendLine($"FILE PROCESSING COMPLETE  {Path.GetFileName(result.SourceFile)}");

        foreach (var seg in commercials)
        {
            long startFrame = (long)(seg.Start.TotalSeconds * fps);
            long endFrame   = (long)(seg.End.TotalSeconds   * fps);
            sb.AppendLine($"{startFrame}\t{endFrame}");
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), cancellationToken);
    }
}

/// <summary>
/// Writes an MKV chapter XML file marking program and commercial segments.
/// Can be embedded into an MKV container with mkvmerge.
/// </summary>
public class MkvChapterWriter : IResultWriter
{
    public OutputFormat Format => OutputFormat.MkvChapters;
    public string FileExtension => ".chapters.xml";

    public async Task WriteAsync(AnalysisResult result, string outputPath,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<Chapters>");
        sb.AppendLine("  <EditionEntry>");
        sb.AppendLine("    <EditionFlagDefault>1</EditionFlagDefault>");

        int programNum    = 1;
        int commercialNum = 1;

        foreach (var seg in result.Segments.OrderBy(s => s.Start))
        {
            string title = seg.Type == SegmentType.Commercial
                ? $"Commercial Break {commercialNum++}"
                : $"Part {programNum++}";

            sb.AppendLine("    <ChapterAtom>");
            sb.AppendLine($"      <ChapterTimeStart>{FormatMkvTime(seg.Start)}</ChapterTimeStart>");
            sb.AppendLine($"      <ChapterTimeEnd>{FormatMkvTime(seg.End)}</ChapterTimeEnd>");
            if (seg.Type == SegmentType.Commercial)
                sb.AppendLine("      <ChapterFlagHidden>1</ChapterFlagHidden>");
            sb.AppendLine("      <ChapterDisplay>");
            sb.AppendLine($"        <ChapterString>{title}</ChapterString>");
            sb.AppendLine("        <ChapLanguageIETF>en</ChapLanguageIETF>");
            sb.AppendLine("      </ChapterDisplay>");
            sb.AppendLine("    </ChapterAtom>");
        }

        sb.AppendLine("  </EditionEntry>");
        sb.AppendLine("</Chapters>");

        await File.WriteAllTextAsync(outputPath, sb.ToString(), cancellationToken);
    }

    private static string FormatMkvTime(TimeSpan ts) =>
        $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}000000";
}

/// <summary>
/// Writes the full AnalysisResult as JSON — useful for debugging and
/// for downstream tools that want the raw segment data.
/// </summary>
public class JsonResultWriter : IResultWriter
{
    public OutputFormat Format => OutputFormat.Json;
    public string FileExtension => ".commdetect.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task WriteAsync(AnalysisResult result, string outputPath,
        CancellationToken cancellationToken = default)
    {
        // Serialize a summary — the full Windows list can be enormous for long files
        var summary = new
        {
            sourceFile        = result.SourceFile,
            totalDuration     = result.TotalDuration.ToString(),
            frameRate         = result.FrameRate,
            resolution        = $"{result.Width}x{result.Height}",
            analysisStarted   = result.AnalysisStartedUtc,
            analysisCompleted = result.AnalysisCompletedUtc,
            analysisDuration  = result.AnalysisDuration.ToString(),
            segments          = result.Segments.Select(s => new
            {
                start       = s.Start.TotalSeconds,
                end         = s.End.TotalSeconds,
                duration    = s.DurationSeconds,
                type        = s.Type.ToString(),
                confidence  = Math.Round(s.Confidence, 4)
            })
        };

        string json = JsonSerializer.Serialize(summary, Options);
        await File.WriteAllTextAsync(outputPath, json, cancellationToken);
    }
}

/// <summary>
/// Writes an FFmpeg metadata file with chapter markers.
/// Can be muxed into any container with: ffmpeg -i input -i chapters.ffmeta
/// -map_metadata 1 -map_chapters 1 -c copy output
/// </summary>
public class FFMetadataWriter : IResultWriter
{
    public OutputFormat Format => OutputFormat.FFMetadata;
    public string FileExtension => ".ffmeta";

    public async Task WriteAsync(AnalysisResult result, string outputPath,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine(";FFMETADATA1");

        int programNum    = 1;
        int commercialNum = 1;

        foreach (var seg in result.Segments.OrderBy(s => s.Start))
        {
            string title = seg.Type == SegmentType.Commercial
                ? $"Commercial Break {commercialNum++}"
                : $"Part {programNum++}";

            // FFmetadata uses milliseconds for chapter timestamps
            long startMs = (long)seg.Start.TotalMilliseconds;
            long endMs   = (long)seg.End.TotalMilliseconds;

            sb.AppendLine("[CHAPTER]");
            sb.AppendLine("TIMEBASE=1/1000");
            sb.AppendLine($"START={startMs}");
            sb.AppendLine($"END={endMs}");
            sb.AppendLine($"title={title}");
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), cancellationToken);
    }
}
