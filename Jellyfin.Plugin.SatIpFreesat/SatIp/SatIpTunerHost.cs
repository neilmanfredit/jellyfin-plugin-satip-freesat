using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SatIpFreesat.Freesat;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SatIpFreesat.SatIp;

/// <summary>
/// Jellyfin ITunerHost implementation for a SAT>IP server carrying Freesat (28.2E).
/// </summary>
public sealed class SatIpTunerHost : ITunerHost
{
    private readonly ILogger<SatIpTunerHost> _logger;
    private readonly FreesatChannelStore _store;
    private readonly IServerApplicationHost _appHost;

    public string Name => "SAT>IP Freesat";
    public string Type => "satip-freesat";

    // Jellyfin's TunerHostManager reads IsSupported exactly once, while building its
    // ITunerHost list at DI-container-construction time — and that happens before
    // BasePlugin<T>'s constructor has run, so Plugin.Instance is still null at that
    // instant. Gating on configuration here would permanently exclude this host from
    // every guide refresh for the process's lifetime, regardless of later config
    // changes or rescans. GetChannels/DiscoverDevices already return empty results
    // when unconfigured, so there's nothing to gain by being conditionally supported.
    public bool IsSupported => true;

    public SatIpTunerHost(ILogger<SatIpTunerHost> logger, FreesatChannelStore store, IServerApplicationHost appHost)
    {
        _logger = logger;
        _store = store;
        _appHost = appHost;
    }

    public Task<List<ChannelInfo>> GetChannels(bool enableCache, CancellationToken ct)
    {
        var scan = _store.Current;
        if (scan is null)
            return Task.FromResult(new List<ChannelInfo>());

        var channels = scan.Channels.Select(ch =>
        {
            var logoUrl = ChannelLogoProvider.GetLogoUrl(ch.Name);
            return new ChannelInfo
            {
                Id = ch.ChannelId,
                Name = ch.Name,
                Number = ch.Number.ToString(),
                ChannelType = ch.IsRadio ? ChannelType.Radio : ChannelType.TV,
                IsHD = ch.IsHD,
                TunerHostId = Type,
                TunerChannelId = ch.ChannelId,
                ImageUrl = logoUrl,
                HasImage = logoUrl is not null,
            };
        }).ToList();

        return Task.FromResult(channels);
    }

    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken ct)
    {
        var stream = CreateLiveStream(channelId, currentStreams: null);
        if (stream is null) return Task.FromResult(new List<MediaSourceInfo>());
        return Task.FromResult(new List<MediaSourceInfo> { stream.MediaSource });
    }

    public async Task<ILiveStream> GetChannelStream(
        string channelId, string streamId,
        IList<ILiveStream> currentLiveStreams, CancellationToken ct)
    {
        var stream = CreateLiveStream(channelId, currentLiveStreams);
        if (stream is null)
            throw new InvalidOperationException($"Channel {channelId} not found or no tuner available");

        _logger.LogInformation("SAT>IP: opening stream for {Id} on frontend {Frontend}",
            channelId, stream.FrontendNumber);

        // Jellyfin's direct-stream-provider live TV flow (used for ITunerHost channels) never
        // calls ILiveStream.Open() itself — it expects the stream to already be open and the
        // backing file already flowing by the time this method returns. We have to do that work
        // here ourselves rather than waiting for a callback that never comes.
        await stream.Open(ct).ConfigureAwait(false);
        return stream;
    }

    public Task<List<TunerHostInfo>> DiscoverDevices(int discoveryDurationMs, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrEmpty(cfg.ServerAddress))
            return Task.FromResult(new List<TunerHostInfo>());

        var primary = cfg.PrimaryTuner;
        return Task.FromResult(new List<TunerHostInfo>
        {
            new()
            {
                Id = $"satip-{cfg.ServerAddress}",
                Url = $"rtsp://{cfg.ServerAddress}:{primary.RtspPort}",
                Type = Type,
                FriendlyName = $"SAT>IP Freesat @ {cfg.ServerAddress}",
                TunerCount = cfg.Tuners.Count > 0 ? cfg.Tuners.Count : 1,
                AllowHWTranscoding = false,
            },
        });
    }

    private SatIpLiveStream? CreateLiveStream(string channelId, IList<ILiveStream>? currentStreams)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrEmpty(cfg.ServerAddress)) return null;

        var scan = _store.Current;
        if (scan is null) return null;

        var channel = scan.Channels.FirstOrDefault(c => c.ChannelId == channelId);
        if (channel is null) return null;

        // Pick the first frontend not already claimed by an active stream
        var usedFrontends = currentStreams?
            .OfType<SatIpLiveStream>()
            .Select(s => s.FrontendNumber)
            .ToHashSet() ?? [];

        var tuners = cfg.Tuners is { Count: > 0 } ? cfg.Tuners : [cfg.PrimaryTuner];
        var tuner = tuners.FirstOrDefault(t => !usedFrontends.Contains(t.FrontendNumber))
                    ?? tuners[0];

        return new SatIpLiveStream(channel, cfg.ServerAddress, tuner, cfg, _logger, _appHost);
    }
}
