using System;
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
    private readonly ILogger<FreesatEpgProvider> _logger;
    private readonly FreesatChannelStore _store;

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

        return await CollectEitAsync(cfg, channel, startDateUtc, endDateUtc, ct).ConfigureAwait(false);
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

    private async Task<List<ProgramInfo>> CollectEitAsync(
        PluginConfiguration cfg, FreesatChannel channel,
        DateTime startDate, DateTime endDate, CancellationToken ct)
    {
        var programs = new List<ProgramInfo>();

        var primary = cfg.PrimaryTuner;
        var muxParams = new SatIpMuxParams
        {
            FrontendNumber = primary.FrontendNumber,
            FrequencyMHz = channel.Mux.FrequencyMHz,
            Polarization = char.ToLowerInvariant(channel.Mux.Polarization),
            SymbolRateKsym = channel.Mux.SymbolRateKsym,
            IsDvbS2 = channel.Mux.IsDvbS2,
        };

        try
        {
            await using var client = new RtspClient(cfg.ServerAddress, primary.RtspPort, _logger);
            await client.ConnectAsync(ct).ConfigureAwait(false);
            await client.SetupAndPlayAsync(muxParams, "18", ct).ConfigureAwait(false);

            var reader = new TsReader();
            reader.SubscribePid(EitParser.PidEit);
            reader.SectionReady += (_, section) =>
            {
                if (!EitParser.IsEitTableId(section[0])) return;
                var evts = EitParser.Parse(section, (onid, tsid, sid) => $"{onid}-{tsid}-{sid}");
                programs.AddRange(evts.Where(p =>
                    p.ChannelId == channel.ChannelId &&
                    p.EndDate > startDate &&
                    p.StartDate < endDate));
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var payload = await client.ReadRtpPacketAsync(cts.Token).ConfigureAwait(false);
                    if (payload is null) break;
                    reader.Feed(payload);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

            await client.TeardownAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SAT>IP EPG: failed to collect EIT for channel {Id}", channel.ChannelId);
        }

        return programs;
    }
}
