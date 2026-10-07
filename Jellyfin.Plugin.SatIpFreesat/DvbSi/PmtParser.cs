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

        int? videoPid = null;
        var audioStreams = new List<AudioStreamInfo>();

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
                });
            }

            pos += esInfoLength;
        }

        return new PmtInfo(serviceId, videoPid, audioStreams);
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

    /// <summary>True when this stream carries audio description (AudioType 0x03).</summary>
    public bool IsAudioDescription => AudioType == 0x03;
}

/// <summary>Elementary stream PIDs resolved from a single service's PMT section.</summary>
public sealed record PmtInfo(int ServiceId, int? VideoPid, IReadOnlyList<AudioStreamInfo> AudioStreams);
