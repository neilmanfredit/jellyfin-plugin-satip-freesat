using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.Plugin.SatIpFreesat.Freesat;

/// <summary>
/// Shared cache of XMLTV-sourced programs, keyed by our own <see cref="FreesatChannel.ChannelId"/>.
/// Populated by <see cref="XmltvEpgCollectorService"/>'s periodic background refresh and read
/// synchronously by <see cref="FreesatEpgProvider"/> to fill in guide depth beyond what OTA
/// EIT delivers. Singleton so both sides see the same data.
/// </summary>
public sealed class XmltvEpgCache
{
    private readonly ConcurrentDictionary<string, List<ProgramInfo>> _byChannel = new();

    public DateTime? LastFetchedUtc { get; private set; }
    public string? LastError { get; private set; }
    public int MappedChannelCount { get; private set; }
    public int TotalProgramCount { get; private set; }

    public bool TryGet(string channelId, out List<ProgramInfo> programs)
    {
        if (_byChannel.TryGetValue(channelId, out var found))
        {
            programs = found;
            return true;
        }

        programs = [];
        return false;
    }

    public void ReplaceAll(Dictionary<string, List<ProgramInfo>> byChannel, int mappedChannelCount)
    {
        // Swap wholesale rather than merge — a stale channel that dropped out of this pass
        // (renamed upstream, removed from the feed) shouldn't keep serving last cycle's data.
        _byChannel.Clear();
        foreach (var (channelId, programs) in byChannel) _byChannel[channelId] = programs;

        LastFetchedUtc = DateTime.UtcNow;
        LastError = null;
        MappedChannelCount = mappedChannelCount;
        TotalProgramCount = byChannel.Values.Sum(l => l.Count);
    }

    public void SetError(string error) => LastError = error;
}
