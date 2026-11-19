#nullable disable

namespace Vda5050AgvSimulator.Models.Vda;

/// <summary>
/// VDA5050 connection 连接状态上报模型。
/// </summary>
public class VdaConnectionModel
{
    /// <summary>
    /// 连接状态。
    /// 常见值：ONLINE、OFFLINE、CONNECTIONBROKEN。
    /// </summary>
    public string connectionState { get; set; } = VdaConnectionState.Offline;

    /// <summary>
    /// 消息头递增 ID。
    /// </summary>
    public long headerId { get; set; }

    /// <summary>
    /// 厂商。
    /// </summary>
    public string manufacturer { get; set; } = "";

    /// <summary>
    /// AGV 序列号。
    /// </summary>
    public string serialNumber { get; set; } = "";

    /// <summary>
    /// UTC 时间戳。
    /// </summary>
    public string timestamp { get; set; } = "";

    /// <summary>
    /// VDA5050 协议版本。
    /// </summary>
    public string version { get; set; } = "";
}

/// <summary>
/// VDA5050 connectionState 常量。
/// </summary>
public static class VdaConnectionState
{
    public const string Online = "ONLINE";

    public const string Offline = "OFFLINE";

    public const string ConnectionBroken = "CONNECTIONBROKEN";
}