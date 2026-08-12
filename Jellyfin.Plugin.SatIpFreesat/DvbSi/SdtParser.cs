using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SatIpFreesat.Freesat;

namespace Jellyfin.Plugin.SatIpFreesat.DvbSi;

/// <summary>
/// Parses DVB SDT (Service Description Table, table_id 0x42/0x46).
/// Extracts service name, provider name, type, and CA status.
/// </summary>
public static class SdtParser
{
    public const int PidSdt = 0x11;
    public const byte TableIdSdtActual = 0x42;
    public const byte TableIdSdtOther = 0x46;

    public static List<ServiceInfo> Parse(ReadOnlySpan<byte> section)
    {
        var services = new List<ServiceInfo>();
        if (section.Length < 11) return services;
        if (section[0] is not (TableIdSdtActual or TableIdSdtOther)) return services;

        int tsid = (section[3] << 8) | section[4];
        int onid = (section[8] << 8) | section[9];

        int pos = 11;
        while (pos + 5 <= section.Length - 4) // -4 for CRC32
        {
            int serviceId = (section[pos] << 8) | section[pos + 1];
            bool eitPresent = (section[pos + 2] & 0x01) != 0;
            int runningStatus = (section[pos + 3] >> 5) & 0x07;
            bool scrambled = (section[pos + 3] & 0x10) != 0;
            int descLen = ((section[pos + 3] & 0x0F) << 8) | section[pos + 4];
            pos += 5;

            if (pos + descLen > section.Length - 4) break;

            var info = ParseServiceDescriptor(section.Slice(pos, descLen));
            if (info is not null)
            {
                services.Add(new ServiceInfo
                {
                    ServiceId = serviceId,
                    Name = info.Value.Name,
                    ProviderName = info.Value.Provider,
                    ServiceType = info.Value.Type,
                    IsEncrypted = scrambled,
                });
            }

            pos += descLen;
        }

        return services;
    }

    private static (string Name, string Provider, int Type)? ParseServiceDescriptor(ReadOnlySpan<byte> descs)
    {
        int i = 0;
        while (i + 2 <= descs.Length)
        {
            byte tag = descs[i];
            int len = descs[i + 1];
            i += 2;
            if (i + len > descs.Length) break;

            if (tag == 0x48 && len >= 2)
            {
                // service_descriptor: type(1), provider_len(1), provider(N), name_len(1), name(M)
                int serviceType = descs[i];
                int provLen = descs[i + 1];
                var provider = DvbTextDecoder.Decode(descs.Slice(i + 2, Math.Min(provLen, len - 2)));
                int nameOffset = i + 2 + provLen;
                if (nameOffset + 1 > i + len) return (string.Empty, provider, serviceType);
                int nameLen = descs[nameOffset];
                var name = DvbTextDecoder.Decode(descs.Slice(nameOffset + 1, Math.Min(nameLen, i + len - nameOffset - 1)));
                return (name.Trim(), provider.Trim(), serviceType);
            }
            i += len;
        }
        return null;
    }
}
