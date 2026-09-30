using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SatIpFreesat.Configuration;
using Jellyfin.Plugin.SatIpFreesat.DvbSi;
using Jellyfin.Plugin.SatIpFreesat.SatIp;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SatIpFreesat.Freesat;

public sealed class FreesatEpgProvider : IListingsProvider
{
    // Real EIT data comes from FreesatEpgCollectorService's background passes (multi-minute
    // dwell per mux, needed to catch the slow-repeating multi-day EIT "schedule" sections —
    // see that class for why). This provider only does its own short (45s) on-demand
    // collection as a first-look fallback for a mux the background collector hasn't reached
    // yet, so a freshly scanned channel isn't left with a completely empty guide while it
    // waits its turn — the background pass overwrites it with much fuller data later.
    private static readonly TimeSpan FallbackCollectTimeout = TimeSpan.FromSeconds(45);

    private readonly ILogger<FreesatEpgProvider> _logger;
    private readonly FreesatChannelStore _store;
    private readonly FreesatEpgCache _cache;
    private readonly XmltvEpgCache _xmltvCache;
    private readonly ConcurrentDictionary<string, Lazy<Task<List<ProgramInfo>>>> _fallbackInFlight = new();

    public string Name => "SAT>IP Freesat EPG";
    public string Type => "satip-freesat";

    public FreesatEpgProvider(
        ILogger<FreesatEpgProvider> logger, FreesatChannelStore store, FreesatEpgCache cache, XmltvEpgCache xmltvCache)
    {
        _logger = logger;
        _store = store;
        _cache = cache;
        _xmltvCache = xmltvCache;
    }

    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        ListingsProviderInfo info,
        string channelId,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrEmpty(cfg.ServerAddress)) return [];

        var scan = _store.Current;
        if (scan is null) return [];

        var channel = scan.Channels.FirstOrDefault(c => c.ChannelId == channelId);
        if (channel is null) return [];

        var muxKey = channel.Mux.Key;
        List<ProgramInfo> muxPrograms;
        if (_cache.TryGet(muxKey, out var cached))
        {
            muxPrograms = cached;
        }
        else
        {
            muxPrograms = await GetOrRunFallbackCollectAsync(cfg, scan, channel.Mux, muxKey, ct)
                .ConfigureAwait(false);
        }

        var eitPrograms = muxPrograms.Where(p => p.ChannelId == channelId);

        // OTA EIT only ever covers a few hours out on this platform (present/following) —
        // see FreesatEpgCollectorService and XmltvEpgSource for why. Fill everything beyond
        // that with the XMLTV-sourced cache, preferring EIT wherever both cover the same
        // slot since it reflects this specific broadcast (last-minute schedule changes etc.).
        var merged = eitPrograms.ToList();
        if (_xmltvCache.TryGet(channelId, out var xmltvPrograms))
        {
            foreach (var p in xmltvPrograms)
            {
                bool overlapsEit = merged.Exists(e => e.StartDate < p.EndDate && e.EndDate > p.StartDate);
                if (!overlapsEit) merged.Add(p);
            }
        }

        return merged
            .Where(p => p.EndDate > startDateUtc && p.StartDate < endDateUtc)
            .OrderBy(p => p.StartDate)
            .ToList();
    }

    public Task<List<ChannelInfo>> GetChannels(ListingsProviderInfo info, CancellationToken ct)
    {
        var scan = _store.Current;
        if (scan is null) return Task.FromResult(new List<ChannelInfo>());

        var channels = scan.Channels.Select(ch => new ChannelInfo
        {
            Id = ch.ChannelId,
            Name = ch.Name,
            Number = ch.Number.ToString(),
        }).ToList();
        return Task.FromResult(channels);
    }

    public Task Validate(ListingsProviderInfo info, bool validateLogin, bool validateListings)
        => Task.CompletedTask;

    public Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location)
        => Task.FromResult(new List<NameIdPair> { new() { Name = "Freesat (28.2E)", Id = "freesat-282e" } });

    // ---- private ----

    /// <summary>
    /// De-duplicates concurrent fallback collections for the same mux (Jellyfin's guide
    /// refresh often calls GetProgramsAsync for several channels on the same mux back to
    /// back) so only one RTSP session actually gets opened per cache-miss mux.
    /// </summary>
    private Task<List<ProgramInfo>> GetOrRunFallbackCollectAsync(
        PluginConfiguration cfg, ScanResult scan, MuxInfo mux, string muxKey, CancellationToken ct)
    {
        if (_fallbackInFlight.TryGetValue(muxKey, out var existing))
            return UnwrapAsync(existing, muxKey);

        var lazy = new Lazy<Task<List<ProgramInfo>>>(() => CollectEitForMuxAsync(cfg, scan, mux, ct));
        lazy = _fallbackInFlight.GetOrAdd(muxKey, lazy);
        return UnwrapAsync(lazy, muxKey);
    }

    private async Task<List<ProgramInfo>> UnwrapAsync(Lazy<Task<List<ProgramInfo>>> lazy, string muxKey)
    {
        try
        {
            var result = await lazy.Value.ConfigureAwait(false);
            _cache.Set(muxKey, result);
            return result;
        }
        finally
        {
            _fallbackInFlight.TryRemove(muxKey, out _);
        }
    }

    private async Task<List<ProgramInfo>> CollectEitForMuxAsync(
        PluginConfiguration cfg, ScanResult scan, MuxInfo mux, CancellationToken ct)
    {
        var programs = new List<ProgramInfo>();
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
        bool useUdp = scan.UseUdpTransport;
        try
        {
            await using var client = new RtspClient(cfg.ServerAddress, primary.RtspPort, _logger);
            if (useUdp) client.EnableUdpTransport();
            await client.ConnectAsync(ct).ConfigureAwait(false);
            await client.SetupAndPlayAsync(muxParams, "18", ct).ConfigureAwait(false);

            var channelIds = scan.Channels
                .Where(c => c.Mux.Key == mux.Key)
                .Select(c => c.ChannelId)
                .ToHashSet();

            var reader = new TsReader();
            reader.SubscribePid(EitParser.PidEit);
            reader.SectionReady += (_, section) =>
            {
                if (!EitParser.IsEitTableId(section[0])) return;
                var evts = EitParser.Parse(section, (onid, tsid, sid) => $"{onid}-{tsid}-{sid}");
                programs.AddRange(evts.Where(p => p.ChannelId is not null && channelIds.Contains(p.ChannelId)));
            };

            await FreesatScanner.ReadStreamAsync(client, reader, FallbackCollectTimeout, ct, _logger)
                .ConfigureAwait(false);

            await client.TeardownAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SAT>IP EPG: fallback collection failed for mux {Mux}", mux.Key);
        }

        return programs;
    }
}
