using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SatIpFreesat.DvbSi;
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
    private readonly int? _pmtPid;
    // PIDs that are allowed through the filter (PAT + PMT + video + audio). Null = no filtering
    // (pids=all fallback for channels whose PMT couldn't be resolved at scan time).
    private readonly HashSet<int>? _allowedPids;
    private readonly CancellationTokenSource _lifetimeCts = new();

    private RtspClient? _client;
    private FileStream? _fileStream;
    private Task? _pumpTask;
    private Task? _keepAliveTask;
    private bool _videoSynced;
    private bool _opened;
    // Cached 188-byte TS packet containing the rewritten PMT (audio+video only).
    private byte[]? _cleanPmtPacket;
    private int _pmtContinuityCounter;

    public string FilePath { get; }

    public SatIpStreamProxy(
        ILogger logger, string host, int port, SatIpMuxParams muxParams, string pids, int? videoPid,
        int? pmtPid = null)
    {
        _logger = logger;
        _host = host;
        _port = port;
        _muxParams = muxParams;
        _pids = pids;
        _videoPid = videoPid;
        _pmtPid = pmtPid;
        _allowedPids = ParseAllowedPids(pids);
        FilePath = Path.Combine(Path.GetTempPath(), $"satip-live-{Guid.NewGuid():N}.ts");
    }

    private static HashSet<int>? ParseAllowedPids(string pids)
    {
        if (pids == "all") return null; // no filtering for whole-transponder fallback
        var allowed = new HashSet<int>();
        foreach (var part in pids.Split(','))
            if (int.TryParse(part.Trim(), out int pid))
                allowed.Add(pid);
        return allowed.Count > 0 ? allowed : null;
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
    /// Periodically sends both an RTCP Receiver Report and an RTSP OPTIONS (session-level
    /// keep-alive). The RTSP request is what actually keeps the session alive: this tuner runs
    /// minisatip, whose 30s per-session stream timeout only resets when it successfully parses
    /// a *recognized* RTSP method tied to the session — and its parser doesn't recognize
    /// GET_PARAMETER (see RtspClient.SendKeepAliveAsync for the source-level finding). RTCP
    /// Receiver Reports are sent too, belt-and-suspenders, since they're the RFC 3550-standard
    /// liveness signal and cost nothing — but confirmed (via minisatip's own source) to not be
    /// what this tuner's timeout logic actually consults. RTCP is sent first and is
    /// fire-and-forget over its own UDP socket, so a slow/non-responding RTSP round trip
    /// (bounded by RtspClient's own timeout) can't delay or skip it.
    /// </summary>
    private async Task KeepAliveAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(5);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                await _client!.SendRtcpReceiverReportAsync(ct).ConfigureAwait(false);
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
        if (_videoPid is not int)
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

                await WritePidFilteredAsync(payload, openCt).ConfigureAwait(false);
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

                await WritePidFilteredAsync(payload, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SAT>IP: live stream pump loop ended unexpectedly");
        }
    }

    /// <summary>
    /// Writes TS packets from <paramref name="tsData"/> to the proxy file with three filters:
    /// (1) drops pre-keyframe video packets (keyframe sync); (2) drops PIDs that aren't in the
    /// allowed set (subtitle, teletext, data — which ffmpeg would otherwise count in its global
    /// stream index numbering, misaligning Jellyfin's audio -map arguments); (3) substitutes
    /// PMT packets with a rewritten version that lists only video and audio ES entries, ensuring
    /// ffmpeg sees audio streams at consecutive indices immediately after video.
    /// </summary>
    private async Task WritePidFilteredAsync(byte[] tsData, CancellationToken ct)
    {
        int runStart = -1;

        for (int off = 0; off + 188 <= tsData.Length; off += 188)
        {
            if (tsData[off] != 0x47) continue;

            int pid = ((tsData[off + 1] & 0x1F) << 8) | tsData[off + 2];

            // 1. Keyframe sync: drop video packets until we see a random-access point.
            if (!_videoSynced && pid == _videoPid)
            {
                if (IsRandomAccessPoint(tsData.AsSpan(off, 188)))
                    _videoSynced = true;
                else
                {
                    if (runStart >= 0)
                    {
                        await _fileStream!.WriteAsync(tsData.AsMemory(runStart, off - runStart), ct).ConfigureAwait(false);
                        runStart = -1;
                    }
                    continue;
                }
            }

            // 2. PMT rewriting: replace the broadcaster's PMT with a stripped version that
            //    lists only video and audio ES entries. This prevents ffmpeg from assigning
            //    a global stream index to subtitle/teletext PIDs and pushing audio indices up.
            if (_pmtPid is int pmtPid && pid == pmtPid)
            {
                if (runStart >= 0)
                {
                    await _fileStream!.WriteAsync(tsData.AsMemory(runStart, off - runStart), ct).ConfigureAwait(false);
                    runStart = -1;
                }

                if (_cleanPmtPacket is null)
                {
                    var section = ExtractSectionFromTsPacket(tsData.AsSpan(off, 188));
                    if (!section.IsEmpty)
                    {
                        var cleanSection = PmtParser.RebuildPmtReordered(section);
                        if (cleanSection is not null)
                            _cleanPmtPacket = PmtParser.BuildPmtTsPacket(cleanSection, pmtPid);
                    }
                }

                if (_cleanPmtPacket is not null)
                {
                    var pmtPkt = (byte[])_cleanPmtPacket.Clone();
                    pmtPkt[3] = (byte)((pmtPkt[3] & 0xF0) | (_pmtContinuityCounter & 0x0F));
                    _pmtContinuityCounter = (_pmtContinuityCounter + 1) & 0x0F;
                    await _fileStream!.WriteAsync(pmtPkt, ct).ConfigureAwait(false);
                }
                else
                {
                    // PMT couldn't be parsed yet — pass original as fallback
                    await _fileStream!.WriteAsync(tsData.AsMemory(off, 188), ct).ConfigureAwait(false);
                }
                continue;
            }

            // 3. PID allow-list: drop everything that isn't PAT, PMT, video, or audio.
            if (_allowedPids is not null && !_allowedPids.Contains(pid))
            {
                if (runStart >= 0)
                {
                    await _fileStream!.WriteAsync(tsData.AsMemory(runStart, off - runStart), ct).ConfigureAwait(false);
                    runStart = -1;
                }
                continue;
            }

            // Keep this packet — accumulate contiguous runs for efficient bulk writes.
            if (runStart < 0)
                runStart = off;
        }

        if (runStart >= 0)
            await _fileStream!.WriteAsync(tsData.AsMemory(runStart, tsData.Length - runStart), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts the first SI section from a TS packet that has the payload_unit_start_indicator
    /// set. Returns an empty span if the packet carries no new section start.
    /// </summary>
    private static ReadOnlySpan<byte> ExtractSectionFromTsPacket(ReadOnlySpan<byte> pkt)
    {
        bool pusi = (pkt[1] & 0x40) != 0;
        if (!pusi) return ReadOnlySpan<byte>.Empty;

        int afc = (pkt[3] >> 4) & 0x03;
        bool hasPayload = (afc & 0x01) != 0;
        if (!hasPayload) return ReadOnlySpan<byte>.Empty;

        int payloadStart = 4;
        if ((afc & 0x02) != 0) // has adaptation field
            payloadStart = 5 + pkt[4];
        if (payloadStart >= 188) return ReadOnlySpan<byte>.Empty;

        int pointerField = pkt[payloadStart];
        int sectionStart = payloadStart + 1 + pointerField;
        if (sectionStart >= 188) return ReadOnlySpan<byte>.Empty;

        return pkt.Slice(sectionStart, 188 - sectionStart);
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
