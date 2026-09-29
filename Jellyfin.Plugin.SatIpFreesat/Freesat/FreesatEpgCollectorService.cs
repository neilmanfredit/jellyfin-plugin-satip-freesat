using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SatIpFreesat.Configuration;
using Jellyfin.Plugin.SatIpFreesat.DvbSi;
using Jellyfin.Plugin.SatIpFreesat.SatIp;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SatIpFreesat.Freesat;

/// <summary>
/// Background service that continuously walks every mux, tuning to PID 18 (EIT) for a long
/// dwell time on each, and stores results into <see cref="FreesatEpgCache"/>.
///
/// Why this exists: DVB EIT "schedule" sub-tables (the ones carrying events several days out,
/// as opposed to EIT present/following which only covers now+next) repeat far less often than
/// near-term sections — broadcasters are only required to repeat far-future segments every
/// several minutes, not every few seconds like present/following. A short (45s) collection
/// window, which is all Jellyfin's synchronous guide-refresh call can afford across ~25 muxes,
/// only ever catches present/following and the nearest schedule segments — which is exactly
/// the "only a few hours of shows" symptom this was built to fix. Running collection in the
/// background with a multi-minute dwell per mux, decoupled from the synchronous
/// IListingsProvider.GetProgramsAsync call Jellyfin makes during a guide refresh, lets us
/// afford that dwell time without blocking (or timing out) the guide refresh itself —
/// GetProgramsAsync just reads whatever's already in the shared cache.
/// </summary>
public sealed class FreesatEpgCollectorService : IHostedService, IDisposable
{
    private static readonly TimeSpan MuxDwellTime = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan CycleRestPeriod = TimeSpan.FromHours(4);

    private readonly FreesatChannelStore _store;
    private readonly FreesatEpgCache _cache;
    private readonly ILogger<FreesatEpgCollectorService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public FreesatEpgCollectorService(
        FreesatChannelStore store, FreesatEpgCache cache, ILogger<FreesatEpgCollectorService> logger)
    {
        _store = store;
        _cache = cache;
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
        // Let ScanSchedulerService's own startup work (and any in-progress scan) settle first.
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var cfg = Plugin.Instance?.Configuration;
            var scan = _store.Current;

            if (cfg is null || string.IsNullOrEmpty(cfg.ServerAddress) || scan is null)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            var muxGroups = scan.Channels
                .GroupBy(c => c.Mux.Key)
                .Select(g => (Mux: g.First().Mux, ChannelIds: g.Select(c => c.ChannelId).ToHashSet()))
                .ToList();

            _logger.LogInformation(
                "SAT>IP EPG: starting background EIT collection pass — {Count} muxes, {Dwell}s dwell each",
                muxGroups.Count, MuxDwellTime.TotalSeconds);

            foreach (var (mux, channelIds) in muxGroups)
            {
                if (ct.IsCancellationRequested) break;

                // Config/scan can change mid-pass (rescan, edits) — re-read every iteration
                // rather than trusting the snapshot taken at the top of the loop.
                var currentCfg = Plugin.Instance?.Configuration;
                if (currentCfg is null || string.IsNullOrEmpty(currentCfg.ServerAddress)) break;

                try
                {
                    var programs = await CollectEitForMuxAsync(currentCfg, scan, mux, channelIds, ct)
                        .ConfigureAwait(false);
                    _cache.Set(mux.Key, programs);
                    _logger.LogInformation(
                        "SAT>IP EPG: mux {Mux} — collected {Count} programs", mux.Key, programs.Count);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SAT>IP EPG: background collection failed for mux {Mux}", mux.Key);
                }
            }

            try { await Task.Delay(CycleRestPeriod, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<List<ProgramInfo>> CollectEitForMuxAsync(
        PluginConfiguration cfg, ScanResult scan, MuxInfo mux, HashSet<string> channelIds, CancellationToken ct)
    {
        var programs = new List<ProgramInfo>();
        var seenEventIds = new HashSet<string>();
        var primary = cfg.PrimaryTuner;

        var muxParams = new SatIpMuxParams
        {
            FrontendNumber = primary.FrontendNumber,
            FrequencyMHz = mux.FrequencyMHz,
            Polarization = char.ToLowerInvariant(mux.Polarization),
            SymbolRateKsym = mux.SymbolRateKsym,
            IsDvbS2 = mux.IsDvbS2,
            ModulationType = mux.ModulationType,
        };

        await using var client = new RtspClient(cfg.ServerAddress, primary.RtspPort, _logger);
        if (scan.UseUdpTransport) client.EnableUdpTransport();
        await client.ConnectAsync(ct).ConfigureAwait(false);
        await client.SetupAndPlayAsync(muxParams, "18", ct).ConfigureAwait(false);

        var reader = new TsReader();
        reader.SubscribePid(EitParser.PidEit);
        reader.SectionReady += (_, section) =>
        {
            if (!EitParser.IsEitTableId(section[0])) return;
            var evts = EitParser.Parse(section, (onid, tsid, sid) => $"{onid}-{tsid}-{sid}");
            foreach (var p in evts)
            {
                if (p.ChannelId is null || !channelIds.Contains(p.ChannelId)) continue;
                if (p.Id is not null && seenEventIds.Add(p.Id)) programs.Add(p);
            }
        };

        await FreesatScanner.ReadStreamAsync(client, reader, MuxDwellTime, ct, _logger).ConfigureAwait(false);
        await client.TeardownAsync(ct).ConfigureAwait(false);

        return programs;
    }
}
