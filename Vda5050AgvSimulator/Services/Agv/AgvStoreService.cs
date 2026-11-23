#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Vda5050AgvSimulator.Helpers;
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

    /// <summary>
    /// 全局AGV状态事件刷新间隔。
    /// 地图、列表等订阅方可以在此基础上继续做各自的低频刷新。
    /// </summary>
    private const int AgvUiRefreshIntervalMs = 500;

    private const int AgvPropertyRefreshIntervalMs = 50;
    private readonly object _agvPropertyRefreshLock = new();
    private readonly List<AgvModel> _agvPropertyRefreshSnapshot = [];
    private bool _agvPropertyRefreshDirty;
    private bool _agvPropertyRefreshPending;
    private long _lastAgvPropertyRefreshAt;

    private readonly object _notifyLock = new();

    private long _lastAgvStateUpdatedNotifyTime;

    private Timer _agvStateUpdatedTimer;
    private readonly List<AgvModel> _agvUiSnapshot = [];

    /// <summary>
    /// 是否已有一个AGV状态回调排在UI Dispatcher中。
    /// UI忙时只保留一个待执行回调，避免BeginInvoke无限排队。
    /// </summary>
    private bool _agvStateUiDispatchPending;

    private bool _agvStateUpdatedTimerPending;
    private bool _agvStateDirty;


    private object LockObj { get; } = new();

    /// <summary>
    /// 初始化AGV存储服务。
    /// </summary>
    private AgvStoreService()
    {
    }

    private void RaiseOnUiThread(Action action)
    {
        if (action == null)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(action);
            return;
        }

        action();
    }

    private void RaiseSelectedAgvChanged(AgvModel agv)
    {
        RaiseOnUiThread(() => SelectedAgvChanged?.Invoke(this, agv));
    }

    private void RaiseSelectedAgvUpdated()
    {
        RaiseOnUiThread(() => SelectedAgvUpdated?.Invoke(this, EventArgs.Empty));
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
            foreach (var previousAgv in Agvs)
            {
                if (previousAgv != null)
                {
                    previousAgv.IsRemovedFromStore = true;
                }
            }

            Agvs.Clear();

            var agvs = ConfigFileService.LoadAgvs();

            foreach (var agv in agvs)
            {
                Agvs.Add(agv);
            }

            SelectedAgv = null;

            _isLoaded = true;
        }

        RaiseSelectedAgvChanged(SelectedAgv);
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
            return [.. Agvs];
        }
    }

    internal void CopySnapshotTo(List<AgvModel> destination)
    {
        lock (LockObj)
        {
            destination.Clear();
            destination.AddRange(Agvs);
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

        RaiseSelectedAgvChanged(agv);
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

        RaiseSelectedAgvChanged(SelectedAgv);
    }

    /// <summary>
    /// 通知当前选中的AGV参数已经更新。
    /// </summary>
    public void NotifySelectedAgvUpdated()
    {
        RaiseSelectedAgvUpdated();
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

            agv.IsRemovedFromStore = false;
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

            agv.IsRemovedFromStore = true;
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

            SetOfflineState(targetAgv);

            if (ReferenceEquals(SelectedAgv, targetAgv) || SelectedAgv?.DeviceID == targetAgv.DeviceID)
            {
                needNotifySelectedAgvUpdated = true;
            }
        }

        if (needNotifySelectedAgvUpdated)
        {
            RaiseSelectedAgvUpdated();
        }

        NotifyAgvStateUpdated();
    }

    /// <summary>
    /// 所有AGV下线。
    /// 优化点：不要在持有 LockObj 时循环调用 SetAgvOffLine，避免事件回调和UI读取再次抢锁。
    /// </summary>
    public void SetAllAgvOffLine()
    {
        var needNotifySelectedAgvUpdated = false;

        lock (LockObj)
        {
            foreach (var agvModel in Agvs)
            {
                if (agvModel == null)
                {
                    continue;
                }

                SetOfflineState(agvModel);

                if (ReferenceEquals(SelectedAgv, agvModel) || SelectedAgv?.DeviceID == agvModel.DeviceID)
                {
                    needNotifySelectedAgvUpdated = true;
                }
            }
        }

        if (needNotifySelectedAgvUpdated)
        {
            RaiseSelectedAgvUpdated();
        }

        NotifyAgvStateUpdated();
    }

    private static void SetOfflineState(AgvModel agv)
    {
        lock (agv.SimulationStateLock)
        {
            agv.OnlineStatus = 2;
            agv.RunStatus = 0;
            agv.Speed = 0;
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

            agv.IsRemovedFromStore = true;
            if (ReferenceEquals(SelectedAgv, agv) || SelectedAgv?.DeviceID == agv.DeviceID)
            {
                SelectedAgv = null;
                needClearSelectedAgv = true;
            }
        }

        if (needClearSelectedAgv)
        {
            RaiseSelectedAgvChanged(SelectedAgv);
        }

        return true;
    }

    /// <summary>
    /// 通知AGV状态已经更新。
    /// 用于地图刷新、坐标刷新、运行状态刷新等全局状态变化。
    /// 优化点：仿真线程可以高频调用，但全局UI事件统一节流到约500ms一次。
    /// </summary>
    public void NotifyAgvStateUpdated()
    {
        RequestAgvPropertyRefresh();

        bool dispatch;
        lock (_notifyLock)
        {
            _agvStateDirty = true;
            dispatch = TryScheduleAgvStateUpdated();
        }

        if (dispatch)
        {
            RaiseAgvStateUpdated();
        }
    }

    private void RequestAgvPropertyRefresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        int delayMs;
        lock (_agvPropertyRefreshLock)
        {
            _agvPropertyRefreshDirty = true;
            delayMs = TryScheduleAgvPropertyRefresh();
        }

        if (delayMs >= 0)
        {
            _ = DispatchAgvPropertyRefreshAsync(dispatcher, delayMs);
        }
    }

    // 在 _agvPropertyRefreshLock 内调用，合并尚未执行的界面刷新。
    private int TryScheduleAgvPropertyRefresh()
    {
        if (_agvPropertyRefreshPending || !_agvPropertyRefreshDirty)
        {
            return -1;
        }

        _agvPropertyRefreshPending = true;
        var elapsedMs = (Stopwatch.GetTimestamp() - _lastAgvPropertyRefreshAt) * 1000.0 / Stopwatch.Frequency;
        return (int)Math.Max(0, Math.Ceiling(AgvPropertyRefreshIntervalMs - elapsedMs));
    }

    private async Task DispatchAgvPropertyRefreshAsync(Dispatcher dispatcher, int delayMs)
    {
        while (delayMs >= 0)
        {
            var refreshed = false;
            try
            {
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }

                if (!dispatcher.HasShutdownStarted)
                {
                    // Background 优先级让键鼠输入和布局渲染先处理。
                    await dispatcher.InvokeAsync(
                        FlushAgvPropertyRefresh, DispatcherPriority.Background).Task.ConfigureAwait(false);
                    refreshed = true;
                }
            }
            catch (OperationCanceledException) when (dispatcher.HasShutdownStarted)
            {
            }
            catch (Exception ex)
            {
                FreezeDiagnosticService.WriteRateLimited("AGV_PROPERTY_REFRESH", "AGV属性刷新异常：" + ex);
            }
            finally
            {
                lock (_agvPropertyRefreshLock)
                {
                    _agvPropertyRefreshPending = false;
                    delayMs = refreshed && !dispatcher.HasShutdownStarted ? TryScheduleAgvPropertyRefresh() : -1;
                }
            }
        }
    }

    private void FlushAgvPropertyRefresh()
    {
        lock (_agvPropertyRefreshLock)
        {
            _agvPropertyRefreshDirty = false;
            _lastAgvPropertyRefreshAt = Stopwatch.GetTimestamp();
        }

        CopySnapshotTo(_agvPropertyRefreshSnapshot);
        try
        {
            foreach (var agv in _agvPropertyRefreshSnapshot)
            {
                agv?.FlushSimulationPropertyChanges();
            }
        }
        finally
        {
            _agvPropertyRefreshSnapshot.Clear();
        }
    }

    // 在 _notifyLock 内调用。每次刷新只保留一个定时回调或一个 Dispatcher 回调。
    private bool TryScheduleAgvStateUpdated()
    {
        if (!_agvStateDirty || _agvStateUpdatedTimerPending || _agvStateUiDispatchPending)
        {
            return false;
        }

        var elapsedMs = _lastAgvStateUpdatedNotifyTime == 0
            ? AgvUiRefreshIntervalMs
            : (Stopwatch.GetTimestamp() - _lastAgvStateUpdatedNotifyTime) * 1000.0 / Stopwatch.Frequency;

        if (elapsedMs < AgvUiRefreshIntervalMs)
        {
            _agvStateUpdatedTimer ??= new Timer(
                _ => FlushPendingAgvStateUpdatedNotify(), null, Timeout.Infinite, Timeout.Infinite);
            _agvStateUpdatedTimerPending = true;
            _agvStateUpdatedTimer.Change(
                (int)Math.Ceiling(AgvUiRefreshIntervalMs - elapsedMs), Timeout.Infinite);
            return false;
        }

        _agvStateUiDispatchPending = true;
        return true;
    }

    /// <summary>
    /// 触发被节流延后的AGV状态通知。
    /// </summary>
    private void FlushPendingAgvStateUpdatedNotify()
    {
        bool dispatch;
        lock (_notifyLock)
        {
            _agvStateUpdatedTimerPending = false;
            dispatch = TryScheduleAgvStateUpdated();
        }

        if (dispatch)
        {
            RaiseAgvStateUpdated();
        }
    }

    /// <summary>
    /// 实际抛出AGV状态事件。
    /// 只通知全局运行状态，不再同时触发SelectedAgvUpdated。
    /// SelectedAgvUpdated只用于用户明确修改选中车辆配置，避免列表页面每500ms保存一次整个AGV配置文件。
    /// </summary>
    private void RaiseAgvStateUpdated(bool defer = false)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher != null && (defer || !dispatcher.CheckAccess()))
        {
            try
            {
                dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(RaiseAgvStateUpdatedCore));

                return;
            }
            catch
            {
                lock (_notifyLock)
                {
                    _agvStateUiDispatchPending = false;
                }

                throw;
            }
        }

        if (defer)
        {
            ThreadPool.QueueUserWorkItem(_ => RaiseAgvStateUpdatedCore());
            return;
        }

        RaiseAgvStateUpdatedCore();
    }

    private void RaiseAgvStateUpdatedCore()
    {
        var startedAt = Stopwatch.GetTimestamp();
        lock (_notifyLock)
        {
            _agvStateDirty = false;
            _lastAgvStateUpdatedNotifyTime = startedAt;
        }

        try
        {
            CopySnapshotTo(_agvUiSnapshot);
            foreach (var agv in _agvUiSnapshot)
            {
                agv?.FlushSimulationPropertyChanges();
            }

            AgvStateUpdated?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _agvUiSnapshot.Clear();
            var elapsedMs = (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs >= 50)
            {
                FreezeDiagnosticService.WriteRateLimited(
                    "AGV_UI_REFRESH_SLOW", $"AGV界面刷新耗时：{elapsedMs:F0}ms");
            }

            bool dispatch;
            lock (_notifyLock)
            {
                _agvStateUiDispatchPending = false;
                dispatch = TryScheduleAgvStateUpdated();
            }

            if (dispatch)
            {
                // 耗时超过刷新间隔时，让 Dispatcher 先处理其他工作。
                RaiseAgvStateUpdated(defer: true);
            }
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

            sourceAgv.IsRemovedFromStore = true;
            editedAgv.IsRemovedFromStore = false;
            Agvs[index] = editedAgv;

            if (ReferenceEquals(SelectedAgv, sourceAgv) || SelectedAgv?.DeviceID == sourceAgv.DeviceID)
            {
                SelectedAgv = editedAgv;
                needNotifySelectedAgvChanged = true;
            }
        }

        if (needNotifySelectedAgvChanged)
        {
            RaiseSelectedAgvChanged(SelectedAgv);
            RaiseSelectedAgvUpdated();
        }

        return true;
    }


    /// <summary>
    /// 批量修改指定AGV设备ID范围内的当前地标、坐标和角度。
    /// 按设备ID升序依次写入地标，step为0时表示全部写成同一个地标。
    /// X/Y不手动输入，统一根据地图地标自动读取。
    /// </summary>
    public int BatchUpdateAgvPositionId(
        int startDeviceId,
        int endDeviceId,
        int startPositionId,
        int step,
        double angle,
        out string message)
    {
        return BatchUpdateAgvPositionId(startDeviceId, endDeviceId, startPositionId, step, angle, null, out message);
    }

    /// <summary>
    /// 批量写入地标、坐标和角度，可同时修改带载状态。loadStatus 为 null 时保持原状态。
    /// </summary>
    public int BatchUpdateAgvPositionId(
        int startDeviceId,
        int endDeviceId,
        int startPositionId,
        int step,
        double angle,
        string loadStatus,
        out string message)
    {
        message = string.Empty;

        if (loadStatus != null && loadStatus != "UNLOAD" && loadStatus != "LOADED")
        {
            message = "带载状态无效。";
            return -1;
        }

        if (startDeviceId > endDeviceId)
        {
            var temp = startDeviceId;
            startDeviceId = endDeviceId;
            endDeviceId = temp;
        }

        lock (LockObj)
        {
            var targetAgvs = Agvs
                .Where(x => x != null && x.DeviceID >= startDeviceId && x.DeviceID <= endDeviceId)
                .OrderBy(x => x.DeviceID)
                .ToList();

            if (targetAgvs.Count <= 0)
            {
                message = "没有找到符合范围的AGV。";
                return 0;
            }

            var map = MapStoreService.GetMap();
            if (map.points == null || map.points.Count == 0)
            {
                message = "地图数据为空，请检查 Config/map.json。";
                return -1;
            }

            // 先从同一份地图解析本批所有地标，确认可写后再更新车辆。
            var updateItems = new List<(AgvModel Agv, int PositionId, double X, double Y)>(targetAgvs.Count);
            for (var i = 0; i < targetAgvs.Count; i++)
            {
                var targetAgv = targetAgvs[i];
                var positionId = startPositionId + (long)i * step;
                if (positionId < int.MinValue || positionId > int.MaxValue)
                {
                    message = $"AGV {targetAgv.DeviceID} 的目标地标 {positionId} 超出整数范围。";
                    return -1;
                }

                var point = map.FindPoint((int)positionId);
                if (point == null)
                {
                    message = $"AGV {targetAgv.DeviceID} 的目标地标 {positionId} 不在地图中。";
                    return -1;
                }

                updateItems.Add((targetAgv, point.id, point.x, point.y));
            }

            foreach (var updateItem in updateItems)
            {
                var targetAgv = updateItem.Agv;

                lock (targetAgv.SimulationStateLock)
                {
                    targetAgv.PositionID = updateItem.PositionId;
                    targetAgv.X = updateItem.X;
                    targetAgv.Y = updateItem.Y;
                    targetAgv.Angle = angle;

                    if (loadStatus == "UNLOAD")
                    {
                        targetAgv.LiftHeight = 0;
                        targetAgv.LoadStatus = loadStatus;
                    }
                    else if (loadStatus == "LOADED")
                    {
                        if (!(targetAgv.LiftHeight > 0) || double.IsInfinity(targetAgv.LiftHeight))
                        {
                            // 与仿真升降动作的默认高位一致。
                            targetAgv.LiftHeight = 0.01;
                        }

                        targetAgv.LoadStatus = loadStatus;
                    }
                }

                var index = Agvs.IndexOf(targetAgv);

                if (index >= 0)
                {
                    Agvs[index] = targetAgv;
                }

                if (ReferenceEquals(SelectedAgv, targetAgv) || SelectedAgv?.DeviceID == targetAgv.DeviceID)
                {
                    SelectedAgv = targetAgv;
                }
            }

            message = $"已批量修改 {updateItems.Count} 台AGV。";
            return updateItems.Count;
        }
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