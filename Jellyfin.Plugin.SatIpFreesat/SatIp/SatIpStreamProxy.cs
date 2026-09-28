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
/// *discarding* data until we see the next keyframe boundary before ffmpeg ever starts reading,
/// ffmpeg's very first bytes are already a clean decode point, eliminating that wait.
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
        _client = new RtspClient(_host, _port, _logger);
        await _client.ConnectAsync(openCt).ConfigureAwait(false);
        await _client.SetupAndPlayAsync(_muxParams, _pids, openCt).ConfigureAwait(false);

        _fileStream = new FileStream(
            FilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);

        await SyncToKeyframeAsync(openCt).ConfigureAwait(false);

        _pumpTask = Task.Run(() => PumpAsync(_lifetimeCts.Token));
    }

    /// <summary>
    /// Discards incoming TS data until a random-access-indicator packet on the video PID is
    /// seen (i.e. the start of a GOP), then writes that packet onward. Bounded by
    /// <see cref="SyncTimeout"/> so a mux that never sets the flag can't hang Open() forever —
    /// if it times out, streaming just starts from whatever arrives next, matching the old
    /// (pre-proxy) behaviour.
    /// </summary>
    private async Task SyncToKeyframeAsync(CancellationToken openCt)
    {
        if (_videoPid is not int videoPid)
        {
            // Unknown video PID (pids=all fallback) — can't target a specific PID's keyframes.
            return;
        }

        using var syncCts = CancellationTokenSource.CreateLinkedTokenSource(openCt);
        syncCts.CancelAfter(SyncTimeout);
        try
        {
            while (true)
            {
                var payload = await _client!.ReadRtpPacketAsync(syncCts.Token).ConfigureAwait(false);
                if (payload is null) return; // stream ended before we ever synced
                if (payload.Length == 0) continue;

                if (ContainsRandomAccessPoint(payload, videoPid))
                {
                    await _fileStream!.WriteAsync(payload, openCt).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!openCt.IsCancellationRequested)
        {
            _logger.LogInformation(
                "SAT>IP: no keyframe boundary seen within {Timeout}s — starting stream without pre-sync",
                SyncTimeout.TotalSeconds);
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

                await _fileStream!.WriteAsync(payload, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SAT>IP: live stream pump loop ended unexpectedly");
        }
    }

    /// <summary>
    /// True if any 188-byte TS packet for <paramref name="targetPid"/> in this RTP payload
    /// carries adaptation_field.random_access_indicator — the broadcaster-signalled start of a
    /// GOP/keyframe, per ISO 13818-1.
    /// </summary>
    private static bool ContainsRandomAccessPoint(ReadOnlySpan<byte> tsData, int targetPid)
    {
        for (int off = 0; off + 188 <= tsData.Length; off += 188)
        {
            var pkt = tsData.Slice(off, 188);
            if (pkt[0] != 0x47) continue;

            int pid = ((pkt[1] & 0x1F) << 8) | pkt[2];
            if (pid != targetPid) continue;

            int adaptationFieldControl = (pkt[3] >> 4) & 0x3;
            if (adaptationFieldControl is 2 or 3 && pkt[4] > 0 && (pkt[5] & 0x40) != 0)
                return true;
        }

        return false;
    }

    public async Task CloseAsync()
    {
        _lifetimeCts.Cancel();
        if (_pumpTask is not null)
        {
            try { await _pumpTask.ConfigureAwait(false); }
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
