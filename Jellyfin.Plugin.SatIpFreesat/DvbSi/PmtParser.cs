using System;
using System.Collections.Generic;
using System.Text;

namespace Jellyfin.Plugin.SatIpFreesat.DvbSi;

/// <summary>
/// Parses DVB PAT (table_id 0x00) and PMT (table_id 0x02) sections.
/// Used to resolve, per service, the PMT PID plus its video and audio elementary stream PIDs
/// (including all audio tracks with language and type metadata) — needed to build a SAT>IP
/// "pids=" list scoped to a single channel instead of the whole transponder multiplex.
/// </summary>
public static class PmtParser
{
    public const int PidPat = 0x00;
    public const byte TableIdPat = 0x00;
    public const byte TableIdPmt = 0x02;

    private static readonly HashSet<int> VideoStreamTypes = [0x01, 0x02, 0x1B, 0x24];
    private static readonly HashSet<int> AudioStreamTypes = [0x03, 0x04, 0x0F, 0x11, 0x81];

    /// <summary>Parses a PAT section into (service_id → PMT PID) pairs.</summary>
    public static Dictionary<int, int> ParsePat(ReadOnlySpan<byte> section)
    {
        var result = new Dictionary<int, int>();
        if (section.Length < 12 || section[0] != TableIdPat) return result;

        int sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        int end = Math.Min(3 + sectionLength - 4, section.Length); // -4 for CRC32

        int pos = 8;
        while (pos + 4 <= end)
        {
            int programNumber = (section[pos] << 8) | section[pos + 1];
            int pid = ((section[pos + 2] & 0x1F) << 8) | section[pos + 3];
            if (programNumber != 0) // program_number 0 = network PID entry, not a service
                result[programNumber] = pid;
            pos += 4;
        }

        return result;
    }

    /// <summary>
    /// Parses a PMT section, returning the video PID and all audio elementary streams.
    /// Audio streams are ordered as they appear in the PMT; callers should prefer non-AD
    /// tracks as the default by sorting on <see cref="AudioStreamInfo.IsAudioDescription"/>.
    /// </summary>
    public static PmtInfo? ParsePmt(ReadOnlySpan<byte> section)
    {
        if (section.Length < 12 || section[0] != TableIdPmt) return null;

        int sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        int end = Math.Min(3 + sectionLength - 4, section.Length); // -4 for CRC32
        int serviceId = (section[3] << 8) | section[4];

        int programInfoLength = ((section[10] & 0x0F) << 8) | section[11];
        int pos = 12 + programInfoLength;

        int streamIndex = 0;
        int? videoPid = null;
        var audioStreams = new List<AudioStreamInfo>();
        var subtitleStreams = new List<SubtitleStreamInfo>();

        while (pos + 5 <= end)
        {
            int streamType = section[pos];
            int elementaryPid = ((section[pos + 1] & 0x1F) << 8) | section[pos + 2];
            int esInfoLength = ((section[pos + 3] & 0x0F) << 8) | section[pos + 4];
            pos += 5;

            if (pos + esInfoLength > end) break;

            var descriptors = section.Slice(pos, esInfoLength);

            bool isVideo = videoPid is null && VideoStreamTypes.Contains(streamType);
            bool isAudio = AudioStreamTypes.Contains(streamType)
                || (streamType == 0x06 && HasAc3Descriptor(descriptors));
            bool isSubtitle = !isAudio && streamType == 0x06 && HasDvbSubtitleDescriptor(descriptors);

            if (isVideo)
            {
                videoPid = elementaryPid;
            }
            else if (isAudio)
            {
                var (language, audioType) = ParseIso639Descriptor(descriptors);
                audioStreams.Add(new AudioStreamInfo
                {
                    Pid = elementaryPid,
                    Language = language,
                    AudioType = audioType,
                    PmtStreamIndex = streamIndex,
                });
            }
            else if (isSubtitle)
            {
                var (language, isHI) = ParseDvbSubtitleDescriptor(descriptors);
                subtitleStreams.Add(new SubtitleStreamInfo
                {
                    Pid = elementaryPid,
                    Language = language,
                    IsHearingImpaired = isHI,
                });
            }

            streamIndex++;
            pos += esInfoLength;
        }

        return new PmtInfo(serviceId, videoPid, audioStreams, subtitleStreams);
    }

    /// <summary>
    /// Rebuilds a PMT section reordering ES entries as: video → audio → subtitle, dropping
    /// teletext, data, and other private streams. This guarantees that ffmpeg's global stream
    /// indices are always video(0), audio(1..N), subtitle(N+1..) regardless of the broadcaster's
    /// original PMT ordering, so Jellyfin's DefaultAudioStreamIndex+1 formula always lands on
    /// the correct audio stream. Returns null if the section is malformed or too large.
    /// </summary>
    public static byte[]? RebuildPmtReordered(ReadOnlySpan<byte> original)
    {
        if (original.Length < 12 || original[0] != TableIdPmt) return null;

        int sectionLength = ((original[1] & 0x0F) << 8) | original[2];
        int end = Math.Min(3 + sectionLength - 4, original.Length); // -4 to exclude existing CRC
        int programInfoLength = ((original[10] & 0x0F) << 8) | original[11];

        // Three ordered buckets; final PMT order = video, then audio, then subtitle.
        var videoRanges    = new List<(int start, int length)>();
        var audioRanges    = new List<(int start, int length)>();
        var subtitleRanges = new List<(int start, int length)>();

        int pos = 12 + programInfoLength;
        while (pos + 5 <= end)
        {
            int streamType = original[pos];
            int esInfoLength = ((original[pos + 3] & 0x0F) << 8) | original[pos + 4];
            int entryEnd = pos + 5 + esInfoLength;
            if (entryEnd > end) break;

            var descs = original.Slice(pos + 5, esInfoLength);
            bool isVideo    = VideoStreamTypes.Contains(streamType);
            bool isAudio    = AudioStreamTypes.Contains(streamType)
                           || (streamType == 0x06 && HasAc3Descriptor(descs));
            bool isSubtitle = !isAudio && streamType == 0x06 && HasDvbSubtitleDescriptor(descs);

            if      (isVideo)    videoRanges.Add((pos, 5 + esInfoLength));
            else if (isAudio)    audioRanges.Add((pos, 5 + esInfoLength));
            else if (isSubtitle) subtitleRanges.Add((pos, 5 + esInfoLength));
            // teletext, data, proprietary: dropped

            pos = entryEnd;
        }

        int esLoopLen = 0;
        foreach (var (_, len) in videoRanges)    esLoopLen += len;
        foreach (var (_, len) in audioRanges)    esLoopLen += len;
        foreach (var (_, len) in subtitleRanges) esLoopLen += len;

        // section_length = service_id(2)+version(1)+sec#(1)+last_sec#(1)+PCR_PID(2)+
        //                  prog_info_len(2)+prog_info+es_loop+CRC32(4)
        int newSectionLength = 9 + programInfoLength + esLoopLen + 4;
        if (newSectionLength > 0x0FFF) return null;

        var s = new byte[3 + newSectionLength];
        s[0] = original[0]; // table_id = 0x02
        s[1] = (byte)(0xB0 | (newSectionLength >> 8));
        s[2] = (byte)(newSectionLength & 0xFF);
        original.Slice(3, 9 + programInfoLength).CopyTo(s.AsSpan(3));
        int w = 12 + programInfoLength;
        foreach (var ranges in new[] { videoRanges, audioRanges, subtitleRanges })
        foreach (var (start, length) in ranges)
        {
            original.Slice(start, length).CopyTo(s.AsSpan(w));
            w += length;
        }
        uint crc = ComputeDvbCrc32(s.AsSpan(0, w));
        s[w++] = (byte)(crc >> 24); s[w++] = (byte)(crc >> 16);
        s[w++] = (byte)(crc >> 8);  s[w] = (byte)(crc & 0xFF);
        return s;
    }

    /// <summary>
    /// Packs a rebuilt PMT section into a 188-byte TS packet. The caller must update the
    /// continuity counter in byte [3] before writing. Returns null if section exceeds 183 bytes.
    /// </summary>
    public static byte[]? BuildPmtTsPacket(byte[] section, int pmtPid)
    {
        if (section.Length > 183) return null;
        var pkt = new byte[188];
        pkt[0] = 0x47;
        pkt[1] = (byte)(0x40 | ((pmtPid >> 8) & 0x1F)); // PUSI=1
        pkt[2] = (byte)(pmtPid & 0xFF);
        pkt[3] = 0x10; // payload-only; caller sets continuity counter
        pkt[4] = 0x00; // pointer_field = 0
        section.CopyTo(pkt, 5);
        for (int i = 5 + section.Length; i < 188; i++) pkt[i] = 0xFF;
        return pkt;
    }

    /// <summary>DVB CRC-32 (polynomial 0x04C11DB7, initial value 0xFFFFFFFF, no output inversion).</summary>
    internal static uint ComputeDvbCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= (uint)b << 24;
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x80000000U) != 0 ? (crc << 1) ^ 0x04C11DB7U : crc << 1;
        }
        return crc;
    }

    private static bool HasAc3Descriptor(ReadOnlySpan<byte> descs)
    {
        int i = 0;
        while (i + 2 <= descs.Length)
        {
            byte tag = descs[i];
            int len = descs[i + 1];
            if (tag is 0x6A or 0x7A) return true; // AC-3 / Enhanced AC-3 descriptor
            i += 2 + len;
        }
        return false;
    }

    private static bool HasDvbSubtitleDescriptor(ReadOnlySpan<byte> descs)
    {
        int i = 0;
        while (i + 2 <= descs.Length)
        {
            if (descs[i] == 0x59) return true; // DVB Subtitling Descriptor
            i += 2 + descs[i + 1];
        }
        return false;
    }

    /// <summary>
    /// Reads the first DVB Subtitling Descriptor (tag 0x59) from a descriptor block.
    /// Returns the language and whether the track is for hearing-impaired viewers.
    /// DVB subtitling_type 0x20–0x2F signals hearing impaired; 0x10–0x1F is standard.
    /// </summary>
    private static (string Language, bool IsHearingImpaired) ParseDvbSubtitleDescriptor(ReadOnlySpan<byte> descs)
    {
        int i = 0;
        while (i + 2 <= descs.Length)
        {
            int tag = descs[i];
            int len = descs[i + 1];
            // Each subtitling_info entry is 8 bytes; need at least one.
            if (tag == 0x59 && len >= 8 && i + 2 + len <= descs.Length)
            {
                string lang = Encoding.ASCII.GetString(descs.Slice(i + 2, 3)).Trim().ToLowerInvariant();
                byte subtitlingType = descs[i + 5];
                bool isHI = subtitlingType is >= 0x20 and <= 0x2F;
                return (lang, isHI);
            }
            i += 2 + len;
        }
        return ("eng", false);
    }

    /// <summary>
    /// Reads the first ISO 639 Language Descriptor (tag 0x0A) from a descriptor block.
    /// Returns the 3-letter language code and DVB audio_type:
    ///   0x00 = undefined (main audio), 0x01 = clean effects,
    ///   0x02 = hearing impaired, 0x03 = visually impaired commentary (audio description).
    /// Defaults to ("eng", 0x00) when the descriptor is absent.
    /// </summary>
    private static (string Language, byte AudioType) ParseIso639Descriptor(ReadOnlySpan<byte> descs)
    {
        int i = 0;
        while (i + 2 <= descs.Length)
        {
            byte tag = descs[i];
            int len = descs[i + 1];
            // ISO 639 Language Descriptor: at least one 4-byte entry (3-char lang + 1-byte type)
            if (tag == 0x0A && len >= 4 && i + 2 + len <= descs.Length)
            {
                string lang = Encoding.ASCII.GetString(descs.Slice(i + 2, 3)).Trim().ToLowerInvariant();
                byte audioType = descs[i + 5];
                return (lang, audioType);
            }
            i += 2 + len;
        }
        return ("eng", 0x00);
    }
}

/// <summary>A single audio elementary stream discovered in a PMT section.</summary>
public sealed class AudioStreamInfo
{
    public int Pid { get; init; }

    /// <summary>ISO 639-2 language code, lower-case (e.g. "eng").</summary>
    public string Language { get; init; } = "eng";

    /// <summary>
    /// DVB audio_type from the ISO 639 Language Descriptor:
    /// 0x00 = main/undefined, 0x02 = hearing impaired, 0x03 = visually impaired (audio description).
    /// </summary>
    public byte AudioType { get; init; }

    /// <summary>True when this stream carries audio description (AudioType 0x03 or language "nar").</summary>
    /// <remarks>
    /// DVB spec defines AudioType 0x03 as "visually impaired commentary" (audio description).
    /// BBC, ITV, and Channel 4 use lang="nar" with AudioType=0x00 instead — catch both conventions.
    /// </remarks>
    public bool IsAudioDescription => AudioType == 0x03 || Language == "nar";

    /// <summary>
    /// The 0-based position of this audio stream in the broadcaster's PMT, counting every
    /// elementary stream type (video, audio, subtitle, teletext, data). This matches the
    /// global stream index ffmpeg assigns when it opens the TS — e.g. if the PMT order is
    /// video(0), nar(1), subtitle(2), eng(3), then the eng audio has PmtStreamIndex=3.
    /// Zero means this field was not populated (channel scanned before this was introduced).
    /// </summary>
    public int PmtStreamIndex { get; init; }
}

/// <summary>Elementary stream PIDs resolved from a single service's PMT section.</summary>
public sealed record PmtInfo(
    int ServiceId,
    int? VideoPid,
    IReadOnlyList<AudioStreamInfo> AudioStreams,
    IReadOnlyList<SubtitleStreamInfo> SubtitleStreams);

/// <summary>A DVB subtitle elementary stream discovered in a PMT section.</summary>
public sealed class SubtitleStreamInfo
{
    public int Pid { get; init; }

    /// <summary>ISO 639-2 language code, lower-case (e.g. "eng").</summary>
    public string Language { get; init; } = "eng";

    /// <summary>
    /// True when DVB subtitling_type is in the 0x20–0x2F range (subtitles for the
    /// hearing impaired), per EN 300 468 Table 26.
    /// </summary>
    public bool IsHearingImpaired { get; init; }
}
