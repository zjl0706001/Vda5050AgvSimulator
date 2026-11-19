#nullable disable

using System.Collections.Generic;

namespace Vda5050AgvSimulator.Models.Vda;

/// <summary>
/// VDA5050 instantActions 即时动作模型。
/// RCS 下发给 AGV 的即时控制命令，例如取消订单、暂停、恢复、急停确认等。
/// </summary>
public class VdaInstantActionsModel
{
    /// <summary>
    /// 即时动作列表。
    /// </summary>
    public List<VdaOrderActionModel> actions { get; set; } = [];

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