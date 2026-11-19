using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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
    /// 高频 state / visualization / connection 报文是否写入界面日志。
    /// 默认关闭，避免MQTT周期报文把UI线程刷爆。
    /// </summary>
    private const bool EnableHighFrequencyMqttUiLog = false;

    private const int MaxMqttMessageCount = 300;

    private const int MaxMqttPayloadLengthForUi = 500;

    private const int MqttMessageUiFlushIntervalMs = 200;

    private readonly object _messageLock = new();

    private readonly List<MqttMessageModel> _pendingMessages = [];

    private bool _messageFlushScheduled;

    /// <summary>
    /// MQTT接收业务处理队列。
    /// MQTT回调线程只负责入队，不直接执行订单解析/状态修改，避免卡住 MQTTnet 内部接收链路。
    /// </summary>
    private readonly ConcurrentQueue<MqttInboundMessage> _inboundMessages = new();

    private readonly SemaphoreSlim _inboundSignal = new(0);

    private int _inboundConsumerStarted;

    private int _inboundSignalPending;

    private int _inboundQueuedCount;

    private DateTime _lastInboundOverflowLogTime = DateTime.MinValue;

    private DateTime _lastPublishTimeoutUiLogTime = DateTime.MinValue;

    private readonly SemaphoreSlim _mqttPublishLock = new(1, 1);

    private const int MaxInboundMessageQueueCount = 2000;

    private const int MaxInboundBatchSize = 50;

    private const int MaxPendingMqttUiMessageCount = 500;

    private const int MaxMqttUiFlushBatchSize = 100;

    private const int MqttPublishTimeoutMs = 2000;
    private long _lastPublishHeartbeatTimestamp = long.MinValue;

    private sealed class MqttInboundMessage
    {
        public string Topic { get; set; } = string.Empty;

        public string Payload { get; set; } = string.Empty;

        public string MessageType { get; set; } = string.Empty;

        public string SerialNumber { get; set; } = string.Empty;

        public DateTime ReceiveTime { get; set; } = DateTime.Now;
    }


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
    /// 这里只做轻量解析和入队，不直接执行外部业务。
    /// 这样即使 HandleOrder / ApplyOrderRoute / PublishState 变慢，也不会拖住 MQTTnet 接收线程。
    /// </summary>
    private Task OnApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arg)
    {
        try
        {
            FreezeDiagnosticService.Heartbeat("MQTT_RECV");

            var topic = arg.ApplicationMessage.Topic;

            var payload = string.Empty;

            var segment = arg.ApplicationMessage.PayloadSegment;
            if (segment.Count > 0)
            {
                payload = Encoding.UTF8.GetString(segment.Array!, segment.Offset, segment.Count);
            }

            var messageType = MqttTopicHelper.GetMessageType(topic);

            var serialNumber = MqttTopicHelper.GetSerialNumber(topic);

            AddMessage("收到", topic, payload, messageType, serialNumber);

            EnqueueInboundMessage(topic, payload, messageType, serialNumber);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"处理MQTT接收消息失败：{ex.Message}");
            FreezeDiagnosticService.Write("MQTT接收回调异常：" + ex);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 将 MQTT 入站消息加入业务处理队列。
    /// </summary>
    private void EnqueueInboundMessage(
        string topic,
        string payload,
        string messageType,
        string serialNumber)
    {
        if (MessageReceived == null)
        {
            return;
        }

        var queueCount = Interlocked.Increment(ref _inboundQueuedCount);

        if (queueCount > MaxInboundMessageQueueCount)
        {
            Interlocked.Decrement(ref _inboundQueuedCount);

            var now = DateTime.Now;

            if ((now - _lastInboundOverflowLogTime).TotalSeconds >= 5)
            {
                _lastInboundOverflowLogTime = now;

                AddSystemMessage($"MQTT接收业务队列积压超过 {MaxInboundMessageQueueCount} 条，已丢弃新消息");
                FreezeDiagnosticService.Write($"MQTT接收业务队列积压，topic={topic}，messageType={messageType}");
            }

            return;
        }

        _inboundMessages.Enqueue(new MqttInboundMessage
        {
            Topic = topic,
            Payload = payload,
            MessageType = messageType,
            SerialNumber = serialNumber,
            ReceiveTime = DateTime.Now
        });

        SignalInboundConsumer();

        StartInboundConsumerOnce();
    }

    /// <summary>
    /// 发送“队列中有数据”信号。
    /// 信号只保持一个，避免一条消息对应一个Semaphore计数，批量出队后又空转消费残留信号。
    /// </summary>
    private void SignalInboundConsumer()
    {
        if (Interlocked.Exchange(ref _inboundSignalPending, 1) == 1)
        {
            return;
        }

        _inboundSignal.Release();
    }

    /// <summary>
    /// 启动 MQTT 入站业务消费者。
    /// </summary>
    private void StartInboundConsumerOnce()
    {
        if (Interlocked.Exchange(ref _inboundConsumerStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(ProcessInboundMessagesAsync);
    }

    /// <summary>
    /// 单消费者处理 MQTT 入站业务，避免多个订单并发修改同一批 AGV 状态。
    /// </summary>
    private async Task ProcessInboundMessagesAsync()
    {
        while (true)
        {
            try
            {
                await _inboundSignal.WaitAsync().ConfigureAwait(false);

                Interlocked.Exchange(ref _inboundSignalPending, 0);

                var processedCount = 0;

                while (processedCount < MaxInboundBatchSize &&
                       _inboundMessages.TryDequeue(out var item))
                {
                    Interlocked.Decrement(ref _inboundQueuedCount);
                    processedCount++;

                    try
                    {
                        using (FreezeDiagnosticService.Scope(
                                   $"MQTT业务处理 {item.MessageType} AGV[{item.SerialNumber}]",
                                   500))
                        {
                            MessageReceived?.Invoke(
                                item.Topic,
                                item.Payload,
                                item.MessageType,
                                item.SerialNumber);
                        }
                    }
                    catch (Exception ex)
                    {
                        AddSystemMessage($"MQTT业务处理失败：{ex.Message}");
                        FreezeDiagnosticService.Write("MQTT业务处理异常：" + ex);
                    }
                }

                if (!_inboundMessages.IsEmpty)
                {
                    SignalInboundConsumer();
                    await Task.Yield();
                }
            }
            catch (Exception ex)
            {
                AddSystemMessage($"MQTT业务消费者异常：{ex.Message}");
                FreezeDiagnosticService.Write("MQTT业务消费者异常：" + ex);

                await Task.Delay(1000).ConfigureAwait(false);
            }
        }
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
    /// 增加超时保护，避免 broker 或网络卡顿时拖死 VDA 仿真/发布循环。
    /// </summary>
    private async Task PublishAsync(
        string topic, string payload, int qos, bool retain, CancellationToken cancellationToken)
    {
        var lockEntered = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(topic))
            {
                AddSystemMessage("发布主题为空，已跳过");
                return;
            }

            var mqttClient = _mqttClient;

            if (mqttClient is not { IsConnected: true })
            {
                AddSystemMessage($"MQTT未连接，无法发布：{topic}");
                return;
            }

            // 单个MQTT客户端严格串行发布。
            // 即使有其他模块绕过Vda5050Service直接调用发布接口，也不会并发堆积MQTTnet内部任务。
            await _mqttPublishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockEntered = true;

            if (!mqttClient.IsConnected || !ReferenceEquals(_mqttClient, mqttClient))
            {
                return;
            }

            var heartbeatAt = Stopwatch.GetTimestamp();
            if (_lastPublishHeartbeatTimestamp == long.MinValue ||
                heartbeatAt - _lastPublishHeartbeatTimestamp >= Stopwatch.Frequency)
            {
                FreezeDiagnosticService.Heartbeat("MQTT_PUBLISH");
                _lastPublishHeartbeatTimestamp = heartbeatAt;
            }

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(ToMqttQos(qos))
                .WithRetainFlag(retain)
                .Build();

            using var timeoutCts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CancellationToken.None)
                : new CancellationTokenSource();
            timeoutCts.CancelAfter(MqttPublishTimeoutMs);

            try
            {
                // 超时和停止都取消底层发送，等待发布任务退出后再释放串行锁。
                var startedAt = Stopwatch.GetTimestamp();
                await mqttClient.PublishAsync(message, timeoutCts.Token).ConfigureAwait(false);
                var elapsedMs = (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
                if (elapsedMs >= 1500)
                {
                    FreezeDiagnosticService.WriteRateLimited(
                        "MQTT_PUBLISH_SLOW", $"MQTT发布耗时：{elapsedMs:F0}ms，topic={topic}");
                }
            }
            catch (OperationCanceledException) when (
                timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;

                if ((now - _lastPublishTimeoutUiLogTime).TotalSeconds >= 5)
                {
                    _lastPublishTimeoutUiLogTime = now;
                    AddSystemMessage($"MQTT发布超时，已取消真实发布：{topic}");
                }

                FreezeDiagnosticService.WriteRateLimited(
                    "MQTT_PUBLISH_TIMEOUT_" + topic,
                    $"MQTT发布超时并已取消：topic={topic}",
                    5000);

                return;
            }

            var messageType = MqttTopicHelper.GetMessageType(topic);
            if (ShouldRecordMqttMessage("发送", messageType))
            {
                AddMessage("发送", topic, payload, messageType, MqttTopicHelper.GetSerialNumber(topic));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AddSystemMessage($"发布消息失败：{ex.Message}");
            FreezeDiagnosticService.WriteRateLimited(
                "MQTT_PUBLISH_EXCEPTION_" + topic,
                "MQTT发布异常：" + ex,
                5000);
        }
        finally
        {
            if (lockEntered)
            {
                _mqttPublishLock.Release();
            }
        }
    }

    /// <summary>
    /// 发布VDA5050 state状态消息。
    /// </summary>
    public Task PublishStateAsync(string serialNumber, string payload)
    {
        return PublishStateAsync(serialNumber, payload, CancellationToken.None);
    }

    internal Task PublishStateAsync(string serialNumber, string payload, CancellationToken token)
    {
        var config = GetCurrentConfig();

        if (config == null)
        {
            AddSystemMessage("MQTT配置为空，无法发布 state");
            return Task.CompletedTask;
        }

        var topic = MqttTopicHelper.ResolveTopic(config.StatePublishTopic, serialNumber);

        return PublishAsync(topic, payload, config.Qos, config.RetainState, token);
    }

    /// <summary>
    /// 发布VDA5050 visualization可视化消息。
    /// </summary>
    public Task PublishVisualizationAsync(string serialNumber, string payload)
    {
        return PublishVisualizationAsync(serialNumber, payload, CancellationToken.None);
    }

    internal Task PublishVisualizationAsync(string serialNumber, string payload, CancellationToken token)
    {
        var config = GetCurrentConfig();

        if (config == null)
        {
            AddSystemMessage("MQTT配置为空，无法发布 visualization");
            return Task.CompletedTask;
        }

        var topic = MqttTopicHelper.ResolveTopic(config.VisualizationPublishTopic, serialNumber);

        return PublishAsync(topic, payload, config.Qos, config.RetainVisualization, token);
    }

    /// <summary>
    /// 发布VDA5050 connection连接状态消息。
    /// </summary>
    public Task PublishConnectionAsync(string serialNumber, string payload)
    {
        return PublishConnectionAsync(serialNumber, payload, CancellationToken.None);
    }

    internal Task PublishConnectionAsync(string serialNumber, string payload, CancellationToken token)
    {
        var config = GetCurrentConfig();

        if (config == null)
        {
            AddSystemMessage("MQTT配置为空，无法发布 connection");
            return Task.CompletedTask;
        }

        var topic = MqttTopicHelper.ResolveTopic(config.ConnectionPublishTopic, serialNumber);

        return PublishAsync(topic, payload, config.Qos, config.RetainConnection, token);
    }

    /// <summary>
    /// 发布VDA5050 factsheet能力说明消息。
    /// </summary>
    public Task PublishFactSheetAsync(string serialNumber, string payload)
    {
        return PublishFactSheetAsync(serialNumber, payload, CancellationToken.None);
    }

    internal Task PublishFactSheetAsync(string serialNumber, string payload, CancellationToken token)
    {
        var config = GetCurrentConfig();

        if (config == null)
        {
            AddSystemMessage("MQTT配置为空，无法发布 factsheet");
            return Task.CompletedTask;
        }

        var topic = MqttTopicHelper.ResolveTopic(config.FactSheetPublishTopic, serialNumber);

        return PublishAsync(topic, payload, config.Qos, config.RetainFactSheet, token);
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
    /// 优化点：
    /// 1、高频 state / visualization / connection 默认不进界面日志。
    /// 2、剩余日志先进入内存队列，再每 200ms 批量刷新一次UI。
    /// 3、限制界面最多保留 300 条，避免 ObservableCollection 长期增长。
    /// </summary>
    private void AddMessage(string direction, string topic, string payload, string messageType, string serialNumber)
    {
        if (!ShouldRecordMqttMessage(direction, messageType))
        {
            return;
        }

        if (!string.IsNullOrEmpty(payload) && payload.Length > MaxMqttPayloadLengthForUi)
        {
            payload = payload.Substring(0, MaxMqttPayloadLengthForUi) + "...";
        }

        var model = new MqttMessageModel
        {
            Time = DateTime.Now,
            Direction = direction,
            Topic = topic,
            Payload = payload,
            MessageType = messageType,
            SerialNumber = serialNumber
        };

        lock (_messageLock)
        {
            if (_pendingMessages.Count >= MaxPendingMqttUiMessageCount)
            {
                var removeCount =
                    _pendingMessages.Count - MaxPendingMqttUiMessageCount + 1;

                _pendingMessages.RemoveRange(0, removeCount);
            }

            _pendingMessages.Add(model);

            if (_messageFlushScheduled)
            {
                return;
            }

            _messageFlushScheduled = true;
        }

        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher != null)
        {
            _ = Task.Delay(MqttMessageUiFlushIntervalMs).ContinueWith(_ =>
            {
                dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)FlushPendingMqttMessages);
            });
        }
        else
        {
            FlushPendingMqttMessages();
        }
    }

    /// <summary>
    /// 判断是否需要记录到界面MQTT日志。
    /// </summary>
    private static bool ShouldRecordMqttMessage(string direction, string messageType)
    {
        if (string.Equals(direction, "系统", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(messageType, "system", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(messageType, "subscribe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(messageType, "order", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(messageType, "instantActions", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!EnableHighFrequencyMqttUiLog &&
            (string.Equals(messageType, "state", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(messageType, "visualization", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(messageType, "connection", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(messageType, "factsheet", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 批量刷新待显示的MQTT消息到UI集合。
    /// </summary>
    private void FlushPendingMqttMessages()
    {
        List<MqttMessageModel> pendingMessages;

        lock (_messageLock)
        {
            if (_pendingMessages.Count == 0)
            {
                _messageFlushScheduled = false;
                return;
            }

            pendingMessages = _pendingMessages.ToList();
            _pendingMessages.Clear();
            _messageFlushScheduled = false;
        }

        // UI长时间繁忙时只显示最新一批，避免恢复后一次插入数百条导致再次卡死。
        if (pendingMessages.Count > MaxMqttUiFlushBatchSize)
        {
            pendingMessages = pendingMessages
                .Skip(pendingMessages.Count - MaxMqttUiFlushBatchSize)
                .ToList();
        }

        // 按时间正序逐条插到顶部，最终最新消息位于第一行。
        foreach (var message in pendingMessages)
        {
            Messages.Insert(0, message);
        }

        while (Messages.Count > MaxMqttMessageCount)
        {
            Messages.RemoveAt(Messages.Count - 1);
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
