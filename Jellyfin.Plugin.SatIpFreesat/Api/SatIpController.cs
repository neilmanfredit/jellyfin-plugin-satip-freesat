using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SatIpFreesat.Freesat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SatIpFreesat.Api;

/// <summary>
/// REST endpoints called by the plugin configuration and status pages.
/// All routes are under /SatIpFreesat/.
/// </summary>
[ApiController]
[Route("SatIpFreesat")]
[Authorize(Policy = "RequiresElevation")]
public sealed class SatIpController : ControllerBase
{
    private readonly FreesatScanner _scanner;
    private readonly FreesatChannelStore _store;
    private readonly ILogger<SatIpController> _logger;

    public SatIpController(
        FreesatScanner scanner,
        FreesatChannelStore store,
        ILogger<SatIpController> logger)
    {
        _scanner = scanner;
        _store = store;
        _logger = logger;
    }

    /// <summary>Resolve a postcode to a region key + label.</summary>
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

    /// <summary>Return all known region keys and labels.</summary>
    [HttpGet("regions")]
    public ActionResult<RegionListItem[]> GetRegions()
    {
        var items = RegionData.Regions
            .Select(kv => new RegionListItem(kv.Key, kv.Value.Label))
            .OrderBy(r => r.Label)
            .ToArray();
        return Ok(items);
    }

    /// <summary>Trigger a full channel scan. Runs synchronously; may take several minutes.</summary>
    [HttpPost("scan")]
    public async Task<ActionResult<ScanStatusResponse>> TriggerScan(
        [FromBody] ScanRequest request,
        CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null) return StatusCode((int)HttpStatusCode.InternalServerError, "Plugin not loaded");

        if (string.IsNullOrWhiteSpace(request.ServerAddress))
            return BadRequest("serverAddress required");
        if (string.IsNullOrWhiteSpace(request.RegionKey) || !RegionData.Regions.ContainsKey(request.RegionKey))
            return BadRequest("valid regionKey required");

        cfg.ServerAddress = request.ServerAddress;
        cfg.RtspPort = request.RtspPort > 0 ? request.RtspPort : 554;
        cfg.FrontendNumber = request.FrontendNumber > 0 ? request.FrontendNumber : 1;
        cfg.TunerCount = request.TunerCount > 0 ? request.TunerCount : 1;
        cfg.RegionKey = request.RegionKey;
        cfg.RegionLabel = RegionData.Regions[request.RegionKey].Label;
        Plugin.Instance!.SaveConfiguration();

        try
        {
            var result = await _scanner.ScanAsync(
                cfg.ServerAddress, cfg.RtspPort, cfg.FrontendNumber,
                cfg.RegionKey, ct).ConfigureAwait(false);

            cfg.LastScanTime = DateTime.UtcNow.ToString("O");
            Plugin.Instance!.SaveConfiguration();

            return Ok(new ScanStatusResponse(
                true, result.Channels.Count,
                $"Scan complete: {result.Channels.Count} channels in {result.RegionLabel}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "SAT>IP scan failed");
            return StatusCode((int)HttpStatusCode.InternalServerError, ex.Message);
        }
    }

    /// <summary>Return current scan status and channel count.</summary>
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

    /// <summary>Return rich status for the status page: device reachability, scan details, plugin info.</summary>
    [HttpGet("detailed-status")]
    public async Task<ActionResult<DetailedStatusResponse>> GetDetailedStatus(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null)
            return StatusCode((int)HttpStatusCode.InternalServerError, "Plugin not loaded");

        var scan = _store.Current;

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
                await tcp.ConnectAsync(cfg.ServerAddress, cfg.RtspPort, probeCts.Token).ConfigureAwait(false);
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

        var channelRows = scan?.Channels
            .OrderBy(c => c.Number)
            .Take(10)
            .Select(c => new ChannelRow(c.Number, c.Name, c.IsHD, c.IsRadio, c.Mux.FrequencyMHz))
            .ToArray() ?? [];

        var response = new DetailedStatusResponse(
            PluginVersion: version,
            ServerAddress: cfg.ServerAddress,
            RtspPort: cfg.RtspPort,
            FrontendNumber: cfg.FrontendNumber,
            TunerCount: cfg.TunerCount,
            DeviceReachable: reachable,
            DeviceReachableMs: reachableMs,
            DeviceReachableError: reachableError,
            HasChannels: scan is not null,
            ChannelCount: scan?.Channels.Count ?? 0,
            MuxCount: scan?.MuxCount ?? 0,
            RegionLabel: scan?.RegionLabel ?? cfg.RegionLabel,
            LastScanTime: scan?.ScannedAt ?? cfg.LastScanTime,
            TopChannels: channelRows,
            GeneratedUtc: DateTime.UtcNow.ToString("O"));

        return Ok(response);
    }

    /// <summary>Invalidate the channel store so the next scan replaces it.</summary>
    [HttpPost("rebuild-channels")]
    public ActionResult RebuildChannels()
    {
        _store.Invalidate();
        return Ok(new { message = "Channel store cleared. Trigger a new scan to repopulate." });
    }

    // ── Request / response types ────────────────────────────────────────────

    public sealed record ResolveRegionResponse(string RegionKey, string RegionLabel);
    public sealed record RegionListItem(string Key, string Label);
    public sealed record ScanStatusResponse(bool HasChannels, int ChannelCount, string Message);

    public sealed record ChannelRow(int Number, string Name, bool IsHD, bool IsRadio, double FrequencyMHz);

    public sealed record DetailedStatusResponse(
        string PluginVersion,
        string ServerAddress,
        int RtspPort,
        int FrontendNumber,
        int TunerCount,
        bool DeviceReachable,
        long DeviceReachableMs,
        string DeviceReachableError,
        bool HasChannels,
        int ChannelCount,
        int MuxCount,
        string RegionLabel,
        string LastScanTime,
        ChannelRow[] TopChannels,
        string GeneratedUtc);

    public sealed class ScanRequest
    {
        public string ServerAddress { get; set; } = string.Empty;
        public int RtspPort { get; set; } = 554;
        public int FrontendNumber { get; set; } = 1;
        public int TunerCount { get; set; } = 1;
        public string RegionKey { get; set; } = string.Empty;
    }
}
