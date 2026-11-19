using System;
using Vda5050AgvSimulator.Models.Agv;
using Vda5050AgvSimulator.Models.Map;

namespace Vda5050AgvSimulator.Services.Agv;

/// <summary>
/// 地图服务。
/// 只负责地图查询和AGV坐标刷新。
/// 地图配置统一从 ConfigStoreService 获取。
/// </summary>
public static class MapStoreService
{
    /// <summary>
    /// 地图变更事件。
    /// </summary>
    public static event EventHandler<MapModel>? MapChanged;

    /// <summary>
    /// 地图变更后是否自动刷新所有AGV当前坐标。
    /// </summary>
    public static bool AutoRefreshAgvPositionWhenMapChanged { get; set; } = true;

    /// <summary>
    /// 静态构造。
    /// 订阅 ConfigStoreService 的地图变更事件。
    /// </summary>
    static MapStoreService()
    {
        ConfigStoreService.Instance.MapConfigChanged += OnMapConfigChanged;
    }

    /// <summary>
    /// 初始化地图服务。
    /// 程序启动时调用一次，用于确保静态构造已执行。
    /// </summary>
    public static void Initialize()
    {
    }

    /// <summary>
    /// 获取地图配置。
    /// </summary>
    public static MapModel GetMap()
    {
        return ConfigStoreService.Instance.MapConfig;
    }

    /// <summary>
    /// 重新加载地图配置。
    /// </summary>
    public static void ReloadMap()
    {
        ConfigStoreService.Instance.ReloadMapConfig();
    }

    /// <summary>
    /// 尝试重新加载地图配置。
    /// </summary>
    public static bool TryReloadMap(out MapModel map, out string message)
    {
        return ConfigStoreService.Instance.TryReloadMapConfig(out map, out message);
    }

    /// <summary>
    /// ConfigStoreService 地图配置变更后触发。
    /// </summary>
    private static void OnMapConfigChanged(object sender, MapModel map)
    {
        if (AutoRefreshAgvPositionWhenMapChanged)
        {
            RefreshAllAgvPositionByCurrentPositionId();
        }

        MapChanged?.Invoke(null, map);
    }

    /// <summary>
    /// 根据所有AGV当前 PositionID，重新刷新 X/Y 坐标。
    /// </summary>
    public static void RefreshAllAgvPositionByCurrentPositionId()
    {
        foreach (var agv in AgvStoreService.Instance.Agvs)
        {
            TryApplyPositionByPositionId(agv, out _);
        }

        AgvStoreService.Instance.NotifyAgvStateUpdated();
    }

    /// <summary>
    /// 根据地标号查找地图点。
    /// </summary>
    public static bool TryGetPoint(string pointId, out MapPointModel? point, out string message)
    {
        point = null;
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(pointId))
        {
            message = "地标号为空。";
            return false;
        }

        if (!int.TryParse(pointId, out var id))
        {
            message = $"地标号不是有效数字：{pointId}";
            return false;
        }

        return TryGetPoint(id, out point, out message);
    }

    /// <summary>
    /// 根据地标号查找地图点。
    /// </summary>
    public static bool TryGetPoint(int pointId, out MapPointModel? point, out string message)
    {
        point = null;
        message = string.Empty;

        var map = GetMap();

        if (map.points.Count == 0)
        {
            message = "地图数据为空，请检查 Config/map.json。";
            return false;
        }

        point = map.FindPoint(pointId);

        if (point != null)
        {
            return true;
        }

        message = $"地图中不存在地标号：{pointId}";
        return false;
    }

    /// <summary>
    /// 根据指定地标号，自动刷新AGV的PositionID/X/Y坐标。
    /// </summary>
    public static bool TryApplyPositionByPointId(AgvModel? agv, string pointId, out string message)
    {
        message = string.Empty;

        if (agv == null)
        {
            message = "AGV为空。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(pointId))
        {
            message = "地标号为空。";
            return false;
        }

        if (!TryGetPoint(pointId, out var point, out message))
        {
            return false;
        }

        agv.PositionID = point.id;
        agv.X = point.x;
        agv.Y = point.y;

        return true;
    }

    /// <summary>
    /// 根据AGV当前地标号，自动刷新AGV的X/Y坐标。
    /// </summary>
    public static bool TryApplyPositionByPositionId(AgvModel? agv, out string message)
    {
        if (agv == null)
        {
            message = "AGV为空。";
            return false;
        }

        if (agv.PositionID <= 0)
        {
            message = $"AGV当前地标号无效：{agv.PositionID}";
            return false;
        }

        return TryApplyPositionByPointId(agv, agv.PositionID.ToString(), out message);
    }
}