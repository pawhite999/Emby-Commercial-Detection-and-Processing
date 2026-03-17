using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CommDetect.Analysis;

/// <summary>
/// XDS (Extended Data Services, EIA-608) program metadata extracted from a recording.
/// </summary>
public class XdsInfo
{
    /// <summary>Program name from XDS Class 1 Type 3 (null if not broadcast).</summary>
    public string? ProgramName { get; init; }

    /// <summary>Total scheduled program length from XDS Class 1 Type 2.</summary>
    public TimeSpan? ProgramLength { get; init; }

    /// <summary>
    /// Whole minutes of the program that had already aired when the recording started.
    /// Zero means the recording started at or before the program's beginning.
    /// Minute-precision only (EIA-608 limitation).
    /// </summary>
    public int ElapsedMinutesAtStart { get; init; }

    /// <summary>True if program length was successfully read from XDS.</summary>
    public bool IsComplete => ProgramLength.HasValue;

    /// <summary>
    /// Seconds of pre-program content at the start of the recording.
    /// Derived from ElapsedMinutesAtStart (minute precision).
    /// </summary>
    public double SkipStartSeconds => ElapsedMinutesAtStart * 60.0;

    /// <summary>
    /// Seconds of post-program content at the end of the recording.
    /// </summary>
    public double GetSkipEndSeconds(TimeSpan recordingDuration)
    {
        if (!ProgramLength.HasValue) return 0;
        double programEnd = SkipStartSeconds + ProgramLength.Value.TotalSeconds;
        return Math.Max(0, recordingDuration.TotalSeconds - programEnd);
    }
}

/// <summary>
/// Reads XDS (Extended Data Services / EIA-608) program metadata directly from
/// an MPEG-2 Transport Stream recording.
///
/// XDS data is embedded in the MPEG-2 video elementary stream as ATSC A/53 user_data()
/// SEI packets (identifier "GA94"), carried in EIA-608 closed-caption field 1 (cc_type=0).
/// Because Emby/HDHomeRun preserves the full video track, this data survives DVR recording —
/// unlike PSIP tables (PID 0x1FFB), which are stripped.
///
/// Extracts XDS Class 1 (Current Program) packets:
///   Type 0x02 — Program Length + Elapsed time-in-show (minute precision)
///   Type 0x03 — Program Name (ASCII)
///
/// EIA-608 XDS byte encoding: all data values are transmitted offset by +0x20 so they
/// fall in the printable ASCII range (0x20–0x7F). Decode: value = byte - 0x20.
///
/// References: ANSI/CTA-608-E §7.8 (XDS); ATSC A/53 Part 4 §6.2 (user_data).
/// </summary>
public static class XdsReader
{
    private const int  TsPacketSize = 188;
    private const byte TsSyncByte   = 0x47;

    /// <summary>
    /// Scans up to <paramref name="maxScanMb"/> MB of <paramref name="filePath"/> for
    /// XDS program metadata. Returns an <see cref="XdsInfo"/> with whatever was found;
    /// IsComplete is false if no usable XDS data was present.
    /// </summary>
    public static XdsInfo Read(string filePath, TimeSpan recordingDuration,
        ILogger? logger = null, int maxScanMb = 30)
    {
        string?   programName   = null;
        TimeSpan? programLength = null;
        int       elapsedMins   = 0;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 65536);
            using var br = new BinaryReader(fs);

            if (!AlignToSync(br)) return new XdsInfo();

            // Phase 1: find the video elementary PID via PAT → PMT.
            int videoPid = FindVideoPid(br, fs, logger);
            if (videoPid < 0)
            {
                logger?.LogWarning("XdsReader: could not find video PID in PAT/PMT");
                return new XdsInfo();
            }
            logger?.LogDebug("XdsReader: video PID = 0x{Pid:X4}", videoPid);

            // Phase 2: walk video PES packets, extract GA94 user_data, parse XDS.
            AlignToSync(br);
            long limit = Math.Min(fs.Length, (long)maxScanMb * 1024 * 1024);

            var state  = new XdsState();
            var esBuf  = new List<byte>(65536); // video ES accumulation buffer

            while (fs.Position + TsPacketSize <= limit)
            {
                var pkt = br.ReadBytes(TsPacketSize);
                if (pkt[0] != TsSyncByte) { AlignToSync(br); continue; }

                int  pid     = ((pkt[1] & 0x1F) << 8) | pkt[2];
                if (pid != videoPid) continue;

                bool hasPusi = (pkt[1] & 0x40) != 0;
                int  afCtrl  = (pkt[3] >> 4) & 0x03;

                int off = 4;
                if ((afCtrl & 0x02) != 0) off = 5 + pkt[4]; // skip adaptation field
                if ((afCtrl & 0x01) == 0 || off >= TsPacketSize) continue;

                if (hasPusi)
                {
                    // New PES packet starting. First, scan the previous frame's ES data.
                    ScanEsBufferForXds(esBuf, state);
                    esBuf.Clear();

                    // Validate and skip the PES header to reach the video ES.
                    if (off + 9 > TsPacketSize) continue;
                    if (pkt[off] != 0x00 || pkt[off + 1] != 0x00 || pkt[off + 2] != 0x01)
                        continue;
                    byte streamId = pkt[off + 3];
                    if (streamId < 0xE0 || streamId > 0xEF) continue; // not video PES

                    int pesHeaderDataLen = pkt[off + 8];
                    off += 9 + pesHeaderDataLen;
                    if (off >= TsPacketSize) continue;
                }

                for (int i = off; i < TsPacketSize; i++)
                    esBuf.Add(pkt[i]);

                // Stop as soon as we have program length (the key piece of data).
                if (state.ProgramLength.HasValue)
                {
                    programLength = state.ProgramLength;
                    elapsedMins   = state.ElapsedMinutes;
                    programName   = state.ProgramName;
                    break;
                }
            }

            // Scan any buffered ES data not yet processed.
            if (programLength == null)
            {
                ScanEsBufferForXds(esBuf, state);
                programLength = state.ProgramLength;
                elapsedMins   = state.ElapsedMinutes;
                programName   = state.ProgramName;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning("XdsReader: error scanning {File}: {Msg}", filePath, ex.Message);
        }

        var info = new XdsInfo
        {
            ProgramName          = programName,
            ProgramLength        = programLength,
            ElapsedMinutesAtStart = elapsedMins
        };

        if (info.IsComplete)
            logger?.LogInformation(
                "XdsReader: \"{Name}\" length {Len}, elapsed {Ela}min " +
                "— skip-start {SS:F0}s, skip-end {SE:F0}s",
                info.ProgramName ?? "(unknown)", info.ProgramLength,
                info.ElapsedMinutesAtStart,
                info.SkipStartSeconds,
                info.GetSkipEndSeconds(recordingDuration));
        else
            logger?.LogWarning(
                "XdsReader: no XDS program length found in first {Mb}MB", maxScanMb);

        return info;
    }

    // ── PAT / PMT parsing ────────────────────────────────────────────────

    /// <summary>
    /// Scans the first 1 MB for PAT (PID 0x0000) then the PMT it references,
    /// and returns the first video elementary PID found.
    /// </summary>
    private static int FindVideoPid(BinaryReader br, FileStream fs, ILogger? logger)
    {
        long limit  = Math.Min(fs.Length, 1L * 1024 * 1024);
        int  pmtPid = -1;

        var patBuf = new List<byte>(512);
        var pmtBuf = new List<byte>(512);

        while (fs.Position + TsPacketSize <= limit)
        {
            var pkt = br.ReadBytes(TsPacketSize);
            if (pkt[0] != TsSyncByte) { AlignToSync(br); continue; }

            int  pid     = ((pkt[1] & 0x1F) << 8) | pkt[2];
            bool hasPusi = (pkt[1] & 0x40) != 0;
            int  afCtrl  = (pkt[3] >> 4) & 0x03;

            int off = 4;
            if ((afCtrl & 0x02) != 0) off = 5 + pkt[4];
            if ((afCtrl & 0x01) == 0 || off >= TsPacketSize) continue;

            if (hasPusi)
            {
                int pointer = pkt[off++];
                off += pointer;
                if (pid == 0x0000) patBuf.Clear();
                else if (pid == pmtPid) pmtBuf.Clear();
            }

            if (pid == 0x0000) // PAT
            {
                for (int i = off; i < TsPacketSize; i++) patBuf.Add(pkt[i]);
                int p = ParsePat(patBuf, logger);
                if (p > 0) { pmtPid = p; patBuf.Clear(); }
            }
            else if (pmtPid > 0 && pid == pmtPid) // PMT
            {
                for (int i = off; i < TsPacketSize; i++) pmtBuf.Add(pkt[i]);
                int v = ParsePmt(pmtBuf, logger);
                if (v > 0) return v;
            }
        }

        return -1;
    }

    /// <summary>
    /// Parses a PAT section and returns the first non-NIT PMT PID, or -1 if incomplete.
    /// </summary>
    private static int ParsePat(List<byte> buf, ILogger? logger)
    {
        if (buf.Count < 8 || buf[0] != 0x00) return -1; // table_id must be 0x00

        int sectionLen = ((buf[1] & 0x0F) << 8) | buf[2];
        if (buf.Count < 3 + sectionLen) return -1;

        int sectionEnd = 3 + sectionLen - 4; // exclude CRC32
        int offset     = 8;                   // skip fixed header fields

        while (offset + 4 <= sectionEnd)
        {
            int programNum = (buf[offset] << 8) | buf[offset + 1];
            int pmtPid     = ((buf[offset + 2] & 0x1F) << 8) | buf[offset + 3];
            offset += 4;

            if (programNum != 0) // 0 = NIT reference, skip
            {
                logger?.LogDebug("XdsReader PAT: program {P} → PMT PID 0x{M:X4}",
                    programNum, pmtPid);
                return pmtPid;
            }
        }

        return -1;
    }

    /// <summary>
    /// Parses a PMT section and returns the PID of the first video elementary stream,
    /// or -1 if incomplete or no video stream found.
    /// Video stream types: 0x01 MPEG-1, 0x02 MPEG-2, 0x1B H.264, 0x24 HEVC.
    /// </summary>
    private static int ParsePmt(List<byte> buf, ILogger? logger)
    {
        if (buf.Count < 12 || buf[0] != 0x02) return -1; // table_id must be 0x02

        int sectionLen  = ((buf[1] & 0x0F) << 8) | buf[2];
        if (buf.Count < 3 + sectionLen) return -1;

        int sectionEnd   = 3 + sectionLen - 4; // exclude CRC32
        int progInfoLen  = ((buf[10] & 0x0F) << 8) | buf[11];
        int offset       = 12 + progInfoLen;   // skip program descriptors

        while (offset + 5 <= sectionEnd)
        {
            int streamType = buf[offset];
            int elemPid    = ((buf[offset + 1] & 0x1F) << 8) | buf[offset + 2];
            int esInfoLen  = ((buf[offset + 3] & 0x0F) << 8) | buf[offset + 4];
            offset += 5 + esInfoLen;

            bool isVideo = streamType == 0x01 || streamType == 0x02  // MPEG-1/2
                        || streamType == 0x1B || streamType == 0x24; // H.264/HEVC
            if (isVideo)
            {
                logger?.LogDebug("XdsReader PMT: video type 0x{T:X2} → PID 0x{P:X4}",
                    streamType, elemPid);
                return elemPid;
            }
        }

        return -1;
    }

    // ── MPEG-2 ES → GA94 user_data ──────────────────────────────────────

    /// <summary>
    /// Scans an MPEG-2 video ES buffer for user_data() elements (start code 0x000001B2)
    /// carrying the ATSC GA94 ("GA94" = 0x47413934) identifier, then extracts the
    /// EIA-608 field-1 CC pairs from the A/53 CC data structure inside each one.
    /// </summary>
    private static void ScanEsBufferForXds(List<byte> esBuf, XdsState state)
    {
        for (int i = 0; i + 8 < esBuf.Count; i++)
        {
            // user_data start code: 0x000001B2
            if (esBuf[i]     != 0x00 || esBuf[i + 1] != 0x00 ||
                esBuf[i + 2] != 0x01 || esBuf[i + 3] != 0xB2)
                continue;

            // ATSC GA94 identifier: "GA94" = 0x47 0x41 0x39 0x34
            if (esBuf[i + 4] != 0x47 || esBuf[i + 5] != 0x41 ||
                esBuf[i + 6] != 0x39 || esBuf[i + 7] != 0x34)
                continue;

            ExtractCcPairs(esBuf, i + 8, state);
        }
    }

    /// <summary>
    /// Parses an ATSC A/53 Part 4 CC data block and feeds field-1 EIA-608 byte pairs
    /// to the XDS state machine.
    ///
    /// A/53 CC data layout (after the 4-byte GA94 identifier):
    ///   [0]   user_data_type_code — must be 0x03
    ///   [1]   process_em_data_flag(1) | process_cc_data_flag(1) |
    ///         additional_data_flag(1) | cc_count(5)
    ///   [2]   em_data (1 byte, always present)
    ///   [3+]  cc_count × 3-byte triplets:
    ///           reserved(5=0b11111) | cc_valid(1) | cc_type(2)
    ///           cc_data_1 (8 bits)
    ///           cc_data_2 (8 bits)
    ///   [-1]  marker_bits (0xFF)
    ///
    /// cc_type 0x00 = NTSC field 1 (carries CC1/CC2 and XDS on EIA-608).
    /// </summary>
    private static void ExtractCcPairs(List<byte> buf, int offset, XdsState state)
    {
        if (offset >= buf.Count || buf[offset] != 0x03) return; // type_code check
        offset++;

        if (offset >= buf.Count) return;
        int ccCount = buf[offset++] & 0x1F; // lower 5 bits

        offset++; // skip em_data byte

        for (int i = 0; i < ccCount && offset + 2 < buf.Count; i++, offset += 3)
        {
            byte ccHeader = buf[offset];
            bool ccValid  = (ccHeader & 0x04) != 0;
            int  ccType   = ccHeader & 0x03;

            byte b1 = (byte)(buf[offset + 1] & 0x7F); // strip odd-parity bit
            byte b2 = (byte)(buf[offset + 2] & 0x7F);

            // Field 1 (cc_type 0) carries EIA-608 CC1/CC2 and XDS.
            if (ccValid && ccType == 0)
                state.ProcessPair(b1, b2);
        }
    }

    // ── EIA-608 XDS state machine ────────────────────────────────────────

    /// <summary>
    /// Accumulates EIA-608 byte pairs and parses XDS Class 1 (Current Program) packets.
    ///
    /// EIA-608 XDS pair classification (first byte, after parity strip):
    ///   0x00        null pair — skip
    ///   0x01–0x0D   XDS control: odd = Start (class = (b+1)/2), even = Continue
    ///   0x0F        End of XDS packet (second byte = checksum)
    ///   0x10–0x1F   CC channel control — interrupts any active XDS packet
    ///   0x20–0x7F   Data bytes (both bytes are payload)
    ///
    /// XDS data values are offset by +0x20 per the EIA-608 spec, which keeps all
    /// data bytes in the printable ASCII range and unambiguously distinct from
    /// control codes. Decode: actual_value = byte − 0x20.
    /// </summary>
    private sealed class XdsState
    {
        private int           _class;
        private int           _type;
        private readonly List<byte> _data = new(16);
        private bool          _inPacket;

        public string?   ProgramName   { get; private set; }
        public TimeSpan? ProgramLength { get; private set; }
        public int       ElapsedMinutes { get; private set; }

        public void ProcessPair(byte b1, byte b2)
        {
            if (b1 == 0x00 && b2 == 0x00) return; // null pair

            if (_inPacket)
            {
                switch (b1)
                {
                    case 0x0F:
                        // End of XDS packet — process and reset
                        ProcessCompletedPacket();
                        _inPacket = false;
                        _data.Clear();
                        return;

                    case >= 0x01 and <= 0x0D:
                    {
                        bool isStart = (b1 & 1) == 1;
                        if (isStart)
                        {
                            // New packet start interrupts the current one
                            _class = (b1 + 1) / 2;
                            _type  = b2;
                            _data.Clear();
                        }
                        else
                        {
                            // Continuation: b2 is a single data byte for this class
                            if ((b1 / 2) == _class)
                                _data.Add(b2);
                        }
                        return;
                    }

                    case >= 0x10 and <= 0x1F:
                        // CC channel control code — ends any active XDS packet
                        _inPacket = false;
                        _data.Clear();
                        return;
                }

                // Data bytes — both are payload
                _data.Add(b1);
                if (b2 != 0x00) _data.Add(b2);
            }
            else
            {
                // Idle: watch for XDS Start codes (odd bytes 0x01–0x0D)
                if (b1 is >= 0x01 and <= 0x0D && (b1 & 1) == 1)
                {
                    _class    = (b1 + 1) / 2;
                    _type     = b2;
                    _data.Clear();
                    _inPacket = true;
                }
            }
        }

        private void ProcessCompletedPacket()
        {
            if (_class != 1) return; // only Class 1 = Current Program

            switch (_type)
            {
                case 0x02: ParseProgramLength(); break;
                case 0x03: ParseProgramName();   break;
            }
        }

        /// <summary>
        /// Class 1 Type 2 — Program Length and Time-in-Show.
        /// Layout (all values encoded as value + 0x20):
        ///   Byte 0: total length hours   (0–23)
        ///   Byte 1: total length minutes (0–59)
        ///   Byte 2: elapsed hours        (0–23)  [optional]
        ///   Byte 3: elapsed minutes      (0–59)  [optional]
        /// </summary>
        private void ParseProgramLength()
        {
            if (_data.Count < 2) return;

            int lenHours = _data[0] - 0x20;
            int lenMins  = _data[1] - 0x20;

            if (lenHours < 0 || lenHours > 23 || lenMins < 0 || lenMins > 59) return;

            ProgramLength = TimeSpan.FromMinutes(lenHours * 60 + lenMins);

            if (_data.Count >= 4)
            {
                int elaHours = _data[2] - 0x20;
                int elaMins  = _data[3] - 0x20;
                if (elaHours >= 0 && elaHours <= 23 && elaMins >= 0 && elaMins <= 59)
                    ElapsedMinutes = elaHours * 60 + elaMins;
            }
        }

        /// <summary>
        /// Class 1 Type 3 — Program Name.
        /// Direct ASCII; bytes already in printable range.
        /// </summary>
        private void ParseProgramName()
        {
            var sb = new StringBuilder(_data.Count);
            foreach (byte b in _data)
                if (b >= 0x20 && b <= 0x7E) sb.Append((char)b);

            string name = sb.ToString().Trim();
            if (name.Length > 0) ProgramName = name;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

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
