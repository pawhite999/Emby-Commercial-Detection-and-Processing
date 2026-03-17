using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CommDetect.Analysis;

/// <summary>
/// PSIP data extracted from an ATSC transport stream recording.
/// </summary>
public class PsipInfo
{
    /// <summary>UTC wall-clock time at the start of the recording (from STT).</summary>
    public DateTime? RecordingStartUtc { get; init; }

    /// <summary>Program title from EIT (null if unavailable or Huffman-compressed).</summary>
    public string? ProgramTitle { get; init; }

    /// <summary>Scheduled program start UTC (from EIT).</summary>
    public DateTime? ProgramStartUtc { get; init; }

    /// <summary>Scheduled program duration (from EIT).</summary>
    public TimeSpan? ProgramDuration { get; init; }

    /// <summary>True if both STT and a matching EIT event were found.</summary>
    public bool IsComplete =>
        RecordingStartUtc.HasValue && ProgramStartUtc.HasValue && ProgramDuration.HasValue;

    /// <summary>
    /// Seconds of DVR pre-roll before the program starts.
    /// Use as skip_start_seconds in the detection config.
    /// </summary>
    public double SkipStartSeconds =>
        RecordingStartUtc.HasValue && ProgramStartUtc.HasValue
            ? Math.Max(0, (ProgramStartUtc.Value - RecordingStartUtc.Value).TotalSeconds)
            : 0;

    /// <summary>
    /// Seconds of DVR post-roll after the program ends.
    /// Use as skip_end_seconds in the detection config.
    /// </summary>
    public double GetSkipEndSeconds(TimeSpan recordingDuration) =>
        RecordingStartUtc.HasValue && ProgramStartUtc.HasValue && ProgramDuration.HasValue
            ? Math.Max(0, (RecordingStartUtc.Value + recordingDuration
                - (ProgramStartUtc.Value + ProgramDuration.Value)).TotalSeconds)
            : 0;
}

/// <summary>
/// Reads ATSC PSIP tables directly from an MPEG-2 Transport Stream recording.
///
/// Scans the PSIP base PID (0x1FFB) for two tables:
///   STT (0xCD) — System Time Table: recording wall-clock UTC + GPS/UTC leap offset
///   EIT (0xCB) — Event Information Table: scheduled program start time and duration
///
/// From these two tables the exact DVR pre-roll and post-roll padding can be derived,
/// removing the need for any external EPG source. The data comes directly from the
/// broadcaster at record time and is not subject to Emby EPG time drift.
///
/// Reference: ATSC A/65C (2013).
/// </summary>
public static class PsipReader
{
    private const int  TsPacketSize = 188;
    private const byte TsSyncByte   = 0x47;
    private const int  PsipPid      = 0x1FFB;
    private const byte TableIdEit   = 0xCB;
    private const byte TableIdStt   = 0xCD;
    private const byte TableIdStuff = 0xFF; // padding; discard rest of section buffer

    // GPS epoch: January 6, 1980 00:00:00 UTC
    // Unix epoch offset: 315964800 seconds
    private static readonly DateTime GpsEpoch =
        new(1980, 1, 6, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Scans up to <paramref name="maxScanMb"/> MB of <paramref name="filePath"/> for
    /// ATSC PSIP data. Returns a <see cref="PsipInfo"/> with whatever was found;
    /// all fields are null if no PSIP tables were present.
    /// </summary>
    public static PsipInfo Read(string filePath, TimeSpan recordingDuration,
        ILogger? logger = null, int maxScanMb = 10)
    {
        DateTime? recStart    = null;
        DateTime? progStart   = null;
        TimeSpan? progDur     = null;
        string?   progTitle   = null;
        byte      leapSeconds = 18; // updated from STT (18 is correct from 2017 onwards)

        var buf = new List<byte>(4096); // section assembly buffer for PID 0x1FFB

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 65536);
            using var br = new BinaryReader(fs);

            if (!AlignToSync(br)) return new PsipInfo();

            long limit = Math.Min(fs.Length, (long)maxScanMb * 1024 * 1024);

            while (fs.Position + TsPacketSize <= limit)
            {
                var pkt = br.ReadBytes(TsPacketSize);
                if (pkt[0] != TsSyncByte) { AlignToSync(br); continue; }

                int pid = ((pkt[1] & 0x1F) << 8) | pkt[2];
                if (pid != PsipPid) continue;

                bool hasPusi = (pkt[1] & 0x40) != 0;
                int  afCtrl  = (pkt[3] >> 4) & 0x03;

                // Determine where the payload starts
                int off = 4;
                if ((afCtrl & 0x02) != 0) off = 5 + pkt[4]; // skip adaptation field
                if ((afCtrl & 0x01) == 0 || off >= TsPacketSize) continue; // no payload

                if (hasPusi)
                {
                    // pointer_field: how many bytes before this packet complete the
                    // previous section. Consume them, process, then start fresh.
                    int pointer = pkt[off++];
                    for (int i = 0; i < pointer && off + i < TsPacketSize; i++)
                        buf.Add(pkt[off + i]);

                    ProcessSections(buf, ref recStart, ref progStart, ref progDur,
                        ref progTitle, ref leapSeconds, logger);
                    buf.Clear();

                    off += pointer; // advance past the completed section bytes
                }

                for (int i = off; i < TsPacketSize; i++)
                    buf.Add(pkt[i]);

                ProcessSections(buf, ref recStart, ref progStart, ref progDur,
                    ref progTitle, ref leapSeconds, logger);

                if (recStart.HasValue && progStart.HasValue) break;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning("PsipReader: error scanning {File}: {Msg}", filePath, ex.Message);
        }

        var info = new PsipInfo
        {
            RecordingStartUtc = recStart,
            ProgramTitle      = progTitle,
            ProgramStartUtc   = progStart,
            ProgramDuration   = progDur
        };

        if (info.IsComplete)
            logger?.LogInformation(
                "PsipReader: \"{Title}\" starts {Start:u} — skip-start {SS:F0}s, skip-end {SE:F0}s",
                info.ProgramTitle ?? "(unknown)", info.ProgramStartUtc!.Value,
                info.SkipStartSeconds, info.GetSkipEndSeconds(recordingDuration));
        else
            logger?.LogWarning("PsipReader: incomplete — STT {Stt}, EIT {Eit}",
                recStart.HasValue ? "found" : "missing",
                progStart.HasValue ? "found" : "missing");

        return info;
    }

    // ── Section assembly ────────────────────────────────────────────────────

    /// <summary>
    /// Extracts and parses complete PSI sections from the buffer, consuming each
    /// once processed. Partial sections remain in the buffer for the next call.
    /// </summary>
    private static void ProcessSections(
        List<byte> buf,
        ref DateTime? recStart,
        ref DateTime? progStart,
        ref TimeSpan? progDur,
        ref string?   progTitle,
        ref byte      leapSeconds,
        ILogger?      logger)
    {
        while (buf.Count >= 3)
        {
            if (buf[0] == TableIdStuff) { buf.Clear(); return; } // stuffing

            int sectionLength = ((buf[1] & 0x0F) << 8) | buf[2];
            int totalBytes    = 3 + sectionLength;
            if (buf.Count < totalBytes) return; // section not yet complete

            byte tableId = buf[0];

            if (tableId == TableIdStt && !recStart.HasValue)
                ParseStt(buf, ref recStart, ref leapSeconds, logger);
            else if (tableId == TableIdEit && !progStart.HasValue && recStart.HasValue)
                ParseEit(buf, recStart.Value, leapSeconds,
                    ref progStart, ref progDur, ref progTitle, logger);

            buf.RemoveRange(0, totalBytes); // consume this section
        }
    }

    // ── STT (System Time Table, table_id 0xCD) ──────────────────────────────

    /// <summary>
    /// Short-section layout (section_syntax_indicator = 0):
    ///   [0]   table_id = 0xCD
    ///   [1-2] 0_1_11 | section_length (12 bits)
    ///   [3]   protocol_version = 0
    ///   [4-7] system_time (GPS seconds, big-endian uint32)
    ///   [8]   GPS_UTC_offset (current leap seconds, e.g. 18)
    ///   [9-10] daylight_saving
    ///   [...] descriptors
    ///   [-4]  CRC32
    /// </summary>
    private static void ParseStt(List<byte> buf, ref DateTime? result,
        ref byte leapSeconds, ILogger? logger)
    {
        if (buf.Count < 9) return;

        uint gpsTime   = ReadUint32(buf, 4);
        leapSeconds    = buf[8];
        result         = GpsEpoch.AddSeconds(gpsTime - leapSeconds);

        logger?.LogDebug("PsipReader STT: GPS={G} - {L}ls → {U:u}", gpsTime, leapSeconds, result);
    }

    // ── EIT (Event Information Table, table_id 0xCB) ───────────────────────

    /// <summary>
    /// Long-section layout (section_syntax_indicator = 1):
    ///   [0]   table_id = 0xCB
    ///   [1-2] 1_1_11 | section_length (12 bits)
    ///   [3-4] source_id (identifies the virtual channel)
    ///   [5]   reserved(2) | version_number(5) | current_next_indicator(1)
    ///   [6]   section_number
    ///   [7]   last_section_number
    ///   [8]   protocol_version = 0
    ///   [9]   num_events_in_section
    ///   [10+] events (variable length)
    ///   [-4]  CRC32
    ///
    /// Per-event layout (ATSC A/65C §6.5):
    ///   [+0-1]  reserved(2) | event_id(14)
    ///   [+2-5]  start_time (GPS seconds, big-endian uint32)
    ///   [+6-8]  reserved(4) | length_in_seconds(20)
    ///   [+9]    reserved(1) | ETM_location(2) | title_length(5)
    ///   [+10..] title bytes (ATSC Multiple String Structure)
    ///   [after] reserved(4) | descriptors_length(12)
    ///   [after] descriptors
    /// </summary>
    private static void ParseEit(
        List<byte> buf, DateTime recStart, byte leapSeconds,
        ref DateTime? progStart, ref TimeSpan? progDur, ref string? progTitle,
        ILogger? logger)
    {
        if (buf.Count < 11) return;

        int sectionEnd = 3 + ((buf[1] & 0x0F) << 8 | buf[2]) - 4; // exclude CRC32
        int numEvents  = buf[9];
        int offset     = 10;

        for (int e = 0; e < numEvents; e++)
        {
            if (offset + 10 > sectionEnd) break;

            uint gpsStart   = ReadUint32(buf, offset + 2);
            uint lengthSecs = (uint)((buf[offset + 6] & 0x0F) << 16
                                   |  buf[offset + 7] << 8
                                   |  buf[offset + 8]);

            var evtStart = GpsEpoch.AddSeconds(gpsStart - leapSeconds);
            var evtEnd   = evtStart.AddSeconds(lengthSecs);

            logger?.LogDebug("PsipReader EIT: event {S:u} → {E:u} ({D}s)", evtStart, evtEnd, lengthSecs);

            // Match: recording start falls within this event (±5 min tolerance for pre-roll)
            if (evtStart <= recStart.AddMinutes(5) && evtEnd > recStart.AddMinutes(-5))
            {
                progStart = evtStart;
                progDur   = TimeSpan.FromSeconds(lengthSecs);
                progTitle = TryParseTitle(buf, offset + 9);
                logger?.LogDebug("PsipReader EIT: matched — title=\"{T}\"", progTitle ?? "(none)");
                return;
            }

            // Advance past this event using title_length and descriptors_length
            int titleLen = buf[offset + 9] & 0x1F;
            int descOff  = offset + 10 + titleLen;
            if (descOff + 2 > sectionEnd) break;
            int descLen = (buf[descOff] & 0x0F) << 8 | buf[descOff + 1];
            offset += 10 + titleLen + 2 + descLen;
        }
    }

    // ── ATSC Multiple String Structure (MSS) title ──────────────────────────

    /// <summary>
    /// Best-effort title parse. Returns null for Huffman-compressed strings
    /// (compression_type != 0) since decoding requires the full ATSC Huffman tables.
    /// Handles uncompressed Latin-1 segments which cover the majority of US broadcasts.
    ///
    /// MSS layout:
    ///   number_strings (8)
    ///   per string: language(24) | number_segments(8)
    ///     per segment: compression_type(8) | mode(8) | number_bytes(8) | bytes[]
    /// </summary>
    private static string? TryParseTitle(List<byte> buf, int offset)
    {
        try
        {
            int titleLen = buf[offset] & 0x1F;
            if (titleLen < 4) return null;

            int mss = offset + 1;
            if (mss >= buf.Count) return null;

            byte numStrings = buf[mss++];
            if (numStrings == 0) return null;

            mss += 3; // skip ISO 639 language code

            if (mss >= buf.Count) return null;
            byte numSegments = buf[mss++];
            if (numSegments == 0) return null;

            if (mss + 3 > buf.Count) return null;
            byte comprType = buf[mss++];
            byte mode      = buf[mss++];
            byte numBytes  = buf[mss++];

            if (mss + numBytes > buf.Count) return null;
            if (comprType != 0x00) return null; // Huffman-compressed; skip

            // mode 0x3F = Standard Latin (ISO-8859-1); mode 0x00 = same per many encoders
            return Encoding.Latin1
                .GetString(buf.GetRange(mss, numBytes).ToArray())
                .Trim('\0', ' ');
        }
        catch
        {
            return null;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static uint ReadUint32(List<byte> buf, int offset) =>
        (uint)(buf[offset]     << 24 | buf[offset + 1] << 16 |
               buf[offset + 2] <<  8 | buf[offset + 3]);

    private static bool AlignToSync(BinaryReader br)
    {
        try
        {
            while (br.BaseStream.Position < br.BaseStream.Length)
            {
                if (br.ReadByte() == TsSyncByte)
                {
                    br.BaseStream.Seek(-1, SeekOrigin.Current);
                    return true;
                }
            }
        }
        catch { /* end of stream */ }
        return false;
    }
}
