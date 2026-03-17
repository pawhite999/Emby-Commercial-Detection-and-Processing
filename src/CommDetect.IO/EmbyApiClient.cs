using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CommDetect.IO;

/// <summary>EPG timing info retrieved from the Emby Server API.</summary>
public class EmbyRecordingInfo
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>EPG scheduled show start (UTC).</summary>
    public DateTime StartDate { get; set; }
    /// <summary>EPG scheduled show end (UTC).</summary>
    public DateTime EndDate { get; set; }
    /// <summary>Seconds of recording before the scheduled show start.</summary>
    public int PrePaddingSeconds { get; set; }
    /// <summary>Seconds of recording after the scheduled show end.</summary>
    public int PostPaddingSeconds { get; set; }
}

/// <summary>
/// Queries the Emby Server Live TV API to retrieve recording metadata.
/// Used to determine exact show boundary times so that skip_start_seconds
/// and skip_end_seconds can be set automatically from the recording's actual
/// pre/post padding — no manual per-show tuning required.
///
/// Matches recordings by filename only (path-agnostic) so CommDetect can run
/// on a different machine from the Emby server.
///
/// All failures are non-fatal: if Emby is unreachable or the recording is not
/// found, the method returns null and detection proceeds with INI defaults.
/// </summary>
public class EmbyApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly ILogger? _logger;

    public EmbyApiClient(string serverUrl, string apiKey, ILogger? logger = null)
    {
        _baseUrl = serverUrl.TrimEnd('/');
        _apiKey  = apiKey;
        _logger  = logger;
        _http    = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// Look up a recording by filename and return its EPG timing info.
    /// Returns null if the recording is not found or the API is unreachable.
    /// </summary>
    public async Task<EmbyRecordingInfo?> GetRecordingByFilenameAsync(
        string filename, string localFilePath, CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}/LiveTv/Recordings" +
                      "?Fields=Path,StartDate,EndDate,PrePaddingSeconds,PostPaddingSeconds" +
                      $"&ApiKey={_apiKey}";

            _logger?.LogDebug("Querying Emby API: {Url}", url.Replace(_apiKey, "***"));

            var json = await _http.GetStringAsync(url, ct);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Items", out var items))
            {
                _logger?.LogWarning("Emby API response missing 'Items' array");
                return null;
            }

            foreach (var item in items.EnumerateArray())
            {
                string itemPath = item.TryGetProperty("Path", out var pathProp)
                    ? pathProp.GetString() ?? "" : "";

                if (!System.IO.Path.GetFileName(itemPath)
                        .Equals(filename, StringComparison.OrdinalIgnoreCase))
                    continue;

                var info = new EmbyRecordingInfo
                {
                    Id        = item.TryGetProperty("Id",  out var id)  ? id.GetString()  ?? "" : "",
                    Path      = itemPath,
                    StartDate = item.TryGetProperty("StartDate", out var sd) && sd.TryGetDateTime(out var sdt)
                                    ? sdt : DateTime.MinValue,
                    EndDate   = item.TryGetProperty("EndDate", out var ed) && ed.TryGetDateTime(out var edt)
                                    ? edt : DateTime.MinValue,
                    PrePaddingSeconds  = item.TryGetProperty("PrePaddingSeconds",  out var pre)  ? pre.GetInt32()  : 0,
                    PostPaddingSeconds = item.TryGetProperty("PostPaddingSeconds", out var post) ? post.GetInt32() : 0,
                };

                // If the API didn't return padding values, estimate PostPadding from
                // file mtime vs scheduled show end (pre-padding harder to estimate).
                if (info.PostPaddingSeconds == 0
                    && info.EndDate != DateTime.MinValue
                    && File.Exists(localFilePath))
                {
                    var fileMtime = File.GetLastWriteTimeUtc(localFilePath);
                    double postSeconds = (fileMtime - info.EndDate).TotalSeconds;
                    if (postSeconds > 0 && postSeconds < 1800) // sanity cap at 30 min
                        info.PostPaddingSeconds = (int)postSeconds;
                }

                _logger?.LogInformation(
                    "Emby EPG: {File} — scheduled {Start:HH:mm}–{End:HH:mm} UTC, " +
                    "pre={Pre}s post={Post}s",
                    filename, info.StartDate, info.EndDate,
                    info.PrePaddingSeconds, info.PostPaddingSeconds);

                return info;
            }

            _logger?.LogDebug("Emby recording not found for: {File}", filename);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Emby API lookup failed (non-fatal): {Error}", ex.Message);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
