using Jellyfin.Plugin.SatIpFreesat.Freesat;
using Jellyfin.Plugin.SatIpFreesat.SatIp;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SatIpFreesat;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<FreesatChannelStore>();
        serviceCollection.AddSingleton<FreesatScanner>();
        serviceCollection.AddSingleton<ScanJobService>();
        serviceCollection.AddSingleton<SatIpTunerHost>();
        serviceCollection.AddSingleton<ITunerHost>(sp => sp.GetRequiredService<SatIpTunerHost>());
        serviceCollection.AddSingleton<FreesatEpgCache>();
        serviceCollection.AddSingleton<XmltvEpgCache>();
        serviceCollection.AddSingleton<XmltvEpgSource>();
        serviceCollection.AddSingleton<FreesatEpgProvider>();
        serviceCollection.AddSingleton<IListingsProvider>(sp => sp.GetRequiredService<FreesatEpgProvider>());
        serviceCollection.AddSingleton<XmltvEpgCollectorService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<XmltvEpgCollectorService>());
        serviceCollection.AddHostedService<ScanSchedulerService>();
        serviceCollection.AddHostedService<FreesatEpgCollectorService>();
    }
}
