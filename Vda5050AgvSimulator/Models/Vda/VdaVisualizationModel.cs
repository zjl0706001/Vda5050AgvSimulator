#nullable disable

// ReSharper disable InconsistentNaming
namespace Vda5050AgvSimulator.Models.Vda;

/// <summary>
/// VDA5050 visualization 可视化上报模型。
/// 用于周期性上报 AGV 的实时位置和速度。
/// </summary>
public class VdaVisualizationModel
{
    /// <summary>
    /// AGV 当前地图位置。
    /// </summary>
    public VdaVisualizationAgvPositionModel agvPosition { get; set; } = new();

    /// <summary>
    /// AGV 当前速度。
    /// </summary>
    public VdaVelocityModel velocity { get; set; } = new();

    /// <summary>
    /// 消息头递增 ID。
    /// </summary>
    public long headerId { get; set; }

    /// <summary>
    /// UTC 时间戳。
    /// </summary>
    public string timestamp { get; set; } = "";

    /// <summary>
    /// VDA5050 协议版本。
    /// </summary>
    public string version { get; set; } = "";

    /// <summary>
    /// 厂商。
    /// </summary>
    public string manufacturer { get; set; } = "";

    /// <summary>
    /// AGV 序列号。
    /// </summary>
    public string serialNumber { get; set; } = "";
}

/// <summary>
/// VDA5050 visualization 中的 AGV 位置。
/// </summary>
public class VdaVisualizationAgvPositionModel
{
    /// <summary>
    /// 位置是否已经初始化。
    /// </summary>
    public bool positionInitialized { get; set; } = true;

    /// <summary>
    /// 定位置信度。
    /// </summary>
    public double localizationScore { get; set; } = 100;

    /// <summary>
    /// X 坐标。
    /// </summary>
    public double x { get; set; }

    /// <summary>
    /// Y 坐标。
    /// </summary>
    public double y { get; set; }

    /// <summary>
    /// AGV 朝向，单位弧度。
    /// </summary>
    public double theta { get; set; }

    /// <summary>
    /// 地图 ID。
    /// </summary>
    public string mapId { get; set; } = "";

    /// <summary>
    /// 地图描述。
    /// </summary>
    public string mapDescription { get; set; } = "";
}