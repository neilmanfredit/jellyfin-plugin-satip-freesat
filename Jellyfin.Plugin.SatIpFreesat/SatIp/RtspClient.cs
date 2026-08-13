using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SatIpFreesat.SatIp;

/// <summary>
/// Minimal RTSP/1.0 client implementing the SAT>IP tuning protocol (ETSI TS 102 034).
/// Supports interleaved RTP-over-TCP for reading the MPEG-TS stream during SI scanning.
/// </summary>
public sealed class RtspClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger _logger;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _cseq;
    private string? _sessionId;
    private string? _controlUrl;

    public RtspClient(string host, int port, ILogger logger)
    {
        _host = host;
        _port = port;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        _stream = _tcp.GetStream();
        _logger.LogDebug("SAT>IP RTSP: connected to {Host}:{Port}", _host, _port);
    }

    /// <summary>
    /// Sends DESCRIBE + SETUP + PLAY for the given mux/PID parameters.
    /// Returns the session ID on success.
    /// </summary>
    public async Task<string> SetupAndPlayAsync(SatIpMuxParams mux, string pids, CancellationToken ct)
    {
        var describeUrl = BuildDescribeUrl(mux, pids);
        _logger.LogDebug("SAT>IP RTSP DESCRIBE: {Url}", describeUrl);

        var sdp = await DescribeAsync(describeUrl, ct).ConfigureAwait(false);
        _controlUrl = ParseControlFromSdp(sdp, _host, _port);

        _logger.LogDebug("SAT>IP RTSP SETUP: {Control}", _controlUrl);
        await SetupAsync(ct).ConfigureAwait(false);

        _logger.LogDebug("SAT>IP RTSP PLAY: session={Session}", _sessionId);
        await PlayAsync(ct).ConfigureAwait(false);

        return _sessionId ?? string.Empty;
    }

    /// <summary>
    /// Sends RTSP TEARDOWN and closes the connection.
    /// </summary>
    public async Task TeardownAsync(CancellationToken ct = default)
    {
        if (_controlUrl is not null && _sessionId is not null)
        {
            try { await SendRequestAsync("TEARDOWN", _controlUrl, null, ct).ConfigureAwait(false); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Reads a single interleaved RTP packet from the TCP stream.
    /// Returns the raw MPEG-TS payload (RTP header stripped).
    /// Returns null on stream end or RTSP data.
    /// </summary>
    public async Task<byte[]?> ReadRtpPacketAsync(CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected");

        // Interleaved format: $ channel(1) length_hi(1) length_lo(1) [data]
        var header = new byte[4];
        int read = 0;
        while (read < 4)
        {
            int n = await _stream.ReadAsync(header.AsMemory(read, 4 - read), ct).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }

        if (header[0] != 0x24) // '$'
        {
            // Got an RTSP response or stray bytes - skip to end of headers
            await SkipRtspResponseAsync(ct).ConfigureAwait(false);
            return Array.Empty<byte>(); // signal "skip" not "stream ended"
        }

        int channel = header[1];
        int length = (header[2] << 8) | header[3];
        var packet = new byte[length];
        int got = 0;
        while (got < length)
        {
            int n = await _stream.ReadAsync(packet.AsMemory(got, length - got), ct).ConfigureAwait(false);
            if (n == 0) return null;
            got += n;
        }

        // Channel 1 = RTCP (keep-alives, ignore payload); channel 0 = RTP data
        // Return empty (not null) so callers can continue reading rather than terminating the loop.
        if (channel != 0) return Array.Empty<byte>();

        // Strip 12-byte RTP fixed header
        if (length < 12) return null;

        // Account for CSRC list (CC * 4 bytes after the fixed header)
        int cc = packet[0] & 0x0F;
        int rtpHeaderLen = 12 + cc * 4;

        // Extension header
        if ((packet[0] & 0x10) != 0 && length > rtpHeaderLen + 3)
        {
            int extLen = ((packet[rtpHeaderLen + 2] << 8) | packet[rtpHeaderLen + 3]) * 4 + 4;
            rtpHeaderLen += extLen;
        }

        if (rtpHeaderLen >= length) return null;
        return packet[rtpHeaderLen..];
    }

    // ---- private ----

    private string BuildDescribeUrl(SatIpMuxParams mux, string pids)
    {
        var sb = new StringBuilder($"rtsp://{_host}:{_port}/?src={mux.FrontendNumber}");
        sb.Append($"&freq={mux.FrequencyMHz:F3}");
        sb.Append($"&pol={mux.Polarization}");
        sb.Append($"&msys={mux.MsysParam}");
        sb.Append($"&sr={(int)mux.SymbolRateKsym}");
        sb.Append("&fec=auto");
        if (mux.IsDvbS2)
            sb.Append("&ro=0.35&mtype=qpsk");
        sb.Append($"&pids={pids}");
        return sb.ToString();
    }

    private async Task<string> DescribeAsync(string url, CancellationToken ct)
    {
        var response = await SendRequestAsync("DESCRIBE", url, new Dictionary<string, string>
        {
            ["Accept"] = "application/sdp",
        }, ct).ConfigureAwait(false);
        return response.Body;
    }

    private async Task SetupAsync(CancellationToken ct)
    {
        var url = _controlUrl ?? throw new InvalidOperationException("No control URL from DESCRIBE");
        var response = await SendRequestAsync("SETUP", url, new Dictionary<string, string>
        {
            ["Transport"] = "RTP/AVP/TCP;unicast;interleaved=0-1",
        }, ct).ConfigureAwait(false);

        // Extract session ID
        if (response.Headers.TryGetValue("session", out var sess))
            _sessionId = sess.Split(';')[0].Trim();
    }

    private async Task PlayAsync(CancellationToken ct)
    {
        var url = _controlUrl ?? throw new InvalidOperationException("No control URL");
        await SendRequestAsync("PLAY", url, new Dictionary<string, string>
        {
            ["Range"] = "npt=0.000-",
        }, ct).ConfigureAwait(false);
    }

    private async Task<RtspResponse> SendRequestAsync(
        string method, string url,
        Dictionary<string, string>? extraHeaders,
        CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected");

        int seq = ++_cseq;
        var sb = new StringBuilder();
        sb.Append($"{method} {url} RTSP/1.0\r\n");
        sb.Append($"CSeq: {seq}\r\n");
        if (_sessionId is not null)
            sb.Append($"Session: {_sessionId}\r\n");
        if (extraHeaders is not null)
            foreach (var (k, v) in extraHeaders)
                sb.Append($"{k}: {v}\r\n");
        sb.Append("\r\n");

        var reqBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await _stream.WriteAsync(reqBytes, ct).ConfigureAwait(false);

        return await ReadResponseAsync(ct).ConfigureAwait(false);
    }

    private async Task<RtspResponse> ReadResponseAsync(CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int contentLength = 0;
        int statusCode = 0;

        // Read headers line by line; handle both \r\n (standard) and \n-only (some embedded devices)
        var lineBuffer = new List<byte>(256);
        while (true)
        {
            var b = await ReadByteAsync(ct).ConfigureAwait(false);
            if (b is (byte)'\r' or (byte)'\n')
            {
                if (b == (byte)'\r') await ReadByteAsync(ct).ConfigureAwait(false); // consume '\n'
                if (lineBuffer.Count == 0) break; // blank line = end of headers
                var line = Encoding.ASCII.GetString(lineBuffer.ToArray());
                lineBuffer.Clear();

                if (line.StartsWith("RTSP/", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ', 3);
                    if (parts.Length >= 2) int.TryParse(parts[1], out statusCode);
                }
                else
                {
                    var idx = line.IndexOf(':', StringComparison.Ordinal);
                    if (idx > 0)
                    {
                        var key = line[..idx].Trim();
                        var val = line[(idx + 1)..].Trim();
                        headers[key] = val;
                        if (key.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(val, out contentLength);
                    }
                }
            }
            else
            {
                lineBuffer.Add(b);
            }
        }

        var body = string.Empty;
        if (contentLength > 0)
        {
            var buf = new byte[contentLength];
            int got = 0;
            while (got < contentLength)
            {
                int n = await _stream.ReadAsync(buf.AsMemory(got, contentLength - got), ct).ConfigureAwait(false);
                if (n == 0) break;
                got += n;
            }
            body = Encoding.UTF8.GetString(buf, 0, got);
        }

        if (statusCode is not 200 and not 0)
            _logger.LogWarning("SAT>IP RTSP: status {Code} in response", statusCode);

        return new RtspResponse(statusCode, headers, body);
    }

    private async Task SkipRtspResponseAsync(CancellationToken ct)
    {
        // Discard bytes until we see \r\n\r\n (end of headers)
        int consecutive = 0;
        while (consecutive < 4)
        {
            var b = await ReadByteAsync(ct).ConfigureAwait(false);
            if (b is (byte)'\r' or (byte)'\n') consecutive++;
            else consecutive = 0;
        }
    }

    private async Task<byte> ReadByteAsync(CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected");
        var buf = new byte[1];
        int n = await _stream.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
        if (n == 0) throw new EndOfStreamException("RTSP stream ended");
        return buf[0];
    }

    private static string ParseControlFromSdp(string sdp, string host, int port)
    {
        // Look for: a=control:stream=N
        foreach (var line in sdp.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("a=control:", StringComparison.OrdinalIgnoreCase))
            {
                var val = t["a=control:".Length..].Trim();
                if (val.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
                    return val;
                // Relative — prepend server base
                return $"rtsp://{host}:{port}/{val.TrimStart('/')}";
            }
        }
        // Fallback: use /stream=1
        return $"rtsp://{host}:{port}/stream=1";
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false);
        _tcp?.Dispose();
    }

    private sealed record RtspResponse(int StatusCode, Dictionary<string, string> Headers, string Body);
}

/// <summary>Parameters needed to tune to a DVB-S/S2 mux via SAT>IP.</summary>
public sealed class SatIpMuxParams
{
    public int FrontendNumber { get; init; } = 1;
    public double FrequencyMHz { get; init; }
    public char Polarization { get; init; }  // 'h' or 'v' (SAT>IP uses lowercase)
    public double SymbolRateKsym { get; init; }
    public bool IsDvbS2 { get; init; }
    public string MsysParam => IsDvbS2 ? "dvbs2" : "dvbs";

    public static SatIpMuxParams Bootstrap(int frontend) => new()
    {
        FrontendNumber = frontend,
        FrequencyMHz = 11425.0,
        Polarization = 'h',
        SymbolRateKsym = 27500,
        IsDvbS2 = false,
    };
}
