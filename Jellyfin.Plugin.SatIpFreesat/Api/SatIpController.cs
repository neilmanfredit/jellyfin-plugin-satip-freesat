using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SatIpFreesat.Freesat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SatIpFreesat.Api;

/// <summary>
/// REST endpoints called by the plugin configuration page.
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

    /// <summary>Trigger a full channel scan. Runs asynchronously; poll /status for completion.</summary>
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

        // Save config first
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

    public sealed record ResolveRegionResponse(string RegionKey, string RegionLabel);
    public sealed record RegionListItem(string Key, string Label);
    public sealed record ScanStatusResponse(bool HasChannels, int ChannelCount, string Message);

    public sealed class ScanRequest
    {
        public string ServerAddress { get; set; } = string.Empty;
        public int RtspPort { get; set; } = 554;
        public int FrontendNumber { get; set; } = 1;
        public int TunerCount { get; set; } = 1;
        public string RegionKey { get; set; } = string.Empty;
    }
}
