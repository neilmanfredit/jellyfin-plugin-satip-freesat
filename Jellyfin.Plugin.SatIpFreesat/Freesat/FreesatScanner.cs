using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SatIpFreesat.DvbSi;
using Jellyfin.Plugin.SatIpFreesat.SatIp;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SatIpFreesat.Freesat;

public sealed record ScanProgress(string Message, int? Percent);

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

    // How long to wait for the RTSP handshake (DESCRIBE + SETUP + PLAY) before giving up
    private static readonly TimeSpan RtspHandshakeTimeout = TimeSpan.FromSeconds(15);

    // Bouquet name patterns that identify BSkyB (always excluded)
    private static readonly string[] SkyPrefixes = ["Sky", "BSkyB"];

    public FreesatScanner(ILogger<FreesatScanner> logger, FreesatChannelStore store)
    {
        _logger = logger;
        _store = store;
    }

    public async Task<ScanResult> ScanAsync(
        string host, int rtspPort, int frontendNumber, string regionKey,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var region = RegionData.Regions.TryGetValue(regionKey, out var r)
            ? r : throw new ArgumentException($"Unknown region key: {regionKey}");

        _logger.LogInformation("SAT>IP Freesat scan: host={Host} region={Region}", host, region.Label);

        // Auto-detect which frontend (LNB port) has signal, and whether the device requires
        // UDP transport (some devices agree to TCP in SETUP but never send data via it).
        progress?.Report(new("Detecting active LNB port and transport…", null));
        var (activeFrontend, useUdp) = await ProbeWorkingFrontendAsync(host, rtspPort, frontendNumber, ct)
            .ConfigureAwait(false);

        // Step 1: tune bootstrap mux, collect NIT + BAT (indeterminate — total mux count unknown)
        progress?.Report(new("Reading network and bouquet tables from bootstrap mux…", null));
        var (muxes, bouquets) = await CollectNitAndBatAsync(host, rtspPort, activeFrontend, useUdp, ct).ConfigureAwait(false);
        _logger.LogInformation("SAT>IP: discovered {MuxCount} muxes, {BouquetCount} bouquets", muxes.Count, bouquets.Count);

        // Step 2: collect SDT from each mux (determinate — percent reported per mux)
        progress?.Report(new($"Discovered {muxes.Count} muxes — scanning services on each…", 0));
        var allServices = await CollectSdtAsync(host, rtspPort, activeFrontend, useUdp, muxes, progress, ct).ConfigureAwait(false);
        _logger.LogInformation("SAT>IP: discovered {ServiceCount} services", allServices.Count);

        // Step 3: build Freesat channel list (fast — show 99% until tracker marks complete)
        progress?.Report(new($"Building channel list from {allServices.Count} services…", 99));
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

    /// <summary>
    /// Probes src=1-8 with TCP interleaved first, then with UDP if TCP delivers nothing.
    /// Returns the active frontend number and whether UDP transport is required.
    /// </summary>
    private async Task<(int Frontend, bool UseUdp)> ProbeWorkingFrontendAsync(
        string host, int port, int preferredFrontend, CancellationToken ct)
    {
        var candidates = Enumerable.Range(1, 8)
            .OrderBy(n => n == preferredFrontend ? 0 : 1)
            .ToList();

        // Round 1: TCP interleaved (fast — dead ports close the stream in ~1 s)
        _logger.LogInformation("SAT>IP: probing frontends with TCP transport…");
        foreach (var frontend in candidates)
        {
            if (ct.IsCancellationRequested) break;
            _logger.LogInformation("SAT>IP: probing src={Frontend} (TCP)…", frontend);
            if (await TryFrontendAsync(host, port, frontend, useUdp: false, ct).ConfigureAwait(false))
            {
                _logger.LogInformation("SAT>IP: active on src={Frontend} via TCP", frontend);
                return (frontend, false);
            }
        }

        // Round 2: UDP unicast (device agrees to TCP in SETUP but never sends data via it)
        _logger.LogInformation("SAT>IP: TCP gave no data — retrying with UDP transport…");
        foreach (var frontend in candidates)
        {
            if (ct.IsCancellationRequested) break;
            _logger.LogInformation("SAT>IP: probing src={Frontend} (UDP)…", frontend);
            if (await TryFrontendAsync(host, port, frontend, useUdp: true, ct).ConfigureAwait(false))
            {
                _logger.LogInformation("SAT>IP: active on src={Frontend} via UDP", frontend);
                return (frontend, true);
            }
        }

        _logger.LogWarning(
            "SAT>IP: no active frontend found via TCP or UDP — defaulting to src={Frontend} UDP. " +
            "If the scan produces 0 channels, check that inbound UDP from {Device} can reach this host " +
            "(a firewall or Docker bridge network may be blocking it).",
            preferredFrontend, host);
        // TCP was proven not to deliver data (stream closes immediately); UDP at least gets some
        // RTCP/probe traffic, so use it as the fallback transport for the actual scan.
        return (preferredFrontend, true);
    }

    /// <summary>
    /// Opens a brief RTSP session on <paramref name="frontend"/> and returns true if any
    /// RTP payload arrives within 4 seconds.
    /// </summary>
    private async Task<bool> TryFrontendAsync(
        string host, int port, int frontend, bool useUdp, CancellationToken ct)
    {
        var muxParams = SatIpMuxParams.Bootstrap(frontend);
        await using var client = new RtspClient(host, port, _logger);
        if (useUdp) client.EnableUdpTransport();

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(RtspHandshakeTimeout);
        try
        {
            await client.ConnectAsync(handshakeCts.Token).ConfigureAwait(false);
            await client.SetupAndPlayAsync(muxParams, "16,17", handshakeCts.Token).ConfigureAwait(false);
        }
        catch { return false; }

        bool gotData = false;
        int packetsReceived = 0;
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            while (!probeCts.IsCancellationRequested)
            {
                var payload = await client.ReadRtpPacketAsync(probeCts.Token).ConfigureAwait(false);
                if (payload is null) break; // TCP EOF
                packetsReceived++;
                // In UDP mode ANY received packet (including RTCP/short) proves the path works.
                // In TCP mode require actual payload data (non-empty = real RTP).
                if (client.IsUdpMode || payload.Length > 0) { gotData = true; break; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug("SAT>IP: probe read exception src={Frontend}: {Msg}", frontend, ex.Message);
        }

        _logger.LogInformation(
            "SAT>IP: src={Frontend} ({Mode}) probe: {Pkts} packet(s) received, active={Got}",
            frontend, useUdp ? "UDP" : "TCP", packetsReceived, gotData);

        try { await client.TeardownAsync(ct).ConfigureAwait(false); } catch { }
        return gotData;
    }

    private async Task<(List<MuxInfo> Muxes, List<BouquetInfo> Bouquets)> CollectNitAndBatAsync(
        string host, int port, int frontend, bool useUdp, CancellationToken ct)
    {
        var muxes = new Dictionary<string, MuxInfo>();
        var bouquets = new Dictionary<int, BouquetInfo>();

        var muxParams = SatIpMuxParams.Bootstrap(frontend);
        await using var client = new RtspClient(host, port, _logger);
        if (useUdp) client.EnableUdpTransport();

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(RtspHandshakeTimeout);
        try
        {
            await client.ConnectAsync(handshakeCts.Token).ConfigureAwait(false);
            // PIDs: 16=NIT, 17=SDT+BAT (PID 0/PAT omitted; some devices reject it in the filter)
            await client.SetupAndPlayAsync(muxParams, "16,17", handshakeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(
                "SAT>IP: RTSP handshake timed out after {Sec}s — check the device address and that RTSP port {Port} is reachable",
                (int)RtspHandshakeTimeout.TotalSeconds, port);
            return ([], []);
        }

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

        await ReadStreamAsync(client, reader, SiCollectTimeout, ct, _logger).ConfigureAwait(false);
        await client.TeardownAsync(ct).ConfigureAwait(false);

        if (muxes.Count == 0)
            _logger.LogWarning(
                "SAT>IP: no muxes found from bootstrap mux — device may not have locked to 11425H DVB-S 27500");

        return (muxes.Values.ToList(), bouquets.Values.ToList());
    }

    private async Task<List<ServiceInfo>> CollectSdtAsync(
        string host, int port, int frontend, bool useUdp,
        List<MuxInfo> muxes, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var allServices = new List<ServiceInfo>();

        // Prioritise bootstrap mux first, then scan others
        var ordered = muxes
            .OrderByDescending(m => Math.Abs(m.FrequencyMHz - 11425.0) < 1)
            .ToList();

        // Limit scan to first 30 muxes to avoid extremely long waits
        var total = Math.Min(ordered.Count, 30);
        for (int i = 0; i < total; i++)
        {
            var mux = ordered[i];
            if (ct.IsCancellationRequested) break;
            // Reserve 0–95% for mux scanning; step 3 takes 99%
            int percent = total > 1 ? (int)Math.Round(i * 95.0 / total) : 0;
            progress?.Report(new($"Scanning mux {i + 1} of {total} ({mux.FrequencyMHz:F3} MHz {mux.Polarization})…", percent));
            try
            {
                var services = await CollectSdtFromMuxAsync(host, port, frontend, useUdp, mux, ct).ConfigureAwait(false);
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
        string host, int port, int frontend, bool useUdp, MuxInfo mux, CancellationToken ct)
    {
        var services = new Dictionary<int, ServiceInfo>();

        var muxParams = new SatIpMuxParams
        {
            FrontendNumber = frontend,
            FrequencyMHz = mux.FrequencyMHz,
            Polarization = char.ToLowerInvariant(mux.Polarization),
            SymbolRateKsym = mux.SymbolRateKsym,
            IsDvbS2 = mux.IsDvbS2,
            ModulationType = mux.ModulationType,
        };

        await using var client = new RtspClient(host, port, _logger);
        if (useUdp) client.EnableUdpTransport();

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(RtspHandshakeTimeout);
        try
        {
            await client.ConnectAsync(handshakeCts.Token).ConfigureAwait(false);
            await client.SetupAndPlayAsync(muxParams, "17", handshakeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "SAT>IP: RTSP handshake timed out for {Freq}{Pol} — skipping mux",
                mux.FrequencyMHz, mux.Polarization);
            return [];
        }

        var reader = new TsReader();
        reader.SubscribePid(SdtParser.PidSdt);
        reader.SectionReady += (_, section) =>
        {
            // Only accept SDT-Actual: SDT-Other describes services on foreign TSes, but the
            // mux we assign here is the one we're tuned to, giving the wrong TSID for lookups.
            if (section[0] != SdtParser.TableIdSdtActual) return;
            foreach (var svc in SdtParser.Parse(section))
                services.TryAdd(svc.ServiceId, svc);
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await ReadStreamAsync(client, reader, TimeSpan.FromSeconds(15), timeout.Token, _logger).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* per-mux timeout */ }

        await client.TeardownAsync(ct).ConfigureAwait(false);
        return services.Values.ToList();
    }

    private static async Task ReadStreamAsync(
        RtspClient client, TsReader reader, TimeSpan timeout, CancellationToken ct,
        ILogger? logger = null)
    {
        // Send periodic GET_PARAMETER keep-alives so the device doesn't time out the session.
        // Interval = half the negotiated timeout (floor 1 s). The keep-alive response arrives
        // as a plain RTSP message on the same TCP channel; ReadRtpPacketAsync skips it safely.
        var keepAliveInterval = TimeSpan.FromSeconds(
            Math.Max(client.SessionTimeout.TotalSeconds / 2.0, 1.0));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var keepAliveTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(keepAliveInterval, cts.Token).ConfigureAwait(false);
                    await client.SendKeepAliveAsync(cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        int packets = 0;
        long bytes = 0;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var payload = await client.ReadRtpPacketAsync(cts.Token).ConfigureAwait(false);
                if (payload is null) break;          // stream closed
                if (payload.Length == 0) continue;   // RTCP or skipped RTSP frame
                packets++;
                bytes += payload.Length;
                reader.Feed(payload);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // normal timeout
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try { await keepAliveTask.ConfigureAwait(false); } catch { }
            logger?.LogInformation("SAT>IP: RTP stream ended — {Packets} packets, {Bytes} bytes received", packets, bytes);
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
