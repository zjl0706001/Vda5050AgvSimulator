#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Vda5050AgvSimulator.Models.Plc;
using Vda5050AgvSimulator.Services.File;

namespace Vda5050AgvSimulator.Services.Plc;

/// <summary>
/// PLC配置存储服务。
/// 模仿AgvStoreService，负责PLC列表、加载、保存、通知。
/// </summary>
public class PlcStoreService
{
    private readonly object _lockObj = new();

    private bool _isLoaded;

    public static PlcStoreService Instance { get; } = new();

    private ObservableCollection<PlcConfigModel> Plcs { get; } = [];

    public event EventHandler PlcConfigChanged;

    private PlcStoreService()
    {
    }

    /// <summary>
    /// 确保PLC配置已经从配置文件加载。
    /// </summary>
    public void EnsureLoadedFromConfig()
    {
        lock (_lockObj)
        {
            if (_isLoaded)
            {
                return;
            }

            LoadFromConfigCore();
        }

        NotifyPlcConfigChanged();
    }

    /// <summary>
    /// 从配置文件加载PLC配置。
    /// 注意：调用方必须持有_lockObj锁。
    /// </summary>
    private void LoadFromConfigCore()
    {
        Plcs.Clear();

        var plcs = ConfigFileService.LoadPlcs();

        foreach (var plc in plcs)
        {
            Plcs.Add(plc.Clone());
        }

        _isLoaded = true;
    }

    /// <summary>
    /// 保存当前PLC配置到配置文件。
    /// </summary>
    public void SaveToConfig()
    {
        EnsureLoadedFromConfig();

        ObservableCollection<PlcConfigModel> snapshot;

        lock (_lockObj)
        {
            snapshot = CreateSnapshotCollection();
        }

        ConfigFileService.SavePlcs(snapshot);

        NotifyPlcConfigChanged();
    }

    /// <summary>
    /// 获取PLC配置快照。
    /// 外部拿到的是Clone副本，不能直接修改内部集合。
    /// </summary>
    public List<PlcConfigModel> GetSnapshotList()
    {
        EnsureLoadedFromConfig();

        lock (_lockObj)
        {
            return Plcs
                .Where(x => x != null)
                .Select(x => x.Clone())
                .ToList();
        }
    }

    /// <summary>
    /// 替换全部PLC配置，并保存到配置文件。
    /// </summary>
    public void ReplaceAll(IEnumerable<PlcConfigModel> plcs)
    {
        if (plcs == null)
        {
            return;
        }

        ObservableCollection<PlcConfigModel> snapshot;

        lock (_lockObj)
        {
            Plcs.Clear();

            foreach (var plc in plcs)
            {
                if (plc == null)
                {
                    continue;
                }

                Plcs.Add(plc.Clone());
            }

            _isLoaded = true;

            snapshot = CreateSnapshotCollection();
        }

        ConfigFileService.SavePlcs(snapshot);

        NotifyPlcConfigChanged();
    }

    /// <summary>
    /// 获取下一个PLC ID。
    /// </summary>
    public int GetNextPlcId()
    {
        EnsureLoadedFromConfig();

        lock (_lockObj)
        {
            if (Plcs.Count == 0)
            {
                return 1;
            }

            return Plcs.Max(x => x.PlcId) + 1;
        }
    }

    /// <summary>
    /// 创建用于保存或读取的PLC配置快照集合。
    /// 注意：调用方必须持有_lockObj锁。
    /// </summary>
    private ObservableCollection<PlcConfigModel> CreateSnapshotCollection()
    {
        return new ObservableCollection<PlcConfigModel>(
            Plcs
                .Where(x => x != null)
                .OrderBy(x => x.PlcId)
                .Select(x => x.Clone()));
    }

    /// <summary>
    /// 通知PLC配置发生变化。
    /// </summary>
    private void NotifyPlcConfigChanged()
    {
        PlcConfigChanged?.Invoke(this, EventArgs.Empty);
    }
}