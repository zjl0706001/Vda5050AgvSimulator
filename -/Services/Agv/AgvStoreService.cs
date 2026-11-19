#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Vda5050AgvSimulator.Models.Agv;
using Vda5050AgvSimulator.Services.File;

namespace Vda5050AgvSimulator.Services.Agv;

public class AgvStoreService
{
    private bool _isLoaded;

    public static AgvStoreService Instance { get; } = new();

    public ObservableCollection<AgvModel> Agvs { get; } = [];

    public AgvModel SelectedAgv { get; private set; }

    public event EventHandler<AgvModel> SelectedAgvChanged;

    public event EventHandler SelectedAgvUpdated;
    public event EventHandler AgvStateUpdated;


    private object LockObj { get; } = new();

    /// <summary>
    /// 初始化AGV存储服务。
    /// </summary>
    private AgvStoreService()
    {
    }

    /// <summary>
    /// 确保AGV配置已经加载。
    /// </summary>
    public void EnsureLoadedFromConfig()
    {
        if (_isLoaded)
        {
            return;
        }

        LoadFromConfig();
    }

    /// <summary>
    /// 从配置文件加载AGV列表。
    /// </summary>
    public void LoadFromConfig()
    {
        lock (LockObj)
        {
            Agvs.Clear();

            var agvs = ConfigFileService.LoadAgvs();

            foreach (var agv in agvs)
            {
                Agvs.Add(agv);
            }

            SelectedAgv = null;

            _isLoaded = true;
        }

        SelectedAgvChanged?.Invoke(this, SelectedAgv);
    }

    /// <summary>
    /// 保存AGV列表到配置文件。
    /// </summary>
    public void SaveToConfig()
    {
        lock (LockObj)
        {
            ConfigFileService.SaveAgvs(Agvs);
        }
    }

    /// <summary>
    /// 按设备ID升序排序AGV列表。
    /// </summary>
    public void SortAgvsByDeviceId()
    {
        lock (LockObj)
        {
            if (Agvs.Count <= 1)
            {
                return;
            }

            var sortedAgvs = Agvs
                .OrderBy(x => x.DeviceID)
                .ToList();

            Agvs.Clear();

            foreach (var agv in sortedAgvs)
            {
                Agvs.Add(agv);
            }
        }
    }

    /// <summary>
    /// 根据设备ID查找AGV。
    /// </summary>
    private AgvModel FindAgv(int deviceId)
    {
        lock (LockObj)
        {
            return Agvs.FirstOrDefault(x => x.DeviceID == deviceId);
        }
    }

    /// <summary>
    /// 根据车辆编号查找AGV。
    /// </summary>
    public AgvModel FindAgv(string serialNumber)
    {
        return !int.TryParse(serialNumber, out var deviceId) ? null : FindAgv(deviceId);
    }

    /// <summary>
    /// 获取AGV列表快照。
    /// </summary>
    public List<AgvModel> GetSnapshotList()
    {
        lock (LockObj)
        {
            return Agvs.ToList();
        }
    }

    /// <summary>
    /// 设置当前选中的AGV。
    /// </summary>
    public void SetSelectedAgv(AgvModel agv)
    {
        lock (LockObj)
        {
            SelectedAgv = agv;
        }

        SelectedAgvChanged?.Invoke(this, agv);
    }

    /// <summary>
    /// 清空当前选中的AGV。
    /// </summary>
    public void ClearSelectedAgv()
    {
        lock (LockObj)
        {
            SelectedAgv = null;
        }

        SelectedAgvChanged?.Invoke(this, SelectedAgv);
    }

    /// <summary>
    /// 通知当前选中的AGV参数已经更新。
    /// </summary>
    public void NotifySelectedAgvUpdated()
    {
        SelectedAgvUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 添加AGV。
    /// </summary>
    public bool AddAgv(AgvModel agv)
    {
        lock (LockObj)
        {
            if (Agvs.Any(x => x.DeviceID == agv.DeviceID))
            {
                return false;
            }

            Agvs.Add(agv);

            return true;
        }
    }

    /// <summary>
    /// 删除AGV。
    /// </summary>
    public bool RemoveAgv(AgvModel agv)
    {
        lock (LockObj)
        {
            var result = Agvs.Remove(agv);

            if (!result)
            {
                return false;
            }

            if (ReferenceEquals(SelectedAgv, agv) || SelectedAgv?.DeviceID == agv.DeviceID)
            {
                SelectedAgv = null;
            }

            return true;
        }
    }

    /// <summary>
    /// 单个AGV下线
    /// </summary>
    /// <param name="agv"></param>
    public void SetAgvOffLine(AgvModel agv)
    {
        if (agv == null)
        {
            return;
        }

        var needNotifySelectedAgvUpdated = false;

        lock (LockObj)
        {
            var targetAgv = Agvs.FirstOrDefault(x => x.DeviceID == agv.DeviceID);

            if (targetAgv == null)
            {
                return;
            }

            targetAgv.OnlineStatus = 2;
            targetAgv.RunStatus = 0;
            targetAgv.Speed = 0;

            if (ReferenceEquals(SelectedAgv, targetAgv) || SelectedAgv?.DeviceID == targetAgv.DeviceID)
            {
                needNotifySelectedAgvUpdated = true;
            }
        }

        if (needNotifySelectedAgvUpdated)
        {
            SelectedAgvUpdated?.Invoke(this, EventArgs.Empty);
        }

        AgvStateUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 所有AGV下线
    /// </summary>
    public void SetAllAgvOffLine()
    {
        lock (LockObj)
        {
            foreach (var agvModel in Agvs)
            {
                SetAgvOffLine(agvModel);
            }
        }
    }

    /// <summary>
    /// 删除AGV后通知选中状态变化。
    /// </summary>
    public bool RemoveAgvAndNotify(AgvModel agv)
    {
        var needClearSelectedAgv = false;

        lock (LockObj)
        {
            var result = Agvs.Remove(agv);

            if (!result)
            {
                return false;
            }

            if (ReferenceEquals(SelectedAgv, agv) || SelectedAgv?.DeviceID == agv.DeviceID)
            {
                SelectedAgv = null;
                needClearSelectedAgv = true;
            }
        }

        if (needClearSelectedAgv)
        {
            SelectedAgvChanged?.Invoke(this, SelectedAgv);
        }

        return true;
    }

    /// <summary>
    /// 通知AGV状态已经更新。
    /// 用于地图刷新、坐标刷新、运行状态刷新等全局状态变化。
    /// </summary>
    public void NotifyAgvStateUpdated()
    {
        AgvStateUpdated?.Invoke(this, EventArgs.Empty);

        if (SelectedAgv != null)
        {
            SelectedAgvUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 替换AGV。
    /// </summary>
    public bool ReplaceAgv(AgvModel sourceAgv, AgvModel editedAgv)
    {
        var needNotifySelectedAgvChanged = false;

        lock (LockObj)
        {
            var index = Agvs.IndexOf(sourceAgv);

            if (index < 0)
            {
                return false;
            }

            var exists = Agvs
                .Where((_, agvIndex) => agvIndex != index)
                .Any(x => x.DeviceID == editedAgv.DeviceID);

            if (exists)
            {
                return false;
            }

            Agvs[index] = editedAgv;

            if (ReferenceEquals(SelectedAgv, sourceAgv) || SelectedAgv?.DeviceID == sourceAgv.DeviceID)
            {
                SelectedAgv = editedAgv;
                needNotifySelectedAgvChanged = true;
            }
        }

        if (needNotifySelectedAgvChanged)
        {
            SelectedAgvChanged?.Invoke(this, SelectedAgv);
            SelectedAgvUpdated?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    /// <summary>
    /// 获取下一个可用的设备ID。
    /// </summary>
    public int GetNextDeviceId()
    {
        lock (LockObj)
        {
            if (Agvs.Count == 0)
            {
                return 1;
            }

            return Agvs.Max(x => x.DeviceID) + 1;
        }
    }
}