using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SatIpFreesat.Freesat;

/// <summary>Persists and retrieves the result of a Freesat channel scan.</summary>
public sealed class FreesatChannelStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _cachePath;
    private readonly ILogger<FreesatChannelStore> _logger;
    private ScanResult? _cache;

    public FreesatChannelStore(IApplicationPaths appPaths, ILogger<FreesatChannelStore> logger)
    {
        _logger = logger;
        _cachePath = Path.Combine(appPaths.DataPath, "satip-freesat-channels.json");
    }

    public ScanResult? Current => _cache ??= TryLoad();

    public async Task SaveAsync(ScanResult result, CancellationToken ct = default)
    {
        _cache = result;
        var json = JsonSerializer.Serialize(result, JsonOpts);
        await File.WriteAllTextAsync(_cachePath, json, ct).ConfigureAwait(false);
        _logger.LogInformation("SAT>IP Freesat: saved {Count} channels to {Path}", result.Channels.Count, _cachePath);
    }

    private ScanResult? TryLoad()
    {
        if (!File.Exists(_cachePath))
            return null;
        try
        {
            var json = File.ReadAllText(_cachePath);
            return JsonSerializer.Deserialize<ScanResult>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SAT>IP Freesat: could not load channel cache from {Path}", _cachePath);
            return null;
        }
    }

    public void Invalidate() => _cache = null;
}
