using System.Collections.Generic;

namespace Jellyfin.Plugin.SatIpFreesat.Freesat;

/// <summary>DVB-S/S2 mux parameters at 28.2E.</summary>
public sealed class MuxInfo
{
    public double FrequencyMHz { get; init; }
    public char Polarization { get; init; }  // 'H' or 'V'
    public double SymbolRateKsym { get; init; }
    public bool IsDvbS2 { get; init; }
    public int TransportStreamId { get; init; }
    public int OriginalNetworkId { get; init; }

    public string SatIpMsys => IsDvbS2 ? "dvbs2" : "dvbs";
    public string SatIpPol => Polarization == 'H' ? "h" : "v";

    /// <summary>Mux key used as a stable identifier in channel IDs.</summary>
    public string Key => $"{OriginalNetworkId}-{TransportStreamId}";
}

/// <summary>A single DVB service (TV or radio channel) discovered via SDT.</summary>
public sealed class ServiceInfo
{
    public int ServiceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ProviderName { get; init; } = string.Empty;
    public int ServiceType { get; init; }
    public bool IsEncrypted { get; init; }
    public MuxInfo? Mux { get; set; }

    /// <summary>Stable Jellyfin channel ID: onid-tsid-sid.</summary>
    public string ChannelId => $"{Mux?.OriginalNetworkId}-{Mux?.TransportStreamId}-{ServiceId}";

    public bool IsTV => ServiceType is 0x01 or 0x11 or 0x16 or 0x19 or 0x1C or 0x1F;
    public bool IsHD => ServiceType is 0x11 or 0x19 or 0x1F;
    public bool IsRadio => ServiceType is 0x02 or 0x0A;

    /// <summary>RTSP pids parameter: PMT PID is found via PAT; for live streaming we use "all".</summary>
    public string PidsParam { get; set; } = "all";
}

/// <summary>A Freesat bouquet entry associating a service with an LCN.</summary>
public sealed class BouquetServiceEntry
{
    public int ServiceId { get; init; }
    public int TransportStreamId { get; init; }
    public int OriginalNetworkId { get; init; }
    public int LogicalChannelNumber { get; init; }
    public bool IsVisible { get; init; }
}

/// <summary>A Freesat bouquet (region or Default).</summary>
public sealed class BouquetInfo
{
    public int BouquetId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsFreesat { get; init; }  // false = BSkyB
    public List<BouquetServiceEntry> Services { get; init; } = [];
}

/// <summary>A resolved channel with LCN, ready to hand to Jellyfin.</summary>
public sealed class FreesatChannel
{
    public string ChannelId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Number { get; init; }
    public bool IsHD { get; init; }
    public bool IsTV { get; init; }
    public bool IsRadio { get; init; }
    public MuxInfo Mux { get; init; } = null!;
    public int ServiceId { get; init; }
}

/// <summary>Persisted result of a channel scan.</summary>
public sealed class ScanResult
{
    public List<FreesatChannel> Channels { get; init; } = [];
    public string RegionKey { get; init; } = string.Empty;
    public string RegionLabel { get; init; } = string.Empty;
    public string ScannedAt { get; init; } = string.Empty;
    public int MuxCount { get; init; }
}
