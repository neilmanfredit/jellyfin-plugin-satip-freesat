using System;
using System.Collections.Generic;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.Plugin.SatIpFreesat.DvbSi;

/// <summary>
/// Parses DVB EIT (Event Information Table, table_id 0x4E-0x6F).
/// Extracts present/following and 8-day schedule events from the MPEG-TS stream.
/// </summary>
public static class EitParser
{
    public const int PidEit = 0x12;

    // EIT actual/other TS, present-following and schedule
    public static bool IsEitTableId(byte id) => id >= 0x4E && id <= 0x6F;

    public static List<ProgramInfo> Parse(ReadOnlySpan<byte> section, Func<int, int, int, string> channelIdBuilder)
    {
        var programs = new List<ProgramInfo>();
        if (section.Length < 14) return programs;
        if (!IsEitTableId(section[0])) return programs;

        int serviceId = (section[3] << 8) | section[4];
        int tsid = (section[8] << 8) | section[9];
        int onid = (section[10] << 8) | section[11];
        string channelId = channelIdBuilder(onid, tsid, serviceId);

        int pos = 14;
        while (pos + 12 <= section.Length - 4)
        {
            int eventId = (section[pos] << 8) | section[pos + 1];
            var startTime = ParseMjdUtc(section[(pos + 2)..]);
            var duration = ParseBcdDuration(section[(pos + 7)..]);
            int descLen = ((section[pos + 10] & 0x0F) << 8) | section[pos + 11];
            pos += 12;

            if (pos + descLen > section.Length - 4) break;
            var (title, synopsis, genres) = ParseEventDescriptors(section.Slice(pos, descLen));
            pos += descLen;

            if (startTime == DateTime.MinValue) continue;

            programs.Add(new ProgramInfo
            {
                Id = $"{channelId}-{eventId}",
                ChannelId = channelId,
                Name = title,
                Overview = synopsis,
                Genres = genres,
                StartDate = startTime,
                EndDate = startTime + duration,
                IsLive = false,
                IsNews = genres.Exists(g => g.Contains("news", StringComparison.OrdinalIgnoreCase)),
            });
        }

        return programs;
    }

    // MJD + UTC BCD: 5 bytes → DateTime
    private static DateTime ParseMjdUtc(ReadOnlySpan<byte> d)
    {
        if (d.Length < 5) return DateTime.MinValue;
        int mjd = (d[0] << 8) | d[1];
        if (mjd == 0 || mjd == 0xFFFF) return DateTime.MinValue;

        // MJD to Gregorian
        double mjdF = mjd;
        int yp = (int)((mjdF - 15078.2) / 365.25);
        int mp = (int)((mjdF - 14956.1 - Math.Floor(yp * 365.25)) / 30.6001);
        int day = (int)(mjdF - 14956 - Math.Floor(yp * 365.25) - Math.Floor(mp * 30.6001));
        int month = mp < 14 ? mp - 1 : mp - 13;
        int year = month > 2 ? yp + 1900 : yp + 1901;

        int hour = BcdByte(d[2]);
        int min = BcdByte(d[3]);
        int sec = BcdByte(d[4]);
        if (hour > 23 || min > 59 || sec > 59) return DateTime.MinValue;

        try { return new DateTime(year, month, day, hour, min, sec, DateTimeKind.Utc); }
        catch { return DateTime.MinValue; }
    }

    private static TimeSpan ParseBcdDuration(ReadOnlySpan<byte> d)
    {
        if (d.Length < 3) return TimeSpan.Zero;
        return new TimeSpan(BcdByte(d[0]), BcdByte(d[1]), BcdByte(d[2]));
    }

    private static int BcdByte(byte b) => ((b >> 4) & 0xF) * 10 + (b & 0xF);

    private static (string Title, string Synopsis, List<string> Genres) ParseEventDescriptors(
        ReadOnlySpan<byte> descs)
    {
        var title = string.Empty;
        var synopsis = string.Empty;
        var genres = new List<string>();

        int i = 0;
        while (i + 2 <= descs.Length)
        {
            byte tag = descs[i];
            int len = descs[i + 1];
            i += 2;
            if (i + len > descs.Length) break;

            if (tag == 0x4D && len >= 4) // short_event_descriptor
            {
                // lang(3) + name_len(1) + name + text_len(1) + text
                int nameLen = descs[i + 3];
                if (i + 4 + nameLen <= i + len)
                {
                    title = DvbTextDecoder.Decode(descs.Slice(i + 4, nameLen)).Trim();
                    int textOffset = i + 4 + nameLen;
                    if (textOffset + 1 <= i + len)
                    {
                        int textLen = descs[textOffset];
                        synopsis = DvbTextDecoder.Decode(descs.Slice(textOffset + 1, Math.Min(textLen, i + len - textOffset - 1))).Trim();
                    }
                }
            }
            else if (tag == 0x54 && len >= 1) // content_descriptor
            {
                for (int j = 0; j + 2 <= len; j += 2)
                {
                    int nibble1 = (descs[i + j] >> 4) & 0xF;
                    genres.Add(NibbleToGenre(nibble1));
                }
            }

            i += len;
        }

        return (title, synopsis, genres);
    }

    private static string NibbleToGenre(int nibble) => nibble switch
    {
        1 => "Movie",
        2 => "News",
        3 => "Show",
        4 => "Sports",
        5 => "Kids",
        6 => "Music",
        7 => "Arts",
        8 => "Current Affairs",
        9 => "Education",
        10 => "Leisure",
        11 => "Lifestyle",
        _ => "General",
    };
}
