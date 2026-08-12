using System.Collections.Generic;
using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SatIpFreesat.Configuration;

/// <summary>One physical tuner on the SAT>IP device (one src= stream slot).</summary>
public sealed class TunerEntry
{
    public int RtspPort { get; set; } = 554;

    /// <summary>SAT>IP src= parameter identifying this tuner/frontend.</summary>
    public int FrontendNumber { get; set; } = 1;
}

public sealed class PluginConfiguration : BasePluginConfiguration
{
    // ── SAT>IP device ───────────────────────────────────────────────────────

    public string ServerAddress { get; set; } = string.Empty;

    /// <summary>One entry per physical tuner on the device.</summary>
    public List<TunerEntry> Tuners { get; set; } = [new TunerEntry()];

    /// <summary>First tuner used for scanning and EPG collection. Never null.</summary>
    [XmlIgnore]
    public TunerEntry PrimaryTuner =>
        Tuners is { Count: > 0 } ? Tuners[0] : new TunerEntry();

    // ── UK region ───────────────────────────────────────────────────────────

    public string Postcode { get; set; } = string.Empty;
    public string RegionKey { get; set; } = string.Empty;
    public string RegionLabel { get; set; } = string.Empty;

    // ── Channel scan ────────────────────────────────────────────────────────

    public int AutoScanIntervalHours { get; set; } = 24;
    public string LastScanTime { get; set; } = string.Empty;

    // ── Streaming ───────────────────────────────────────────────────────────

    public bool EnableStreamSharing { get; set; } = true;
    public int RtpReceiveBufferKiB { get; set; } = 512;
    public int PacketTimeoutSeconds { get; set; } = 10;

    // ── Subtitles ───────────────────────────────────────────────────────────

    public bool ExposeSubtitleStreams { get; set; } = false;
    public string PreferredSubtitleLanguage { get; set; } = "eng";

    // ── Video ───────────────────────────────────────────────────────────────

    public bool ForceDeinterlace { get; set; } = false;
}
