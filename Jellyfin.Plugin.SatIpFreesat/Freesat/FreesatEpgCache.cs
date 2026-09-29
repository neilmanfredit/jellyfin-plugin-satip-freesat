using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.Plugin.SatIpFreesat.Freesat;

/// <summary>
/// Shared per-mux EIT cache, keyed by <see cref="MuxInfo.Key"/>. Populated by
/// <see cref="FreesatEpgCollectorService"/> in the background with long (multi-minute)
/// per-mux collection windows, and read synchronously by <see cref="FreesatEpgProvider"/>
/// when Jellyfin's guide refresh asks for programs. Singleton so both sides see the same data.
/// </summary>
public sealed class FreesatEpgCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public bool TryGet(string muxKey, out List<ProgramInfo> programs)
    {
        if (_entries.TryGetValue(muxKey, out var entry))
        {
            programs = entry.Programs;
            return true;
        }

        programs = [];
        return false;
    }

    public void Set(string muxKey, List<ProgramInfo> programs)
        => _entries[muxKey] = new Entry(DateTime.UtcNow, programs);

    private sealed record Entry(DateTime CollectedAtUtc, List<ProgramInfo> Programs);
}
