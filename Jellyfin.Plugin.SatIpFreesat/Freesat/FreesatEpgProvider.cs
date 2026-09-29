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

/// <summary>
/// Jellyfin IListingsProvider implementation.
/// Reads DVB EIT tables from the SAT>IP stream to build program guide data.
/// </summary>
public sealed class FreesatEpgProvider : IListingsProvider
{
    // EIT (PID 18) is broadcast per-transponder — a single tuned session on any channel's mux
    // carries schedule data for every channel sharing that mux, not just the one requested.
    // Jellyfin's guide refresh calls GetProgramsAsync once per CHANNEL though, and the naive
    // per-channel implementation this replaced re-tuned and re-collected a fresh 45s RTSP
    // session for every single one — for ~150-200 Freesat channels across ~8-10 transponders,
    // that's 15-20x more RTSP session churn than the data requires. Confirmed against
    // production logs: a full refresh took 46 minutes and ended with every remaining channel
    // failing "Connection refused" — minisatip's session table got exhausted by the sheer
    // volume of short-lived sessions, on top of intermittent contention with concurrently
    // active live playback (both paths independently pick a tuner with no shared coordination).
    // Caching one collection per mux, keyed by MuxInfo.Key (onid-tsid), turns that into ~8-10
    // sessions per refresh instead of ~150-200.
    private static readonly TimeSpan MuxCacheLifetime = TimeSpan.FromHours(2);

    private readonly ILogger<FreesatEpgProvider> _logger;
    private readonly FreesatChannelStore _store;
    private readonly ConcurrentDictionary<string, Lazy<Task<MuxEitResult>>> _muxCache = new();

    public string Name => "SAT>IP Freesat EPG";
    public string Type => "satip-freesat";

    public FreesatEpgProvider(ILogger<FreesatEpgProvider> logger, FreesatChannelStore store)
    {
        _logger = logger;
        _store = store;
    }

    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        ListingsProviderInfo info,
        string channelId,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrEmpty(cfg.ServerAddress))
            return [];

        var scan = _store.Current;
        if (scan is null) return [];

        var channel = scan.Channels.FirstOrDefault(c => c.ChannelId == channelId);
        if (channel is null) return [];

        var muxPrograms = await GetOrCollectMuxAsync(cfg, scan, channel.Mux, ct).ConfigureAwait(false);
        return muxPrograms
            .Where(p => p.ChannelId == channelId && p.EndDate > startDateUtc && p.StartDate < endDateUtc)
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
    /// Returns every program on <paramref name="mux"/>'s transponder (all channels), collecting
    /// via RTSP only on a cache miss. A <see cref="Lazy{T}"/> per mux key ensures that if
    /// Jellyfin's guide refresh calls GetProgramsAsync for several channels on the same mux
    /// concurrently (the common case — refreshes iterate channels, and consecutive channels
    /// are often on the same transponder), only one RTSP session actually gets opened; the
    /// rest await the same in-flight collection instead of each starting their own.
    /// </summary>
    private Task<List<ProgramInfo>> GetOrCollectMuxAsync(
        PluginConfiguration cfg, ScanResult scan, MuxInfo mux, CancellationToken ct)
    {
        var muxKey = mux.Key;

        if (_muxCache.TryGetValue(muxKey, out var existing))
        {
            return AwaitFreshOrRecollectAsync(cfg, scan, mux, muxKey, existing, ct);
        }

        var lazy = new Lazy<Task<MuxEitResult>>(() => CollectEitForMuxAsync(cfg, scan, mux, ct));
        lazy = _muxCache.GetOrAdd(muxKey, lazy);
        return UnwrapAsync(lazy, ct);
    }

    private async Task<List<ProgramInfo>> AwaitFreshOrRecollectAsync(
        PluginConfiguration cfg, ScanResult scan, MuxInfo mux, string muxKey,
        Lazy<Task<MuxEitResult>> existing, CancellationToken ct)
    {
        var result = await existing.Value.ConfigureAwait(false);
        if (DateTime.UtcNow - result.CollectedAtUtc < MuxCacheLifetime)
            return result.Programs;

        // Stale — replace only if nobody else already did (avoid duplicate re-collection from
        // concurrent callers who both saw the same stale entry).
        var fresh = new Lazy<Task<MuxEitResult>>(() => CollectEitForMuxAsync(cfg, scan, mux, ct));
        var winner = _muxCache.AddOrUpdate(muxKey, fresh, (_, current) => current == existing ? fresh : current);
        return await UnwrapAsync(winner, ct).ConfigureAwait(false);
    }

    private static async Task<List<ProgramInfo>> UnwrapAsync(Lazy<Task<MuxEitResult>> lazy, CancellationToken ct)
    {
        var result = await lazy.Value.ConfigureAwait(false);
        return result.Programs;
    }

    private async Task<MuxEitResult> CollectEitForMuxAsync(
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

        // Use the same transport (TCP vs UDP) the scan detected for this device.
        // If the device requires UDP (TCP delivers no data), TCP collection silently
        // times out after 45 s and returns nothing.
        bool useUdp = scan.UseUdpTransport;

        try
        {
            await using var client = new RtspClient(cfg.ServerAddress, primary.RtspPort, _logger);
            if (useUdp) client.EnableUdpTransport();
            await client.ConnectAsync(ct).ConfigureAwait(false);
            await client.SetupAndPlayAsync(muxParams, "18", ct).ConfigureAwait(false);

            // All channels on this mux, so EIT sections for any of them get kept.
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

            // Use the shared ReadStreamAsync helper so keep-alives fire during the 45-second
            // collection window — without them the session dies at the device's 30-second
            // timeout and we get no 8-day schedule data.
            await FreesatScanner.ReadStreamAsync(client, reader, TimeSpan.FromSeconds(45), ct, _logger)
                .ConfigureAwait(false);

            await client.TeardownAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SAT>IP EPG: failed to collect EIT for mux {Mux}", mux.Key);
        }

        return new MuxEitResult(DateTime.UtcNow, programs);
    }

    private sealed record MuxEitResult(DateTime CollectedAtUtc, List<ProgramInfo> Programs);
}
