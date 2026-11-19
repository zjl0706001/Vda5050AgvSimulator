namespace Vda5050AgvSimulator.Models.Mqtt;

/// <summary>
/// MQTT与VDA5050协议相关配置模型。
/// </summary>
public class MqttConfigModel
{
    /// <summary>
    /// MQTT服务端IP地址。
    /// </summary>
    public string ServerIp { get; set; } = "127.0.0.1";

    /// <summary>
    /// MQTT服务端端口，默认1883。
    /// </summary>
    public int ServerPort { get; set; } = 1883;

    /// <summary>
    /// MQTT登录用户名。
    /// </summary>
    public string Username { get; set; } = "admin";

    /// <summary>
    /// MQTT登录密码。
    /// </summary>
    public string Password { get; set; } = "Huaxiao@123.com";

    /// <summary>
    /// MQTT客户端ID，用于标识当前模拟器客户端。
    /// </summary>
    public string ClientId { get; set; } = "Vda5050AgvSimulator";

    /// <summary>
    /// 是否启用MQTT自动重连。
    /// </summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// MQTT心跳间隔，单位：秒。
    /// </summary>
    public int KeepAliveSeconds { get; set; } = 30;

    /// <summary>
    /// VDA5050接口名称，通常为VDA。
    /// </summary>
    public string InterfaceName { get; set; } = "VDA";

    /// <summary>
    /// VDA5050协议版本。
    /// </summary>
    public string ProtocolVersion { get; set; } = "V2.0.0";

    /// <summary>
    /// 厂商标识，需要与RCS配置保持一致。
    /// </summary>
    public string Manufacturer { get; set; } = "HX";

    /// <summary>
    /// 地图ID，需要与RCS地图配置保持一致。
    /// </summary>
    public string MapId { get; set; } = "1";

    /// <summary>
    /// 区域ID，用于区分不同区域或作业区。
    /// </summary>
    public string ZoneId { get; set; } = "1";

    /// <summary>
    /// Order任务订阅Topic，用于接收RCS下发的order消息。
    /// </summary>
    public string OrderSubscribeTopic { get; set; } = "VDA/V2.0.0/HX/+/order";

    /// <summary>
    /// 即时动作订阅Topic，用于接收RCS下发的instantActions消息。
    /// </summary>
    public string InstantActionsSubscribeTopic { get; set; } = "VDA/V2.0.0/HX/+/instantActions";

    /// <summary>
    /// State状态发布Topic，{serialNumber} 会被替换为AGV编号。
    /// </summary>
    public string StatePublishTopic { get; set; } = "VDA/V2.0.0/HX/{serialNumber}/state";

    /// <summary>
    /// Visualization可视化位置发布Topic，{serialNumber} 会被替换为AGV编号。
    /// </summary>
    public string VisualizationPublishTopic { get; set; } = "VDA/V2.0.0/HX/{serialNumber}/visualization";

    /// <summary>
    /// Connection连接状态发布Topic，{serialNumber} 会被替换为AGV编号。
    /// </summary>
    public string ConnectionPublishTopic { get; set; } = "VDA/V2.0.0/HX/{serialNumber}/connection";

    /// <summary>
    /// FactSheet能力说明发布Topic，{serialNumber} 会被替换为AGV编号。
    /// </summary>
    public string FactSheetPublishTopic { get; set; } = "VDA/V2.0.0/HX/{serialNumber}/factsheet";

    /// <summary>
    /// State状态消息发布周期，单位：毫秒。
    /// </summary>
    public int StatePublishIntervalMs { get; set; } = 250;

    /// <summary>
    /// AGV仿真最大并行数。0=根据CPU自动分配，1=串行。
    /// </summary>
    public int SimulationMaxParallelism { get; set; }

    /// <summary>
    /// Visualization可视化消息发布周期，单位：毫秒。
    /// </summary>
    public int VisualizationPublishIntervalMs { get; set; } = 500;

    /// <summary>
    /// 行驶或转向时的坐标报文周期，单位：毫秒。0=沿用普通坐标发布周期。
    /// </summary>
    public int MovingVisualizationPublishIntervalMs { get; set; } = 150;

    /// <summary>
    /// Connection连接状态消息发布周期，单位：毫秒。
    /// </summary>
    public int ConnectionPublishIntervalMs { get; set; } = 5000;

    /// <summary>
    /// MQTT消息服务质量等级，只能为0、1、2。
    /// </summary>
    public int Qos { get; set; } = 0;

    /// <summary>
    /// 发布State状态消息时是否保留最后一条消息。
    /// </summary>
    public bool RetainState { get; set; } = false;

    /// <summary>
    /// 发布Visualization可视化消息时是否保留最后一条消息。
    /// </summary>
    public bool RetainVisualization { get; set; } = false;

    /// <summary>
    /// 发布Connection连接状态消息时是否保留最后一条消息。
    /// </summary>
    public bool RetainConnection { get; set; } = false;

    /// <summary>
    /// 发布FactSheet时是否保留最后一条消息。
    /// </summary>
    public bool RetainFactSheet { get; set; } = false;

    /// <summary>
    /// AGV上线后是否发布一次FactSheet。
    /// </summary>
    public bool PublishFactSheetOnOnline { get; set; } = true;

    /// <summary>
    /// FactSheet能力参数。
    /// </summary>
    public FactSheetConfigModel FactSheetConfig { get; set; } = new();
}

/// <summary>
/// FactSheet配置项。
/// </summary>
public class FactSheetConfigModel
{
    /// <summary>
    /// AGV运动学类型。
    /// </summary>
    public int AgvKinematic { get; set; } = 0;

    /// <summary>
    /// AGV类型。
    /// </summary>
    public int AgvClass { get; set; } = 0;

    /// <summary>
    /// 最大载重，单位kg。
    /// </summary>
    public int MaxLoadMass { get; set; } = 0;
}