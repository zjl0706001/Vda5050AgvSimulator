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

    /// <summary>
    /// 运行时唯一的全局PLC复位模板。
    /// 所有PLC、所有DB块都从这里获取复位项，避免逐DB重复维护配置。
    /// </summary>
    private readonly ObservableCollection<PlcDbBlockResetItemModel> _globalResetItems = [];

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

        LoadGlobalResetItemsFromConfigCore();
        ApplyGlobalResetItemsToAllPlcsCore();

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
            ApplyGlobalResetItemsToAllPlcsCore();
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
            ApplyGlobalResetItemsToAllPlcsCore();

            return Plcs
                .Where(x => x != null)
                .Select(x => x.Clone())
                .ToList();
        }
    }

    /// <summary>
    /// 获取全局PLC复位模板快照。
    /// 调用方拿到的是Clone副本，修改副本不会直接影响运行时配置。
    /// </summary>
    public List<PlcDbBlockResetItemModel> GetGlobalResetItemsSnapshot()
    {
        EnsureLoadedFromConfig();

        lock (_lockObj)
        {
            return _globalResetItems
                .Where(x => x != null)
                .OrderBy(x => x.ResetItemId)
                .Select(x => x.Clone())
                .ToList();
        }
    }

    /// <summary>
    /// 替换全局PLC复位模板。
    /// 该方法同步更新内存中的所有PLC和DB块；持久化仍由配置窗口原有保存流程统一完成。
    /// </summary>
    public void ReplaceGlobalResetItems(IEnumerable<PlcDbBlockResetItemModel> resetItems)
    {
        EnsureLoadedFromConfig();

        lock (_lockObj)
        {
            _globalResetItems.Clear();

            if (resetItems != null)
            {
                foreach (var resetItem in resetItems
                             .Where(x => x != null)
                             .OrderBy(x => x.ResetItemId))
                {
                    _globalResetItems.Add(resetItem.Clone());
                }
            }

            ApplyGlobalResetItemsToAllPlcsCore();
        }

        NotifyPlcConfigChanged();
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

        EnsureLoadedFromConfig();

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

            ApplyGlobalResetItemsToAllPlcsCore();

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
    /// 从配置文件中恢复全局复位模板。
    /// 新配置优先读取 PlcConfigModel.GlobalResetItems；旧配置则从第一个存在复位项的DB块迁移。
    /// 注意：调用方必须持有_lockObj锁。
    /// </summary>
    private void LoadGlobalResetItemsFromConfigCore()
    {
        _globalResetItems.Clear();

        var globalSource = Plcs
            .Where(x => x != null && x.GlobalResetItems != null && x.GlobalResetItems.Count > 0)
            .OrderBy(x => x.PlcId)
            .Select(x => x.GlobalResetItems)
            .FirstOrDefault();

        var source = globalSource;

        if (source == null)
        {
            source = Plcs
                .Where(x => x != null && x.DbBlocks != null)
                .OrderBy(x => x.PlcId)
                .SelectMany(x => x.DbBlocks
                    .Where(db => db != null && db.ResetItems != null && db.ResetItems.Count > 0)
                    .OrderBy(db => db.DbNumber))
                .Select(db => db.ResetItems)
                .FirstOrDefault();
        }

        if (source == null)
        {
            return;
        }

        foreach (var resetItem in source
                     .Where(x => x != null)
                     .OrderBy(x => x.ResetItemId))
        {
            _globalResetItems.Add(resetItem.Clone());
        }
    }

    /// <summary>
    /// 把唯一的全局复位模板同步到所有PLC配置。
    /// 同时同步旧版DB块字段，用于兼容旧配置文件和旧版本程序。
    /// 注意：调用方必须持有_lockObj锁。
    /// </summary>
    private void ApplyGlobalResetItemsToAllPlcsCore()
    {
        foreach (var plc in Plcs.Where(x => x != null))
        {
            plc.GlobalResetItems ??= [];
            CopyResetItems(_globalResetItems, plc.GlobalResetItems);

            if (plc.DbBlocks == null)
            {
                continue;
            }

            foreach (var dbBlock in plc.DbBlocks.Where(x => x != null))
            {
                dbBlock.ResetItems ??= [];
                CopyResetItems(_globalResetItems, dbBlock.ResetItems);
            }
        }
    }

    /// <summary>
    /// 克隆复位项到目标集合，避免多个配置对象直接共享同一个可变集合实例。
    /// </summary>
    private static void CopyResetItems(
        IEnumerable<PlcDbBlockResetItemModel> source,
        ObservableCollection<PlcDbBlockResetItemModel> target)
    {
        if (target == null)
        {
            return;
        }

        target.Clear();

        if (source == null)
        {
            return;
        }

        foreach (var resetItem in source
                     .Where(x => x != null)
                     .OrderBy(x => x.ResetItemId))
        {
            target.Add(resetItem.Clone());
        }
    }

    /// <summary>
    /// 通知PLC配置发生变化。
    /// </summary>
    private void NotifyPlcConfigChanged()
    {
        PlcConfigChanged?.Invoke(this, EventArgs.Empty);
    }
}
