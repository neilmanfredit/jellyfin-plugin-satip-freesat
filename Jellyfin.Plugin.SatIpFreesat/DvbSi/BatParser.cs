using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SatIpFreesat.Freesat;

namespace Jellyfin.Plugin.SatIpFreesat.DvbSi;

/// <summary>
/// Parses DVB BAT (Bouquet Association Table, table_id 0x4A).
/// Shares PID 0x11 with SDT. Extracts bouquet names and LCNs.
/// </summary>
public static class BatParser
{
    // BAT shares PID 0x11 (SdtParser.PidSdt)
    public const byte TableIdBat = 0x4A;

    // Descriptor tags
    private const byte TagBouquetName = 0x47;
    private const byte TagLcn = 0x83;        // NorDig / UK LCN descriptor

    public static BouquetInfo? Parse(ReadOnlySpan<byte> section)
    {
        if (section.Length < 10) return null;
        if (section[0] != TableIdBat) return null;

        int bouquetId = (section[3] << 8) | section[4];
        int bouquetDescLen = ((section[8] & 0x0F) << 8) | section[9];

        string name = string.Empty;
        if (bouquetDescLen > 0 && 10 + bouquetDescLen <= section.Length)
            name = ParseBouquetName(section.Slice(10, bouquetDescLen));

        int tsLoopStart = 10 + bouquetDescLen;
        if (tsLoopStart + 2 > section.Length) return new BouquetInfo { BouquetId = bouquetId, Name = name };

        int tsLoopLen = ((section[tsLoopStart] & 0x0F) << 8) | section[tsLoopStart + 1];
        int pos = tsLoopStart + 2;
        int end = Math.Min(pos + tsLoopLen, section.Length - 4);

        var services = new List<BouquetServiceEntry>();
        while (pos + 6 <= end)
        {
            int tsid = (section[pos] << 8) | section[pos + 1];
            int onid = (section[pos + 2] << 8) | section[pos + 3];
            int descLen = ((section[pos + 4] & 0x0F) << 8) | section[pos + 5];
            pos += 6;

            if (pos + descLen > end) break;
            ParseLcnDescriptors(section.Slice(pos, descLen), tsid, onid, services);
            pos += descLen;
        }

        return new BouquetInfo
        {
            BouquetId = bouquetId,
            Name = name,
            Services = services,
        };
    }

    private static string ParseBouquetName(ReadOnlySpan<byte> descs)
    {
        int i = 0;
        while (i + 2 <= descs.Length)
        {
            byte tag = descs[i];
            int len = descs[i + 1];
            i += 2;
            if (tag == TagBouquetName && len > 0 && i + len <= descs.Length)
                return DvbTextDecoder.Decode(descs.Slice(i, len)).Trim();
            i += len;
        }
        return string.Empty;
    }

    private static void ParseLcnDescriptors(
        ReadOnlySpan<byte> descs, int tsid, int onid,
        List<BouquetServiceEntry> services)
    {
        int i = 0;
        while (i + 2 <= descs.Length)
        {
            byte tag = descs[i];
            int len = descs[i + 1];
            i += 2;
            if (i + len > descs.Length) break;

            if (tag == TagLcn)
            {
                // NorDig LCN v1: each entry is 4 bytes: service_id(16) | visible(1) | reserved(5) | lcn(10)
                for (int j = 0; j + 4 <= len; j += 4)
                {
                    int serviceId = (descs[i + j] << 8) | descs[i + j + 1];
                    bool visible = (descs[i + j + 2] & 0x80) != 0;
                    int lcn = ((descs[i + j + 2] & 0x03) << 8) | descs[i + j + 3];
                    services.Add(new BouquetServiceEntry
                    {
                        ServiceId = serviceId,
                        TransportStreamId = tsid,
                        OriginalNetworkId = onid,
                        LogicalChannelNumber = lcn,
                        IsVisible = visible,
                    });
                }
            }
            i += len;
        }
    }
}
