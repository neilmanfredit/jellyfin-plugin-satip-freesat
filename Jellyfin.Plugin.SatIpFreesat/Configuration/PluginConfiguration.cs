using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SatIpFreesat.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    // ── SAT>IP device ───────────────────────────────────────────────────────

    public string ServerAddress { get; set; } = string.Empty;
    public int RtspPort { get; set; } = 554;
    public int FrontendNumber { get; set; } = 1;
    public int TunerCount { get; set; } = 1;

    // ── UK region ───────────────────────────────────────────────────────────

    public string Postcode { get; set; } = string.Empty;
    public string RegionKey { get; set; } = string.Empty;
    public string RegionLabel { get; set; } = string.Empty;

    // ── Channel scan ────────────────────────────────────────────────────────

    public int AutoScanIntervalHours { get; set; } = 24;
    public string LastScanTime { get; set; } = string.Empty;

    // ── Streaming ───────────────────────────────────────────────────────────

    /// <summary>Share one RTSP session between Jellyfin viewers of the same channel.</summary>
    public bool EnableStreamSharing { get; set; } = true;

    /// <summary>RTP receive buffer in kibibytes. Larger values reduce packet loss on busy networks.</summary>
    public int RtpReceiveBufferKiB { get; set; } = 512;

    /// <summary>Seconds with no RTP data before the plugin considers the stream stalled. 0 = disabled.</summary>
    public int PacketTimeoutSeconds { get; set; } = 10;

    // ── Subtitles ───────────────────────────────────────────────────────────

    /// <summary>Report DVB subtitle and teletext tracks to Jellyfin. Disable if channels hang on load.</summary>
    public bool ExposeSubtitleStreams { get; set; } = false;

    /// <summary>Three-letter ISO 639-2 code for the preferred subtitle language (e.g. eng).</summary>
    public string PreferredSubtitleLanguage { get; set; } = "eng";

    // ── Video ───────────────────────────────────────────────────────────────

    /// <summary>Tell Jellyfin/ffmpeg to deinterlace all live TV streams. Requires a server restart.</summary>
    public bool ForceDeinterlace { get; set; } = false;
}
