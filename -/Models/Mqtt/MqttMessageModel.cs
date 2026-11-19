using System;

namespace Vda5050AgvSimulator.Models.Mqtt;

public class MqttMessageModel
{
    public DateTime Time { get; set; } = DateTime.Now;

    public string Direction { get; set; } = string.Empty;

    public string Topic { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public string MessageType { get; set; } = string.Empty;

    public string SerialNumber { get; set; } = string.Empty;

    public string DisplayTime => Time.ToString("HH:mm:ss.fff");
}