using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
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

    private volatile PublishSession? _publishSession;
    private bool _publishRequested;
    private long _publishRequestRevision;

    private int _headerId;

    /// <summary>
    /// 发布循环生命周期锁，防止连接状态连续变化时重复启动多个循环。
    /// </summary>
    private readonly object _publishLifecycleLock = new();

    private const int MaxPendingFactsheets = 256;
    private const int MinActiveAgvsForAutoParallelism = 8;
    private const int ActiveAgvsPerWorker = 8;
    private const int AutomaticParallelismLimit = 8;
    private const int MaxCatchUpSteps = 2;
    private const int MaxStateRepliesInARow = 8;

    private enum PublishKind
    {
        State,
        Connection,
        Visualization,
        FactSheet
    }

    [Flags]
    private enum PublishDue
    {
        None = 0,
        State = 1,
        Connection = 2,
        Visualization = 4,
        FactSheet = 8,
        MovingVisualization = 16
    }

    private readonly struct PublishWorkItem
    {
        public AgvModel Agv { get; }
        public int DeviceId { get; }
        public PublishKind Kind { get; }
        public VdaPayloadBuilder.StateSnapshot? StateSnapshot { get; }
        public bool IsAutomaticFactsheet { get; }

        public PublishWorkItem(
            AgvModel agv,
            int deviceId,
            PublishKind kind,
            VdaPayloadBuilder.StateSnapshot? stateSnapshot = null,
            bool isAutomaticFactsheet = false)
        {
            Agv = agv;
            DeviceId = deviceId;
            Kind = kind;
            StateSnapshot = stateSnapshot;
            IsAutomaticFactsheet = isAutomaticFactsheet;
        }
    }

    private sealed class AgvReferenceComparer : IEqualityComparer<AgvModel>
    {
        public static readonly AgvReferenceComparer Instance = new();

        public bool Equals(AgvModel? x, AgvModel? y) => ReferenceEquals(x, y);

        public int GetHashCode(AgvModel agv) => RuntimeHelpers.GetHashCode(agv);
    }

    private sealed class PublishSession : IDisposable
    {
        private readonly object _queueLock = new();
        private readonly Queue<PublishWorkItem> _requestedFactsheets = new();
        private readonly Queue<PublishWorkItem> _automaticFactsheets = new();
        private readonly Queue<int> _stateReplyKeys = new();
        private readonly Dictionary<int, PublishWorkItem> _stateReplies = new();
        private readonly Queue<(int DeviceId, PublishKind Kind)> _latestKeys = new();
        private readonly Dictionary<(int DeviceId, PublishKind Kind), PublishWorkItem> _latestItems = new();
        private readonly CancellationTokenSource _cancellation = new();
        private volatile Task? _cancelTask;
        private bool _wakePending;
        private long _coalescedCount;

        public MqttConfigModel Config { get; }
        public CancellationToken Token { get; }
        public SemaphoreSlim Signal { get; } = new(0, 1);
        public long RequestRevision { get; }
        public bool IsStopping => _cancelTask != null;

        public PublishSession(MqttConfigModel config, long requestRevision)
        {
            Config = config;
            Token = _cancellation.Token;
            RequestRevision = requestRevision;
        }

        public Task CancelAsync()
        {
            lock (_queueLock)
            {
                return _cancelTask ??= Task.Run(() => _cancellation.Cancel());
            }
        }

        public bool EnqueueFactsheetResponse(AgvModel agv, VdaPayloadBuilder.StateSnapshot state)
        {
            lock (_queueLock)
            {
                if (_cancelTask != null || _requestedFactsheets.Count >= MaxPendingFactsheets)
                {
                    return false;
                }

                var deviceId = agv.DeviceID;
                _requestedFactsheets.Enqueue(new PublishWorkItem(agv, deviceId, PublishKind.FactSheet));
                EnqueueStateReply(new PublishWorkItem(agv, deviceId, PublishKind.State, state));
                SignalIfNeeded();
                return true;
            }
        }

        public void EnqueueState(AgvModel agv, VdaPayloadBuilder.StateSnapshot state)
        {
            lock (_queueLock)
            {
                if (_cancelTask != null)
                {
                    return;
                }

                EnqueueStateReply(new PublishWorkItem(agv, agv.DeviceID, PublishKind.State, state));
                SignalIfNeeded();
            }
        }

        private void EnqueueStateReply(PublishWorkItem item)
        {
            EnqueueLatest(_stateReplyKeys, _stateReplies, item.DeviceId, item);
        }

        public void EnqueuePeriodic(
            List<AgvModel> onlineAgvs, Dictionary<int, AgvModel> factsheets, PublishDue due, bool fasterMovingVisualization)
        {
            lock (_queueLock)
            {
                if (_cancelTask != null)
                {
                    return;
                }

                var visualizationDue = due & (PublishDue.Visualization | PublishDue.MovingVisualization);
                foreach (var agv in onlineAgvs)
                {
                    if (!IsAgvOnline(agv))
                    {
                        continue;
                    }

                    var deviceId = agv.DeviceID;
                    if ((due & PublishDue.FactSheet) != 0 &&
                        _automaticFactsheets.Count < MaxPendingFactsheets &&
                        (!factsheets.TryGetValue(deviceId, out var publishedAgv) ||
                         !ReferenceEquals(publishedAgv, agv)))
                    {
                        _automaticFactsheets.Enqueue(
                            new PublishWorkItem(agv, deviceId, PublishKind.FactSheet, isAutomaticFactsheet: true));
                        factsheets[deviceId] = agv;
                    }

                    if ((due & PublishDue.State) != 0)
                    {
                        EnqueuePeriodicItem(agv, deviceId, PublishKind.State);
                    }

                    if ((due & PublishDue.Connection) != 0)
                    {
                        EnqueuePeriodicItem(agv, deviceId, PublishKind.Connection);
                    }

                    if (visualizationDue != PublishDue.None)
                    {
                        var positionDue = fasterMovingVisualization && IsAgvMoving(agv)
                            ? PublishDue.MovingVisualization
                            : PublishDue.Visualization;
                        if ((visualizationDue & positionDue) != 0)
                        {
                            EnqueuePeriodicItem(agv, deviceId, PublishKind.Visualization);
                        }
                    }
                }

                SignalIfNeeded();
            }
        }

        public void PrunePublishItems(HashSet<AgvModel> currentAgvs)
        {
            lock (_queueLock)
            {
                PruneLatest(_latestKeys, _latestItems, currentAgvs);
                PruneLatest(_stateReplyKeys, _stateReplies, currentAgvs);
                PruneFactsheets(_automaticFactsheets, currentAgvs);
                PruneFactsheets(_requestedFactsheets, currentAgvs);
            }
        }

        private static void PruneLatest<TKey>(
            Queue<TKey> keys, Dictionary<TKey, PublishWorkItem> items, HashSet<AgvModel> currentAgvs)
            where TKey : notnull
        {
            var count = keys.Count;
            for (var index = 0; index < count; index++)
            {
                var key = keys.Dequeue();
                if (IsCurrentOnlineAgv(items[key], currentAgvs))
                {
                    keys.Enqueue(key);
                }
                else
                {
                    items.Remove(key);
                }
            }
        }

        private static void PruneFactsheets(Queue<PublishWorkItem> queue, HashSet<AgvModel> onlineAgvs)
        {
            var count = queue.Count;
            for (var index = 0; index < count; index++)
            {
                var item = queue.Dequeue();
                if (IsCurrentOnlineAgv(item, onlineAgvs))
                {
                    queue.Enqueue(item);
                }
            }
        }

        private static bool IsCurrentOnlineAgv(PublishWorkItem item, HashSet<AgvModel> agvs)
        {
            return agvs.Contains(item.Agv) && IsAgvOnline(item.Agv) && item.DeviceId == item.Agv.DeviceID;
        }

        private void EnqueuePeriodicItem(AgvModel agv, int deviceId, PublishKind kind)
        {
            EnqueueLatest(_latestKeys, _latestItems, (deviceId, kind), new PublishWorkItem(agv, deviceId, kind));
        }

        private void EnqueueLatest<TKey>(
            Queue<TKey> keys, Dictionary<TKey, PublishWorkItem> items, TKey key, PublishWorkItem item)
            where TKey : notnull
        {
            var alreadyQueued = items.ContainsKey(key);
            items[key] = item;
            if (alreadyQueued)
            {
                _coalescedCount++;
            }
            else
            {
                keys.Enqueue(key);
            }
        }

        private void SignalIfNeeded()
        {
            // 标记保持到消费者发现队列为空，整批报文共用一次唤醒。
            if (!_wakePending && PendingCount > 0)
            {
                _wakePending = true;
                Signal.Release();
            }
        }

        public bool TryTake(out PublishWorkItem item, bool preferPeriodic, bool preferLatest)
        {
            lock (_queueLock)
            {
                if (_requestedFactsheets.Count != 0)
                {
                    item = _requestedFactsheets.Dequeue();
                    return true;
                }

                if (!preferPeriodic && TryTakeStateReply(out item))
                {
                    return true;
                }

                if (!preferLatest && _automaticFactsheets.Count != 0)
                {
                    item = _automaticFactsheets.Dequeue();
                    return true;
                }

                if (_latestKeys.Count != 0)
                {
                    var key = _latestKeys.Dequeue();
                    item = _latestItems[key];
                    _latestItems.Remove(key);
                    return true;
                }

                if (_automaticFactsheets.Count != 0)
                {
                    item = _automaticFactsheets.Dequeue();
                    return true;
                }

                if (TryTakeStateReply(out item))
                {
                    return true;
                }

                _wakePending = false;
                return false;
            }
        }

        private bool TryTakeStateReply(out PublishWorkItem item)
        {
            if (_stateReplyKeys.Count != 0)
            {
                var deviceId = _stateReplyKeys.Dequeue();
                item = _stateReplies[deviceId];
                _stateReplies.Remove(deviceId);
                return true;
            }

            item = default;
            return false;
        }

        private int PendingCount => _requestedFactsheets.Count + _automaticFactsheets.Count +
                                    _stateReplies.Count + _latestItems.Count;

        public int GetPendingCount()
        {
            lock (_queueLock)
            {
                return PendingCount;
            }
        }

        public (int PendingCount, long CoalescedCount) TakePublishStatistics()
        {
            lock (_queueLock)
            {
                var coalescedCount = _coalescedCount;
                _coalescedCount = 0;
                return (PendingCount, coalescedCount);
            }
        }

        public void Dispose()
        {
            lock (_queueLock)
            {
                // 会话已取消，之后仍在处理的旧订单在 Enqueue 内直接退出。
                Signal.Dispose();
                _cancellation.Dispose();
            }
        }
    }

    /// <summary>
    /// 仿真按50ms推进，MQTT发布仍按各报文的配置频率执行。
    /// </summary>
    private const int SimulationTickIntervalMs = 50;
    private const int MinMqttPublishIntervalMs = 100;

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
        lock (_publishLifecycleLock)
        {
            if (!_publishRequested || _publishSession is { IsStopping: true })
            {
                _publishRequested = true;
                _publishRequestRevision++;
            }

            if (_publishSession == null)
            {
                StartPublishSession();
            }
        }
    }

    // 在生命周期锁内调用。由旧会话退出后接手重连，确保仿真轮次不会重叠。
    private void StartPublishSession()
    {
        var session = new PublishSession(ConfigStoreService.Instance.MqttConfig, _publishRequestRevision);
        _publishSession = session;
        _ = Task.Run(() => RunPublishSessionAsync(session));
    }

    private async Task RunPublishSessionAsync(PublishSession session)
    {
        var token = session.Token;
        var worker = PublishWorkerAsync(session, token);

        try
        {
            await PublishLoopAsync(session, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            FreezeDiagnosticService.Write("VDA发布循环外层异常：" + ex);
        }
        finally
        {
            try
            {
                await session.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FreezeDiagnosticService.Write("VDA发布取消异常：" + ex);
            }

            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                FreezeDiagnosticService.Write("VDA发布消费者退出异常：" + ex);
            }
            finally
            {
                lock (_publishLifecycleLock)
                {
                    _publishSession = null;
                    session.Dispose();
                    if (_publishRequested && _publishRequestRevision != session.RequestRevision)
                    {
                        StartPublishSession();
                    }
                }
            }
        }
    }

    /// <summary>
    /// 停止仿真与发布，取消当前 MQTT 发送。
    /// </summary>
    public void StopPublishLoop()
    {
        lock (_publishLifecycleLock)
        {
            _publishRequested = false;
            if (_publishSession is { } session)
            {
                _ = session.CancelAsync();
            }
        }
    }

    private async Task PublishLoopAsync(PublishSession session, CancellationToken token)
    {
        var config = session.Config;
        var parallelism = config.SimulationMaxParallelism > 0
            ? Math.Min(config.SimulationMaxParallelism, Environment.ProcessorCount)
            : Math.Min(AutomaticParallelismLimit, Math.Max(1, Environment.ProcessorCount - 2));
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = token
        };

        var agvs = new List<AgvModel>();
        var onlineAgvs = new List<AgvModel>();
        var activeAgvs = new List<AgvModel>();
        var currentAgvSet = new HashSet<AgvModel>(AgvReferenceComparer.Instance);
        var factsheets = new Dictionary<int, AgvModel>();
        var staleFactsheets = new List<int>();
        var stateIntervalMs = Math.Max(config.StatePublishIntervalMs, MinMqttPublishIntervalMs);
        var connectionIntervalMs = Math.Max(config.ConnectionPublishIntervalMs, MinMqttPublishIntervalMs);
        var visualizationIntervalMs = Math.Max(config.VisualizationPublishIntervalMs, MinMqttPublishIntervalMs);
        var movingVisualizationIntervalMs = config.MovingVisualizationPublishIntervalMs > 0
            ? Math.Min(visualizationIntervalMs, Math.Max(config.MovingVisualizationPublishIntervalMs, MinMqttPublishIntervalMs))
            : visualizationIntervalMs;
        var fasterMovingVisualization = movingVisualizationIntervalMs < visualizationIntervalMs;
        var nextStateMs = 0L;
        var nextConnectionMs = 0L;
        var nextVisualizationMs = 0L;
        var nextMovingVisualizationMs = 0L;
        var nextPruneMs = 1000L;
        var nextTickMs = (long)SimulationTickIntervalMs;
        var clock = Stopwatch.StartNew();
        var previousTick = clock.ElapsedTicks;

        while (!token.IsCancellationRequested)
        {
            var tickStarted = clock.ElapsedTicks;
            var tickStartedMs = clock.ElapsedMilliseconds;
            var actualDeltaSeconds = (tickStarted - previousTick) / (double)Stopwatch.Frequency;
            previousTick = tickStarted;

            try
            {
                FreezeDiagnosticService.Heartbeat("VDA_LOOP");

                _agvStoreService.CopySnapshotTo(agvs);
                onlineAgvs.Clear();
                activeAgvs.Clear();
                foreach (var agv in agvs)
                {
                    if (agv == null)
                    {
                        continue;
                    }

                    if (!IsAgvOnline(agv))
                    {
                        factsheets.Remove(agv.DeviceID);
                        continue;
                    }

                    onlineAgvs.Add(agv);
                    if (_agvSimulationService.HasSimulationWork(agv))
                    {
                        activeAgvs.Add(agv);
                    }
                }

                var simulationStartMs = clock.ElapsedMilliseconds;
                var autoWorkers = activeAgvs.Count < MinActiveAgvsForAutoParallelism
                    ? 1
                    : 2 + (activeAgvs.Count - MinActiveAgvsForAutoParallelism) / ActiveAgvsPerWorker;
                parallelOptions.MaxDegreeOfParallelism = Math.Min(
                    parallelism, config.SimulationMaxParallelism > 0 ? Math.Max(1, activeAgvs.Count) : autoWorkers);
                var changed = StepActiveAgvs(activeAgvs, actualDeltaSeconds, parallelOptions);
                var simulationMs = clock.ElapsedMilliseconds - simulationStartMs;

                if (changed)
                {
                    _agvStoreService.NotifyAgvStateUpdated();
                }

                if (tickStartedMs >= nextPruneMs)
                {
                    nextPruneMs = tickStartedMs + 1000;
                    currentAgvSet.Clear();
                    currentAgvSet.UnionWith(agvs);
                    session.PrunePublishItems(currentAgvSet);
                    staleFactsheets.Clear();
                    foreach (var factsheet in factsheets)
                    {
                        if (!currentAgvSet.Contains(factsheet.Value) || !IsAgvOnline(factsheet.Value) ||
                            factsheet.Key != factsheet.Value.DeviceID)
                        {
                            staleFactsheets.Add(factsheet.Key);
                        }
                    }
                    foreach (var deviceId in staleFactsheets)
                    {
                        factsheets.Remove(deviceId);
                    }
                }

                if (onlineAgvs.Count == 0)
                {
                    nextStateMs = nextConnectionMs = nextVisualizationMs = nextMovingVisualizationMs = 0;
                }
                else if (_mqttService.IsConnected)
                {
                    var due = config.PublishFactSheetOnOnline ? PublishDue.FactSheet : PublishDue.None;
                    if (IsPublishDue(tickStartedMs, stateIntervalMs, ref nextStateMs))
                    {
                        due |= PublishDue.State;
                    }
                    if (IsPublishDue(tickStartedMs, connectionIntervalMs, ref nextConnectionMs))
                    {
                        due |= PublishDue.Connection;
                    }
                    if (IsPublishDue(tickStartedMs, visualizationIntervalMs, ref nextVisualizationMs))
                    {
                        due |= PublishDue.Visualization;
                    }
                    if (fasterMovingVisualization &&
                        IsPublishDue(tickStartedMs, movingVisualizationIntervalMs, ref nextMovingVisualizationMs))
                    {
                        due |= PublishDue.MovingVisualization;
                    }

                    if (due != PublishDue.None)
                    {
                        session.EnqueuePeriodic(onlineAgvs, factsheets, due, fasterMovingVisualization);
                    }
                }

                var loopMs = clock.ElapsedMilliseconds - tickStartedMs;
                if (loopMs >= SimulationTickIntervalMs || actualDeltaSeconds > AgvSimulationService.MaxDeltaSeconds)
                {
                    FreezeDiagnosticService.WriteRateLimited(
                        "VDA_TICK_SLOW",
                        $"VDA循环：间隔={actualDeltaSeconds * 1000:F0}ms，处理={loopMs}ms，仿真={simulationMs}ms，在线={onlineAgvs.Count}，活动={activeAgvs.Count}，本轮并行上限={parallelOptions.MaxDegreeOfParallelism}，待发布={session.GetPendingCount()}");
                }

                // 使用固定的目标时刻，吸收计时器抖动。超时后从当前时刻恢复正常节拍。
                var finishedMs = clock.ElapsedMilliseconds;
                var remainingMs = nextTickMs - finishedMs;
                if (remainingMs <= 0)
                {
                    // 调度延迟时跳过已错过的时刻，避免出现长步后紧跟一个极短步。
                    var overdueMs = finishedMs - nextTickMs;
                    remainingMs = SimulationTickIntervalMs - overdueMs % SimulationTickIntervalMs;
                }

                nextTickMs = finishedMs + remainingMs + SimulationTickIntervalMs;
                await Task.Delay((int)remainingMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                FreezeDiagnosticService.WriteRateLimited("VDA_TICK_ERROR", "VDA循环异常：" + ex);
                nextTickMs = clock.ElapsedMilliseconds + 2 * SimulationTickIntervalMs;
                await Task.Delay(SimulationTickIntervalMs, token).ConfigureAwait(false);
            }
        }
    }

    private static bool IsPublishDue(long nowMs, int intervalMs, ref long nextMs)
    {
        if (nowMs < nextMs)
        {
            return false;
        }

        // 跳过已经错过的报文周期，保留目标频率。
        var elapsedMs = nowMs - nextMs;
        nextMs = nowMs + intervalMs - elapsedMs % intervalMs;
        return true;
    }

    private bool StepActiveAgvs(List<AgvModel> agvs, double deltaSeconds, ParallelOptions options)
    {
        if (agvs.Count == 0 || deltaSeconds <= 0)
        {
            return false;
        }

        deltaSeconds = Math.Min(deltaSeconds, AgvSimulationService.MaxDeltaSeconds * MaxCatchUpSteps);

        if (options.MaxDegreeOfParallelism == 1)
        {
            var changed = false;
            foreach (var agv in agvs)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                changed |= StepAgvSafe(agv, deltaSeconds);
            }

            return changed;
        }

        var anyChanged = 0;
        Parallel.For(0, agvs.Count, options,
            () => false,
            (index, _, changed) => StepAgvSafe(agvs[index], deltaSeconds) || changed,
            changed =>
            {
                if (changed)
                {
                    Interlocked.Exchange(ref anyChanged, 1);
                }
            });
        return anyChanged != 0;
    }

    private bool StepAgvSafe(AgvModel agv, double deltaSeconds)
    {
        try
        {
            var changed = _agvSimulationService.StepOnlineAgv(agv, deltaSeconds);
            if (deltaSeconds > AgvSimulationService.MaxDeltaSeconds)
            {
                changed |= _agvSimulationService.StepOnlineAgv(
                    agv, deltaSeconds - AgvSimulationService.MaxDeltaSeconds);
            }

            return changed;
        }
        catch (Exception ex)
        {
            FreezeDiagnosticService.WriteRateLimited(
                $"VDA_STEP_{agv.DeviceID}", $"AGV[{agv.DeviceID}] 仿真异常：{ex}");
            // 失败前可能已经改变车体状态，仍将这一轮的变化通知界面。
            return true;
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
            var session = _publishSession;
            if (string.IsNullOrWhiteSpace(payload))
            {
                return;
            }

            var order = JsonConvert.DeserializeObject<VdaOrderModel>(payload);

            if (order == null)
            {
                return;
            }

            var topicSerialNumber = MqttTopicHelper.GetSerialNumber(topic).Trim();

            var payloadSerialNumber = order.serialNumber?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(topicSerialNumber) &&
                string.IsNullOrWhiteSpace(payloadSerialNumber))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(topicSerialNumber) &&
                !string.IsNullOrWhiteSpace(payloadSerialNumber) &&
                !string.Equals(topicSerialNumber, payloadSerialNumber, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var serialNumber = !string.IsNullOrWhiteSpace(topicSerialNumber)
                ? topicSerialNumber
                : payloadSerialNumber;

            var agv = FindAgv(serialNumber);

            if (agv == null)
            {
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

            if (!applied)
            {
                FreezeDiagnosticService.Write(
                    $"订单未应用：AGV[{agv.DeviceID}] orderId={order.orderId} updateId={order.orderUpdateId}");
            }

            if (session != null)
            {
                QueueImmediateState(session, agv);
            }
        }
        catch (Exception ex)
        {
            FreezeDiagnosticService.Write("处理订单异常：" + ex);
        }
    }

    /// <summary>
    /// 处理RCS下发的VDA5050即时动作。
    /// </summary>
    private void HandleInstantActions(string topic, string payload)
    {
        try
        {
            var session = _publishSession;
            var serialNumber = MqttTopicHelper.GetSerialNumber(topic);

            var agv = FindAgv(serialNumber);

            if (agv == null)
            {
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

            if (session != null)
            {
                var factsheetRequested = actions.Any(action =>
                    action != null && string.Equals(action.actionType, "factsheetRequest", StringComparison.OrdinalIgnoreCase));
                QueueImmediateState(session, agv, factsheetRequested);
            }
        }
        catch (Exception ex)
        {
            FreezeDiagnosticService.Write("处理即时动作异常：" + ex);
        }
    }

    /// <summary>
    /// 安全地立即投递一次AGV State。
    /// 不直接 await MQTT，避免 MQTT 慢时阻塞订单处理链路。
    /// </summary>
    private void QueueImmediateState(PublishSession session, AgvModel agv, bool factsheetRequested = false)
    {
        if (session.Token.IsCancellationRequested || !_mqttService.IsConnected || !IsAgvOnline(agv))
        {
            return;
        }

        var state = VdaPayloadBuilder.CaptureStateSnapshot(session.Config, agv);
        if (factsheetRequested)
        {
            if (session.EnqueueFactsheetResponse(agv, state))
            {
                return;
            }

            if (!session.Token.IsCancellationRequested)
            {
                FreezeDiagnosticService.WriteRateLimited(
                    "VDA_FACTSHEET_QUEUE_FULL", $"AGV[{agv.DeviceID}] factsheetRequest 未入队。");
            }
        }

        session.EnqueueState(agv, state);
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

        var result = new List<VdaOrderNodeModel>(nodes.Count);

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

        return result;
    }

    /// <summary>
    /// 过滤已释放的VDA5050订单边，交由路线服务统一排序。
    /// </summary>
    private static List<VdaOrderEdgeModel> NormalizeReleasedOrderEdges(List<VdaOrderEdgeModel>? edges)
    {
        if (edges == null || edges.Count == 0)
        {
            return [];
        }

        return edges
            .FindAll(x => x != null && x.released);
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
    /// MQTT单连接按队列顺序发送。周期报文只在出队时抓取车况并生成JSON。
    /// </summary>
    private async Task PublishWorkerAsync(PublishSession session, CancellationToken token)
    {
        const int maxAutomaticFactsheetsInARow = 4;
        var automaticFactsheetsInARow = 0;
        var stateRepliesInARow = 0;
        var nextLagCheckAt = 5000L;
        var clock = Stopwatch.StartNew();

        while (!token.IsCancellationRequested)
        {
            await session.Signal.WaitAsync(token).ConfigureAwait(false);

            while (!token.IsCancellationRequested &&
                   session.TryTake(out var item,
                       stateRepliesInARow >= MaxStateRepliesInARow,
                       automaticFactsheetsInARow >= maxAutomaticFactsheetsInARow))
            {
                var itemStartedMs = clock.ElapsedMilliseconds;
                try
                {
                    await PublishWorkItemAsync(session.Config, item, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    FreezeDiagnosticService.WriteRateLimited(
                        $"VDA_PUBLISH_{item.Kind}_{item.DeviceId}",
                        $"AGV[{item.DeviceId}] {item.Kind} 发布异常：{ex}");
                }

                var itemFinishedMs = clock.ElapsedMilliseconds;
                var costMs = itemFinishedMs - itemStartedMs;
                if (costMs >= 1500)
                {
                    FreezeDiagnosticService.WriteRateLimited(
                        "VDA_PUBLISH_SLOW", $"AGV[{item.DeviceId}] {item.Kind} 发布耗时：{costMs}ms");
                }

                if (itemFinishedMs >= nextLagCheckAt)
                {
                    nextLagCheckAt = itemFinishedMs + 5000;
                    var (pendingCount, coalescedCount) = session.TakePublishStatistics();
                    if (coalescedCount > 0)
                    {
                        FreezeDiagnosticService.Write(
                            $"VDA发布队列：待发布={pendingCount}，周期内合并={coalescedCount}，当前={item.Kind}，处理={costMs}ms");
                    }
                }

                automaticFactsheetsInARow = item.IsAutomaticFactsheet
                    ? Math.Min(maxAutomaticFactsheetsInARow, automaticFactsheetsInARow + 1)
                    : 0;
                stateRepliesInARow = item.StateSnapshot != null
                    ? Math.Min(MaxStateRepliesInARow, stateRepliesInARow + 1)
                    : 0;
            }
        }
    }

    private Task PublishWorkItemAsync(MqttConfigModel config, PublishWorkItem item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_mqttService.IsConnected || !IsAgvOnline(item.Agv) || item.Agv.DeviceID != item.DeviceId)
        {
            return Task.CompletedTask;
        }

        var serialNumber = item.DeviceId.ToString();
        var headerId = GetNextHeaderId();
        switch (item.Kind)
        {
            case PublishKind.State:
                var snapshot = item.StateSnapshot;
                // 周期State可能已经发送了更新的车况，回执用当前状态避免位置和动作回退。
                if (snapshot == null || snapshot.CapturedAt <= item.Agv.LastPublishedStateSampleAt)
                {
                    snapshot = VdaPayloadBuilder.CaptureStateSnapshot(config, item.Agv);
                }

                item.Agv.LastPublishedStateSampleAt = snapshot.CapturedAt;
                return _mqttService.PublishStateAsync(
                    serialNumber, VdaPayloadBuilder.SerializeStateSnapshot(snapshot, headerId), token);
            case PublishKind.Connection:
                return _mqttService.PublishConnectionAsync(
                    serialNumber, VdaPayloadBuilder.BuildConnectionPayload(config, item.Agv, 0, headerId), token);
            case PublishKind.Visualization:
                return _mqttService.PublishVisualizationAsync(
                    serialNumber, VdaPayloadBuilder.BuildVisualizationPayload(config, item.Agv, headerId), token);
            case PublishKind.FactSheet:
                return _mqttService.PublishFactSheetAsync(
                    serialNumber, VdaPayloadBuilder.BuildFactSheetPayload(config, item.Agv, headerId), token);
            default:
                throw new ArgumentOutOfRangeException(nameof(item));
        }
    }

    /// <summary>
    /// 根据车辆编号查找AGV。
    /// </summary>
    private AgvModel? FindAgv(string serialNumber)
    {
        return _agvStoreService.FindAgv(serialNumber);
    }

    /// <summary>
    /// 判断AGV是否在线。
    /// </summary>
    private static bool IsAgvOnline(AgvModel agv)
    {
        return !agv.IsRemovedFromStore && agv.OnlineStatus == 1;
    }

    private static bool IsAgvMoving(AgvModel agv)
    {
        return !agv.VdaPaused && (agv.RunStatus == 1 || agv.RouteRuntime is { IsRotating: true });
    }

    /// <summary>
    /// 获取下一个VDA5050 headerId。
    /// </summary>
    private int GetNextHeaderId()
    {
        return Interlocked.Increment(ref _headerId);
    }
}
