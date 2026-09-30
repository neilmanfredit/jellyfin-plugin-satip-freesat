using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SatIpFreesat.Configuration;
using Jellyfin.Plugin.SatIpFreesat.Freesat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SatIpFreesat.Api;

[ApiController]
[Route("SatIpFreesat")]
[Authorize(Policy = "RequiresElevation")]
public sealed class SatIpController : ControllerBase
{
    private readonly FreesatScanner _scanner;
    private readonly FreesatChannelStore _store;
    private readonly ScanJobService _scanJob;
    private readonly XmltvEpgCache _xmltvCache;
    private readonly XmltvEpgCollectorService _xmltvCollector;
    private readonly ILogger<SatIpController> _logger;

    public SatIpController(
        FreesatScanner scanner,
        FreesatChannelStore store,
        ScanJobService scanJob,
        XmltvEpgCache xmltvCache,
        XmltvEpgCollectorService xmltvCollector,
        ILogger<SatIpController> logger)
    {
        _scanner = scanner;
        _store = store;
        _scanJob = scanJob;
        _xmltvCache = xmltvCache;
        _xmltvCollector = xmltvCollector;
        _logger = logger;
    }

    // ── Plugin configuration (bypasses Jellyfin generic config API) ─────────

    [HttpGet("config")]
    public ActionResult<PluginConfigResponse> GetConfig()
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null) return StatusCode(500, "Plugin not loaded");
        return Ok(new PluginConfigResponse(
            cfg.ServerAddress,
            cfg.Tuners,
            cfg.Postcode,
            cfg.RegionKey,
            cfg.RegionLabel,
            cfg.AutoScanIntervalHours,
            cfg.EnableStreamSharing,
            cfg.RtpReceiveBufferKiB,
            cfg.PacketTimeoutSeconds,
            cfg.ExposeSubtitleStreams,
            cfg.PreferredSubtitleLanguage,
            cfg.ForceDeinterlace,
            cfg.EnableXmltvEpg,
            cfg.XmltvUrl,
            cfg.XmltvRefreshHours));
    }

    [HttpPost("config")]
    public ActionResult SaveConfig([FromBody] PluginConfigRequest req)
    {
        var plugin = Plugin.Instance;
        if (plugin is null) return StatusCode(500, "Plugin not loaded");
        var cfg = plugin.Configuration;

        cfg.ServerAddress = req.ServerAddress ?? cfg.ServerAddress;
        if (req.Tuners is { Count: > 0 }) cfg.Tuners = req.Tuners;
        cfg.Postcode = req.Postcode ?? cfg.Postcode;
        cfg.RegionKey = req.RegionKey ?? cfg.RegionKey;
        cfg.RegionLabel = req.RegionLabel ?? cfg.RegionLabel;
        if (req.AutoScanIntervalHours.HasValue) cfg.AutoScanIntervalHours = req.AutoScanIntervalHours.Value;
        if (req.EnableStreamSharing.HasValue) cfg.EnableStreamSharing = req.EnableStreamSharing.Value;
        if (req.RtpReceiveBufferKiB.HasValue) cfg.RtpReceiveBufferKiB = req.RtpReceiveBufferKiB.Value;
        if (req.PacketTimeoutSeconds.HasValue) cfg.PacketTimeoutSeconds = req.PacketTimeoutSeconds.Value;
        if (req.ExposeSubtitleStreams.HasValue) cfg.ExposeSubtitleStreams = req.ExposeSubtitleStreams.Value;
        cfg.PreferredSubtitleLanguage = req.PreferredSubtitleLanguage ?? cfg.PreferredSubtitleLanguage;
        if (req.ForceDeinterlace.HasValue) cfg.ForceDeinterlace = req.ForceDeinterlace.Value;
        if (req.EnableXmltvEpg.HasValue) cfg.EnableXmltvEpg = req.EnableXmltvEpg.Value;
        cfg.XmltvUrl = req.XmltvUrl ?? cfg.XmltvUrl;
        if (req.XmltvRefreshHours.HasValue) cfg.XmltvRefreshHours = req.XmltvRefreshHours.Value;

        plugin.SaveConfiguration();
        _logger.LogInformation(
            "SAT>IP config saved: server={Server}, tuners={Tuners}, region={Region}",
            cfg.ServerAddress, cfg.Tuners.Count, cfg.RegionKey);
        return Ok(new { message = "Configuration saved." });
    }

    [HttpGet("resolve-region")]
    public ActionResult<ResolveRegionResponse> ResolveRegion([FromQuery] string postcode)
    {
        if (string.IsNullOrWhiteSpace(postcode))
            return BadRequest("postcode required");

        var key = RegionData.ResolveRegion(postcode);
        if (key is null)
            return NotFound($"No Freesat region mapping for postcode '{postcode}'");

        var info = RegionData.Regions[key];
        return Ok(new ResolveRegionResponse(key, info.Label));
    }

    [HttpGet("regions")]
    public ActionResult<RegionListItem[]> GetRegions()
    {
        var items = RegionData.Regions
            .Select(kv => new RegionListItem(kv.Key, kv.Value.Label))
            .OrderBy(r => r.Label)
            .ToArray();
        return Ok(items);
    }

    [HttpPost("scan")]
    public ActionResult<ScanProgressResponse> TriggerScan([FromBody] ScanRequest request)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null) return StatusCode((int)HttpStatusCode.InternalServerError, "Plugin not loaded");

        if (string.IsNullOrWhiteSpace(request.ServerAddress))
            return BadRequest("serverAddress required");
        if (string.IsNullOrWhiteSpace(request.RegionKey) || !RegionData.Regions.ContainsKey(request.RegionKey))
            return BadRequest("valid regionKey required");

        if (!_scanJob.TryStart())
            return Conflict(new ScanProgressResponse("scanning", "A scan is already in progress", 0));

        cfg.ServerAddress = request.ServerAddress;
        cfg.RegionKey = request.RegionKey;
        cfg.RegionLabel = RegionData.Regions[request.RegionKey].Label;

        if (request.Tuners is { Count: > 0 })
            cfg.Tuners = request.Tuners;

        Plugin.Instance!.SaveConfiguration();

        var primary = cfg.PrimaryTuner;
        var host = cfg.ServerAddress;
        var port = primary.RtspPort;
        var frontend = primary.FrontendNumber;
        var regionKey = cfg.RegionKey;
        var scanner = _scanner;
        var logger = _logger;

        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<ScanProgress>(p => _scanJob.Update(p.Message, p.Percent));
                var result = await scanner.ScanAsync(host, port, frontend, regionKey, progress, CancellationToken.None)
                    .ConfigureAwait(false);

                var pluginCfg = Plugin.Instance?.Configuration;
                if (pluginCfg is not null)
                {
                    pluginCfg.LastScanTime = DateTime.UtcNow.ToString("O");
                    Plugin.Instance!.SaveConfiguration();
                }

                _scanJob.Complete(result.Channels.Count,
                    $"Scan complete — {result.Channels.Count} channels in {result.RegionLabel}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SAT>IP background scan failed");
                _scanJob.Fail($"Scan failed: {ex.Message}");
            }
        });

        return Accepted(ToResponse(_scanJob.GetProgress()));
    }

    [HttpGet("scan/progress")]
    public ActionResult<ScanProgressResponse> GetScanProgress()
    {
        var prog = ToResponse(_scanJob.GetProgress());

        // When idle, enrich with last stored scan result so callers don't need a second endpoint
        if (prog.State == "idle")
        {
            var scan = _store.Current;
            if (scan is not null)
                return Ok(prog with
                {
                    Message = $"{scan.Channels.Count} channels ({scan.RegionLabel}) — last scanned {scan.ScannedAt}",
                    ChannelCount = scan.Channels.Count,
                });
        }

        return Ok(prog);
    }

    private static ScanProgressResponse ToResponse(ScanJobService.ScanProgressInfo p) =>
        new(p.State, p.Message, p.ChannelCount, p.Percent);

    [HttpGet("status")]
    public ActionResult<ScanStatusResponse> GetStatus()
    {
        var scan = _store.Current;
        if (scan is null)
            return Ok(new ScanStatusResponse(false, 0, "No scan results — run a scan first"));

        return Ok(new ScanStatusResponse(
            true, scan.Channels.Count,
            $"{scan.Channels.Count} channels ({scan.RegionLabel}) — last scanned {scan.ScannedAt}"));
    }

    [HttpGet("detailed-status")]
    public async Task<ActionResult<DetailedStatusResponse>> GetDetailedStatus(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null)
            return StatusCode((int)HttpStatusCode.InternalServerError, "Plugin not loaded");

        var scan = _store.Current;
        var primary = cfg.PrimaryTuner;

        var reachable = false;
        var reachableMs = 0L;
        var reachableError = string.Empty;

        if (!string.IsNullOrWhiteSpace(cfg.ServerAddress))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(TimeSpan.FromSeconds(3));
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(cfg.ServerAddress, primary.RtspPort, probeCts.Token).ConfigureAwait(false);
                reachable = true;
                reachableMs = sw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                reachableMs = sw.ElapsedMilliseconds;
                reachableError = ex is OperationCanceledException ? "timeout" : ex.Message;
            }
        }

        var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown";
        var tunerRows = cfg.Tuners.Select((t, i) =>
            new TunerRow(i + 1, t.RtspPort, t.FrontendNumber, t.DiSEqCPort, t.SatelliteLabel)).ToArray();

        var channelRows = scan?.Channels
            .OrderBy(c => c.Number)
            .Take(10)
            .Select(c => new ChannelRow(c.Number, c.Name, c.IsHD, c.IsRadio, c.Mux.FrequencyMHz))
            .ToArray() ?? [];

        return Ok(new DetailedStatusResponse(
            PluginVersion: version,
            ServerAddress: cfg.ServerAddress,
            Tuners: tunerRows,
            DeviceReachable: reachable,
            DeviceReachableMs: reachableMs,
            DeviceReachableError: reachableError,
            HasChannels: scan is not null,
            ChannelCount: scan?.Channels.Count ?? 0,
            MuxCount: scan?.MuxCount ?? 0,
            RegionLabel: scan?.RegionLabel ?? cfg.RegionLabel,
            LastScanTime: scan?.ScannedAt ?? cfg.LastScanTime,
            TopChannels: channelRows,
            GeneratedUtc: DateTime.UtcNow.ToString("O")));
    }

    [HttpPost("rebuild-channels")]
    public ActionResult RebuildChannels()
    {
        _store.Invalidate();
        return Ok(new { message = "Channel store cleared. Trigger a new scan to repopulate." });
    }

    [HttpGet("xmltv-status")]
    public ActionResult<XmltvStatusResponse> GetXmltvStatus()
    {
        return Ok(new XmltvStatusResponse(
            _xmltvCache.LastFetchedUtc?.ToString("O"),
            _xmltvCache.LastError,
            _xmltvCache.MappedChannelCount,
            _xmltvCache.TotalProgramCount,
            _store.Current?.Channels.Count ?? 0));
    }

    [HttpPost("xmltv-refresh")]
    public ActionResult TriggerXmltvRefresh()
    {
        _xmltvCollector.RequestImmediateRefresh();
        return Ok(new { message = "XMLTV refresh requested — check status shortly." });
    }

    // ── Request / response types ────────────────────────────────────────────────

    public sealed record ResolveRegionResponse(
        [property: JsonPropertyName("regionKey")] string RegionKey,
        [property: JsonPropertyName("regionLabel")] string RegionLabel);

    public sealed record RegionListItem(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("label")] string Label);

    public sealed record ScanStatusResponse(
        [property: JsonPropertyName("hasChannels")] bool HasChannels,
        [property: JsonPropertyName("channelCount")] int ChannelCount,
        [property: JsonPropertyName("message")] string Message);

    public sealed record ScanProgressResponse(
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("channelCount")] int ChannelCount,
        [property: JsonPropertyName("percent")] int? Percent = null);

    public sealed record TunerRow(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("rtspPort")] int RtspPort,
        [property: JsonPropertyName("frontendNumber")] int FrontendNumber,
        [property: JsonPropertyName("diSEqCPort")] string DiSEqCPort,
        [property: JsonPropertyName("satelliteLabel")] string SatelliteLabel);

    public sealed record ChannelRow(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("isHD")] bool IsHD,
        [property: JsonPropertyName("isRadio")] bool IsRadio,
        [property: JsonPropertyName("frequencyMHz")] double FrequencyMHz);

    public sealed record DetailedStatusResponse(
        [property: JsonPropertyName("pluginVersion")] string PluginVersion,
        [property: JsonPropertyName("serverAddress")] string ServerAddress,
        [property: JsonPropertyName("tuners")] TunerRow[] Tuners,
        [property: JsonPropertyName("deviceReachable")] bool DeviceReachable,
        [property: JsonPropertyName("deviceReachableMs")] long DeviceReachableMs,
        [property: JsonPropertyName("deviceReachableError")] string DeviceReachableError,
        [property: JsonPropertyName("hasChannels")] bool HasChannels,
        [property: JsonPropertyName("channelCount")] int ChannelCount,
        [property: JsonPropertyName("muxCount")] int MuxCount,
        [property: JsonPropertyName("regionLabel")] string RegionLabel,
        [property: JsonPropertyName("lastScanTime")] string LastScanTime,
        [property: JsonPropertyName("topChannels")] ChannelRow[] TopChannels,
        [property: JsonPropertyName("generatedUtc")] string GeneratedUtc);

    public sealed class ScanRequest
    {
        public string ServerAddress { get; set; } = string.Empty;
        public List<TunerEntry> Tuners { get; set; } = [];
        public string RegionKey { get; set; } = string.Empty;
    }

    public sealed record PluginConfigResponse(
        [property: JsonPropertyName("serverAddress")] string ServerAddress,
        [property: JsonPropertyName("tuners")] List<TunerEntry> Tuners,
        [property: JsonPropertyName("postcode")] string Postcode,
        [property: JsonPropertyName("regionKey")] string RegionKey,
        [property: JsonPropertyName("regionLabel")] string RegionLabel,
        [property: JsonPropertyName("autoScanIntervalHours")] int AutoScanIntervalHours,
        [property: JsonPropertyName("enableStreamSharing")] bool EnableStreamSharing,
        [property: JsonPropertyName("rtpReceiveBufferKiB")] int RtpReceiveBufferKiB,
        [property: JsonPropertyName("packetTimeoutSeconds")] int PacketTimeoutSeconds,
        [property: JsonPropertyName("exposeSubtitleStreams")] bool ExposeSubtitleStreams,
        [property: JsonPropertyName("preferredSubtitleLanguage")] string PreferredSubtitleLanguage,
        [property: JsonPropertyName("forceDeinterlace")] bool ForceDeinterlace,
        [property: JsonPropertyName("enableXmltvEpg")] bool EnableXmltvEpg,
        [property: JsonPropertyName("xmltvUrl")] string XmltvUrl,
        [property: JsonPropertyName("xmltvRefreshHours")] int XmltvRefreshHours);

    public sealed class PluginConfigRequest
    {
        [JsonPropertyName("serverAddress")] public string? ServerAddress { get; set; }
        [JsonPropertyName("tuners")] public List<TunerEntry>? Tuners { get; set; }
        [JsonPropertyName("postcode")] public string? Postcode { get; set; }
        [JsonPropertyName("regionKey")] public string? RegionKey { get; set; }
        [JsonPropertyName("regionLabel")] public string? RegionLabel { get; set; }
        [JsonPropertyName("autoScanIntervalHours")] public int? AutoScanIntervalHours { get; set; }
        [JsonPropertyName("enableStreamSharing")] public bool? EnableStreamSharing { get; set; }
        [JsonPropertyName("rtpReceiveBufferKiB")] public int? RtpReceiveBufferKiB { get; set; }
        [JsonPropertyName("packetTimeoutSeconds")] public int? PacketTimeoutSeconds { get; set; }
        [JsonPropertyName("exposeSubtitleStreams")] public bool? ExposeSubtitleStreams { get; set; }
        [JsonPropertyName("preferredSubtitleLanguage")] public string? PreferredSubtitleLanguage { get; set; }
        [JsonPropertyName("forceDeinterlace")] public bool? ForceDeinterlace { get; set; }
        [JsonPropertyName("enableXmltvEpg")] public bool? EnableXmltvEpg { get; set; }
        [JsonPropertyName("xmltvUrl")] public string? XmltvUrl { get; set; }
        [JsonPropertyName("xmltvRefreshHours")] public int? XmltvRefreshHours { get; set; }
    }

    public sealed record XmltvStatusResponse(
        [property: JsonPropertyName("lastFetchedUtc")] string? LastFetchedUtc,
        [property: JsonPropertyName("lastError")] string? LastError,
        [property: JsonPropertyName("mappedChannelCount")] int MappedChannelCount,
        [property: JsonPropertyName("totalProgramCount")] int TotalProgramCount,
        [property: JsonPropertyName("totalChannelCount")] int TotalChannelCount);
}
