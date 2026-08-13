using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
    private readonly ILogger<SatIpController> _logger;

    // Shared across all controller instances (controllers are transient)
    private static readonly ScanJobTracker _scanJob = new();

    public SatIpController(
        FreesatScanner scanner,
        FreesatChannelStore store,
        ILogger<SatIpController> logger)
    {
        _scanner = scanner;
        _store = store;
        _logger = logger;
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

        return Accepted(_scanJob.GetProgress());
    }

    [HttpGet("scan/progress")]
    public ActionResult<ScanProgressResponse> GetScanProgress()
    {
        var prog = _scanJob.GetProgress();

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

    // ── Scan job tracker ────────────────────────────────────────────────────────

    private sealed class ScanJobTracker
    {
        private string _state = "idle";
        private string _message = "No scan run yet — click Scan to begin";
        private int _channelCount;
        private int? _percent;
        private readonly object _lock = new();

        public bool TryStart()
        {
            lock (_lock)
            {
                if (_state == "scanning") return false;
                _state = "scanning";
                _message = "Starting scan…";
                _channelCount = 0;
                _percent = null;
                return true;
            }
        }

        public void Update(string message, int? percent) { lock (_lock) { _message = message; _percent = percent; } }

        public void Complete(int count, string message)
        {
            lock (_lock) { _state = "done"; _channelCount = count; _message = message; _percent = 100; }
        }

        public void Fail(string message)
        {
            lock (_lock) { _state = "failed"; _message = message; _percent = null; }
        }

        public ScanProgressResponse GetProgress()
        {
            lock (_lock) { return new ScanProgressResponse(_state, _message, _channelCount, _percent); }
        }
    }

    // ── Request / response types ────────────────────────────────────────────────

    public sealed record ResolveRegionResponse(string RegionKey, string RegionLabel);
    public sealed record RegionListItem(string Key, string Label);
    public sealed record ScanStatusResponse(bool HasChannels, int ChannelCount, string Message);
    public sealed record ScanProgressResponse(string State, string Message, int ChannelCount, int? Percent = null);
    public sealed record TunerRow(int Index, int RtspPort, int FrontendNumber, string DiSEqCPort, string SatelliteLabel);
    public sealed record ChannelRow(int Number, string Name, bool IsHD, bool IsRadio, double FrequencyMHz);

    public sealed record DetailedStatusResponse(
        string PluginVersion,
        string ServerAddress,
        TunerRow[] Tuners,
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
        public List<TunerEntry> Tuners { get; set; } = [];
        public string RegionKey { get; set; } = string.Empty;
    }
}
