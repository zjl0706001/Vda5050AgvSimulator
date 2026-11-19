using System;

namespace Vda5050AgvSimulator.Helpers;

/// <summary>
/// MQTT Topic 辅助类。
/// 用于根据模板生成 Topic、解析消息类型、解析 AGV 编号、判断 Topic 类型。
/// </summary>
public static class MqttTopicHelper
{
    #region Topic Resolve

    /// <summary>
    /// 根据 Topic 模板和 AGV 序列号生成实际 Topic。
    /// </summary>
    /// <param name="topicTemplate">Topic 模板，例如 VDA/V2.0.0/HX/{serialNumber}/state。</param>
    /// <param name="serialNumber">AGV 序列号。</param>
    /// <returns>替换后的实际 Topic。</returns>
    public static string ResolveTopic(string topicTemplate, string serialNumber)
    {
        return string.IsNullOrWhiteSpace(topicTemplate)
            ? string.Empty
            : topicTemplate.Replace("{serialNumber}", serialNumber);
    }

    /// <summary>
    /// 根据 Topic 模板和设备 ID 生成实际 Topic。
    /// </summary>
    /// <param name="topicTemplate">Topic 模板，例如 VDA/V2.0.0/HX/{serialNumber}/state。</param>
    /// <param name="deviceId">设备 ID。</param>
    /// <returns>替换后的实际 Topic。</returns>
    public static string ResolveTopic(string topicTemplate, int deviceId)
    {
        return ResolveTopic(topicTemplate, deviceId.ToString());
    }

    #endregion

    #region Topic Parse

    /// <summary>
    /// 获取 Topic 的消息类型。
    /// </summary>
    /// <param name="topic">MQTT Topic。</param>
    /// <returns>消息类型，例如 state、order、connection、visualization。</returns>
    public static string GetMessageType(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return "Unknown";
        var parts = topic.Split('/');
        return parts.Length == 0 ? "Unknown" : parts[parts.Length - 1];
    }

    /// <summary>
    /// 从 VDA5050 Topic 中解析 AGV 序列号。
    /// </summary>
    /// <param name="topic">MQTT Topic，例如 VDA/V2.0.0/HX/2/state。</param>
    /// <returns>AGV 序列号；解析失败时返回空字符串。</returns>
    public static string GetSerialNumber(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return string.Empty;
        var parts = topic.Split('/');
        return parts.Length >= 5 ? parts[3] : string.Empty;
    }

    #endregion

    #region Device Match

    /// <summary>
    /// 判断 Topic 是否属于指定 AGV。
    /// </summary>
    /// <param name="topic">MQTT Topic。</param>
    /// <param name="deviceId">设备 ID 或 AGV 序列号。</param>
    /// <returns>属于该 AGV 返回 true，否则返回 false。</returns>
    public static bool IsTopicForDevice(string topic, string deviceId)
    {
        return GetSerialNumber(topic).Equals(deviceId, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Message Type Check

    /// <summary>
    /// 判断是否为 order 订单 Topic。
    /// </summary>
    public static bool IsOrderTopic(string topic)
    {
        return GetMessageType(topic).Equals("order", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断是否为 instantActions 即时动作 Topic。
    /// </summary>
    public static bool IsInstantActionsTopic(string topic)
    {
        return GetMessageType(topic).Equals("instantActions", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断是否为 state 状态 Topic。
    /// </summary>
    public static bool IsStateTopic(string topic)
    {
        return GetMessageType(topic).Equals("state", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断是否为 visualization 可视化 Topic。
    /// </summary>
    public static bool IsVisualizationTopic(string topic)
    {
        return GetMessageType(topic).Equals("visualization", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断是否为 connection 连接状态 Topic。
    /// </summary>
    public static bool IsConnectionTopic(string topic)
    {
        return GetMessageType(topic).Equals("connection", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断是否为 factsheet 设备参数 Topic。
    /// </summary>
    public static bool IsFactSheetTopic(string topic)
    {
        return GetMessageType(topic).Equals("factsheet", StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}