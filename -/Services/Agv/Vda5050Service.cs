using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Vda5050AgvSimulator.Helpers;
using Vda5050AgvSimulator.Models.Agv;
using Vda5050AgvSimulator.Models.Mqtt;
using Vda5050AgvSimulator.Models.Vda;

namespace Vda5050AgvSimulator.Services.Agv;

public class Vda5050Service
{
    private readonly MqttService _mqttService;

    private readonly AgvSimulationService _agvSimulationService;

    private readonly AgvStoreService _agvStoreService;

    private CancellationTokenSource? _publishCts;

    private Task? _publishTask;

    private MqttConfigModel? _mqttConfig;

    private int _headerId;

    private readonly HashSet<string> _publishedFactSheetAgvKeys = [];

    /// <summary>
    /// 初始化VDA5050协议服务。
    /// </summary>
    public Vda5050Service(
        MqttService mqttService,
        AgvSimulationService agvSimulationService)
    {
        _mqttService = mqttService;

        _agvSimulationService = agvSimulationService;

        _agvStoreService = AgvStoreService.Instance;

        _agvStoreService.EnsureLoadedFromConfig();
    }

    /// <summary>
    /// 启动VDA5050周期发布循环。
    /// 配置统一从 ConfigStoreService 获取。
    /// 启动后使用当前配置快照，运行中不再重复读取配置。
    /// </summary>
    public void StartPublishLoop()
    {
        if (_publishTask is { IsCompleted: false })
        {
            Console.WriteLine(@"VDA5050周期发布循环已经在运行，不重复启动。");
            return;
        }

        var config = ConfigStoreService.Instance.MqttConfig;

        _mqttConfig = config;

        _publishedFactSheetAgvKeys.Clear();

        _publishCts = new CancellationTokenSource();

        var cts = _publishCts;

        var token = cts.Token;

        _publishTask = Task.Run(async () =>
        {
            try
            {
                await PublishLoopAsync(token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine(@"VDA5050周期发布循环已取消。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($@"VDA5050周期发布循环异常退出：{ex}");
            }
            finally
            {
                try
                {
                    cts.Dispose();
                }
                catch
                {
                    // 释放失败不影响主流程
                }

                if (ReferenceEquals(_publishCts, cts))
                {
                    _publishCts = null;
                }
            }
        }, cts.Token);

        Console.WriteLine(@"VDA5050周期发布循环已启动。");
    }

    /// <summary>
    /// 停止VDA5050周期发布循环。
    /// </summary>
    public void StopPublishLoop()
    {
        var cts = _publishCts;

        if (cts == null)
        {
            return;
        }

        try
        {
            if (!cts.IsCancellationRequested)
            {
                cts.Cancel();
            }
        }
        catch
        {
            // 停止循环失败不影响主流程
        }
    }

    /// <summary>
    /// 执行VDA5050周期发布主循环。
    /// </summary>
    private async Task PublishLoopAsync(CancellationToken token)
    {
        var config = _mqttConfig;

        if (config == null)
        {
            Console.WriteLine(@"VDA5050周期发布循环退出：MQTT配置为空。");
            return;
        }

        var lastConnectionPublishTime = DateTime.MinValue;

        var lastVisualizationPublishTime = DateTime.MinValue;

        var lastTickTime = DateTime.Now;

        while (!token.IsCancellationRequested)
        {
            try
            {
                const double maxDeltaSeconds = 5.0;

                var now = DateTime.Now;

                var defaultDeltaSeconds = Math.Max(config.StatePublishIntervalMs, 50) / 1000.0;

                var deltaSeconds = (now - lastTickTime).TotalSeconds;

                lastTickTime = now;

                // AGV速度单位是 m/s，所以仿真步进时间使用秒。
                // 时间异常时使用默认步进时间，避免断点调试、线程卡顿、系统休眠导致车辆瞬移。
                if (deltaSeconds is <= 0 or > maxDeltaSeconds)
                {
                    deltaSeconds = defaultDeltaSeconds;
                }

                var shouldPublishConnection =
                    (now - lastConnectionPublishTime).TotalMilliseconds >=
                    config.ConnectionPublishIntervalMs;

                var shouldPublishVisualization =
                    (now - lastVisualizationPublishTime).TotalMilliseconds >=
                    config.VisualizationPublishIntervalMs;

                var agvList = GetAgvSnapshotList();

                var hasOnlineAgv = false;

                foreach (var agv in agvList)
                {
                    if (!IsAgvOnline(agv))
                    {
                        _publishedFactSheetAgvKeys.Remove(agv.DeviceID.ToString());
                        continue;
                    }

                    hasOnlineAgv = true;

                    _agvSimulationService.StepAgvRoute(agv, deltaSeconds);

                    await PublishAgvFactSheetOnceAsync(agv, config);

                    await PublishAgvStateAsync(agv, config);

                    if (shouldPublishConnection)
                    {
                        await PublishAgvConnectionAsync(agv, config);
                    }

                    if (shouldPublishVisualization)
                    {
                        await PublishAgvVisualizationAsync(agv, config);
                    }
                }

                if (hasOnlineAgv && shouldPublishConnection)
                {
                    lastConnectionPublishTime = now;
                }

                if (hasOnlineAgv && shouldPublishVisualization)
                {
                    lastVisualizationPublishTime = now;
                }

                var delay = Math.Max(config.StatePublishIntervalMs, 50);

                await Task.Delay(delay, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($@"VDA5050周期发布循环异常：{ex}");

                try
                {
                    await Task.Delay(1000, token);
                }
                catch
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 处理MQTT收到的VDA5050消息。
    /// </summary>
    public void HandleMqttMessage(string topic, string payload)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return;
        }

        if (topic.IndexOf("/order", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            HandleOrder(topic, payload);
        }
        else if (topic.IndexOf("/instantActions", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            HandleInstantActions(topic, payload);
        }
    }

    /// <summary>
    /// 处理RCS下发的VDA5050订单。
    /// </summary>
    private void HandleOrder(string topic, string payload)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return;
            }

            var json = JObject.Parse(payload);

            var order = json.ToObject<VdaOrderModel>();

            if (order == null)
            {
                return;
            }

            var topicSerialNumber = MqttTopicHelper.GetSerialNumber(topic).Trim();

            var payloadSerialNumber = order.serialNumber?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(topicSerialNumber) &&
                string.IsNullOrWhiteSpace(payloadSerialNumber))
            {
                Console.WriteLine(@"收到Order失败：Topic和Payload中都没有车辆编号。");
                return;
            }

            if (!string.IsNullOrWhiteSpace(topicSerialNumber) &&
                !string.IsNullOrWhiteSpace(payloadSerialNumber) &&
                !string.Equals(topicSerialNumber, payloadSerialNumber, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(
                    $@"收到Order失败：Topic车辆编号和Payload车辆编号不一致。Topic车辆编号：{topicSerialNumber}，Payload车辆编号：{payloadSerialNumber}");

                return;
            }

            var serialNumber = !string.IsNullOrWhiteSpace(topicSerialNumber)
                ? topicSerialNumber
                : payloadSerialNumber;

            var agv = FindAgv(serialNumber);

            if (agv == null)
            {
                Console.WriteLine($@"收到未知AGV的Order，车辆编号：{serialNumber}");
                return;
            }

            order.nodes = NormalizeReleasedOrderNodes(order.nodes);

            order.edges = NormalizeReleasedOrderEdges(order.edges);

            bool applied;

            if (order.nodes.Count == 0)
            {
                applied = _agvSimulationService.ApplyOrderHeader(
                    agv,
                    order.orderId,
                    order.orderUpdateId);
            }
            else
            {
                applied = _agvSimulationService.ApplyOrderRoute(
                    agv,
                    order.orderId,
                    order.orderUpdateId,
                    order.nodes,
                    order.edges);
            }

            if (applied)
            {
                Console.WriteLine(
                    $@"Order已应用到AGV，AGV={agv.DeviceID}，CurrentOrderId={agv.CurrentOrderId}，CurrentOrderUpdateId={agv.CurrentOrderUpdateId}，PositionID={agv.PositionID}，LastNodeSequenceId={agv.LastNodeSequenceId}，X={agv.X}，Y={agv.Y}");
            }
            else
            {
                Console.WriteLine(
                    $@"Order未应用到AGV，AGV={agv.DeviceID}，orderId={order.orderId}，orderUpdateId={order.orderUpdateId}，CurrentOrderId={agv.CurrentOrderId}，CurrentOrderUpdateId={agv.CurrentOrderUpdateId}，PositionID={agv.PositionID}，LastNodeSequenceId={agv.LastNodeSequenceId}，X={agv.X}，Y={agv.Y}");
            }

            var config = _mqttConfig;

            if (config != null)
            {
                PublishAgvStateSafe(agv, config);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($@"处理VDA5050 Order失败：{ex}");
        }
    }

    /// <summary>
    /// 处理RCS下发的VDA5050即时动作。
    /// </summary>
    private void HandleInstantActions(string topic, string payload)
    {
        try
        {
            var serialNumber = MqttTopicHelper.GetSerialNumber(topic);

            var agv = FindAgv(serialNumber);

            if (agv == null)
            {
                Console.WriteLine($@"收到未知AGV的InstantActions，车辆编号：{serialNumber}");
                return;
            }

            if (string.IsNullOrWhiteSpace(payload))
            {
                return;
            }

            var json = JObject.Parse(payload);

            var actionsToken = json["actions"] ?? json["instantActions"];

            var actions = ParseActions(actionsToken);

            _agvSimulationService.ApplyInstantActions(agv, actions);

            var config = _mqttConfig;

            if (config != null)
            {
                PublishAgvStateSafe(agv, config);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($@"处理VDA5050 InstantActions失败：{ex}");
        }
    }

    /// <summary>
    /// 安全地立即发布一次AGV State。
    /// </summary>
    private void PublishAgvStateSafe(AgvModel agv, MqttConfigModel config)
    {
        _ = PublishAgvStateSafeAsync(agv, config);
    }

    /// <summary>
    /// 安全地立即发布一次AGV State。
    /// </summary>
    private async Task PublishAgvStateSafeAsync(AgvModel agv, MqttConfigModel config)
    {
        try
        {
            await PublishAgvStateAsync(agv, config);
        }
        catch (Exception ex)
        {
            Console.WriteLine($@"立即发布AGV State失败：{ex}");
        }
    }

    /// <summary>
    /// 过滤并修正已释放的VDA5050订单节点。
    /// </summary>
    private static List<VdaOrderNodeModel> NormalizeReleasedOrderNodes(List<VdaOrderNodeModel>? nodes)
    {
        if (nodes == null || nodes.Count == 0)
        {
            return [];
        }

        var result = new List<VdaOrderNodeModel>();

        foreach (var node in nodes)
        {
            if (node == null)
            {
                continue;
            }

            if (!node.released)
            {
                continue;
            }

            node.nodePosition ??= new VdaOrderNodePositionModel();

            if (MapStoreService.TryGetPoint(node.nodeId, out var mapPoint, out _))
            {
                if (mapPoint != null)
                {
                    node.nodePosition.x = mapPoint.x;

                    node.nodePosition.y = mapPoint.y;
                }
            }

            result.Add(node);
        }

        return result
            .OrderBy(x => x.sequenceId)
            .ToList();
    }

    /// <summary>
    /// 过滤并排序已释放的VDA5050订单边。
    /// </summary>
    private static List<VdaOrderEdgeModel> NormalizeReleasedOrderEdges(List<VdaOrderEdgeModel>? edges)
    {
        if (edges == null || edges.Count == 0)
        {
            return [];
        }

        return edges
            .Where(x => x != null)
            .Where(x => x.released)
            .OrderBy(x => x.sequenceId)
            .ToList();
    }

    /// <summary>
    /// 解析VDA5050动作列表。
    /// </summary>
    private static List<AgvActionModel> ParseActions(JToken? actionsToken)
    {
        if (actionsToken == null)
        {
            return [];
        }

        try
        {
            var actions = actionsToken.ToObject<List<AgvActionModel>>();

            return actions ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 发布单台AGV的factsheet报文。
    /// 每次AGV上线周期内只发布一次。
    /// AGV下线后，外层循环会移除发布标记，下次上线会重新发布。
    /// </summary>
    private async Task PublishAgvFactSheetOnceAsync(AgvModel? agv, MqttConfigModel? config)
    {
        if (agv == null) return;
        if (config == null) return;
        if (!config.PublishFactSheetOnOnline) return;
        if (!_mqttService.IsConnected) return;
        if (!IsAgvOnline(agv)) return;

        var agvKey = agv.DeviceID.ToString();

        if (_publishedFactSheetAgvKeys.Contains(agvKey)) return;

        var payload = VdaPayloadBuilder.BuildFactSheetPayload(
            config,
            agv,
            GetNextHeaderId());

        await _mqttService.PublishFactSheetAsync(agvKey, payload);

        _publishedFactSheetAgvKeys.Add(agvKey);
    }

    /// <summary>
    /// 发布单台AGV的state报文。
    /// </summary>
    private async Task PublishAgvStateAsync(AgvModel agv, MqttConfigModel config)
    {
        if (!_mqttService.IsConnected)
        {
            return;
        }

        if (!IsAgvOnline(agv))
        {
            return;
        }

        var payload = VdaPayloadBuilder.BuildStatePayload(
            config,
            agv,
            GetNextHeaderId());

        await _mqttService.PublishStateAsync(agv.DeviceID.ToString(), payload);
    }

    /// <summary>
    /// 发布单台AGV的connection报文。
    /// </summary>
    private async Task PublishAgvConnectionAsync(AgvModel agv, MqttConfigModel config)
    {
        if (!_mqttService.IsConnected)
        {
            return;
        }

        if (!IsAgvOnline(agv))
        {
            return;
        }

        var payload = VdaPayloadBuilder.BuildConnectionPayload(
            config,
            agv,
            0,
            GetNextHeaderId());

        await _mqttService.PublishConnectionAsync(agv.DeviceID.ToString(), payload);
    }

    /// <summary>
    /// 发布单台AGV的visualization报文。
    /// </summary>
    private async Task PublishAgvVisualizationAsync(AgvModel agv, MqttConfigModel config)
    {
        if (!_mqttService.IsConnected)
        {
            return;
        }

        if (!IsAgvOnline(agv))
        {
            return;
        }

        var payload = VdaPayloadBuilder.BuildVisualizationPayload(
            config,
            agv,
            GetNextHeaderId());

        await _mqttService.PublishVisualizationAsync(agv.DeviceID.ToString(), payload);
    }

    /// <summary>
    /// 根据车辆编号查找AGV。
    /// </summary>
    private AgvModel? FindAgv(string serialNumber)
    {
        return _agvStoreService.FindAgv(serialNumber);
    }

    /// <summary>
    /// 获取AGV列表快照。
    /// </summary>
    private List<AgvModel> GetAgvSnapshotList()
    {
        return _agvStoreService.GetSnapshotList();
    }

    /// <summary>
    /// 判断AGV是否在线。
    /// </summary>
    private static bool IsAgvOnline(AgvModel agv)
    {
        return agv.OnlineStatus == 1;
    }

    /// <summary>
    /// 获取下一个VDA5050 headerId。
    /// </summary>
    private int GetNextHeaderId()
    {
        return Interlocked.Increment(ref _headerId);
    }
}