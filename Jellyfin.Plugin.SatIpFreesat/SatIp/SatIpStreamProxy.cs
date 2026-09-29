using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SatIpFreesat.SatIp;

/// <summary>
/// Pulls RTSP/RTP MPEG-TS data from the SAT>IP tuner ourselves and writes it into a local
/// growing file that ffmpeg then reads as a plain file source, instead of handing ffmpeg the
/// raw rtsp:// URL directly.
///
/// Why: when ffmpeg opens an rtsp:// URL itself, it has to start decoding wherever it happens
/// to join the live H.264 elementary stream — which, per spec, can't begin until the next
/// SPS+PPS+IDR ("keyframe") boundary arrives. That wait is random (anywhere from instant to
/// most of a GOP length) and occasionally exceeds the client's patience, producing "Source
/// error" even though the underlying tuner/signal is fine. By pulling the stream ourselves and
/// *discarding video packets* until we see the next keyframe boundary before ffmpeg ever starts
/// reading (PAT/PMT/audio keep flowing throughout), ffmpeg's very first bytes are already a
/// clean decode point with a complete stream to probe, eliminating that wait.
/// </summary>
public sealed class SatIpStreamProxy : IAsyncDisposable
{
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(6);

    private readonly ILogger _logger;
    private readonly string _host;
    private readonly int _port;
    private readonly SatIpMuxParams _muxParams;
    private readonly string _pids;
    private readonly int? _videoPid;
    private readonly CancellationTokenSource _lifetimeCts = new();

    private RtspClient? _client;
    private FileStream? _fileStream;
    private Task? _pumpTask;
    private Task? _keepAliveTask;
    private bool _videoSynced;
    private bool _opened;

    public string FilePath { get; }

    public SatIpStreamProxy(
        ILogger logger, string host, int port, SatIpMuxParams muxParams, string pids, int? videoPid)
    {
        _logger = logger;
        _host = host;
        _port = port;
        _muxParams = muxParams;
        _pids = pids;
        _videoPid = videoPid;
        FilePath = Path.Combine(Path.GetTempPath(), $"satip-live-{Guid.NewGuid():N}.ts");
    }

    public async Task OpenAsync(CancellationToken openCt)
    {
        // Jellyfin's newer direct-stream-provider live TV flow calls ITunerHost.GetChannelStream
        // and expects the stream to already be flowing by the time it returns — it never invokes
        // ILiveStream.Open() itself for this flow. SatIpTunerHost.GetChannelStream calls this
        // explicitly to satisfy that. Guard against a redundant call in case some other Jellyfin
        // version *does* still call Open() afterwards.
        if (_opened) return;
        _opened = true;

        _client = new RtspClient(_host, _port, _logger);

        // Live playback keeps this RTSP session open for as long as the user watches, which
        // means SendKeepAliveAsync has to run periodically for the whole duration (see
        // KeepAliveAsync below) — unlike the short-lived TCP-interleaved sessions used
        // elsewhere (e.g. FreesatScanner), which usually finish before a keep-alive is ever
        // due. In TCP interleaved mode, RTP data and RTSP control messages share one socket,
        // so the keep-alive's GET_PARAMETER response has to be spliced in between RTP frames
        // by the tuner — and testing showed the pump reliably stalling within ~10-20s of a
        // keep-alive being sent, consistent with that splice desyncing the interleaved framing
        // parser. UDP unicast avoids the shared socket entirely: RTP arrives on its own socket,
        // so the keep-alive can safely do a full request/response round trip on the RTSP TCP
        // control channel without any risk of colliding with the data stream.
        _client.EnableUdpTransport();
        await _client.ConnectAsync(openCt).ConfigureAwait(false);
        await _client.SetupAndPlayAsync(_muxParams, _pids, openCt).ConfigureAwait(false);

        _fileStream = new FileStream(
            FilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);

        await SyncToKeyframeAsync(openCt).ConfigureAwait(false);

        _pumpTask = Task.Run(() => PumpAsync(_lifetimeCts.Token));
        _keepAliveTask = Task.Run(() => KeepAliveAsync(_lifetimeCts.Token));
    }

    /// <summary>
    /// Periodically sends an RTSP GET_PARAMETER on the tuner's session so it doesn't expire
    /// mid-stream. The tuner's SETUP response advertises a session timeout (observed as low as
    /// 30s on some devices) and tears the session down — silently stopping RTP delivery, with
    /// no error surfaced here — if it goes that long without any request on the session. Nothing
    /// else touches the RTSP control channel once PLAY has been sent, so without this the pump
    /// loop just stalls forever partway through playback.
    /// </summary>
    private async Task KeepAliveAsync(CancellationToken ct)
    {
        // Fixed 15s safety margin before the advertised timeout, rather than a fraction of it —
        // a 30s timeout with a /2 margin only gave ~15s of slack for the very first keep-alive
        // to land, which observed testing showed wasn't reliably enough once SyncToKeyframeAsync
        // and scheduling jitter ate into it.
        var interval = TimeSpan.FromSeconds(Math.Max(5, _client!.SessionTimeout.TotalSeconds - 15));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                await _client.SendKeepAliveAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Waits until a random-access-indicator packet on the video PID is seen (i.e. the start of
    /// a GOP). While waiting, every packet is still written to the file EXCEPT video-PID packets
    /// (which would be a partial/undecodable GOP) — PAT/PMT/audio must keep flowing so ffmpeg's
    /// initial probe sees a complete stream (its hardcoded -map 0:0 -map 0:1 fails outright if
    /// the audio stream hasn't appeared yet). Bounded by <see cref="SyncTimeout"/> so a mux that
    /// never sets the flag can't hang Open() forever — if it times out, video packets just start
    /// flowing unfiltered, matching the old (pre-proxy) behaviour.
    /// </summary>
    private async Task SyncToKeyframeAsync(CancellationToken openCt)
    {
        if (_videoPid is not int videoPid)
        {
            // Unknown video PID (pids=all fallback) — can't target a specific PID's keyframes.
            _videoSynced = true;
            return;
        }

        using var syncCts = CancellationTokenSource.CreateLinkedTokenSource(openCt);
        syncCts.CancelAfter(SyncTimeout);
        try
        {
            while (!_videoSynced)
            {
                var payload = await _client!.ReadRtpPacketAsync(syncCts.Token).ConfigureAwait(false);
                if (payload is null) return; // stream ended before we ever synced
                if (payload.Length == 0) continue;

                await WriteFilteredAsync(payload, videoPid, openCt).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!openCt.IsCancellationRequested)
        {
            _logger.LogInformation(
                "SAT>IP: no keyframe boundary seen within {Timeout}s — starting stream without pre-sync",
                SyncTimeout.TotalSeconds);
            _videoSynced = true;
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? payload;
                try
                {
                    payload = await _client!.ReadRtpPacketAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (payload is null) break;
                if (payload.Length == 0) continue;

                if (_videoSynced || _videoPid is not int videoPid)
                {
                    await _fileStream!.WriteAsync(payload, ct).ConfigureAwait(false);
                }
                else
                {
                    await WriteFilteredAsync(payload, videoPid, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SAT>IP: live stream pump loop ended unexpectedly");
        }
    }

    /// <summary>
    /// Writes every 188-byte TS packet in <paramref name="tsData"/> to the file except
    /// video-PID packets before the first random-access point, which are dropped. Once a
    /// random-access point on the video PID is found, <see cref="_videoSynced"/> is set and
    /// that packet (and everything after it in this payload) is written normally.
    /// </summary>
    private async Task WriteFilteredAsync(byte[] tsData, int videoPid, CancellationToken ct)
    {
        int runStart = -1;

        for (int off = 0; off + 188 <= tsData.Length; off += 188)
        {
            bool isVideoPacket = tsData[off] == 0x47
                && (((tsData[off + 1] & 0x1F) << 8) | tsData[off + 2]) == videoPid;

            bool drop = isVideoPacket && !_videoSynced;

            if (drop && isVideoPacket && IsRandomAccessPoint(tsData.AsSpan(off, 188)))
            {
                _videoSynced = true;
                drop = false;
            }

            if (drop)
            {
                if (runStart >= 0)
                {
                    await _fileStream!.WriteAsync(tsData.AsMemory(runStart, off - runStart), ct).ConfigureAwait(false);
                    runStart = -1;
                }
            }
            else if (runStart < 0)
            {
                runStart = off;
            }
        }

        if (runStart >= 0)
        {
            await _fileStream!.WriteAsync(tsData.AsMemory(runStart, tsData.Length - runStart), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True if this single 188-byte TS packet carries adaptation_field.random_access_indicator —
    /// the broadcaster-signalled start of a GOP/keyframe, per ISO 13818-1. Caller has already
    /// confirmed the sync byte and PID match.
    /// </summary>
    private static bool IsRandomAccessPoint(ReadOnlySpan<byte> pkt)
    {
        int adaptationFieldControl = (pkt[3] >> 4) & 0x3;
        return adaptationFieldControl is 2 or 3 && pkt[4] > 0 && (pkt[5] & 0x40) != 0;
    }

    public async Task CloseAsync()
    {
        _lifetimeCts.Cancel();
        if (_pumpTask is not null)
        {
            try { await _pumpTask.ConfigureAwait(false); }
            catch { /* best-effort */ }
        }

        if (_keepAliveTask is not null)
        {
            try { await _keepAliveTask.ConfigureAwait(false); }
            catch { /* best-effort */ }
        }

        if (_client is not null)
        {
            await _client.TeardownAsync().ConfigureAwait(false);
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        if (_fileStream is not null)
            await _fileStream.DisposeAsync().ConfigureAwait(false);

        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch { /* best-effort cleanup */ }
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
