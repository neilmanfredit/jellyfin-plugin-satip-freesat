using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SatIpFreesat.DvbSi;

/// <summary>
/// Parses DVB PAT (table_id 0x00) and PMT (table_id 0x02) sections.
/// Used to resolve, per service, the PMT PID plus its video and (primary) audio elementary
/// stream PIDs — needed to build a SAT>IP "pids=" list scoped to a single channel instead of
/// the whole transponder multiplex.
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

    /// <summary>Parses a PMT section, returning the primary video/audio elementary PIDs.</summary>
    public static PmtInfo? ParsePmt(ReadOnlySpan<byte> section)
    {
        if (section.Length < 12 || section[0] != TableIdPmt) return null;

        int sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        int end = Math.Min(3 + sectionLength - 4, section.Length); // -4 for CRC32
        int serviceId = (section[3] << 8) | section[4];

        int programInfoLength = ((section[10] & 0x0F) << 8) | section[11];
        int pos = 12 + programInfoLength;

        int? videoPid = null;
        int? audioPid = null;

        while (pos + 5 <= end)
        {
            int streamType = section[pos];
            int elementaryPid = ((section[pos + 1] & 0x1F) << 8) | section[pos + 2];
            int esInfoLength = ((section[pos + 3] & 0x0F) << 8) | section[pos + 4];
            pos += 5;

            if (pos + esInfoLength > end) break;

            if (videoPid is null && VideoStreamTypes.Contains(streamType))
                videoPid = elementaryPid;
            else if (audioPid is null && AudioStreamTypes.Contains(streamType))
                audioPid = elementaryPid;
            else if (audioPid is null && streamType == 0x06 && HasAc3Descriptor(section.Slice(pos, esInfoLength)))
                audioPid = elementaryPid;

            pos += esInfoLength;
        }

        return new PmtInfo(serviceId, videoPid, audioPid);
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
}

/// <summary>Elementary stream PIDs resolved from a single service's PMT section.</summary>
public sealed record PmtInfo(int ServiceId, int? VideoPid, int? AudioPid);
