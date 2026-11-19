using System;
using Vda5050AgvSimulator.Services.Agv;

namespace Vda5050AgvSimulator;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    public App()
    {
        ConfigStoreService.Instance.EnsureLoaded();

        MapStoreService.Initialize();

        ConfigStoreService.Instance.StartMapConfigWatcher();

        ConfigStoreService.Instance.MapConfigReloadFailed += (_, message) =>
        {
            Console.WriteLine($@"地图配置重新加载失败：{message}");
        };
    }
}