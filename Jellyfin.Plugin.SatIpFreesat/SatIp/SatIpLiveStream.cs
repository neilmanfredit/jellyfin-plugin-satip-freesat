using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SatIpFreesat.Configuration;
using Jellyfin.Plugin.SatIpFreesat.Freesat;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SatIpFreesat.SatIp;

/// <summary>
/// Jellyfin ILiveStream implementation for a SAT>IP channel.
/// We pull RTSP/RTP data from the tuner ourselves via <see cref="SatIpStreamProxy"/> and write
/// it to a local growing file that ffmpeg reads as a plain file source — see the proxy class for
/// why (letting ffmpeg open the rtsp:// URL directly means it has to wait, unpredictably, for
/// the next H.264 keyframe boundary before it can decode anything).
/// </summary>
public sealed class SatIpLiveStream : ILiveStream
{
    public string OriginalStreamId { get; set; }
    public string UniqueId { get; } = Guid.NewGuid().ToString("N");
    public bool EnableStreamSharing { get; }
    public int ConsumerCount { get; set; }
    public string TunerHostId { get; }
    public TunerChannelMapping TunerChannelMapping { get; set; } = null!;
    public MediaSourceInfo MediaSource { get; set; }

    /// <summary>Which SAT>IP frontend (src=) this stream is using.</summary>
    public int FrontendNumber { get; }

    private readonly SatIpStreamProxy _proxy;
    private readonly IServerApplicationHost _appHost;

    public SatIpLiveStream(
        FreesatChannel channel, string serverAddress, TunerEntry tuner,
        Configuration.PluginConfiguration cfg, ILogger logger, IServerApplicationHost appHost)
    {
        _appHost = appHost;
        FrontendNumber = tuner.FrontendNumber;
        OriginalStreamId = channel.ChannelId;
        TunerHostId = "satip-freesat";
        EnableStreamSharing = cfg.EnableStreamSharing;

        var muxParams = new SatIpMuxParams
        {
            FrontendNumber = tuner.FrontendNumber,
            FrequencyMHz = channel.Mux.FrequencyMHz,
            Polarization = char.ToLowerInvariant(channel.Mux.Polarization),
            SymbolRateKsym = channel.Mux.SymbolRateKsym,
            IsDvbS2 = channel.Mux.IsDvbS2,
            ModulationType = channel.Mux.ModulationType,
        };

        var pids = BuildPids(channel);
        _proxy = new SatIpStreamProxy(logger, serverAddress, tuner.RtspPort, muxParams, pids, channel.VideoPid);

        MediaSource = new MediaSourceInfo
        {
            Id = UniqueId,
            Path = _proxy.FilePath,
            Protocol = MediaProtocol.File,
            Container = "mpegts",
            IsRemote = false,
            IsInfiniteStream = true,
            ReadAtNativeFramerate = false,
            RequiresOpening = true,
            RequiresClosing = true,
            SupportsProbing = true,
            MediaStreams =
            [
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    IsDefault = true,
                    // IsInterlaced hints Jellyfin's transcoder to apply a deinterlace filter.
                    // Has no effect on direct-play paths (ffmpeg owns the connection there).
                    IsInterlaced = cfg.ForceDeinterlace,
                    // Freesat mandates MPEG-4 AVC across its entire platform (unlike Freeview,
                    // it has never carried MPEG-2 video) at 8-bit 4:2:0, confirmed by direct
                    // ffprobe/ffmpeg testing against the live RTSP source. Jellyfin's
                    // EncodingHelper.GetQsvHwVidDecoder (and the VAAPI/NVENC equivalents)
                    // require BOTH Codec and PixelFormat to be set before it will select a
                    // hardware decoder; leaving them null forces software decode, which then
                    // pairs with the QSV *encoder* down a code path that never inserts an
                    // hwupload filter — the sw-decoded frames never reach the QSV encoder as
                    // hardware surfaces, so hevc_qsv silently stalls encoding zero frames.
                    Codec = "h264",
                    PixelFormat = "yuv420p",
                },
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    IsDefault = true,
                    Language = "eng",
                },
            ],
        };
    }

    public Stream GetStream() => new FileStream(
        _proxy.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    public async Task Open(CancellationToken ct)
    {
        await _proxy.OpenAsync(ct).ConfigureAwait(false);

        // ffmpeg's file: protocol treats a growing local file as finite and exits cleanly on
        // EOF instead of polling for new bytes. Jellyfin's own tuner hosts (HdHomerun, M3U)
        // avoid this by routing playback through Jellyfin's own /LiveTv/LiveStreamFiles HTTP
        // endpoint, which wraps GetStream()'s result in a ProgressiveFileStream that knows how
        // to wait for growth. Mirror that here rather than exposing the raw file path.
        MediaSource.Path = _appHost.GetApiUrlForLocalAccess(null, true) + "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
        MediaSource.Protocol = MediaProtocol.Http;
    }
    public Task Close() => _proxy.CloseAsync();
    public void Dispose() { }

    private static string BuildPids(FreesatChannel channel)
    {
        // Request only PAT + this channel's PMT + its video (+ audio, if known) elementary
        // streams. Requesting "pids=all" makes minisatip deliver the *entire* transponder —
        // every channel sharing the mux, potentially 5+ programs and dozens of streams — which
        // blows the probe budget and breaks the hardcoded MediaStream.Index=0/1 mapping above.
        if (channel.PmtPid is int pmtPid && channel.VideoPid is int videoPid)
        {
            var pids = $"0,{pmtPid},{videoPid}";
            if (channel.AudioPid is int audioPid) pids += $",{audioPid}";
            return pids;
        }

        // Scan couldn't resolve this channel's PMT/PIDs (e.g. PAT/PMT collection timed out on a
        // busy mux) — fall back to the old behaviour rather than fail to play.
        return "all";
    }
}
