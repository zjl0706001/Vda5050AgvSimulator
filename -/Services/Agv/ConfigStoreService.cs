using System;
using System.IO;
using System.Threading;
using Vda5050AgvSimulator.Models.Map;
using Vda5050AgvSimulator.Models.Mqtt;
using Vda5050AgvSimulator.Services.File;

namespace Vda5050AgvSimulator.Services.Agv;

/// <summary>
/// 配置运行期存储服务。
/// 用于统一缓存、读取、保存 MQTT 配置和地图配置。
/// 文件读写由 ConfigFileService 负责。
/// </summary>
public class ConfigStoreService
{
    private readonly object _lockObj = new();

    private MqttConfigModel? _mqttConfig;

    private MapModel? _mapConfig;

    private FileSystemWatcher? _mapFileWatcher;

    private Timer? _mapReloadDebounceTimer;

    private const int MapReloadDebounceMilliseconds = 600;

    /// <summary>
    /// 单例实例。
    /// </summary>
    public static ConfigStoreService Instance { get; } = new();

    /// <summary>
    /// MQTT配置变更事件。
    /// </summary>
    public event EventHandler<MqttConfigModel>? MqttConfigChanged;

    /// <summary>
    /// 地图配置变更事件。
    /// </summary>
    public event EventHandler<MapModel>? MapConfigChanged;

    /// <summary>
    /// 地图配置重新加载失败事件。
    /// </summary>
    public event EventHandler<string>? MapConfigReloadFailed;

    /// <summary>
    /// 私有构造，禁止外部 new。
    /// </summary>
    private ConfigStoreService()
    {
    }

    /// <summary>
    /// 当前MQTT配置。
    /// 首次访问时自动从 mqtt.json 加载。
    /// </summary>
    public MqttConfigModel MqttConfig
    {
        get
        {
            lock (_lockObj)
            {
                _mqttConfig ??= ConfigFileService.LoadMqttConfig();

                return _mqttConfig;
            }
        }
    }

    /// <summary>
    /// 当前地图配置。
    /// 首次访问时自动从 map.json 加载。
    /// </summary>
    public MapModel MapConfig
    {
        get
        {
            lock (_lockObj)
            {
                _mapConfig ??= ConfigFileService.LoadMap();

                return _mapConfig;
            }
        }
    }

    /// <summary>
    /// 确保配置已加载。
    /// 建议程序启动时调用一次。
    /// </summary>
    public void EnsureLoaded()
    {
        lock (_lockObj)
        {
            _mqttConfig ??= ConfigFileService.LoadMqttConfig();

            _mapConfig ??= ConfigFileService.LoadMap();
        }
    }

    /// <summary>
    /// 启动地图配置文件监听。
    /// 手动替换 map.json 后，会自动重新加载地图配置。
    /// </summary>
    public void StartMapConfigWatcher()
    {
        lock (_lockObj)
        {
            if (_mapFileWatcher != null)
            {
                return;
            }

            var mapFilePath = ConfigFileService.GetMapConfigFilePath();

            var mapDirectory = Path.GetDirectoryName(mapFilePath);

            var mapFileName = Path.GetFileName(mapFilePath);

            if (string.IsNullOrWhiteSpace(mapDirectory) || string.IsNullOrWhiteSpace(mapFileName))
            {
                return;
            }

            _mapReloadDebounceTimer = new Timer(
                OnMapReloadDebounceTimerElapsed,
                null,
                Timeout.Infinite,
                Timeout.Infinite);

            _mapFileWatcher = new FileSystemWatcher(mapDirectory, mapFileName)
            {
                IncludeSubdirectories = false,
                NotifyFilter =
                    NotifyFilters.FileName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size |
                    NotifyFilters.CreationTime
            };

            _mapFileWatcher.Changed += OnMapFileChanged;
            _mapFileWatcher.Created += OnMapFileChanged;
            _mapFileWatcher.Deleted += OnMapFileChanged;
            _mapFileWatcher.Renamed += OnMapFileRenamed;

            _mapFileWatcher.EnableRaisingEvents = true;
        }
    }

    /// <summary>
    /// 停止地图配置文件监听。
    /// 程序退出时可调用。
    /// </summary>
    public void StopMapConfigWatcher()
    {
        FileSystemWatcher? watcher;

        Timer? timer;

        lock (_lockObj)
        {
            watcher = _mapFileWatcher;

            timer = _mapReloadDebounceTimer;

            if (watcher != null)
            {
                watcher.EnableRaisingEvents = false;

                watcher.Changed -= OnMapFileChanged;
                watcher.Created -= OnMapFileChanged;
                watcher.Deleted -= OnMapFileChanged;
                watcher.Renamed -= OnMapFileRenamed;
            }

            _mapFileWatcher = null;

            _mapReloadDebounceTimer = null;
        }

        watcher?.Dispose();

        timer?.Dispose();
    }

    private void OnMapFileChanged(object sender, FileSystemEventArgs e)
    {
        RestartMapReloadDebounceTimer();
    }

    private void OnMapFileRenamed(object sender, RenamedEventArgs e)
    {
        RestartMapReloadDebounceTimer();
    }

    /// <summary>
    /// 延迟重新加载，避免文件还在复制过程中就读取。
    /// </summary>
    private void RestartMapReloadDebounceTimer()
    {
        lock (_lockObj)
        {
            _mapReloadDebounceTimer?.Change(
                MapReloadDebounceMilliseconds,
                Timeout.Infinite);
        }
    }

    private void OnMapReloadDebounceTimerElapsed(object? state)
    {
        if (TryReloadMapConfig(out _, out var message))
        {
            return;
        }

        MapConfigReloadFailed?.Invoke(this, message);
    }

    /// <summary>
    /// 重新加载所有配置。
    /// </summary>
    public void ReloadAll()
    {
        MqttConfigModel mqttConfig;

        MapModel mapConfig;

        lock (_lockObj)
        {
            _mqttConfig = ConfigFileService.LoadMqttConfig();

            _mapConfig = ConfigFileService.LoadMap();

            mqttConfig = _mqttConfig;

            mapConfig = _mapConfig;
        }

        MqttConfigChanged?.Invoke(this, mqttConfig);

        MapConfigChanged?.Invoke(this, mapConfig);
    }

    /// <summary>
    /// 重新加载MQTT配置。
    /// </summary>
    public MqttConfigModel ReloadMqttConfig()
    {
        MqttConfigModel config;

        lock (_lockObj)
        {
            _mqttConfig = ConfigFileService.LoadMqttConfig();

            config = _mqttConfig;
        }

        MqttConfigChanged?.Invoke(this, config);

        return config;
    }

    /// <summary>
    /// 保存MQTT配置，并同步更新运行期缓存。
    /// </summary>
    public void SaveMqttConfig(MqttConfigModel? config)
    {
        if (config == null)
        {
            return;
        }

        lock (_lockObj)
        {
            ConfigFileService.SaveMqttConfig(config);

            _mqttConfig = config;
        }

        MqttConfigChanged?.Invoke(this, config);
    }

    /// <summary>
    /// 重新加载地图配置。
    /// </summary>
    public MapModel ReloadMapConfig()
    {
        MapModel map;

        lock (_lockObj)
        {
            _mapConfig = ConfigFileService.LoadMap();

            map = _mapConfig;
        }

        MapConfigChanged?.Invoke(this, map);

        return map;
    }

    /// <summary>
    /// 尝试重新加载地图配置。
    /// </summary>
    public bool TryReloadMapConfig(out MapModel map, out string message)
    {
        bool success;

        lock (_lockObj)
        {
            success = ConfigFileService.TryLoadMap(out map, out message);

            if (success)
            {
                _mapConfig = map;
            }
        }

        if (success)
        {
            MapConfigChanged?.Invoke(this, map);
        }

        return success;
    }

    /// <summary>
    /// 清空运行期缓存。
    /// 下次访问配置时会重新从文件加载。
    /// </summary>
    public void ClearCache()
    {
        lock (_lockObj)
        {
            _mqttConfig = null;

            _mapConfig = null;
        }
    }
}