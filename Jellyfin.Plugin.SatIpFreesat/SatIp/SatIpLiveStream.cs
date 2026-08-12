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
    public bool EnableStreamSharing { get; } = true;
    public int ConsumerCount { get; set; }
    public string TunerHostId { get; }
    public TunerChannelMapping TunerChannelMapping { get; set; } = null!;
    public MediaSourceInfo MediaSource { get; set; }

    /// <summary>Which SAT>IP frontend (src=) this stream is using.</summary>
    public int FrontendNumber { get; }

    public SatIpLiveStream(FreesatChannel channel, string serverAddress, TunerEntry tuner)
    {
        FrontendNumber = tuner.FrontendNumber;
        OriginalStreamId = channel.ChannelId;
        TunerHostId = "satip-freesat";

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
            Container = "ts",
            MediaStreams =
            [
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    IsDefault = true,
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
        return $"rtsp://{host}:{port}/stream={frontend}?src={frontend}" +
               $"&freq={mux.FrequencyMHz:F3}&pol={pol}&msys={msys}&sr={sr}" +
               "&fec=auto&pids=all";
    }
}
