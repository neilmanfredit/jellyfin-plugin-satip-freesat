using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SatIpFreesat.Configuration;
using Jellyfin.Plugin.SatIpFreesat.DvbSi;
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
    private readonly ILogger _logger;
    // PMT PID for this channel — used in Open() to probe the proxy file for actual stream indices.
    private readonly int? _pmtPid;

    public SatIpLiveStream(
        FreesatChannel channel, string serverAddress, TunerEntry tuner,
        Configuration.PluginConfiguration cfg, ILogger logger, IServerApplicationHost appHost)
    {
        _appHost = appHost;
        _logger = logger;
        _pmtPid = channel.PmtPid;
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
            // For channels with multiple audio streams (e.g. BBC HD: AC-3 main + MP2 nar), the
            // AC-3 stream (stream_type 0x06) takes several seconds longer to appear in minisatip's
            // remapped RTP/TS output than the MP2 stream. 3 s is insufficient to detect both, so
            // we extend the probe window to 8 s for multi-audio channels. Single-audio channels
            // keep the 3 s window to avoid unnecessary startup latency.
            AnalyzeDurationMs = channel.AudioStreams.Count > 1 ? 8000 : 3000,
            RequiresOpening = true,
            RequiresClosing = true,
            SupportsProbing = true,
            MediaStreams = BuildMediaStreams(channel, cfg),
        };
    }

    public Stream GetStream() => new FileStream(
        _proxy.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    public async Task Open(CancellationToken ct)
    {
        await _proxy.OpenAsync(ct).ConfigureAwait(false);

        // For channels with multiple audio streams, the stream indices we declared in
        // BuildMediaStreams (Index = 1 + i) may be wrong. The broadcaster's PMT can include
        // subtitle, teletext, and data streams between or around the audio streams, and
        // minisatip delivers ALL of them — including ones we didn't request via pids=. ffmpeg
        // assigns global stream indices (0:0, 0:1, 0:2…) in PMT order counting every
        // elementary stream type, so e.g. BBC channels give: video(0), nar_audio(1),
        // subtitle(2), eng_audio(3). With Index=2 for eng we'd map the subtitle instead.
        // Read the PMT from the proxy file (which has been collecting data during keyframe
        // sync) to find the real global index for each audio stream and fix the declaration
        // before Jellyfin reads MediaSource.MediaStreams to build the ffmpeg -map arguments.
        if (_pmtPid is int pmtPid && MediaSource.MediaStreams.Count(s => s.Type == MediaStreamType.Audio) > 1)
            TryFixAudioIndicesFromProxy(pmtPid);

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

    private void TryFixAudioIndicesFromProxy(int pmtPid)
    {
        try
        {
            var buf = new byte[64 * 1024];
            int bytesRead;
            using (var fs = new FileStream(_proxy.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                bytesRead = fs.Read(buf, 0, buf.Length);

            if (bytesRead < TsReader.PacketSize) return;

            int[]? audioIndices = null;
            var reader = new TsReader();
            reader.SubscribePid(pmtPid);
            reader.SectionReady += (_, section) =>
            {
                if (audioIndices is not null) return;
                var pmt = PmtParser.ParsePmt(section);
                if (pmt is not null && pmt.AudioStreams.Count > 0)
                    audioIndices = pmt.AudioStreams.Select(a => a.PmtStreamIndex).ToArray();
            };
            reader.Feed(buf.AsSpan(0, bytesRead));

            if (audioIndices is null)
            {
                _logger.LogWarning("SAT>IP: PMT not found in first {Bytes} bytes of proxy file — audio stream indices may be wrong", bytesRead);
                return;
            }

            var audioStreams = MediaSource.MediaStreams.Where(s => s.Type == MediaStreamType.Audio).ToList();
            bool changed = false;
            for (int i = 0; i < audioIndices.Length && i < audioStreams.Count; i++)
            {
                if (audioIndices[i] > 0 && audioStreams[i].Index != audioIndices[i])
                {
                    audioStreams[i].Index = audioIndices[i];
                    changed = true;
                }
            }

            if (changed)
                _logger.LogInformation("SAT>IP: corrected audio stream indices from proxy PMT: [{Indices}]",
                    string.Join(", ", audioIndices));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SAT>IP: failed to read proxy PMT for audio index correction");
        }
    }
    public Task Close() => _proxy.CloseAsync();
    public void Dispose() { }

    private static string BuildPids(FreesatChannel channel)
    {
        // Request only PAT + this channel's PMT + its video + all audio elementary streams.
        // Requesting "pids=all" makes minisatip deliver the *entire* transponder — every channel
        // sharing the mux, potentially 5+ programs and dozens of streams — which blows the probe
        // budget and prevents ffmpeg from mapping the correct per-channel streams.
        if (channel.PmtPid is int pmtPid && channel.VideoPid is int videoPid)
        {
            var pids = $"0,{pmtPid},{videoPid}";
            foreach (var audio in channel.AudioStreams)
                pids += $",{audio.Pid}";
            return pids;
        }

        // Scan couldn't resolve this channel's PMT/PIDs (e.g. PAT/PMT collection timed out on a
        // busy mux) — fall back to the old behaviour rather than fail to play.
        return "all";
    }

    private static List<MediaStream> BuildMediaStreams(FreesatChannel channel, PluginConfiguration cfg)
    {
        var streams = new List<MediaStream>
        {
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
        };

        // Keep audio streams in PMT order — do NOT sort by IsAudioDescription here.
        // minisatip remaps the TS and assigns sequential PIDs (0x101, 0x102...) to elementary
        // streams in the order they appear in the broadcaster's PMT. ffmpeg then assigns stream
        // indices (0:1, 0:2...) in the same order. Our declared Index values must therefore match
        // PMT order: whatever the PMT puts first becomes stream 0:1 = our Index 1, and so on.
        // Sorting here would misalign our Index N with ffmpeg's 0:N and map wrong streams.
        // For BBC HD channels the PMT puts the nar/AD track first (stream 0:1) and the eng/AC-3
        // main audio second (stream 0:2). We set IsDefault on the first non-AD stream regardless
        // of its position, so the user always starts with the main programme audio by default.
        var audioStreams = channel.AudioStreams;

        if (audioStreams.Count == 0)
        {
            // PMT audio not resolved — declare a generic stereo stream as a hint.
            // Without a declared Channels count, EncodingHelper.GetNumAudioChannelsParam
            // (audioStream.Channels is null so its "clamp to input" branch never runs)
            // falls back to the client profile's max audio channels for the target codec
            // instead of the real source — against a Fire TV profile that meant ffmpeg
            // was told to transcode this channel's plain stereo MP2 audio into 8-channel
            // AAC. ffmpeg's ADTS muxer only supports up to 7 channels
            // (channelConfiguration > 7 is not supported in ADTS), so it failed outright
            // (exit code 183) on every single playback attempt on that client.
            streams.Add(new MediaStream
            {
                Type = MediaStreamType.Audio,
                Index = 1,
                IsDefault = true,
                Language = "eng",
                Channels = 2,
            });
        }
        else
        {
            bool defaultAssigned = false;
            for (int i = 0; i < audioStreams.Count; i++)
            {
                var audio = audioStreams[i];
                bool isDefault = !audio.IsAudioDescription && !defaultAssigned;
                if (isDefault) defaultAssigned = true;

                // Use the PMT-order stream index when the channel was scanned with the new
                // PmtStreamIndex field (non-zero). For older scan data (PmtStreamIndex=0) we
                // fall back to 1+i and rely on TryFixAudioIndicesFromProxy() in Open() to
                // correct it at stream-open time once the live PMT is readable.
                int streamIndex = audio.PmtStreamIndex > 0 ? audio.PmtStreamIndex : 1 + i;

                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = streamIndex,
                    IsDefault = isDefault,
                    // Freesat's SD/HD channels are overwhelmingly plain stereo MP2/AAC; 2 is a
                    // safe best-effort default. The same Channels=2 constraint that prevents the
                    // 8-channel AAC transcode failure above applies to every audio track here.
                    Channels = 2,
                    Language = string.IsNullOrEmpty(audio.Language) ? "eng" : audio.Language,
                    Title = audio.IsAudioDescription ? "Audio Description" : null,
                });
            }

            // Safety: if every track is AD (unlikely) make the first one the default so
            // playback doesn't start muted.
            if (!defaultAssigned && streams.Count > 1)
                streams[1].IsDefault = true;
        }

        return streams;
    }
}
