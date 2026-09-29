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
            // Without this, EncodingHelper falls back to the server's raw -analyzeduration
            // default (200M/200s) plus the global -probesize (1G) when it builds ffmpeg's input
            // modifier — because those are only applied when AnalyzeDurationMs is unset. Against
            // this tuner that meant ffmpeg's probe phase alone took 3+ minutes before the first
            // HLS segment appeared, well past Jellyfin's live-stream kill timer and any client's
            // patience, producing a silent "still connecting" hang even though every layer below
            // ffmpeg (RTSP session, RTP receive, proxy file) was healthy the whole time. 3000ms
            // is Jellyfin's own convention for an already-open live source (see
            // Emby.Server.Implementations/Library/LiveStreamHelper.cs, which sets this exact
            // value after its internal probe) — safe here because SyncToKeyframeAsync already
            // guarantees the proxy file begins at a keyframe boundary with PAT/PMT/audio
            // continuously present, so ffmpeg needs far less data than an arbitrary mid-GOP join
            // to identify the streams.
            AnalyzeDurationMs = 3000,
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
                    // Without a declared Channels count, EncodingHelper.GetNumAudioChannelsParam
                    // (audioStream.Channels is null so its "clamp to input" branch never runs)
                    // falls back to the client profile's max audio channels for the target codec
                    // instead of the real source — against a Fire TV profile that meant ffmpeg
                    // was told to transcode this channel's plain stereo MP2 audio into 8-channel
                    // AAC. ffmpeg's ADTS muxer only supports up to 7 channels
                    // (channelConfiguration > 7 is not supported in ADTS), so it failed outright
                    // (exit code 183) on every single playback attempt on that client, while
                    // direct-play clients (the web browser) never hit this transcode path at all.
                    // Freesat's SD/HD channels are overwhelmingly plain stereo MP2/AAC; this is a
                    // best-effort default, not a probe of the actual per-channel PMT audio
                    // descriptor.
                    Channels = 2,
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
        //
        // allowHttps: false is deliberate — GetApiUrlForLocalAccess builds the URL from this
        // server's LAN IP, but this server's TLS cert only covers its hostname (*.domain),
        // not the bare IP, so ffmpeg's TLS handshake rejects it outright ("no alternative
        // certificate subject name matches target IP"). This request never leaves the host, so
        // plain HTTP is fine.
        MediaSource.Path = _appHost.GetApiUrlForLocalAccess(null, false) + "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
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
