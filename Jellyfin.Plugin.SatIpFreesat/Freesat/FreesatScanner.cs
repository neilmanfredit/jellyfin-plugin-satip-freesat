using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SatIpFreesat.DvbSi;
using Jellyfin.Plugin.SatIpFreesat.SatIp;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SatIpFreesat.Freesat;

/// <summary>
/// Orchestrates a full Freesat channel scan:
///   1. Tune bootstrap mux (11425H) and read NIT → discover all muxes
///   2. For each mux, read SDT → discover services
///   3. Read BAT on the bootstrap mux → discover bouquets + LCNs
///   4. Apply Freesat bouquet/LCN logic to build the channel list
/// </summary>
public sealed class FreesatScanner
{
    private readonly ILogger<FreesatScanner> _logger;
    private readonly FreesatChannelStore _store;

    // How long to collect SI data per mux before moving on
    private static readonly TimeSpan SiCollectTimeout = TimeSpan.FromSeconds(30);

    // Bouquet name patterns that identify BSkyB (always excluded)
    private static readonly string[] SkyPrefixes = ["Sky", "BSkyB"];

    public FreesatScanner(ILogger<FreesatScanner> logger, FreesatChannelStore store)
    {
        _logger = logger;
        _store = store;
    }

    public async Task<ScanResult> ScanAsync(
        string host, int rtspPort, int frontendNumber, string regionKey,
        CancellationToken ct)
    {
        var region = RegionData.Regions.TryGetValue(regionKey, out var r)
            ? r : throw new ArgumentException($"Unknown region key: {regionKey}");

        _logger.LogInformation("SAT>IP Freesat scan: host={Host} region={Region}", host, region.Label);

        // Step 1: tune bootstrap mux, collect NIT + BAT
        var (muxes, bouquets) = await CollectNitAndBatAsync(host, rtspPort, frontendNumber, ct).ConfigureAwait(false);
        _logger.LogInformation("SAT>IP: discovered {MuxCount} muxes, {BouquetCount} bouquets", muxes.Count, bouquets.Count);

        // Step 2: collect SDT from each mux (services)
        var allServices = await CollectSdtAsync(host, rtspPort, frontendNumber, muxes, ct).ConfigureAwait(false);
        _logger.LogInformation("SAT>IP: discovered {ServiceCount} services", allServices.Count);

        // Step 3: build Freesat channel list
        var channels = BuildChannelList(allServices, bouquets, regionKey, region);
        _logger.LogInformation("SAT>IP: built {ChannelCount} Freesat channels", channels.Count);

        var result = new ScanResult
        {
            Channels = channels,
            RegionKey = regionKey,
            RegionLabel = region.Label,
            ScannedAt = DateTime.UtcNow.ToString("O"),
            MuxCount = muxes.Count,
        };

        await _store.SaveAsync(result, ct).ConfigureAwait(false);
        return result;
    }

    // ---- SI collection ----

    private async Task<(List<MuxInfo> Muxes, List<BouquetInfo> Bouquets)> CollectNitAndBatAsync(
        string host, int port, int frontend, CancellationToken ct)
    {
        var muxes = new Dictionary<string, MuxInfo>();
        var bouquets = new Dictionary<int, BouquetInfo>();

        var muxParams = SatIpMuxParams.Bootstrap(frontend);
        await using var client = new RtspClient(host, port, _logger);
        await client.ConnectAsync(ct).ConfigureAwait(false);
        // PIDs: 16=NIT, 17=SDT+BAT
        await client.SetupAndPlayAsync(muxParams, "0,16,17", ct).ConfigureAwait(false);

        var reader = new TsReader();
        reader.SubscribePid(NitParser.PidNit);
        reader.SubscribePid(SdtParser.PidSdt);  // 0x11 carries both SDT and BAT

        reader.SectionReady += (pid, section) =>
        {
            if (section[0] is NitParser.TableIdNitActual or NitParser.TableIdNitOther)
            {
                foreach (var mux in NitParser.Parse(section))
                {
                    var key = $"{mux.FrequencyMHz:F3}-{mux.Polarization}";
                    muxes.TryAdd(key, mux);
                }
            }
            else if (section[0] == BatParser.TableIdBat)
            {
                var bq = BatParser.Parse(section);
                if (bq is not null)
                    bouquets[bq.BouquetId] = bq;
            }
        };

        await ReadStreamAsync(client, reader, SiCollectTimeout, ct).ConfigureAwait(false);
        await client.TeardownAsync(ct).ConfigureAwait(false);

        return (muxes.Values.ToList(), bouquets.Values.ToList());
    }

    private async Task<List<ServiceInfo>> CollectSdtAsync(
        string host, int port, int frontend,
        List<MuxInfo> muxes, CancellationToken ct)
    {
        var allServices = new List<ServiceInfo>();

        // Prioritise bootstrap mux first, then scan others
        var ordered = muxes
            .OrderByDescending(m => Math.Abs(m.FrequencyMHz - 11425.0) < 1)
            .ToList();

        // Limit scan to first 30 muxes to avoid extremely long waits
        foreach (var mux in ordered.Take(30))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var services = await CollectSdtFromMuxAsync(host, port, frontend, mux, ct).ConfigureAwait(false);
                foreach (var svc in services)
                    svc.Mux = mux;
                allServices.AddRange(services);
                _logger.LogDebug("SAT>IP: {Freq}{Pol} → {Count} services", mux.FrequencyMHz, mux.Polarization, services.Count);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SAT>IP: skipping mux {Freq}{Pol} (SDT error)", mux.FrequencyMHz, mux.Polarization);
            }
        }

        return allServices;
    }

    private async Task<List<ServiceInfo>> CollectSdtFromMuxAsync(
        string host, int port, int frontend, MuxInfo mux, CancellationToken ct)
    {
        var services = new Dictionary<int, ServiceInfo>();

        var muxParams = new SatIpMuxParams
        {
            FrontendNumber = frontend,
            FrequencyMHz = mux.FrequencyMHz,
            Polarization = char.ToLowerInvariant(mux.Polarization),
            SymbolRateKsym = mux.SymbolRateKsym,
            IsDvbS2 = mux.IsDvbS2,
        };

        await using var client = new RtspClient(host, port, _logger);
        await client.ConnectAsync(ct).ConfigureAwait(false);
        await client.SetupAndPlayAsync(muxParams, "17", ct).ConfigureAwait(false);

        var reader = new TsReader();
        reader.SubscribePid(SdtParser.PidSdt);
        reader.SectionReady += (_, section) =>
        {
            if (section[0] is not (SdtParser.TableIdSdtActual or SdtParser.TableIdSdtOther)) return;
            foreach (var svc in SdtParser.Parse(section))
                services.TryAdd(svc.ServiceId, svc);
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await ReadStreamAsync(client, reader, TimeSpan.FromSeconds(15), timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* per-mux timeout */ }

        await client.TeardownAsync(ct).ConfigureAwait(false);
        return services.Values.ToList();
    }

    private static async Task ReadStreamAsync(
        RtspClient client, TsReader reader, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var payload = await client.ReadRtpPacketAsync(cts.Token).ConfigureAwait(false);
                if (payload is null) break;
                reader.Feed(payload);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // normal timeout
        }
    }

    // ---- Channel list construction (Tasks 4 + 6) ----

    private List<FreesatChannel> BuildChannelList(
        List<ServiceInfo> allServices,
        List<BouquetInfo> bouquets,
        string regionKey,
        RegionData.RegionInfo region)
    {
        // Index services by DVB triplet
        var svcIndex = new Dictionary<(int onid, int tsid, int sid), ServiceInfo>();
        foreach (var svc in allServices)
        {
            if (svc.Mux is null) continue;
            var key = (svc.Mux.OriginalNetworkId, svc.Mux.TransportStreamId, svc.ServiceId);
            svcIndex.TryAdd(key, svc);
        }

        // Separate Freesat vs Sky bouquets; filter Sky out completely.
        // Freesat bouquets contain "HD" tier; exclude "G2"/"SD" duplicates.
        var freesatBouquets = bouquets
            .Where(b => !IsSkyBouquet(b.Name))
            .Where(b => IsHdTier(b.Name))
            .ToList();

        // Find bouquets matching the selected region
        var regionMatches = freesatBouquets
            .Where(b => region.Match.Any(m => b.Name.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Also include the nation-level "Default" bouquet (carries shared national channels)
        var defaultBouquets = freesatBouquets
            .Where(b => b.Name.Contains("Default", StringComparison.OrdinalIgnoreCase))
            .Where(b => regionMatches.Any(r => TierPrefix(r.Name) == TierPrefix(b.Name)))
            .ToList();

        var activeBouquets = regionMatches.Concat(defaultBouquets).ToList();

        bool haveLiveBatLcns = activeBouquets.Any(b => b.Services.Count > 0);

        if (!haveLiveBatLcns)
        {
            _logger.LogWarning("SAT>IP: no BAT LCN data in bouquets — falling back to curated LCN table");
            return BuildFromCuratedTable(svcIndex, regionKey);
        }

        return BuildFromBouquetLcns(activeBouquets, regionMatches, svcIndex, regionKey);
    }

    private List<FreesatChannel> BuildFromBouquetLcns(
        List<BouquetInfo> activeBouquets,
        List<BouquetInfo> regionBouquets,
        Dictionary<(int onid, int tsid, int sid), ServiceInfo> svcIndex,
        string regionKey)
    {
        // Collect all LCN entries; for duplicates, prefer region bouquets over Default.
        var regionBouquetIds = new HashSet<int>(regionBouquets.Select(b => b.BouquetId));
        var lcnMap = new Dictionary<int, (BouquetServiceEntry Entry, bool IsRegion)>();

        foreach (var bq in activeBouquets)
        {
            bool isRegion = regionBouquetIds.Contains(bq.BouquetId);
            foreach (var entry in bq.Services)
            {
                if (entry.LogicalChannelNumber <= 0) continue;
                if (!lcnMap.TryGetValue(entry.LogicalChannelNumber, out var existing))
                {
                    lcnMap[entry.LogicalChannelNumber] = (entry, isRegion);
                }
                else if (isRegion && !existing.IsRegion)
                {
                    lcnMap[entry.LogicalChannelNumber] = (entry, isRegion);
                }
            }
        }

        // Override the selected region's BBC One to LCN 101
        if (LcnTable.RegionBbcOneName.TryGetValue(regionKey, out var bbcOneName))
        {
            foreach (var (lcn, (entry, _)) in lcnMap)
            {
                var key = (entry.OriginalNetworkId, entry.TransportStreamId, entry.ServiceId);
                if (svcIndex.TryGetValue(key, out var svc) &&
                    svc.Name.Equals(bbcOneName, StringComparison.OrdinalIgnoreCase))
                {
                    lcnMap.Remove(lcn);
                    lcnMap[101] = (entry, true);
                    break;
                }
            }
        }

        var channels = new List<FreesatChannel>();
        foreach (var (lcn, (entry, _)) in lcnMap.OrderBy(kv => kv.Key))
        {
            var key = (entry.OriginalNetworkId, entry.TransportStreamId, entry.ServiceId);
            if (!svcIndex.TryGetValue(key, out var svc)) continue;
            if (svc.IsEncrypted || (!svc.IsTV && !svc.IsRadio)) continue;

            channels.Add(new FreesatChannel
            {
                ChannelId = svc.ChannelId,
                Name = svc.Name,
                Number = lcn,
                IsHD = svc.IsHD,
                IsTV = svc.IsTV,
                IsRadio = svc.IsRadio,
                Mux = svc.Mux!,
                ServiceId = svc.ServiceId,
            });
        }

        return channels;
    }

    private List<FreesatChannel> BuildFromCuratedTable(
        Dictionary<(int onid, int tsid, int sid), ServiceInfo> svcIndex,
        string regionKey)
    {
        var targets = LcnTable.BuildTargets(regionKey);
        var usedLcns = new HashSet<int>();
        var itv1Assigned = false;
        var channels = new List<FreesatChannel>();

        foreach (var svc in svcIndex.Values.OrderBy(s => s.Name))
        {
            if (svc.IsEncrypted || svc.Mux is null) continue;
            if (!svc.IsTV && !svc.IsRadio) continue;

            string nameKey = svc.Name;
            if (!targets.TryGetValue(nameKey, out int lcn)) continue;

            // ITV1 HD: first instance only
            if (lcn == 103 && itv1Assigned) continue;
            if (lcn == 103) itv1Assigned = true;

            if (!usedLcns.Add(lcn)) continue;

            channels.Add(new FreesatChannel
            {
                ChannelId = svc.ChannelId,
                Name = svc.Name,
                Number = lcn,
                IsHD = svc.IsHD,
                IsTV = svc.IsTV,
                IsRadio = svc.IsRadio,
                Mux = svc.Mux,
                ServiceId = svc.ServiceId,
            });
        }

        return channels.OrderBy(c => c.Number).ToList();
    }

    private static bool IsSkyBouquet(string name) =>
        SkyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static bool IsHdTier(string name)
    {
        var prefix = TierPrefix(name);
        return prefix.EndsWith("HD", StringComparison.OrdinalIgnoreCase);
    }

    private static string TierPrefix(string name) =>
        name.Contains(':') ? name[..name.IndexOf(':')].Trim() : name.Trim();
}
