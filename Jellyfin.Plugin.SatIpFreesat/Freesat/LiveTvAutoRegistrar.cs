using System;
using System.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SatIpFreesat.Freesat;

/// <summary>
/// Jellyfin's web UI does not offer a way to add a third-party plugin's <c>ITunerHost</c>
/// or <c>IListingsProvider</c> from the "Add Tuner Device" / "Add TV Guide Data Provider"
/// dialogs — those dropdowns only list Jellyfin's built-in types. So instead of relying on
/// the user to register us there (they can't), we write ourselves directly into the
/// server's "livetv" configuration once we have a working server address, exactly like the
/// dialogs would have done on the user's behalf.
/// </summary>
public static class LiveTvAutoRegistrar
{
    private const string TypeKey = "satip-freesat";
    private const string LiveTvConfigKey = "livetv";

    public static void EnsureRegistered(
        IConfigurationManager configManager, ILogger logger, string host, int rtspPort)
    {
        try
        {
            var options = configManager.GetConfiguration<LiveTvOptions>(LiveTvConfigKey);
            var url = $"rtsp://{host}:{rtspPort}";
            bool changed = false;

            var tuners = options.TunerHosts?.ToList() ?? [];
            var tuner = tuners.FirstOrDefault(t => t.Type == TypeKey);
            if (tuner is null)
            {
                tuners.Add(new TunerHostInfo
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Type = TypeKey,
                    Url = url,
                    FriendlyName = "SAT>IP Freesat",
                    AllowHWTranscoding = false,
                    AllowStreamSharing = true,
                });
                options.TunerHosts = tuners.ToArray();
                changed = true;
                logger.LogInformation("SAT>IP Freesat: registered tuner device in Live TV settings");
            }
            else if (tuner.Url != url)
            {
                tuner.Url = url;
                options.TunerHosts = tuners.ToArray();
                changed = true;
            }

            var providers = options.ListingProviders?.ToList() ?? [];
            if (providers.All(p => p.Type != TypeKey))
            {
                providers.Add(new ListingsProviderInfo
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Type = TypeKey,
                    EnableAllTuners = true,
                });
                options.ListingProviders = providers.ToArray();
                changed = true;
                logger.LogInformation("SAT>IP Freesat: registered TV guide data provider in Live TV settings");
            }

            if (changed)
                configManager.SaveConfiguration(LiveTvConfigKey, options);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SAT>IP Freesat: could not auto-register Live TV tuner/EPG provider");
        }
    }
}
