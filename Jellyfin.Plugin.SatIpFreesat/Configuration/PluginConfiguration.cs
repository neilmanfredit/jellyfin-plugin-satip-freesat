using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SatIpFreesat.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>IP address or hostname of the SAT>IP server (e.g. 192.168.1.100).</summary>
    public string ServerAddress { get; set; } = string.Empty;

    /// <summary>RTSP port on the SAT>IP server. Usually 554.</summary>
    public int RtspPort { get; set; } = 554;

    /// <summary>SAT>IP frontend/source number to use (src= parameter). Usually 1.</summary>
    public int FrontendNumber { get; set; } = 1;

    /// <summary>Number of concurrent tuners the device exposes.</summary>
    public int TunerCount { get; set; } = 1;

    /// <summary>UK postcode used to pick the correct Freesat regional bouquet.</summary>
    public string Postcode { get; set; } = string.Empty;

    /// <summary>Region key resolved from Postcode (set automatically by wizard).</summary>
    public string RegionKey { get; set; } = string.Empty;

    /// <summary>Human-readable region label (set automatically by wizard).</summary>
    public string RegionLabel { get; set; } = string.Empty;

    /// <summary>Automatically rescan for channels this many hours after the last scan. 0 = manual only.</summary>
    public int AutoScanIntervalHours { get; set; } = 24;

    /// <summary>UTC timestamp of the last successful channel scan (ISO-8601).</summary>
    public string LastScanTime { get; set; } = string.Empty;
}
