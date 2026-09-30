using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SatIpFreesat.Freesat;

/// <summary>
/// Background service that periodically downloads and parses a third-party XMLTV guide
/// (see <see cref="XmltvEpgSource"/>) and stores the result in <see cref="XmltvEpgCache"/>.
/// </summary>
public sealed class XmltvEpgCollectorService : IHostedService, IDisposable
{
    private readonly FreesatChannelStore _store;
    private readonly XmltvEpgCache _cache;
    private readonly XmltvEpgSource _source;
    private readonly ILogger<XmltvEpgCollectorService> _logger;

    // Lets the "Refresh now" button on the config page wake the loop early without waiting
    // out the configured refresh interval.
    private readonly SemaphoreSlim _wake = new(0);

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public XmltvEpgCollectorService(
        FreesatChannelStore store, XmltvEpgCache cache, XmltvEpgSource source,
        ILogger<XmltvEpgCollectorService> logger)
    {
        _store = store;
        _cache = cache;
        _source = source;
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

    public void Dispose()
    {
        _cts?.Dispose();
        _wake.Dispose();
    }

    /// <summary>Wakes the background loop to refresh immediately, bypassing the sleep interval.</summary>
    public void RequestImmediateRefresh()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var cfg = Plugin.Instance?.Configuration;
            var scan = _store.Current;

            if (cfg is { EnableXmltvEpg: true } && !string.IsNullOrWhiteSpace(cfg.XmltvUrl) && scan is not null)
            {
                try
                {
                    var result = await _source.FetchAsync(cfg.XmltvUrl, scan.Channels, ct).ConfigureAwait(false);
                    _cache.ReplaceAll(result.ProgramsByChannelId, result.MappedChannelCount);
                    _logger.LogInformation(
                        "SAT>IP EPG (XMLTV): refreshed — {Mapped}/{Total} channels mapped, {Progs} programs",
                        result.MappedChannelCount, scan.Channels.Count, result.TotalPrograms);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _cache.SetError(ex.Message);
                    _logger.LogWarning(ex, "SAT>IP EPG (XMLTV): refresh failed");
                }
            }

            var delayHours = Math.Max(1, cfg?.XmltvRefreshHours ?? 8);
            try
            {
                var wakeTask = _wake.WaitAsync(ct);
                await Task.WhenAny(Task.Delay(TimeSpan.FromHours(delayHours), ct), wakeTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
        }
    }
}
