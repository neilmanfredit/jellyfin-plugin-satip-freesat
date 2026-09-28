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
    private readonly string _generationPath;
    private readonly ILogger<FreesatChannelStore> _logger;
    private ScanResult? _cache;

    public FreesatChannelStore(IApplicationPaths appPaths, ILogger<FreesatChannelStore> logger)
    {
        _logger = logger;
        _cachePath = Path.Combine(appPaths.DataPath, "satip-freesat-channels.json");
        _generationPath = Path.Combine(appPaths.DataPath, "satip-freesat-scan.generation");
    }

    public ScanResult? Current => _cache ??= TryLoad();

    /// <summary>
    /// Claims the "most recent scan" slot and returns a token to pass to <see cref="SaveAsync"/>.
    /// The token is persisted to disk (not just in memory) so it stays valid across a plugin
    /// hot-reload: if Jellyfin swaps in a new plugin DLL mid-scan, any scan task still running
    /// under the old assembly holds a stale token and its eventual <see cref="SaveAsync"/> call
    /// will be rejected once a newer scan has called <see cref="BeginScan"/>.
    /// </summary>
    public string BeginScan()
    {
        var token = Guid.NewGuid().ToString("N");
        try { File.WriteAllText(_generationPath, token); }
        catch (Exception ex) { _logger.LogWarning(ex, "SAT>IP Freesat: could not write scan generation token"); }
        return token;
    }

    /// <summary>
    /// Saves scan results, unless a newer scan has since called <see cref="BeginScan"/> — in
    /// which case the results are discarded to avoid a stale/superseded scan overwriting the
    /// output of a scan that started after it. Returns false when the save was discarded.
    /// </summary>
    public async Task<bool> SaveAsync(ScanResult result, string scanToken, CancellationToken ct = default)
    {
        if (!IsCurrentScan(scanToken))
        {
            _logger.LogWarning(
                "SAT>IP Freesat: discarding scan results — a newer scan has started since this one began");
            return false;
        }

        _cache = result;
        var json = JsonSerializer.Serialize(result, JsonOpts);
        await File.WriteAllTextAsync(_cachePath, json, ct).ConfigureAwait(false);
        _logger.LogInformation("SAT>IP Freesat: saved {Count} channels to {Path}", result.Channels.Count, _cachePath);
        return true;
    }

    private bool IsCurrentScan(string token)
    {
        try
        {
            return File.Exists(_generationPath) && File.ReadAllText(_generationPath).Trim() == token;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SAT>IP Freesat: could not read scan generation token — allowing save");
            return true; // fail open — a broken check shouldn't block a legitimate save
        }
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

    public void Invalidate()
    {
        _cache = null;
        try { if (File.Exists(_cachePath)) File.Delete(_cachePath); }
        catch (Exception ex) { _logger.LogWarning(ex, "SAT>IP Freesat: could not delete channel cache at {Path}", _cachePath); }
    }
}
