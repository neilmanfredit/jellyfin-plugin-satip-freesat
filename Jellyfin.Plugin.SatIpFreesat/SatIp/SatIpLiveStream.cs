using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SatIpFreesat.Configuration;
using Jellyfin.Plugin.SatIpFreesat.Freesat;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.SatIpFreesat.SatIp;

/// <summary>
/// Jellyfin ILiveStream implementation for a SAT>IP channel.
/// The RTSP URL is passed to Jellyfin/ffmpeg directly; no in-process proxying.
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

    public SatIpLiveStream(FreesatChannel channel, string serverAddress, TunerEntry tuner, Configuration.PluginConfiguration cfg)
    {
        FrontendNumber = tuner.FrontendNumber;
        OriginalStreamId = channel.ChannelId;
        TunerHostId = "satip-freesat";
        EnableStreamSharing = cfg.EnableStreamSharing;

        var rtspUrl = BuildRtspUrl(channel, serverAddress, tuner.RtspPort, tuner.FrontendNumber);

        MediaSource = new MediaSourceInfo
        {
            Id = UniqueId,
            Path = rtspUrl,
            Protocol = MediaProtocol.Rtsp,
            IsRemote = true,
            IsInfiniteStream = true,
            ReadAtNativeFramerate = false,
            RequiresOpening = true,
            RequiresClosing = false,
            SupportsProbing = true,
            // Leave Container unset. Jellyfin's EncodingHelper maps Container="ts" to an
            // explicit "-f mpegts" flag injected before "-i rtsp://...". That forces ffmpeg
            // to open the URL via the generic protocol layer instead of auto-detecting the
            // rtsp demuxer — and "rtsp" isn't a registered ffmpeg URL protocol (only a
            // demuxer), so the input fails with "Protocol not found". ffprobe already
            // reports this source's format_name as "rtsp", not "ts"/"mpegts", so leaving
            // Container unset lets ffmpeg auto-detect correctly, matching what actually works.
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

    public Stream GetStream() => Stream.Null;

    public Task Open(CancellationToken ct) => Task.CompletedTask;
    public Task Close() => Task.CompletedTask;
    public void Dispose() { }

    private static string BuildRtspUrl(FreesatChannel channel, string host, int port, int frontend)
    {
        var mux = channel.Mux;
        var pol = mux.Polarization == 'H' ? "h" : "v";
        var msys = mux.IsDvbS2 ? "dvbs2" : "dvbs";
        var sr = (int)mux.SymbolRateKsym;
        // SAT>IP DESCRIBE URL: path must be "/" (or "/?..."), not "/stream=N".
        // "/stream=N" is a server-assigned session ID returned after SETUP — the client
        // must not put it in the initial DESCRIBE or the SAT>IP server will reject it.
        var url = $"rtsp://{host}:{port}/?src={frontend}" +
                  $"&freq={mux.FrequencyMHz:F3}&pol={pol}&msys={msys}&sr={sr}&fec=auto";
        if (mux.IsDvbS2)
            url += $"&ro=0.35&mtype={mux.ModulationType}";

        if (channel.PmtPid is int pmtPid && channel.VideoPid is int videoPid)
        {
            // Request only PAT + this channel's PMT + its video (+ audio, if known) elementary
            // streams. "pids=all" used to be sent here, which makes minisatip deliver the
            // *entire* transponder — every channel sharing the mux, potentially 5+ programs
            // and dozens of streams. That blows ffmpeg's probe budget (repeated SIGKILL/
            // cancel retry loops) and breaks the hardcoded MediaStream.Index=0/1 mapping below,
            // since index 0/1 of the full multiplex doesn't correspond to this channel's
            // actual video/audio streams once other programs are present.
            var pids = $"0,{pmtPid},{videoPid}";
            if (channel.AudioPid is int audioPid) pids += $",{audioPid}";
            url += $"&pids={pids}";
        }
        else
        {
            // Scan couldn't resolve this channel's PMT/PIDs (e.g. PAT/PMT collection timed
            // out on a busy mux) — fall back to the old behaviour rather than fail to play.
            url += "&pids=all";
        }

        return url;
    }
}
