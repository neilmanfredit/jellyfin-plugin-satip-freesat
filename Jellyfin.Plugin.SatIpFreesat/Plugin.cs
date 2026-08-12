using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SatIpFreesat.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SatIpFreesat;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "SAT>IP Freesat";
    public override Guid Id => Guid.Parse("9d4f3a1c-8c7e-4b5d-a6f0-2e9b1c8d7a3f");
    public override string Description => "SAT>IP tuner and UK Freesat EPG source — no tvheadend required";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "satipfreesatconfig",
            DisplayName = "SAT>IP Freesat",
            EmbeddedResourcePath = $"{GetType().Namespace}.Web.configpage.html",
            EnableInMainMenu = true,
        },
        new PluginPageInfo
        {
            Name = "satipfreesatconfigjs",
            EmbeddedResourcePath = $"{GetType().Namespace}.Web.configpage.js",
        },
        new PluginPageInfo
        {
            Name = "satipfreesatstatus",
            DisplayName = "SAT>IP Freesat Status",
            EmbeddedResourcePath = $"{GetType().Namespace}.Web.statuspage.html",
            EnableInMainMenu = true,
        },
        new PluginPageInfo
        {
            Name = "satipfreesatstatusjs",
            EmbeddedResourcePath = $"{GetType().Namespace}.Web.statuspage.js",
        },
    ];
}
