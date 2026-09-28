using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SatIpFreesat.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SatIpFreesat.Freesat;

/// <summary>
/// Background service that triggers a channel re-scan automatically based on
/// <see cref="PluginConfiguration.AutoScanIntervalHours"/>.
/// Coordinates with the manual-scan path via <see cref="ScanJobService.TryStart"/>
/// so that auto and manual scans never run concurrently.
/// </summary>
public sealed class ScanSchedulerService : IHostedService, IDisposable
{
    private readonly FreesatScanner _scanner;
    private readonly ScanJobService _scanJob;
    private readonly ILogger<ScanSchedulerService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public ScanSchedulerService(
        FreesatScanner scanner,
        ScanJobService scanJob,
        ILogger<ScanSchedulerService> logger)
    {
        _scanner = scanner;
        _scanJob = scanJob;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loopTask is not null)
        {
            try { await _loopTask.WaitAsync(ct).ConfigureAwait(false); }
            catch { /* shutdown — suppress */ }
        }
    }

    public void Dispose() => _cts?.Dispose();

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Give Jellyfin time to fully initialize before checking the schedule.
        try { await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var cfg = Plugin.Instance?.Configuration;

            if (cfg is null || cfg.AutoScanIntervalHours <= 0 ||
                string.IsNullOrEmpty(cfg.ServerAddress) || string.IsNullOrEmpty(cfg.RegionKey))
            {
                // Auto-scan not configured; re-check in 1 h in case the user enables it.
                try { await Task.Delay(TimeSpan.FromHours(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            var nextScan = NextScanTime(cfg);
            var wait = nextScan - DateTime.UtcNow;

            if (wait > TimeSpan.Zero)
            {
                // Cap individual sleeps at 1 h so config changes take effect promptly.
                var sleep = wait < TimeSpan.FromHours(1) ? wait : TimeSpan.FromHours(1);
                try { await Task.Delay(sleep, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            await RunScanAsync(ct).ConfigureAwait(false);
        }
    }

    private static DateTime NextScanTime(PluginConfiguration cfg)
    {
        if (string.IsNullOrEmpty(cfg.LastScanTime) ||
            !DateTime.TryParse(cfg.LastScanTime, null, DateTimeStyles.RoundtripKind, out var last))
            return DateTime.UtcNow; // never scanned — due immediately

        return last.AddHours(cfg.AutoScanIntervalHours);
    }

    private async Task RunScanAsync(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrEmpty(cfg.ServerAddress) || string.IsNullOrEmpty(cfg.RegionKey))
            return;

        if (!_scanJob.TryStart())
        {
            _logger.LogInformation("SAT>IP: auto-scan skipped — a scan is already in progress");
            return;
        }

        var primary = cfg.PrimaryTuner;
        _logger.LogInformation(
            "SAT>IP: starting scheduled auto-scan (interval={Hours}h, server={Host}, region={Region})",
            cfg.AutoScanIntervalHours, cfg.ServerAddress, cfg.RegionKey);

        try
        {
            var progress = new Progress<ScanProgress>(p => _scanJob.Update(p.Message, p.Percent));
            var result = await _scanner.ScanAsync(
                cfg.ServerAddress, primary.RtspPort, primary.FrontendNumber, cfg.RegionKey,
                progress, ct).ConfigureAwait(false);

            var pluginCfg = Plugin.Instance?.Configuration;
            if (pluginCfg is not null)
            {
                pluginCfg.LastScanTime = DateTime.UtcNow.ToString("O");
                Plugin.Instance!.SaveConfiguration();
            }

            _scanJob.Complete(result.Channels.Count,
                $"Auto-scan complete — {result.Channels.Count} channels in {result.RegionLabel}");
            _logger.LogInformation("SAT>IP: auto-scan complete — {Count} channels", result.Channels.Count);
        }
        catch (OperationCanceledException)
        {
            _scanJob.Fail("Auto-scan cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SAT>IP: auto-scan failed");
            _scanJob.Fail($"Auto-scan failed: {ex.Message}");
        }
    }
}
