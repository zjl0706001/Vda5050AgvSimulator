using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vda5050AgvSimulator.Helpers;
using Vda5050AgvSimulator.Models.Agv;
using Vda5050AgvSimulator.Models.Vda;

namespace Vda5050AgvSimulator.Services.Agv;

public class AgvSimulationService
{
    private readonly object _lockObj = new();

    private readonly AgvStoreService _agvStoreService;

    private const double DefaultMoveSpeed = 0.5;

    private const double MinMoveSpeed = 0.05;

    private const double ReachTolerance = 0.02;

    private const double DefaultRotateSpeedDegreePerSecond = 30.0;

    private const double RotateToleranceDegree = 0.5;

    private const double MaxSimulationStepSeconds = 0.05;

    private const double MaxDeltaSeconds = 0.5;

    private const double DefaultLiftActionDurationSeconds = 4.0;

    private const double LiftActionInitializingSeconds = 0.2;

    private const double LiftActionDownLoadSwitchSeconds = 0.4;

    private const double LiftActionUpLoadSwitchRatio = 0.65;

    private const int EmptyLoadStatus = 0;

    private const int LoadedStatus = 1;

    private readonly Dictionary<string, LiftActionRuntimeModel> _liftActionRuntimes = [];

    private sealed class LiftActionRuntimeModel
    {
        public int DeviceId { get; set; }

        public string ActionId { get; set; } = string.Empty;

        public string OrderId { get; set; } = string.Empty;

        public int OrderUpdateId { get; set; }

        public AgvActionModel Action { get; set; } = new();

        public LiftActionDirection Direction { get; set; }

        public int TargetHeight { get; set; }

        public double ElapsedSeconds { get; set; }

        public double DurationSeconds { get; set; } = DefaultLiftActionDurationSeconds;

        public bool LoadStatusApplied { get; set; }
    }

    private enum LiftActionDirection
    {
        Up = 1,

        Down = 2
    }

    /// <summary>
    /// 初始化AGV仿真服务。
    /// </summary>
    public AgvSimulationService()
    {
        _agvStoreService = AgvStoreService.Instance;

        _agvStoreService.EnsureLoadedFromConfig();
    }

    /// <summary>
    /// 应用VDA5050订单基础信息。
    /// </summary>
    public bool ApplyOrderHeader(AgvModel? agv, string orderId, int orderUpdateId)
    {
        if (agv == null)
        {
            return false;
        }

        bool needNotify;
        bool applied;

        lock (_lockObj)
        {
            if (string.Equals(agv.CurrentOrderId, orderId, StringComparison.OrdinalIgnoreCase) &&
                orderUpdateId < agv.CurrentOrderUpdateId)
            {
                Console.WriteLine(
                    $@"收到旧的Order Header，忽略。AGV={agv.DeviceID}，当前orderId={agv.CurrentOrderId}，当前orderUpdateId={agv.CurrentOrderUpdateId}，收到orderId={orderId}，收到orderUpdateId={orderUpdateId}");

                return false;
            }

            agv.CurrentOrderId = orderId;
            agv.CurrentOrderUpdateId = orderUpdateId;
            agv.VdaPaused = false;

            applied = true;
            needNotify = true;
        }

        if (needNotify)
        {
            NotifyAgvStateUpdated();
        }

        return applied;
    }

    /// <summary>
    /// 应用完整订单路线。
    /// 不再直接跳到最后一个节点，先停在第一个节点，再按周期逐步移动到后续节点。
    /// 同时支持 nodes 只有 1 个、edges 为空、动作挂在 node.actions 上的单节点动作订单。
    /// </summary>
    public bool ApplyOrderRoute(
        AgvModel agv,
        string orderId,
        int orderUpdateId,
        List<VdaOrderNodeModel> nodes,
        List<VdaOrderEdgeModel>? edges)
    {
        var needNotify = false;
        bool applied;

        lock (_lockObj)
        {
            if (nodes.Count == 0)
            {
                agv.RouteRuntime = null;
                agv.RunStatus = 0;

                needNotify = true;
                applied = false;
            }
            else
            {
                var routeNodes = nodes
                    .OrderBy(x => x.sequenceId)
                    .ToList();

                var routeEdges = (edges ?? [])
                    .OrderBy(x => x.sequenceId)
                    .ToList();

                if (IsSingleNodeActionOrder(routeNodes, routeEdges))
                {
                    applied = ApplySingleNodeActionOrder(
                        agv,
                        orderId,
                        orderUpdateId,
                        routeNodes[0]);

                    needNotify = applied;
                }
                else
                {
                    var existingRuntime = agv.RouteRuntime;

                    if (existingRuntime != null &&
                        string.Equals(existingRuntime.OrderId, orderId, StringComparison.OrdinalIgnoreCase))
                    {
                        applied = ApplySameOrderUpdate(
                            agv,
                            existingRuntime,
                            orderId,
                            orderUpdateId,
                            routeNodes,
                            routeEdges);

                        needNotify = applied;
                    }
                    else if (string.Equals(agv.CurrentOrderId, orderId, StringComparison.OrdinalIgnoreCase) &&
                             orderUpdateId < agv.CurrentOrderUpdateId)
                    {
                        Console.WriteLine($@"收到旧的Order Route，忽略。AGV={agv.DeviceID}");

                        applied = false;
                    }
                    else if (!IsAgvAtStartNode(agv, routeNodes[0]))
                    {
                        applied = false;
                    }
                    else
                    {
                        agv.RouteRuntime = null;

                        agv.CurrentOrderId = orderId;
                        agv.CurrentOrderUpdateId = orderUpdateId;
                        agv.VdaPaused = false;

                        ApplyReachedNode(agv, routeNodes[0], false);

                        if (routeNodes.Count <= 1)
                        {
                            agv.RouteRuntime = null;
                            agv.RunStatus = 2;

                            applied = true;
                            needNotify = true;
                        }
                        else
                        {
                            var runtime = new AgvRouteRuntimeModel
                            {
                                OrderId = orderId,
                                OrderUpdateId = orderUpdateId,
                                Nodes = routeNodes,
                                Edges = routeEdges,
                                TargetNodeIndex = 1,
                                IsCompleted = false
                            };

                            agv.RouteRuntime = runtime;

                            agv.RunStatus = 1;

                            applied = true;
                            needNotify = true;
                        }
                    }
                }
            }
        }

        if (needNotify)
        {
            NotifyAgvStateUpdated();
        }

        return applied;
    }

    /// <summary>
    /// 判断是否是单节点动作订单。
    /// 例如：
    /// nodes 只有 1 个，edges 为空，动作挂在 nodes[0].actions 上。
    /// RCS 发送的 Action_O48 / Action_O63 / Action_O42 就属于这种。
    /// </summary>
    private static bool IsSingleNodeActionOrder(
        List<VdaOrderNodeModel> nodes,
        List<VdaOrderEdgeModel> edges)
    {
        if (nodes.Count != 1)
        {
            return false;
        }

        if (edges.Count > 0)
        {
            return false;
        }

        return nodes[0].actions is { Count: > 0 };
    }

    /// <summary>
    /// 应用单节点动作订单。
    /// 这种订单不需要路线移动，只需要解析并执行当前节点动作，然后上报 FINISHED。
    /// </summary>
    private bool ApplySingleNodeActionOrder(
        AgvModel agv,
        string orderId,
        int orderUpdateId,
        VdaOrderNodeModel node)
    {
        if (string.Equals(agv.CurrentOrderId, orderId, StringComparison.OrdinalIgnoreCase) &&
            orderUpdateId < agv.CurrentOrderUpdateId)
        {
            Console.WriteLine(
                $@"收到旧的单节点动作Order，忽略。AGV={agv.DeviceID}，当前orderId={agv.CurrentOrderId}，当前orderUpdateId={agv.CurrentOrderUpdateId}，收到orderId={orderId}，收到orderUpdateId={orderUpdateId}");

            return false;
        }

        agv.RouteRuntime = null;

        agv.CurrentOrderId = orderId;
        agv.CurrentOrderUpdateId = orderUpdateId;
        agv.VdaPaused = false;

        ApplyReachedNode(agv, node, false);

        agv.RunStatus = 2;

        Console.WriteLine(
            $@"执行单节点动作Order，AGV={agv.DeviceID}，orderId={orderId}，orderUpdateId={orderUpdateId}，nodeId={node.nodeId}，动作数量={node.actions?.Count ?? 0}");

        return true;
    }

    /// <summary>
    /// 应用同一个orderId的路线更新。
    /// VDA5050中，同一个orderId的后续orderUpdateId可能从未来节点开始扩展路线，
    /// 例如第一段 99 -> 127，第二段从 127 -> 142。
    /// 这种情况下不能要求AGV当前已经在新Nodes[0]处，而是应该把新路线合并到旧runtime中。
    /// 注意：该方法由外层持锁调用，内部不再触发通知。
    /// </summary>
    private bool ApplySameOrderUpdate(
        AgvModel? agv,
        AgvRouteRuntimeModel? runtime,
        string orderId,
        int orderUpdateId,
        List<VdaOrderNodeModel>? updateNodes,
        List<VdaOrderEdgeModel>? updateEdges)
    {
        if (agv == null)
        {
            return false;
        }

        if (runtime == null)
        {
            return false;
        }

        if (updateNodes == null || updateNodes.Count == 0)
        {
            return false;
        }

        updateEdges ??= [];

        if (orderUpdateId < runtime.OrderUpdateId)
        {
            Console.WriteLine($@"收到旧的Order更新，忽略。AGV={agv.DeviceID}");

            return false;
        }

        if (orderUpdateId == runtime.OrderUpdateId)
        {
            Console.WriteLine($@"收到重复的Order更新，忽略。AGV={agv.DeviceID}");

            return true;
        }

        var mergeNode = updateNodes[0];

        var mergeNodeIndex = runtime.Nodes.FindIndex(x =>
            x.sequenceId == mergeNode.sequenceId &&
            string.Equals(x.nodeId, mergeNode.nodeId, StringComparison.OrdinalIgnoreCase));

        if (mergeNodeIndex < 0)
        {
            Console.WriteLine($@"同订单更新的衔接节点不在当前路线中，拒绝合并。AGV={agv.DeviceID}");

            return false;
        }

        if (mergeNodeIndex < runtime.TargetNodeIndex - 1)
        {
            Console.WriteLine(
                $@"同订单更新的衔接节点已经落后于当前执行进度，拒绝合并。AGV={agv.DeviceID}");

            return false;
        }

        var oldTargetNodeIndex = runtime.TargetNodeIndex;

        VdaOrderNodeModel? oldTargetNode = null;

        if (oldTargetNodeIndex >= 0 &&
            oldTargetNodeIndex < runtime.Nodes.Count &&
            !runtime.IsCompleted)
        {
            oldTargetNode = runtime.Nodes[oldTargetNodeIndex];
        }

        var updateStartsAfterCurrentTarget =
            oldTargetNode != null &&
            mergeNodeIndex > oldTargetNodeIndex;

        var updateStartsAtCurrentTarget =
            oldTargetNode != null &&
            mergeNodeIndex == oldTargetNodeIndex;

        runtime.OrderId = orderId;
        runtime.OrderUpdateId = orderUpdateId;

        var oldMergeNode = runtime.Nodes[mergeNodeIndex];

        var mergedMergeNode = MergeConnectNode(oldMergeNode, mergeNode);

        var mergedUpdateNodes = new List<VdaOrderNodeModel>
        {
            mergedMergeNode
        };

        mergedUpdateNodes.AddRange(updateNodes.Skip(1));

        runtime.Nodes = runtime.Nodes
            .Take(mergeNodeIndex)
            .Concat(mergedUpdateNodes)
            .GroupBy(x => x.sequenceId)
            .Select(x => x.Last())
            .OrderBy(x => x.sequenceId)
            .ToList();

        runtime.Edges = runtime.Edges
            .Where(x => x.sequenceId < mergeNode.sequenceId)
            .Concat(updateEdges)
            .GroupBy(x => x.sequenceId)
            .Select(x => x.Last())
            .OrderBy(x => x.sequenceId)
            .ToList();

        int newTargetNodeIndex;

        if (updateStartsAfterCurrentTarget && oldTargetNode != null)
        {
            newTargetNodeIndex = runtime.Nodes.FindIndex(x =>
                x.sequenceId == oldTargetNode.sequenceId &&
                string.Equals(x.nodeId, oldTargetNode.nodeId, StringComparison.OrdinalIgnoreCase));
        }
        else if (updateStartsAtCurrentTarget)
        {
            newTargetNodeIndex = runtime.Nodes.FindIndex(x =>
                x.sequenceId == mergeNode.sequenceId &&
                string.Equals(x.nodeId, mergeNode.nodeId, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            newTargetNodeIndex = runtime.Nodes.FindIndex(x =>
                x.sequenceId > mergeNode.sequenceId);
        }

        if (newTargetNodeIndex < 0)
        {
            runtime.TargetNodeIndex = runtime.Nodes.Count > 0
                ? runtime.Nodes.Count - 1
                : 0;

            runtime.IsCompleted = true;
            agv.RunStatus = 2;
        }
        else
        {
            runtime.TargetNodeIndex = newTargetNodeIndex;
            runtime.IsCompleted = false;

            if (agv.RunStatus is 0 or 2)
            {
                agv.RunStatus = 1;
            }
        }

        var isRotatingProtectedNode =
            runtime is { IsRotating: true, RotatingNode: not null } &&
            (
                oldTargetNode != null &&
                runtime.RotatingNode.sequenceId == oldTargetNode.sequenceId &&
                string.Equals(runtime.RotatingNode.nodeId, oldTargetNode.nodeId, StringComparison.OrdinalIgnoreCase)
                ||
                runtime.RotatingNode.sequenceId == mergeNode.sequenceId &&
                string.Equals(runtime.RotatingNode.nodeId, mergeNode.nodeId, StringComparison.OrdinalIgnoreCase)
            );

        if (!updateStartsAfterCurrentTarget && !isRotatingProtectedNode)
        {
            runtime.IsRotating = false;
            runtime.RotatingNode = null;
            runtime.RotateTargetAngleDegree = 0;
            runtime.RotateMode = RotateMode.None;
            runtime.RotateDirection = AgvTurnHelper.TurnDirection.NoTurn;
        }

        runtime.FinishedEdgeActionSequenceIds.RemoveWhere(x => x >= mergeNode.sequenceId);

        agv.RouteRuntime = runtime;
        agv.CurrentOrderId = orderId;
        agv.CurrentOrderUpdateId = orderUpdateId;
        agv.VdaPaused = false;

        Console.WriteLine(
            $@"同订单路线更新已合并。AGV={agv.DeviceID}，orderId={orderId}，orderUpdateId={orderUpdateId}，衔接节点={mergeNode.nodeId}，sequenceId={mergeNode.sequenceId}，合并后节点数量={runtime.Nodes.Count}，合并后边数量={runtime.Edges.Count}，当前TargetNodeIndex={runtime.TargetNodeIndex}");

        return true;
    }

    /// <summary>
    /// 合并同订单更新的衔接节点。
    /// 衔接节点通常已经存在于旧路线中，不建议用 updateNodes[0] 完全覆盖。
    /// 这里保留旧节点的 nodePosition/theta，避免后续更新把已规划好的角度覆盖掉。
    /// 但如果 updateNodes[0] 带 actions，则保留新的 actions，避免动作丢失。
    /// </summary>
    private static VdaOrderNodeModel MergeConnectNode(
        VdaOrderNodeModel oldNode,
        VdaOrderNodeModel updateNode)
    {
        if (updateNode.actions is { Count: > 0 })
        {
            oldNode.actions = updateNode.actions;
        }

        if (!string.IsNullOrWhiteSpace(updateNode.nodeDescription))
        {
            oldNode.nodeDescription = updateNode.nodeDescription;
        }

        oldNode.released = oldNode.released || updateNode.released;

        return oldNode;
    }

    /// <summary>
    /// 按周期推进AGV路线。
    /// </summary>
    public void StepAgvRoute(AgvModel? agv, double deltaSeconds)
    {
        if (agv == null)
        {
            return;
        }

        if (deltaSeconds <= 0)
        {
            return;
        }

        if (deltaSeconds > MaxDeltaSeconds)
        {
            deltaSeconds = MaxDeltaSeconds;
        }

        var remainSeconds = deltaSeconds;

        var needNotify = false;

        while (remainSeconds > 0)
        {
            var stepSeconds = Math.Min(remainSeconds, MaxSimulationStepSeconds);

            needNotify |= StepAgvRouteCore(agv, stepSeconds);

            needNotify |= StepAgvLiftActions(agv, stepSeconds);

            remainSeconds -= stepSeconds;

            if (agv.RouteRuntime == null && !HasActiveLiftAction(agv))
            {
                break;
            }
        }

        if (needNotify)
        {
            NotifyAgvStateUpdated();
        }
    }

    /// <summary>
    /// 单个小步推进AGV路线。
    /// </summary>
    private bool StepAgvRouteCore(AgvModel? agv, double deltaSeconds)
    {
        if (agv == null)
        {
            return false;
        }

        if (deltaSeconds <= 0)
        {
            return false;
        }

        lock (_lockObj)
        {
            var runtime = agv.RouteRuntime;

            if (runtime == null)
            {
                return false;
            }

            if (runtime.OrderId != agv.CurrentOrderId ||
                runtime.OrderUpdateId != agv.CurrentOrderUpdateId)
            {
                agv.RouteRuntime = null;

                return true;
            }

            if (runtime.IsCompleted)
            {
                agv.RouteRuntime = null;
                agv.RunStatus = 2;

                return true;
            }

            if (agv.VdaPaused)
            {
                agv.RunStatus = 0;

                return true;
            }

            if (runtime.IsRotating)
            {
                StepAgvRotate(agv, runtime, deltaSeconds);

                return true;
            }

            if (runtime.TargetNodeIndex < 0 ||
                runtime.TargetNodeIndex >= runtime.Nodes.Count)
            {
                runtime.IsCompleted = true;
                agv.RouteRuntime = null;
                agv.RunStatus = 2;

                return true;
            }

            var targetNode = runtime.Nodes[runtime.TargetNodeIndex];

            if (TryApplyCurrentEdgeActions(agv, runtime, targetNode))
            {
                return true;
            }

            var targetX = GetNodeX(targetNode);

            var targetY = GetNodeY(targetNode);

            var dx = targetX - agv.X;

            var dy = targetY - agv.Y;

            var distance = Math.Sqrt(dx * dx + dy * dy);

            var speed = GetMoveSpeed(agv, runtime, targetNode);

            var moveDistance = speed * deltaSeconds;

            if (distance <= ReachTolerance || distance <= moveDistance)
            {
                agv.X = targetX;

                agv.Y = targetY;

                if (TryStartRotateAfterArrive(agv, runtime, targetNode))
                {
                    return true;
                }

                CompleteReachedTargetNode(agv, runtime, targetNode);

                return true;
            }

            var ratio = moveDistance / distance;

            agv.X += dx * ratio;

            agv.Y += dy * ratio;

            agv.RunStatus = 1;

            return true;
        }
    }

    /// <summary>
    /// 执行当前目标节点前一条边上的动作。
    /// 例如 240 -> 240、110 -> 110 的 rotateLoad 零长度边动作。
    /// rotateLoad 本身不修改 AGV.Angle；托盘相对角度由AgvModel.TurntableTheta自动反算。
    /// 目标节点的 theta 在动作边执行后按车头角度处理。
    /// </summary>
    private bool TryApplyCurrentEdgeActions(
        AgvModel agv,
        AgvRouteRuntimeModel runtime,
        VdaOrderNodeModel targetNode)
    {
        var edge = GetCurrentEdge(runtime, targetNode);

        if (edge == null)
        {
            return false;
        }

        if (edge.actions == null || edge.actions.Count == 0)
        {
            return false;
        }

        if (!runtime.FinishedEdgeActionSequenceIds.Add(edge.sequenceId))
        {
            return false;
        }

        ApplyEdgeActions(agv, edge);

        agv.RunStatus = 2;

        if (IsZeroLengthEdge(edge, targetNode))
        {
            if (HasRotateLoadAction(edge))
            {
                // 0长度 rotateLoad 边表示托盘/转盘姿态动作。
                // 当前项目逻辑：651车头90°托盘0°直行到245，左转90°后车头180°托盘-90°，再直行到208。
                // 因此这里不能再把重复目标节点的 nodePosition.theta 当成车头角度执行，
                // 否则会把 245(seq4) 的 -90° 错误当成车头目标，导致多转一次。
                CompleteReachedTargetNode(agv, runtime, targetNode);

                return true;
            }

            if (TryStartRotateAfterArrive(agv, runtime, targetNode))
            {
                return true;
            }

            CompleteReachedTargetNode(agv, runtime, targetNode);
        }

        return true;
    }

    /// <summary>
    /// 应用边动作。
    /// 当前仿真器将边动作按立即完成处理，避免RCS一直等待动作完成。
    /// rotateLoad 在这里只标记动作完成，不修改 AGV.Angle。
    /// </summary>
    private void ApplyEdgeActions(
        AgvModel agv,
        VdaOrderEdgeModel edge)
    {
        var actions = ToAgvActions(edge.actions);

        agv.actions = actions;

        ApplyActions(agv, actions);

        Console.WriteLine(
            $@"执行边动作，AGV={agv.DeviceID}，edgeId={edge.edgeId}，sequenceId={edge.sequenceId}，动作数量={actions.Count}");
    }

    /// <summary>
    /// 判断是否为零长度边。
    /// 例如 240 -> 240、110 -> 110 的 rotateLoad 动作边。
    /// </summary>
    private static bool IsZeroLengthEdge(
        VdaOrderEdgeModel edge,
        VdaOrderNodeModel targetNode)
    {
        if (edge.length <= ReachTolerance)
        {
            return true;
        }

        if (string.Equals(edge.startNodeId, edge.endNodeId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(edge.startNodeId, targetNode.nodeId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(edge.endNodeId, targetNode.nodeId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 判断边上是否包含 rotateLoad 动作。
    /// </summary>
    private static bool HasRotateLoadAction(VdaOrderEdgeModel edge)
    {
        if (edge.actions == null || edge.actions.Count == 0)
        {
            return false;
        }

        return edge.actions.Any(x =>
            string.Equals(x.actionType, "rotateLoad", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 获取当前目标节点对应的前一条边。
    /// 优先按 sequenceId 匹配，避免两个相同 nodeId 时匹配错边。
    /// </summary>
    private static VdaOrderEdgeModel? GetCurrentEdge(
        AgvRouteRuntimeModel runtime,
        VdaOrderNodeModel targetNode)
    {
        var sequenceEdge = runtime.Edges.FirstOrDefault(x =>
            x.sequenceId == targetNode.sequenceId - 1);

        if (sequenceEdge != null)
        {
            return sequenceEdge;
        }

        return runtime.Edges.FirstOrDefault(x =>
            x.endNodeId == targetNode.nodeId);
    }

    /// <summary>
    /// 应用VDA5050即时动作。
    /// </summary>
    public void ApplyInstantActions(AgvModel? agv, List<AgvActionModel>? actions)
    {
        if (agv == null)
        {
            return;
        }

        if (actions == null || actions.Count == 0)
        {
            return;
        }

        var needNotify = false;

        lock (_lockObj)
        {
            agv.actions ??= [];

            foreach (var action in actions)
            {
                if (action == null)
                {
                    continue;
                }

                agv.actions.Add(action);

                if (IsLiftAction(action))
                {
                    StartLiftAction(agv, action);

                    needNotify = true;

                    continue;
                }

                ApplySimpleAction(agv, action);

                UpsertActionState(
                    agv,
                    action,
                    "FINISHED",
                    BuildActionResultDescription(action));

                needNotify = true;
            }
        }

        if (needNotify)
        {
            NotifyAgvStateUpdated();
        }
    }

    /// <summary>
    /// 应用节点动作。
    /// 当前仿真器将节点动作按立即完成处理，避免RCS一直等待动作完成。
    /// </summary>
    private void ApplyNodeActions(
        AgvModel agv,
        VdaOrderNodeModel node)
    {
        var actions = ToAgvActions(node.actions);

        agv.actions = actions;

        ApplyActions(agv, actions);

        if (actions.Count > 0)
        {
            Console.WriteLine(
                $@"执行节点动作，AGV={agv.DeviceID}，nodeId={node.nodeId}，sequenceId={node.sequenceId}，动作数量={actions.Count}");
        }
    }

    /// <summary>
    /// 应用简单动作。
    /// pause/resume/cancelOrder 会影响仿真状态。
    /// tine_up/tine_down/fork_up/fork_down/lift/lower 当前按立即完成处理，并保留 height 参数到 actionStates。
    /// </summary>
    private void ApplySimpleAction(AgvModel agv, AgvActionModel action)
    {
        var actionType = action.actionType ?? string.Empty;

        if (actionType.Equals("pause", StringComparison.OrdinalIgnoreCase) ||
            actionType.Equals("startPause", StringComparison.OrdinalIgnoreCase))
        {
            agv.VdaPaused = true;
            agv.RunStatus = 0;
        }
        else if (actionType.Equals("resume", StringComparison.OrdinalIgnoreCase) ||
                 actionType.Equals("stopPause", StringComparison.OrdinalIgnoreCase))
        {
            agv.VdaPaused = false;
        }
        else if (actionType.Equals("cancelOrder", StringComparison.OrdinalIgnoreCase))
        {
            ClearAgvLiftActions(agv);

            agv.RouteRuntime = null;

            agv.CurrentOrderId = string.Empty;
            agv.CurrentOrderUpdateId = 0;
            agv.actions = [];
            agv.actionStates.Clear();
            agv.RunStatus = 0;
        }
        else if (IsRotateLoadAction(action))
        {
            var theta = GetActionParameterDoubleValue(action, "theta");

            // rotateLoad 的 theta 在当前项目中按“托盘相对车体角度”理解。
            // 例如车头180°时 rotateLoad theta=-90°，表示托盘在地图中的绝对姿态仍保持90°。
            // 因此：PalletWorldAngle = BodyAngle + TurntableRelativeAngle。
            // 注意：这里只更新托盘地图绝对姿态，不修改AGV车头角度。
            if (theta.HasValue)
            {
                var relativeTurntableAngleDegree = VdaAngleHelper.VdaThetaToDegree(theta.Value);

                agv.SetPalletWorldAngleDegree(agv.Angle + relativeTurntableAngleDegree);
            }

            Console.WriteLine(
                $@"执行rotateLoad动作，AGV={agv.DeviceID}，actionId={action.actionId}，theta={theta}，车头角度={agv.Angle}，托盘地图绝对角度={agv.PalletWorldAngle}，转盘相对角度={agv.TurntableAngle}");
        }
        else if (IsLiftAction(action))
        {
            var direction = GetLiftActionDirection(action);

            var targetHeight = GetLiftTargetHeight(action, direction);

            ApplyLiftActionResult(agv, direction, targetHeight);

            var height = GetActionParameterValue(action, "height");

            Console.WriteLine(
                $@"执行货叉动作，AGV={agv.DeviceID}，actionType={action.actionType}，actionId={action.actionId}，height={height}，direction={direction}，LiftHeight={agv.LiftHeight}，LoadStatus={agv.LoadStatus}");
        }
    }

    /// <summary>
    /// 到达目标点后，如果目标点有指定角度，则进入到点后旋转过程。
    /// 如果当前节点后面紧跟 0 长度 rotateLoad 边，则根据下一个重复节点角度决定是否跳过当前节点角度。
    /// </summary>
    private bool TryStartRotateAfterArrive(
        AgvModel agv,
        AgvRouteRuntimeModel runtime,
        VdaOrderNodeModel targetNode)
    {
        if (!ShouldApplyNodeThetaAfterArrive(runtime, targetNode))
        {
            Console.WriteLine(
                $@"跳过当前节点到点角度，等待后续零长度rotateLoad动作边处理。AGV={agv.DeviceID}，nodeId={targetNode.nodeId}，sequenceId={targetNode.sequenceId}");

            return false;
        }

        var angleDegree = GetNodeAngleDegree(targetNode);

        if (!angleDegree.HasValue)
        {
            return false;
        }

        var targetAngle = AgvTurnHelper.NormalizeAngleDegree(angleDegree.Value);

        var turnDecision = CalculateFreeTurn(agv.Angle, targetAngle);

        if (turnDecision.Direction == AgvTurnHelper.TurnDirection.NoTurn ||
            turnDecision.RotationAngle <= RotateToleranceDegree)
        {
            agv.SetBodyAngleDegree(targetAngle);
            return false;
        }

        runtime.IsRotating = true;
        runtime.RotatingNode = targetNode;
        runtime.RotateTargetAngleDegree = targetAngle;
        runtime.RotateMode = RotateMode.AfterArrive;
        runtime.RotateDirection = turnDecision.Direction;

        agv.RunStatus = 2;

        Console.WriteLine(
            $@"到达节点后开始旋转，AGV={agv.DeviceID}，nodeId={targetNode.nodeId}，sequenceId={targetNode.sequenceId}，当前角度={agv.Angle}，目标角度={targetAngle}，旋转方向={turnDecision.Direction}，旋转角度={turnDecision.RotationAngle}");

        return true;
    }

    /// <summary>
    /// 判断到达当前节点后是否应该立即执行 node.theta。
    /// 当前项目确认：
    /// 651车头90°托盘0°直行到245，左转90°到车头180°托盘-90°，然后直行到208。
    /// 因此 245(seq2) 的 node.theta=180° 必须执行；
    /// 后续 0长度 rotateLoad 边到达的重复节点 245(seq4) 不再执行 node.theta，
    /// 该跳过逻辑已经放在 TryApplyCurrentEdgeActions 的 HasRotateLoadAction(edge) 分支中处理。
    /// </summary>
    private static bool ShouldApplyNodeThetaAfterArrive(
        AgvRouteRuntimeModel runtime,
        VdaOrderNodeModel targetNode)
    {
        return true;
    }

    /// <summary>
    /// 判断两个角度是否在容差范围内相同。
    /// </summary>
    private static bool IsSameAngleByTolerance(double angle1, double angle2)
    {
        var diff = Math.Abs(NormalizeAngleDegree(angle1) - NormalizeAngleDegree(angle2));

        if (diff > 180.0)
        {
            diff = 360.0 - diff;
        }

        return diff <= RotateToleranceDegree;
    }

    /// <summary>
    /// 自由计算旋转方向。
    /// 不使用 AgvTurnHelper.CalculateTurn 的业务限制。
    /// 用于 node.theta 到点后强制调整车体角度。
    /// LeftTurn = 角度增加。
    /// RightTurn = 角度减少。
    /// </summary>
    private static (AgvTurnHelper.TurnDirection Direction, double RotationAngle) CalculateFreeTurn(
        double currentAngleDegree,
        double targetAngleDegree)
    {
        var currentAngle = AgvTurnHelper.NormalizeAngleDegree(currentAngleDegree);

        var targetAngle = AgvTurnHelper.NormalizeAngleDegree(targetAngleDegree);

        var leftAngle = targetAngle - currentAngle;

        if (leftAngle < 0)
        {
            leftAngle += 360.0;
        }

        var rightAngle = currentAngle - targetAngle;

        if (rightAngle < 0)
        {
            rightAngle += 360.0;
        }

        if (leftAngle <= RotateToleranceDegree || rightAngle <= RotateToleranceDegree)
        {
            return (AgvTurnHelper.TurnDirection.NoTurn, 0);
        }

        if (leftAngle <= rightAngle)
        {
            return (AgvTurnHelper.TurnDirection.LeftTurn, leftAngle);
        }

        return (AgvTurnHelper.TurnDirection.RightTurn, rightAngle);
    }

    /// <summary>
    /// 按周期推进AGV原地旋转。
    /// 注意：这里不直接 NotifyAgvStateUpdated，通知由外层 StepAgvRoute 在 lock 外统一触发。
    /// </summary>
    private void StepAgvRotate(
        AgvModel agv,
        AgvRouteRuntimeModel runtime,
        double deltaSeconds)
    {
        var rotateFinished = RotateAgvToAngle(
            agv,
            runtime.RotateTargetAngleDegree,
            runtime.RotateDirection,
            deltaSeconds);

        agv.RunStatus = 2;

        if (!rotateFinished)
        {
            return;
        }

        if (runtime.RotateMode == RotateMode.BeforeMove)
        {
            runtime.IsRotating = false;
            runtime.RotatingNode = null;
            runtime.RotateMode = RotateMode.None;
            runtime.RotateDirection = AgvTurnHelper.TurnDirection.NoTurn;

            return;
        }

        if (runtime.RotateMode == RotateMode.AfterArrive)
        {
            var reachedNode = runtime.RotatingNode;

            runtime.IsRotating = false;
            runtime.RotatingNode = null;
            runtime.RotateMode = RotateMode.None;
            runtime.RotateDirection = AgvTurnHelper.TurnDirection.NoTurn;

            if (reachedNode == null)
            {
                return;
            }

            CompleteReachedTargetNode(agv, runtime, reachedNode);

            return;
        }

        runtime.IsRotating = false;
        runtime.RotatingNode = null;
        runtime.RotateMode = RotateMode.None;
        runtime.RotateDirection = AgvTurnHelper.TurnDirection.NoTurn;
    }

    /// <summary>
    /// 按指定方向将AGV旋转到目标角度。
    /// 返回true表示旋转完成。
    /// </summary>
    private static bool RotateAgvToAngle(
        AgvModel agv,
        double targetAngleDegree,
        AgvTurnHelper.TurnDirection direction,
        double deltaSeconds)
    {
        var currentAngle = AgvTurnHelper.NormalizeAngleDegree(agv.Angle);

        var targetAngle = AgvTurnHelper.NormalizeAngleDegree(targetAngleDegree);

        if (AgvTurnHelper.IsSameAngle(currentAngle, targetAngle))
        {
            agv.SetBodyAngleDegree(targetAngle);
            return true;
        }

        if (direction == AgvTurnHelper.TurnDirection.NoTurn)
        {
            return true;
        }

        var maxRotateAngle = DefaultRotateSpeedDegreePerSecond * deltaSeconds;

        if (maxRotateAngle <= 0)
        {
            return false;
        }

        var remainAngle = direction == AgvTurnHelper.TurnDirection.LeftTurn
            ? AgvTurnHelper.GetLeftTurnDifference(currentAngle, targetAngle)
            : AgvTurnHelper.GetRightTurnDifference(currentAngle, targetAngle);

        if (remainAngle <= RotateToleranceDegree)
        {
            agv.SetBodyAngleDegree(targetAngle);
            return true;
        }

        var actualRotateAngle = Math.Min(remainAngle, maxRotateAngle);

        var nextAngle = direction == AgvTurnHelper.TurnDirection.LeftTurn
            ? AgvTurnHelper.NormalizeAngleDegree(currentAngle + actualRotateAngle)
            : AgvTurnHelper.NormalizeAngleDegree(currentAngle - actualRotateAngle);

        agv.SetBodyAngleDegree(nextAngle);

        currentAngle = AgvTurnHelper.NormalizeAngleDegree(agv.Angle);

        remainAngle = direction == AgvTurnHelper.TurnDirection.LeftTurn
            ? AgvTurnHelper.GetLeftTurnDifference(currentAngle, targetAngle)
            : AgvTurnHelper.GetRightTurnDifference(currentAngle, targetAngle);

        if (!(remainAngle <= RotateToleranceDegree))
        {
            return false;
        }

        agv.SetBodyAngleDegree(targetAngle);
        return true;
    }

    /// <summary>
    /// 实现目标节点到达。
    /// </summary>
    private void CompleteReachedTargetNode(
        AgvModel agv,
        AgvRouteRuntimeModel runtime,
        VdaOrderNodeModel targetNode)
    {
        ApplyReachedNode(agv, targetNode, false);

        runtime.TargetNodeIndex++;

        if (runtime.TargetNodeIndex >= runtime.Nodes.Count)
        {
            runtime.IsCompleted = true;

            agv.RouteRuntime = null;

            agv.RunStatus = 2;
        }
        else
        {
            agv.RunStatus = 2;
        }
    }

    /// <summary>
    /// 应用已经到达的节点。
    /// </summary>
    private void ApplyReachedNode(AgvModel agv, VdaOrderNodeModel node, bool applyAngle = true)
    {
        if (int.TryParse(node.nodeId, out var positionId))
        {
            agv.PositionID = positionId;
        }

        agv.LastNodeSequenceId = node.sequenceId;

        agv.X = GetNodeX(node);

        agv.Y = GetNodeY(node);

        var angleDegree = GetNodeAngleDegree(node);

        if (applyAngle && angleDegree.HasValue)
        {
            agv.SetBodyAngleDegree(angleDegree.Value);
        }

        ApplyNodeActions(agv, node);
    }

    /// <summary>
    /// 刷新动作状态。
    /// 仿真器先按立即完成处理，避免RCS一直等待动作完成。
    /// </summary>
    private void RefreshActionStates(AgvModel agv, List<AgvActionModel> actions)
    {
        agv.actionStates.Clear();

        foreach (var action in actions)
        {
            if (IsLiftAction(action))
            {
                StartLiftAction(agv, action);

                continue;
            }

            UpsertActionState(
                agv,
                action,
                "FINISHED",
                BuildActionResultDescription(action));
        }
    }

    /// <summary>
    /// 构建动作执行结果描述。
    /// </summary>
    private static string? BuildActionResultDescription(AgvActionModel action)
    {
        var actionType = action.actionType ?? string.Empty;

        if (IsLiftAction(action))
        {
            var height = GetActionParameterValue(action, "height");

            var direction = GetLiftActionDirection(action);

            if (!string.IsNullOrWhiteSpace(height))
            {
                return $@"动作执行完成，height={height}，direction={direction}";
            }

            return $@"动作执行完成，direction={direction}";
        }

        if (actionType.Equals("pause", StringComparison.OrdinalIgnoreCase) ||
            actionType.Equals("startPause", StringComparison.OrdinalIgnoreCase))
        {
            return "暂停完成";
        }

        if (actionType.Equals("resume", StringComparison.OrdinalIgnoreCase) ||
            actionType.Equals("stopPause", StringComparison.OrdinalIgnoreCase))
        {
            return "恢复完成";
        }

        if (actionType.Equals("cancelOrder", StringComparison.OrdinalIgnoreCase))
        {
            return "取消订单完成";
        }

        if (actionType.Equals("rotateLoad", StringComparison.OrdinalIgnoreCase))
        {
            return "动作执行完成，托盘绝对姿态保持不变";
        }

        return "动作执行完成";
    }

    /// <summary>
    /// 应用一组动作。
    /// 货叉/托举动作按仿真周期推进，其它简单动作立即完成。
    /// </summary>
    private void ApplyActions(AgvModel agv, List<AgvActionModel> actions)
    {
        agv.actionStates.Clear();

        foreach (var action in actions)
        {
            if (action == null)
            {
                continue;
            }

            if (IsLiftAction(action))
            {
                StartLiftAction(agv, action);

                continue;
            }

            ApplySimpleAction(agv, action);

            UpsertActionState(
                agv,
                action,
                "FINISHED",
                BuildActionResultDescription(action));
        }
    }

    /// <summary>
    /// 启动托举/货叉动作仿真。
    /// </summary>
    private void StartLiftAction(AgvModel agv, AgvActionModel action)
    {
        var direction = GetLiftActionDirection(action);

        var targetHeight = GetLiftTargetHeight(action, direction);

        var runtimeKey = BuildLiftActionRuntimeKey(agv, action);

        _liftActionRuntimes[runtimeKey] = new LiftActionRuntimeModel
        {
            DeviceId = agv.DeviceID,
            ActionId = action.actionId ?? string.Empty,
            OrderId = agv.CurrentOrderId ?? string.Empty,
            OrderUpdateId = agv.CurrentOrderUpdateId,
            Action = action,
            Direction = direction,
            TargetHeight = targetHeight,
            ElapsedSeconds = 0,
            DurationSeconds = DefaultLiftActionDurationSeconds,
            LoadStatusApplied = false
        };

        UpsertActionState(agv, action, "INITIALIZING", string.Empty);

        agv.RunStatus = 2;

        Console.WriteLine(
            $@"开始货叉动作仿真，AGV={agv.DeviceID}，actionType={action.actionType}，actionId={action.actionId}，direction={direction}，targetHeight={targetHeight}");
    }

    /// <summary>
    /// 按周期推进托举/货叉动作。
    /// </summary>
    private bool StepAgvLiftActions(AgvModel agv, double deltaSeconds)
    {
        if (deltaSeconds <= 0)
        {
            return false;
        }

        lock (_lockObj)
        {
            var runtimes = _liftActionRuntimes
                .Where(x => x.Value.DeviceId == agv.DeviceID)
                .Select(x => new
                {
                    Key = x.Key,
                    Runtime = x.Value
                })
                .ToList();

            if (runtimes.Count == 0)
            {
                return false;
            }

            var changed = false;

            foreach (var item in runtimes)
            {
                var runtime = item.Runtime;

                runtime.ElapsedSeconds += deltaSeconds;

                if (!runtime.LoadStatusApplied &&
                    runtime.ElapsedSeconds >= GetLiftActionLoadSwitchSeconds(runtime))
                {
                    ApplyLiftActionResult(
                        agv,
                        runtime.Direction,
                        runtime.TargetHeight);

                    runtime.LoadStatusApplied = true;

                    changed = true;
                }

                if (runtime.ElapsedSeconds >= runtime.DurationSeconds)
                {
                    ApplyLiftActionResult(
                        agv,
                        runtime.Direction,
                        runtime.TargetHeight);

                    UpsertActionState(
                        agv,
                        runtime.Action,
                        "FINISHED",
                        BuildActionResultDescription(runtime.Action));

                    _liftActionRuntimes.Remove(item.Key);

                    agv.RunStatus = 2;

                    Console.WriteLine(
                        $@"货叉动作仿真完成，AGV={agv.DeviceID}，actionType={runtime.Action.actionType}，actionId={runtime.Action.actionId}，direction={runtime.Direction}，LiftHeight={agv.LiftHeight}，LoadStatus={agv.LoadStatus}");

                    changed = true;

                    continue;
                }

                var status = runtime.ElapsedSeconds < LiftActionInitializingSeconds
                    ? "INITIALIZING"
                    : "RUNNING";

                UpsertActionState(
                    agv,
                    runtime.Action,
                    status,
                    string.Empty);

                agv.RunStatus = 2;

                changed = true;
            }

            return changed;
        }
    }

    /// <summary>
    /// 是否还有当前AGV的托举/货叉动作在执行。
    /// </summary>
    private bool HasActiveLiftAction(AgvModel agv)
    {
        lock (_lockObj)
        {
            return _liftActionRuntimes.Values.Any(x => x.DeviceId == agv.DeviceID);
        }
    }

    /// <summary>
    /// 清除当前AGV的托举/货叉动作运行时。
    /// </summary>
    private void ClearAgvLiftActions(AgvModel agv)
    {
        var keys = _liftActionRuntimes
            .Where(x => x.Value.DeviceId == agv.DeviceID)
            .Select(x => x.Key)
            .ToList();

        foreach (var key in keys)
        {
            _liftActionRuntimes.Remove(key);
        }
    }

    /// <summary>
    /// 应用托举/货叉动作的实际结果。
    /// </summary>
    private static void ApplyLiftActionResult(
        AgvModel agv,
        LiftActionDirection direction,
        int targetHeight)
    {
        agv.LiftHeight = Math.Max(0, targetHeight);

        if (direction == LiftActionDirection.Up)
        {
            agv.LoadStatus = LoadedStatus;
            return;
        }

        if (agv.LiftHeight <= 0)
        {
            agv.LoadStatus = EmptyLoadStatus;
        }
    }

    /// <summary>
    /// 更新或插入动作状态。
    /// </summary>
    private static void UpsertActionState(
        AgvModel agv,
        AgvActionModel action,
        string actionStatus,
        string? resultDescription)
    {
        var actionId = action.actionId ?? string.Empty;

        var actionState = agv.actionStates.FirstOrDefault(x =>
            string.Equals(x.actionId, actionId, StringComparison.OrdinalIgnoreCase));

        if (actionState == null)
        {
            actionState = new AgvActionStateModel
            {
                actionId = actionId,
                actionType = action.actionType,
                actionDescription = action.actionDescription
            };

            agv.actionStates.Add(actionState);
        }

        actionState.actionType = action.actionType;
        actionState.actionDescription = action.actionDescription;
        actionState.actionStatus = actionStatus;
        actionState.resultDescription = resultDescription ?? string.Empty;
    }

    /// <summary>
    /// 判断是否为托盘/载具旋转动作。
    /// 当前项目中rotateLoad不直接改变车头角度，托盘相对角度由AgvModel按“托盘绝对姿态 - 车头角度”反算。
    /// </summary>
    private static bool IsRotateLoadAction(AgvActionModel action)
    {
        var actionType = action.actionType ?? string.Empty;

        return actionType.Equals("rotateLoad", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断是否为托举/货叉动作。
    /// </summary>
    private static bool IsLiftAction(AgvActionModel action)
    {
        var actionType = action.actionType ?? string.Empty;

        return actionType.Equals("tine_up", StringComparison.OrdinalIgnoreCase) ||
               actionType.Equals("tine_down", StringComparison.OrdinalIgnoreCase) ||
               actionType.Equals("fork_up", StringComparison.OrdinalIgnoreCase) ||
               actionType.Equals("fork_down", StringComparison.OrdinalIgnoreCase) ||
               actionType.Equals("lift", StringComparison.OrdinalIgnoreCase) ||
               actionType.Equals("lower", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 获取托举/货叉动作方向。
    /// 注意：部分RCS会用 tine_up + height=0 表示下降/放货。
    /// </summary>
    private static LiftActionDirection GetLiftActionDirection(AgvActionModel action)
    {
        var actionType = action.actionType ?? string.Empty;

        if (actionType.Equals("tine_down", StringComparison.OrdinalIgnoreCase) ||
            actionType.Equals("fork_down", StringComparison.OrdinalIgnoreCase) ||
            actionType.Equals("lower", StringComparison.OrdinalIgnoreCase))
        {
            return LiftActionDirection.Down;
        }

        var height = GetActionParameterDoubleValue(action, "height");

        if (height.HasValue && height.Value <= 0)
        {
            return LiftActionDirection.Down;
        }

        return LiftActionDirection.Up;
    }

    /// <summary>
    /// 获取托举/货叉动作目标高度。
    /// </summary>
    private static int GetLiftTargetHeight(
        AgvActionModel action,
        LiftActionDirection direction)
    {
        var height = GetActionParameterDoubleValue(action, "height");

        if (height.HasValue)
        {
            return Math.Max(0, (int)Math.Round(height.Value));
        }

        return direction == LiftActionDirection.Down
            ? 0
            : 1;
    }

    /// <summary>
    /// 获取托举/货叉动作载货状态切换时间。
    /// 下降动作尽早切为空载，上升动作在动作后段切为载货，更接近实车上报节奏。
    /// </summary>
    private static double GetLiftActionLoadSwitchSeconds(LiftActionRuntimeModel runtime)
    {
        if (runtime.Direction == LiftActionDirection.Down)
        {
            return Math.Min(LiftActionDownLoadSwitchSeconds, runtime.DurationSeconds);
        }

        return Math.Max(
            LiftActionInitializingSeconds,
            runtime.DurationSeconds * LiftActionUpLoadSwitchRatio);
    }

    /// <summary>
    /// 构建托举/货叉动作运行时Key。
    /// </summary>
    private static string BuildLiftActionRuntimeKey(AgvModel agv, AgvActionModel action)
    {
        var actionId = !string.IsNullOrWhiteSpace(action.actionId)
            ? action.actionId
            : $@"{agv.CurrentOrderId}_{action.actionType}";

        return $@"{agv.DeviceID}_{actionId}";
    }

    /// <summary>
    /// 获取动作数值参数。
    /// </summary>
    private static double? GetActionParameterDoubleValue(
        AgvActionModel action,
        string key)
    {
        var value = GetActionParameterValue(action, key);

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var result))
        {
            return result;
        }

        if (double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out result))
        {
            return result;
        }

        return null;
    }

    /// <summary>
    /// 获取动作参数。
    /// </summary>
    private static string? GetActionParameterValue(
        AgvActionModel action,
        string key)
    {
        if (action.actionParameters.Count == 0)
        {
            return null;
        }

        var parameter = action.actionParameters
            .FirstOrDefault(x => string.Equals(x.key, key, StringComparison.OrdinalIgnoreCase));

        return parameter?.value?.ToString();
    }

    /// <summary>
    /// 获取AGV移动速度。
    /// 优先使用AgvModel.Speed，如果超过当前边的maxSpeed，则按edge.maxSpeed限速。
    /// </summary>
    private double GetMoveSpeed(
        AgvModel agv,
        AgvRouteRuntimeModel runtime,
        VdaOrderNodeModel targetNode)
    {
        var speed = agv.Speed;

        if (speed <= 0)
        {
            speed = DefaultMoveSpeed;
        }

        if (speed < MinMoveSpeed)
        {
            speed = MinMoveSpeed;
        }

        var edge = GetCurrentEdge(runtime, targetNode);

        var edgeMaxSpeed = edge?.maxSpeed ?? 0;

        if (edgeMaxSpeed > 0 && speed > edgeMaxSpeed)
        {
            speed = edgeMaxSpeed;
        }

        return speed;
    }

    /// <summary>
    /// 获取VDA订单节点X坐标。
    /// </summary>
    private static double GetNodeX(VdaOrderNodeModel node)
    {
        return node.nodePosition?.x ?? 0;
    }

    /// <summary>
    /// 获取VDA订单节点Y坐标。
    /// </summary>
    private static double GetNodeY(VdaOrderNodeModel node)
    {
        return node.nodePosition?.y ?? 0;
    }

    /// <summary>
    /// 获取VDA订单节点角度，返回角度制。
    /// VDA5050中的theta是弧度，这里转换成AGV内部使用的角度。
    /// </summary>
    private static double? GetNodeAngleDegree(VdaOrderNodeModel node)
    {
        return node.nodePosition?.theta * 180.0 / Math.PI;
    }

    /// <summary>
    /// 将VDA订单动作转换成AGV内部动作。
    /// </summary>
    private static List<AgvActionModel> ToAgvActions(List<VdaOrderActionModel>? actions)
    {
        if (actions == null || actions.Count == 0)
        {
            return [];
        }

        return actions
            .Select(x => new AgvActionModel
            {
                actionId = x.actionId,
                actionType = x.actionType,
                actionDescription = x.actionDescription,
                blockingType = x.blockingType,
                actionParameters = ToAgvActionParameters(x.actionParameters)
            })
            .ToList();
    }

    /// <summary>
    /// 将VDA动作参数转换成AGV内部动作参数。
    /// </summary>
    private static List<AgvActionParameterModel> ToAgvActionParameters(List<VdaOrderActionParameterModel>? parameters)
    {
        if (parameters == null || parameters.Count == 0)
        {
            return [];
        }

        return parameters
            .Select(x => new AgvActionParameterModel
            {
                key = x.key,
                value = x.value
            })
            .ToList();
    }

    /// <summary>
    /// 角度归一化到 0 到 360。
    /// </summary>
    private static double NormalizeAngleDegree(double angleDegree)
    {
        var result = angleDegree % 360.0;

        if (result < 0)
        {
            result += 360.0;
        }

        return result;
    }

    /// <summary>
    /// 通知AGV状态已经变化。
    /// </summary>
    private void NotifyAgvStateUpdated()
    {
        _agvStoreService.NotifyAgvStateUpdated();
    }

    /// <summary>
    /// 判断 AGV 是否在路线起点。
    /// </summary>
    private static bool IsAgvAtStartNode(AgvModel agv, VdaOrderNodeModel? startNode)
    {
        if (startNode == null)
        {
            return false;
        }

        var currentNodeId = agv.PositionID.ToString();

        return string.Equals(
            currentNodeId,
            startNode.nodeId,
            StringComparison.OrdinalIgnoreCase);
    }
}