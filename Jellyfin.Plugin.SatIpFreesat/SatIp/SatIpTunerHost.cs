using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SatIpFreesat.Freesat;
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

    public string Name => "SAT>IP Freesat";
    public string Type => "satip-freesat";
    public bool IsSupported => !string.IsNullOrEmpty(Plugin.Instance?.Configuration?.ServerAddress);

    public SatIpTunerHost(ILogger<SatIpTunerHost> logger, FreesatChannelStore store)
    {
        _logger = logger;
        _store = store;
    }

    public Task<List<ChannelInfo>> GetChannels(bool enableCache, CancellationToken ct)
    {
        var scan = _store.Current;
        if (scan is null)
            return Task.FromResult(new List<ChannelInfo>());

        var channels = scan.Channels.Select(ch => new ChannelInfo
        {
            Id = ch.ChannelId,
            Name = ch.Name,
            Number = ch.Number.ToString(),
            ChannelType = ch.IsRadio ? ChannelType.Radio : ChannelType.TV,
            IsHD = ch.IsHD,
            TunerHostId = Type,
            TunerChannelId = ch.ChannelId,
        }).ToList();

        return Task.FromResult(channels);
    }

    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken ct)
    {
        var stream = CreateLiveStream(channelId, currentStreams: null);
        if (stream is null) return Task.FromResult(new List<MediaSourceInfo>());
        return Task.FromResult(new List<MediaSourceInfo> { stream.MediaSource });
    }

    public Task<ILiveStream> GetChannelStream(
        string channelId, string streamId,
        IList<ILiveStream> currentLiveStreams, CancellationToken ct)
    {
        var stream = CreateLiveStream(channelId, currentLiveStreams);
        if (stream is null)
            throw new InvalidOperationException($"Channel {channelId} not found or no tuner available");

        _logger.LogInformation("SAT>IP: opening stream for {Id} on frontend {Frontend}",
            channelId, stream.FrontendNumber);
        return Task.FromResult<ILiveStream>(stream);
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

        return new SatIpLiveStream(channel, cfg.ServerAddress, tuner);
    }
}
