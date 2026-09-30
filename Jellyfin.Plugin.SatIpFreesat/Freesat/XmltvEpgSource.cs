using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SatIpFreesat.Freesat;

public sealed record XmltvFetchResult(
    Dictionary<string, List<ProgramInfo>> ProgramsByChannelId,
    int MappedChannelCount,
    int TotalPrograms);

/// <summary>
/// Downloads and parses a third-party XMLTV guide (gzip or plain XML) and maps its channels
/// onto our own Freesat-scanned channel list by name (see <see cref="XmltvChannelMapper"/>).
/// </summary>
/// <remarks>
/// This exists because DVB EIT "schedule" sub-tables carrying multi-day-out events are, on
/// this platform, effectively never observed over the air — confirmed via direct raw RTP
/// capture with zero packet loss over a full dwell window that still yielded zero schedule
/// sections (see project memory, 2026-09-29 session). Far-future guide depth can only come
/// from an IP-delivered source instead; <see cref="FreesatEpgCollectorService"/>/EIT remains
/// the source for near-term present/following, which the OTA broadcast does deliver reliably.
/// </remarks>
public sealed class XmltvEpgSource
{
    // XMLTV feeds like this run for minutes on a slow connection once decompressed and
    // parsed; a short default HttpClient timeout would abort mid-download on a cold cache.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };

    private readonly ILogger<XmltvEpgSource> _logger;

    public XmltvEpgSource(ILogger<XmltvEpgSource> logger) => _logger = logger;

    public async Task<XmltvFetchResult> FetchAsync(
        string url, IReadOnlyList<FreesatChannel> channels, CancellationToken ct)
    {
        await using var downloaded = await Http.GetStreamAsync(url, ct).ConfigureAwait(false);
        await using var buffered = new MemoryStream();
        await downloaded.CopyToAsync(buffered, ct).ConfigureAwait(false);
        buffered.Position = 0;

        Stream xmlStream = LooksGzip(buffered) || url.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(buffered, CompressionMode.Decompress)
            : buffered;

        var xmltvDisplayNamesById = new Dictionary<string, List<string>>();
        var reverseMap = new Dictionary<string, string>(); // xmltv channel id -> our ChannelId
        var wantedXmltvIds = new HashSet<string>();
        var programsByChannelId = new Dictionary<string, List<ProgramInfo>>();
        var channelsMapped = false;
        var mappedCount = 0;

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
        using var reader = XmlReader.Create(xmlStream, settings);

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;

            switch (reader.Name)
            {
                case "channel":
                    ReadChannelElement(reader, xmltvDisplayNamesById);
                    break;

                case "programme":
                {
                    // XMLTV lists every <channel> before the first <programme> (DTD-mandated
                    // ordering) — safe to finalize the name mapping the first time we see one.
                    if (!channelsMapped)
                    {
                        var mapping = XmltvChannelMapper.BuildMapping(channels, xmltvDisplayNamesById);
                        mappedCount = mapping.Count;
                        foreach (var (ourId, xmltvId) in mapping) reverseMap.TryAdd(xmltvId, ourId);
                        wantedXmltvIds = reverseMap.Keys.ToHashSet();
                        channelsMapped = true;
                        _logger.LogInformation(
                            "SAT>IP EPG (XMLTV): mapped {Mapped}/{Total} channels by name",
                            mappedCount, channels.Count);
                    }

                    var xmltvChannelId = reader.GetAttribute("channel");
                    if (xmltvChannelId is null || !wantedXmltvIds.Contains(xmltvChannelId))
                    {
                        reader.Skip();
                        continue;
                    }

                    var ourChannelId = reverseMap[xmltvChannelId];
                    var program = ReadProgrammeElement(reader, ourChannelId);
                    if (program is not null)
                    {
                        if (!programsByChannelId.TryGetValue(ourChannelId, out var list))
                            programsByChannelId[ourChannelId] = list = [];
                        list.Add(program);
                    }

                    break;
                }
            }
        }

        if (!channelsMapped)
        {
            // Feed had channels but no programmes at all — still surface the mapping count.
            mappedCount = XmltvChannelMapper.BuildMapping(channels, xmltvDisplayNamesById).Count;
        }

        return new XmltvFetchResult(
            programsByChannelId, mappedCount, programsByChannelId.Values.Sum(l => l.Count));
    }

    private static void ReadChannelElement(XmlReader reader, Dictionary<string, List<string>> namesById)
    {
        var id = reader.GetAttribute("id");
        var names = new List<string>();
        using var sub = reader.ReadSubtree();
        while (sub.Read())
        {
            if (sub.NodeType == XmlNodeType.Element && sub.Name == "display-name")
                names.Add(sub.ReadElementContentAsString());
        }

        if (!string.IsNullOrEmpty(id) && names.Count > 0) namesById[id] = names;
    }

    private static ProgramInfo? ReadProgrammeElement(XmlReader reader, string ourChannelId)
    {
        var start = ParseXmltvDate(reader.GetAttribute("start"));
        var stop = ParseXmltvDate(reader.GetAttribute("stop"));

        var title = string.Empty;
        var desc = string.Empty;
        var genres = new List<string>();

        using var sub = reader.ReadSubtree();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element) continue;
            switch (sub.Name)
            {
                case "title" when title.Length == 0:
                    title = sub.ReadElementContentAsString();
                    break;
                case "desc" when desc.Length == 0:
                    desc = sub.ReadElementContentAsString();
                    break;
                case "category":
                    genres.Add(sub.ReadElementContentAsString());
                    break;
            }
        }

        if (start is null || stop is null || stop <= start || title.Length == 0) return null;

        return new ProgramInfo
        {
            Id = $"xmltv-{ourChannelId}-{start:yyyyMMddHHmmss}",
            ChannelId = ourChannelId,
            Name = title,
            Overview = desc,
            Genres = genres,
            StartDate = start.Value,
            EndDate = stop.Value,
            IsLive = false,
            IsNews = genres.Exists(g => g.Contains("news", StringComparison.OrdinalIgnoreCase)),
        };
    }

    // XMLTV date format: "yyyyMMddHHmmss [+-]hhmm", e.g. "20260929224000 +0100".
    private static DateTime? ParseXmltvDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!DateTime.TryParseExact(
                parts[0], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;

        var offset = TimeSpan.Zero;
        if (parts.Length > 1 && parts[1].Length == 5 && (parts[1][0] == '+' || parts[1][0] == '-'))
        {
            var sign = parts[1][0] == '-' ? -1 : 1;
            if (int.TryParse(parts[1].AsSpan(1, 2), out var oh) && int.TryParse(parts[1].AsSpan(3, 2), out var om))
                offset = TimeSpan.FromMinutes(sign * (oh * 60 + om));
        }

        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset).UtcDateTime;
    }

    private static bool LooksGzip(Stream s)
    {
        if (!s.CanSeek || s.Length < 2) return false;
        var pos = s.Position;
        Span<byte> header = stackalloc byte[2];
        s.ReadExactly(header);
        s.Position = pos;
        return header[0] == 0x1F && header[1] == 0x8B;
    }
}
