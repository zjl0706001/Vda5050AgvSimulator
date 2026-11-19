using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Vda5050AgvSimulator.Helpers;
using Vda5050AgvSimulator.Models.Mqtt;

namespace Vda5050AgvSimulator.Services.Agv;

/// <summary>
/// MQTT服务类，负责MQTT连接、断开、自动重连、订阅、发布以及消息接收处理。
/// </summary>
public class MqttService
{
    /// <summary>
    /// MQTT客户端实例。
    /// </summary>
    private IMqttClient? _mqttClient;

    /// <summary>
    /// MQTT客户端连接参数。
    /// </summary>
    private MqttClientOptions? _mqttClientOptions;

    /// <summary>
    /// 当前使用的MQTT配置。
    /// 配置来源统一为 ConfigStoreService。
    /// </summary>
    private MqttConfigModel? _config;

    /// <summary>
    /// 是否为用户主动断开连接。
    /// </summary>
    private bool _isManualDisconnect;

    /// <summary>
    /// 是否正在自动重连中，防止重复进入重连循环。
    /// </summary>
    private bool _isReconnecting;

    /// <summary>
    /// MQTT消息记录集合，用于界面显示发送、接收、订阅、系统日志等消息。
    /// </summary>
    private ObservableCollection<MqttMessageModel> Messages { get; } = [];

    /// <summary>
    /// 当前MQTT是否处于连接状态。
    /// </summary>
    public bool IsConnected => _mqttClient is { IsConnected: true };

    /// <summary>
    /// MQTT连接状态变化事件。
    /// true表示已连接，false表示已断开。
    /// </summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>
    /// MQTT消息接收事件。
    /// 参数依次为：Topic、Payload、消息类型、AGV编号。
    /// </summary>
    public event Action<string, string, string, string>? MessageReceived;

    /// <summary>
    /// 初始化MQTT服务。
    /// </summary>
    public MqttService()
    {
        _config = ConfigStoreService.Instance.MqttConfig;

        ConfigStoreService.Instance.MqttConfigChanged += OnMqttConfigChanged;
    }

    /// <summary>
    /// 使用运行期配置连接MQTT服务端。
    /// </summary>
    public Task ConnectAsync()
    {
        var config = ConfigStoreService.Instance.MqttConfig;

        return ConnectAsync(config);
    }

    /// <summary>
    /// 根据配置连接MQTT服务端。
    /// </summary>
    public async Task ConnectAsync(MqttConfigModel? config)
    {
        try
        {
            if (config == null)
            {
                AddSystemMessage("MQTT配置为空，无法连接");
                ConnectionChanged?.Invoke(false);
                return;
            }

            _config = config;
            _isManualDisconnect = false;

            // 如果当前客户端已经连接，则不重复连接
            if (_mqttClient is { IsConnected: true })
            {
                AddSystemMessage("MQTT已经连接，无需重复连接");
                return;
            }

            var factory = new MqttFactory();

            _mqttClient = factory.CreateMqttClient();

            // 注册连接成功事件
            _mqttClient.ConnectedAsync += OnConnectedAsync;

            // 注册连接断开事件
            _mqttClient.DisconnectedAsync += OnDisconnectedAsync;

            // 注册消息接收事件
            _mqttClient.ApplicationMessageReceivedAsync += OnApplicationMessageReceivedAsync;

            // 构建MQTT客户端连接参数
            var builder = new MqttClientOptionsBuilder()
                .WithClientId(config.ClientId)
                .WithTcpServer(config.ServerIp, config.ServerPort)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(config.KeepAliveSeconds))
                .WithCleanSession();

            // 如果配置了用户名，则启用账号密码认证
            if (!string.IsNullOrWhiteSpace(config.Username))
            {
                builder = builder.WithCredentials(config.Username, config.Password);
            }

            _mqttClientOptions = builder.Build();

            AddSystemMessage($"正在连接 MQTT：{config.ServerIp}:{config.ServerPort}");

            await _mqttClient.ConnectAsync(_mqttClientOptions);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"MQTT连接失败：{ex.Message}");
            ConnectionChanged?.Invoke(false);
        }
    }

    /// <summary>
    /// 主动断开MQTT连接。
    /// </summary>
    public async Task DisconnectAsync()
    {
        try
        {
            _isManualDisconnect = true;

            if (_mqttClient == null)
            {
                return;
            }

            if (!_mqttClient.IsConnected)
            {
                return;
            }

            await _mqttClient.DisconnectAsync();

            AddSystemMessage("已主动断开 MQTT");
        }
        catch (Exception ex)
        {
            AddSystemMessage($"断开 MQTT 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// MQTT配置变更后的处理。
    /// </summary>
    private void OnMqttConfigChanged(object? sender, MqttConfigModel config)
    {
        _config = config;

        AddSystemMessage("MQTT运行期配置已同步");

        if (_mqttClient is { IsConnected: true })
        {
            _ = SubscribeDefaultTopicsAsync(config);
        }
    }

    /// <summary>
    /// MQTT连接成功后的回调。
    /// </summary>
    private async Task OnConnectedAsync(MqttClientConnectedEventArgs arg)
    {
        AddSystemMessage("MQTT连接成功");

        ConnectionChanged?.Invoke(true);

        // 连接成功后自动订阅默认Topic
        if (_config != null)
        {
            await SubscribeDefaultTopicsAsync(_config);
        }
    }

    /// <summary>
    /// MQTT连接断开后的回调。
    /// 如果不是手动断开，并且开启了自动重连，则进入自动重连流程。
    /// </summary>
    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs arg)
    {
        AddSystemMessage("MQTT连接断开");

        ConnectionChanged?.Invoke(false);

        // 用户主动断开时，不执行自动重连
        if (_isManualDisconnect)
        {
            return;
        }

        if (_config == null)
        {
            return;
        }

        if (!_config.AutoReconnect)
        {
            return;
        }

        if (_mqttClientOptions == null)
        {
            return;
        }

        // 防止多个断开事件同时触发重复重连
        if (_isReconnecting)
        {
            return;
        }

        _isReconnecting = true;

        try
        {
            // 自动重连循环，直到手动断开或关闭自动重连
            while (!_isManualDisconnect && _config.AutoReconnect)
            {
                try
                {
                    AddSystemMessage("3秒后尝试自动重连 MQTT");

                    await Task.Delay(3000);

                    if (_mqttClient == null)
                    {
                        return;
                    }

                    if (_mqttClient.IsConnected)
                    {
                        return;
                    }

                    await _mqttClient.ConnectAsync(_mqttClientOptions);

                    if (_mqttClient.IsConnected)
                    {
                        AddSystemMessage("MQTT自动重连成功");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AddSystemMessage($"MQTT自动重连失败：{ex.Message}");
                }
            }
        }
        finally
        {
            _isReconnecting = false;
        }
    }

    /// <summary>
    /// MQTT收到消息后的回调。
    /// 负责解析Topic、Payload、消息类型、AGV编号，并通知外部业务处理。
    /// </summary>
    private Task OnApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arg)
    {
        try
        {
            var topic = arg.ApplicationMessage.Topic;

            var payload = string.Empty;

            // 解析MQTT消息Payload
            if (arg.ApplicationMessage.PayloadSegment.Count > 0)
            {
                payload = Encoding.UTF8.GetString(arg.ApplicationMessage.PayloadSegment.ToArray());
            }

            // 根据Topic解析消息类型，例如 order、state、connection、visualization 等
            var messageType = MqttTopicHelper.GetMessageType(topic);

            // 根据Topic解析AGV编号
            var serialNumber = MqttTopicHelper.GetSerialNumber(topic);

            AddMessage("收到", topic, payload, messageType, serialNumber);

            Console.WriteLine($@"MQTT收到消息 Topic={topic}");
            Console.WriteLine($@"MQTT收到消息 Type={messageType}");
            Console.WriteLine($@"MQTT收到消息 SerialNumber={serialNumber}");
            Console.WriteLine(payload);

            // 通知外部订阅者处理收到的MQTT消息
            MessageReceived?.Invoke(topic, payload, messageType, serialNumber);

            Console.WriteLine($@"MessageReceived事件已触发 Topic={topic}");
        }
        catch (Exception ex)
        {
            AddSystemMessage($"处理MQTT接收消息失败：{ex.Message}");

            Console.WriteLine($@"处理MQTT接收消息失败：{ex}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 订阅默认Topic。
    /// 默认订阅Order和InstantActions，用于接收RCS下发任务和即时动作。
    /// </summary>
    private async Task SubscribeDefaultTopicsAsync(MqttConfigModel config)
    {
        await SubscribeAsync(config.OrderSubscribeTopic, config.Qos);

        await SubscribeAsync(config.InstantActionsSubscribeTopic, config.Qos);
    }

    /// <summary>
    /// 订阅指定MQTT主题。
    /// </summary>
    private async Task SubscribeAsync(string topic, int qos)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(topic))
            {
                AddSystemMessage("订阅主题为空，已跳过");
                return;
            }

            if (_mqttClient is not { IsConnected: true })
            {
                AddSystemMessage($"MQTT未连接，无法订阅：{topic}");
                return;
            }

            // 构建订阅参数
            var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f =>
                {
                    f.WithTopic(topic);
                    f.WithQualityOfServiceLevel(ToMqttQos(qos));
                })
                .Build();

            await _mqttClient.SubscribeAsync(subscribeOptions);

            AddMessage("订阅", topic, string.Empty, "subscribe", string.Empty);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"订阅主题失败：{topic}，原因：{ex.Message}");
        }
    }

    /// <summary>
    /// 发布MQTT消息。
    /// </summary>
    private async Task PublishAsync(string topic, string payload, int qos = 0, bool retain = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(topic))
            {
                AddSystemMessage("发布主题为空，已跳过");
                return;
            }

            if (_mqttClient is not { IsConnected: true })
            {
                AddSystemMessage($"MQTT未连接，无法发布：{topic}");
                return;
            }

            // 构建MQTT发布消息
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(ToMqttQos(qos))
                .WithRetainFlag(retain)
                .Build();

            await _mqttClient.PublishAsync(message);

            var messageType = MqttTopicHelper.GetMessageType(topic);

            var serialNumber = MqttTopicHelper.GetSerialNumber(topic);

            AddMessage("发送", topic, payload, messageType, serialNumber);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"发布消息失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 发布VDA5050 state状态消息。
    /// </summary>
    public async Task PublishStateAsync(string serialNumber, string payload)
    {
        var config = GetCurrentConfig();

        if (config == null)
        {
            AddSystemMessage("MQTT配置为空，无法发布 state");
            return;
        }

        var topic = MqttTopicHelper.ResolveTopic(config.StatePublishTopic, serialNumber);

        await PublishAsync(topic, payload, config.Qos, config.RetainState);
    }

    /// <summary>
    /// 发布VDA5050 visualization可视化消息。
    /// </summary>
    public async Task PublishVisualizationAsync(string serialNumber, string payload)
    {
        var config = GetCurrentConfig();

        if (config == null)
        {
            AddSystemMessage("MQTT配置为空，无法发布 visualization");
            return;
        }

        var topic = MqttTopicHelper.ResolveTopic(config.VisualizationPublishTopic, serialNumber);

        await PublishAsync(topic, payload, config.Qos, config.RetainVisualization);
    }

    /// <summary>
    /// 发布VDA5050 connection连接状态消息。
    /// </summary>
    public async Task PublishConnectionAsync(string serialNumber, string payload)
    {
        var config = GetCurrentConfig();

        if (config == null)
        {
            AddSystemMessage("MQTT配置为空，无法发布 connection");
            return;
        }

        var topic = MqttTopicHelper.ResolveTopic(config.ConnectionPublishTopic, serialNumber);

        await PublishAsync(topic, payload, config.Qos, config.RetainConnection);
    }

    /// <summary>
    /// 发布VDA5050 factsheet能力说明消息。
    /// </summary>
    public async Task PublishFactSheetAsync(string serialNumber, string payload)
    {
        var config = GetCurrentConfig();

        if (config == null)
        {
            AddSystemMessage("MQTT配置为空，无法发布 factsheet");
            return;
        }

        var topic = MqttTopicHelper.ResolveTopic(config.FactSheetPublishTopic, serialNumber);

        await PublishAsync(topic, payload, config.Qos, config.RetainFactSheet);
    }

    /// <summary>
    /// 获取当前MQTT配置。
    /// </summary>
    private MqttConfigModel? GetCurrentConfig()
    {
        _config ??= ConfigStoreService.Instance.MqttConfig;

        return _config;
    }

    /// <summary>
    /// 添加系统类型MQTT消息。
    /// </summary>
    private void AddSystemMessage(string message)
    {
        AddMessage("系统", "MQTT", message, "system", string.Empty);
    }

    /// <summary>
    /// 添加MQTT消息记录到消息集合。
    /// </summary>
    private void AddMessage(string direction, string topic, string payload, string messageType, string serialNumber)
    {
        var model = new MqttMessageModel
        {
            Time = DateTime.Now,
            Direction = direction,
            Topic = topic,
            Payload = payload,
            MessageType = messageType,
            SerialNumber = serialNumber
        };

        // ObservableCollection通常绑定到UI，必须在UI线程修改
        if (Application.Current != null && Application.Current.Dispatcher != null)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                Messages.Insert(0, model);

                // 限制最大消息数量，避免长时间运行导致内存增长
                if (Messages.Count > 1000)
                {
                    Messages.RemoveAt(Messages.Count - 1);
                }
            });
        }
        else
        {
            Messages.Insert(0, model);

            // 限制最大消息数量，避免长时间运行导致内存增长
            if (Messages.Count > 1000)
            {
                Messages.RemoveAt(Messages.Count - 1);
            }
        }
    }

    /// <summary>
    /// 将整数QoS转换为MQTTNet使用的QoS枚举。
    /// </summary>
    private static MqttQualityOfServiceLevel ToMqttQos(int qos)
    {
        return qos switch
        {
            <= 0 => MqttQualityOfServiceLevel.AtMostOnce,
            1 => MqttQualityOfServiceLevel.AtLeastOnce,
            _ => MqttQualityOfServiceLevel.ExactlyOnce
        };
    }
}