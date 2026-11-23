using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Vda5050AgvSimulator.Helpers;
using Vda5050AgvSimulator.Models.Agv;
using Vda5050AgvSimulator.Models.Vda;

namespace Vda5050AgvSimulator.Services.Agv;

public class AgvSimulationService
{
    private readonly AgvStoreService _agvStoreService;

    private const double DefaultMoveSpeed = 1.0;

    /// <summary>
    /// 仿真移动加速倍率。
    /// 只影响仿真显示速度，不代表真实AGV速度。
    /// 例如 RCS 下发 maxSpeed=0.5，倍率=5，则仿真按 2.5 移动。
    /// </summary>
    private const double SimulationMoveSpeedScale = 1.0;

    private const double MinMoveSpeed = 0.05;

    private const double ReachTolerance = 0.03;

    private const double PositionEpsilon = 0.000001;

    private const double DefaultRotateSpeedDegreePerSecond = 30.0;

    /// <summary>
    /// 仿真显示最小移动速度。
    /// 防止 RCS 下发 edge.maxSpeed 很小时，乘倍率后仍然太慢。
    /// </summary>
    private const double SimulationMinMoveSpeed = 0.2;

    /// <summary>
    /// 仿真显示最大移动速度。
    /// 防止倍率过大导致直接瞬移，可按需要调大。
    /// </summary>
    private const double SimulationMaxMoveSpeed = 5.0;

    /// <summary>
    /// 仿真旋转加速倍率。
    /// 只影响仿真旋转显示速度。
    /// 例如原来 30°/s，倍率=4，则仿真按 120°/s 旋转。
    /// </summary>
    private const double SimulationRotateSpeedScale = 1.0;

    private const double RotateToleranceDegree = 1.0;

    /// <summary>
    /// 单次仿真推进允许的最大时间。
    /// 卡顿/调试暂停后最多按该时间补偿，避免一次性跳太远。
    /// </summary>
    internal const double MaxDeltaSeconds = 0.2;

    private const int MaxTransitionsPerStep = 64;

    private const double DefaultLiftActionDurationSeconds = 4.0;

    private const double LiftActionInitializingSeconds = 0.2;

    private const string EmptyLoadStatus = "UNLOAD";

    private const string LoadedStatus = "LOADED";

    private readonly ConditionalWeakTable<AgvModel, Queue<LiftActionRuntimeModel>> _liftActionQueues = new();

    private sealed class LiftActionRuntimeModel
    {
        public AgvActionModel Action { get; set; } = new();

        public string? SingleNodeId { get; set; }

        public double TargetHeight { get; set; }

        public double ElapsedSeconds { get; set; }
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

        lock (agv.SimulationStateLock)
        {
            if (string.Equals(agv.CurrentOrderId, orderId, StringComparison.OrdinalIgnoreCase) &&
                orderUpdateId < agv.CurrentOrderUpdateId)
            {
                return false;
            }

            agv.CurrentOrderId = orderId;
            agv.CurrentOrderUpdateId = orderUpdateId;
            agv.VdaPaused = false;

            // 新订单被接受后，不再保留上次无订单可取消的警告。
            agv.VdaErrors.RemoveAll(error =>
                string.Equals(error.errorType, "noOrderToCancel", StringComparison.OrdinalIgnoreCase));

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

        lock (agv.SimulationStateLock)
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

                        // 新订单开始时只清理一次旧动作状态。
                        // 后续边动作/节点动作不能再 Clear，否则 0 长度边动作状态会被重复节点清掉。
                        _liftActionQueues.Remove(agv);
                        agv.actions = [];
                        agv.actionStates.Clear();

                        // 起点节点代表当前点位的初始姿态，必须立即应用 theta。
                        // 例如 613 的 theta=90°，车辆从 613 出发前应先处于 90°。
                        ApplyReachedNode(agv, routeNodes[0], true, true);

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
                                TargetNodeIndex = 1,
                                IsCompleted = false
                            };
                            runtime.SetSortedEdges(routeEdges);

                            agv.RouteRuntime = runtime;

                            agv.RunStatus = _liftActionQueues.TryGetValue(agv, out _) ? 2 : 1;

                            applied = true;
                            needNotify = true;
                        }
                    }
                }
            }

            if (applied)
            {
                agv.VdaErrors.RemoveAll(error =>
                    string.Equals(error.errorType, "noOrderToCancel", StringComparison.OrdinalIgnoreCase));
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
        if (string.Equals(agv.CurrentOrderId, orderId, StringComparison.OrdinalIgnoreCase))
        {
            if (orderUpdateId < agv.CurrentOrderUpdateId)
            {
                return false;
            }

            // RCS 重发动作订单或更新订单版本时，已受理的 actionId 继续执行或保留完成状态。
            if (agv.RouteRuntime == null && node.actions!.All(action =>
                    !string.IsNullOrWhiteSpace(action.actionId) &&
                    agv.actionStates.Any(state =>
                        string.Equals(state.actionId, action.actionId, StringComparison.OrdinalIgnoreCase))))
            {
                agv.CurrentOrderUpdateId = orderUpdateId;
                return true;
            }
        }

        var actions = ToAgvActions(node.actions);

        agv.RouteRuntime = null;

        agv.CurrentOrderId = orderId;
        agv.CurrentOrderUpdateId = orderUpdateId;
        agv.VdaPaused = false;

        // state 只回报当前订单的动作，升降机构的进度可以继续使用。
        agv.actionStates.Clear();
        agv.actions = actions;

        ApplyReachedNode(agv, node, true, false);

        if (actions.Count == 1 && IsLiftAction(actions[0]))
        {
            ApplySingleNodeLiftAction(agv, node.nodeId, actions[0]);
        }
        else
        {
            _liftActionQueues.Remove(agv);
            ApplyActions(agv, actions);
        }

        agv.RunStatus = 2;
        return true;
    }

    /// <summary>
    /// RCS 连续下发同一地标、同一高度的独立升降订单时，沿用机构进度并回报最新动作ID。
    /// </summary>
    private void ApplySingleNodeLiftAction(AgvModel agv, string nodeId, AgvActionModel action)
    {
        var targetHeight = GetLiftTargetHeight(action);

        if (_liftActionQueues.TryGetValue(agv, out var queue))
        {
            var current = queue.Peek();

            if (queue.Count == 1 &&
                current.SingleNodeId != null &&
                string.Equals(current.SingleNodeId, nodeId, StringComparison.OrdinalIgnoreCase) &&
                current.TargetHeight == targetHeight)
            {
                current.Action = action;
                UpsertActionState(agv, action, GetLiftActionProgressStatus(current.ElapsedSeconds), string.Empty);
                return;
            }

            _liftActionQueues.Remove(agv);
        }
        else
        {
            var targetLoadStatus = targetHeight > 0 ? LoadedStatus : EmptyLoadStatus;

            if (agv.LiftHeight == targetHeight &&
                string.Equals(agv.LoadStatus?.Trim(), targetLoadStatus, StringComparison.OrdinalIgnoreCase))
            {
                UpsertActionState(agv, action, "FINISHED", string.Empty);
                return;
            }
        }

        EnqueueLiftAction(agv, action, targetHeight, nodeId);
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
            return false;
        }

        if (orderUpdateId == runtime.OrderUpdateId)
        {
            return true;
        }

        var mergeNode = updateNodes[0];

        var mergeNodeIndex = runtime.Nodes.FindIndex(x =>
            x.sequenceId == mergeNode.sequenceId &&
            string.Equals(x.nodeId, mergeNode.nodeId, StringComparison.OrdinalIgnoreCase));

        if (mergeNodeIndex < 0)
        {
            return false;
        }

        if (mergeNodeIndex < runtime.TargetNodeIndex - 1)
        {
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

        runtime.Nodes =
        [
            .. runtime.Nodes
                .Take(mergeNodeIndex)
                .Concat(mergedUpdateNodes)
                .GroupBy(x => x.sequenceId)
                .Select(x => x.Last())
                .OrderBy(x => x.sequenceId)
        ];

        runtime.SetSortedEdges([
            .. runtime.RouteEdges
                .Where(x => x.sequenceId < mergeNode.sequenceId)
                .Concat(updateEdges)
                .GroupBy(x => x.sequenceId)
                .Select(x => x.Last())
                .OrderBy(x => x.sequenceId)
        ]);

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

            if ((agv.RunStatus is 0 or 2) && !_liftActionQueues.TryGetValue(agv, out _))
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
    /// 每次按传入的时间步推进，路线和升降共用单车锁。
    /// </summary>
    public void StepAgvRoute(AgvModel? agv, double deltaSeconds)
    {
        if (agv != null && StepAgv(agv, deltaSeconds, false))
        {
            NotifyAgvStateUpdated();
        }
    }

    internal bool HasSimulationWork(AgvModel agv)
    {
        return agv.RouteRuntime != null || _liftActionQueues.TryGetValue(agv, out _);
    }

    internal bool StepOnlineAgv(AgvModel agv, double deltaSeconds)
    {
        return StepAgv(agv, deltaSeconds, true);
    }

    private bool StepAgv(AgvModel agv, double deltaSeconds, bool requireOnline)
    {
        if (deltaSeconds <= 0 || double.IsNaN(deltaSeconds))
        {
            return false;
        }

        deltaSeconds = Math.Min(deltaSeconds, MaxDeltaSeconds);

        lock (agv.SimulationStateLock)
        {
            if (requireOnline && (agv.IsRemovedFromStore || agv.OnlineStatus != 1))
            {
                return false;
            }

            // 升降保持原有动作顺序，完成后下一轮才恢复路线。行驶和转向共用本轮剩余时间。
            if (_liftActionQueues.TryGetValue(agv, out var currentLiftActions))
            {
                return StepAgvLiftActions(agv, currentLiftActions, deltaSeconds);
            }

            var changed = false;
            for (var transition = 0; deltaSeconds > 0 && transition < MaxTransitionsPerStep; transition++)
            {
                if (!StepAgvRouteCore(agv, ref deltaSeconds))
                {
                    return changed;
                }

                changed = true;
                if (deltaSeconds > 0 && _liftActionQueues.TryGetValue(agv, out var nextLiftActions))
                {
                    StepAgvLiftActions(agv, nextLiftActions, deltaSeconds);
                    return true;
                }
            }

            return changed;
        }
    }

    /// <summary>
    /// 单次推进AGV路线。
    /// </summary>
    private bool StepAgvRouteCore(AgvModel agv, ref double deltaSeconds)
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
            agv.RunStatus = 0;

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
            if (agv.RunStatus == 0)
            {
                return false;
            }

            agv.RunStatus = 0;

            return true;
        }

        if (runtime.IsRotating)
        {
            StepAgvRotate(agv, runtime, ref deltaSeconds);

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

        var currentEdge = GetCurrentEdge(runtime, targetNode);

        if (TryApplyCurrentEdgeActions(agv, runtime, targetNode, currentEdge))
        {
            return true;
        }

        var targetX = GetNodeX(targetNode);

        var targetY = GetNodeY(targetNode);

        var dx = targetX - agv.X;

        var dy = targetY - agv.Y;

        var distance = Math.Sqrt(dx * dx + dy * dy);

        var speed = GetMoveSpeed(agv, currentEdge);

        var moveDistance = speed * deltaSeconds;

        if (double.IsNaN(distance) ||
            double.IsInfinity(distance) ||
            double.IsNaN(moveDistance) ||
            double.IsInfinity(moveDistance))
        {
            agv.RouteRuntime = null;
            agv.RunStatus = 0;

            return true;
        }

        if (distance <= PositionEpsilon || distance <= moveDistance)
        {
            deltaSeconds = Math.Max(0, deltaSeconds - distance / speed);

            // 坐标到点后，先更新点位信息，避免“坐标到了但PositionID还停在上一个点”。
            // 注意：这里先不执行节点动作，节点动作要等车体 theta 在该点旋转完成后再执行。
            ApplyReachedNode(agv, targetNode, false, false);

            if (TryStartRotateAfterArrive(agv, runtime, targetNode))
            {
                return true;
            }

            CompleteReachedTargetNode(agv, runtime, targetNode, distance > PositionEpsilon);

            return true;
        }

        var ratio = moveDistance / distance;

        agv.X += dx * ratio;

        agv.Y += dy * ratio;

        agv.RunStatus = 1;

        deltaSeconds = 0;
        return true;
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
        VdaOrderNodeModel targetNode,
        VdaOrderEdgeModel? edge)
    {
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

        if (!ReferenceEquals(agv.RouteRuntime, runtime))
        {
            return true;
        }

        if (IsZeroLengthEdge(agv, edge, targetNode))
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
    }

    /// <summary>
    /// 判断是否为零长度边。
    /// 例如 240 -> 240、110 -> 110 的 rotateLoad 动作边。
    /// </summary>
    private static bool IsZeroLengthEdge(
        AgvModel agv,
        VdaOrderEdgeModel edge,
        VdaOrderNodeModel targetNode)
    {
        var dx = GetNodeX(targetNode) - agv.X;
        var dy = GetNodeY(targetNode) - agv.Y;
        return dx * dx + dy * dy <= ReachTolerance * ReachTolerance &&
               (edge.length <= ReachTolerance ||
                string.Equals(edge.startNodeId, edge.endNodeId, StringComparison.OrdinalIgnoreCase));
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
        var sequenceId = targetNode.sequenceId - 1;
        if (runtime.EdgesAreSorted)
        {
            var index = runtime.GetFirstEdgeIndex(sequenceId);
            if (index < runtime.RouteEdges.Count && runtime.RouteEdges[index].sequenceId == sequenceId)
            {
                return runtime.RouteEdges[index];
            }

            return runtime.RouteEdges.Find(edge => edge.endNodeId == targetNode.nodeId);
        }

        VdaOrderEdgeModel? firstMatchingEndNode = null;
        foreach (var edge in runtime.RouteEdges)
        {
            if (edge.sequenceId == sequenceId)
            {
                return edge;
            }

            if (firstMatchingEndNode == null && edge.endNodeId == targetNode.nodeId)
            {
                firstMatchingEndNode = edge;
            }
        }

        return firstMatchingEndNode;
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

        lock (agv.SimulationStateLock)
        {
            agv.actions ??= [];

            foreach (var action in actions)
            {
                if (action == null)
                {
                    continue;
                }

                agv.actions.Add(action);

                if (string.Equals(action.actionType, "cancelOrder", StringComparison.OrdinalIgnoreCase))
                {
                    // 已完成的订单仍保留 orderId，但不表示还有正在执行的订单。
                    // 只有存在活动路线或尚未结束的订单动作，才允许取消。
                    var hasActiveRoute = agv.RouteRuntime is { IsCompleted: false };

                    var hasRunningActions = agv.actionStates.Any(state =>
                        !string.Equals(state.actionStatus, "FINISHED", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(state.actionStatus, "FAILED", StringComparison.OrdinalIgnoreCase));

                    if (!hasActiveRoute && !hasRunningActions)
                    {
                        UpsertActionState(agv, action, "FAILED", "当前没有正在执行的订单，无法取消");

                        agv.VdaErrors.RemoveAll(error =>
                            string.Equals(error.errorType, "noOrderToCancel", StringComparison.OrdinalIgnoreCase));

                        agv.VdaErrors.Add(new AgvVdaErrorModel
                        {
                            errorType = "noOrderToCancel",
                            errorLevel = "WARNING",
                            errorDescription = "当前没有可取消的订单",
                            errorReferences =
                            [
                                new AgvVdaErrorReferenceModel
                                {
                                    referenceKey = "actionId",
                                    referenceValue = action.actionId ?? string.Empty
                                }
                            ]
                        });

                        needNotify = true;
                        continue;
                    }

                    agv.VdaErrors.RemoveAll(error =>
                        string.Equals(error.errorType, "noOrderToCancel", StringComparison.OrdinalIgnoreCase));

                    // 未执行完的订单动作需要作为取消结果上报 FAILED，不能直接丢失。
                    foreach (var actionState in agv.actionStates)
                    {
                        if (!string.Equals(actionState.actionStatus, "FINISHED", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(actionState.actionStatus, "FAILED", StringComparison.OrdinalIgnoreCase))
                        {
                            actionState.actionStatus = "FAILED";
                            actionState.resultDescription = "订单取消，动作已终止";
                        }
                    }

                    ApplySimpleAction(agv, action);

                    UpsertActionState(agv, action, "FINISHED", BuildActionResultDescription(action));

                    needNotify = true;
                    continue;
                }

                if (IsLiftAction(action))
                {
                    EnqueueLiftAction(agv, action);

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
    /// 升降动作按仿真周期执行，其余动作立即处理。
    /// </summary>
    private void ApplyNodeActions(
        AgvModel agv,
        VdaOrderNodeModel node)
    {
        var actions = ToAgvActions(node.actions);

        if (actions.Count <= 0)
        {
            return;
        }

        agv.actions = actions;

        ApplyActions(agv, actions);
    }

    /// <summary>
    /// 应用简单动作。
    /// pause/resume/cancelOrder 会影响仿真状态。
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
            _liftActionQueues.Remove(agv);

            agv.RouteRuntime = null;

            // 只取消活动订单，不清除最后已接收订单的历史标识。
            // VDA5050 2.0.0 要求保留 orderId/orderUpdateId，直到新订单到来。
            agv.actions = [];
            // 已取消动作的 FAILED 状态要继续留在 actionStates 中供调度端确认。
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
            return false;
        }

        var angleDegree = GetNodeAngleDegree(targetNode);

        if (!angleDegree.HasValue)
        {
            return false;
        }

        var targetAngle = AgvTurnHelper.NormalizeAngleDegree(angleDegree.Value);
        if (double.IsNaN(targetAngle))
        {
            return false;
        }

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
        if (double.IsNaN(currentAngle))
        {
            return (AgvTurnHelper.TurnDirection.NoTurn, 0);
        }

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
        ref double deltaSeconds)
    {
        var rotateFinished = RotateAgvToAngle(
            agv,
            runtime.RotateTargetAngleDegree,
            runtime.RotateDirection,
            ref deltaSeconds);

        agv.RunStatus = 2;

        if (!rotateFinished)
        {
            return;
        }

        var reachedNode = runtime.RotateMode == RotateMode.AfterArrive ? runtime.RotatingNode : null;
        runtime.IsRotating = false;
        runtime.RotatingNode = null;
        runtime.RotateMode = RotateMode.None;
        runtime.RotateDirection = AgvTurnHelper.TurnDirection.NoTurn;

        if (reachedNode != null)
        {
            CompleteReachedTargetNode(agv, runtime, reachedNode);
        }
    }

    /// <summary>
    /// 按指定方向将AGV旋转到目标角度。
    /// 返回true表示旋转完成。
    /// </summary>
    private static bool RotateAgvToAngle(
        AgvModel agv,
        double targetAngleDegree,
        AgvTurnHelper.TurnDirection direction,
        ref double deltaSeconds)
    {
        var currentAngle = AgvTurnHelper.NormalizeAngleDegree(agv.Angle);

        var targetAngle = AgvTurnHelper.NormalizeAngleDegree(targetAngleDegree);

        if (double.IsNaN(targetAngle))
        {
            return true;
        }

        if (direction == AgvTurnHelper.TurnDirection.NoTurn || double.IsNaN(currentAngle))
        {
            agv.SetBodyAngleDegree(targetAngle);
            return true;
        }

        var rotateSpeed = DefaultRotateSpeedDegreePerSecond * SimulationRotateSpeedScale;

        if (rotateSpeed <= 0)
        {
            deltaSeconds = 0;
            return false;
        }

        var leftTurn = direction == AgvTurnHelper.TurnDirection.LeftTurn;
        var remainAngle = leftTurn
            ? AgvTurnHelper.GetLeftTurnDifference(currentAngle, targetAngle)
            : AgvTurnHelper.GetRightTurnDifference(currentAngle, targetAngle);

        var angleStep = rotateSpeed * deltaSeconds;
        if (remainAngle <= angleStep || remainAngle >= 360.0 - RotateToleranceDegree)
        {
            agv.SetBodyAngleDegree(targetAngle);
            var angleToFinish = Math.Min(remainAngle, 360.0 - remainAngle);
            deltaSeconds = Math.Max(0, deltaSeconds - angleToFinish / rotateSpeed);
            return true;
        }

        agv.SetBodyAngleDegree(leftTurn ? currentAngle + angleStep : currentAngle - angleStep);
        deltaSeconds = 0;
        return false;
    }

    /// <summary>
    /// 实现目标节点到达。
    /// </summary>
    private void CompleteReachedTargetNode(
        AgvModel agv,
        AgvRouteRuntimeModel runtime,
        VdaOrderNodeModel targetNode,
        bool wasDriving = false)
    {
        if (agv.LastNodeSequenceId != targetNode.sequenceId)
        {
            ApplyReachedNode(agv, targetNode, false, false);
        }

        // 节点动作必须在“到达节点 + 车体姿态旋转完成”之后执行。
        // 否则 rotateLoad 会基于旧车头角度计算托盘角度，或者被空重复节点清掉。
        ApplyNodeActions(agv, targetNode);

        if (!ReferenceEquals(agv.RouteRuntime, runtime))
        {
            return;
        }

        runtime.TargetNodeIndex++;

        if (runtime.TargetNodeIndex >= runtime.Nodes.Count)
        {
            runtime.IsCompleted = true;

            agv.RouteRuntime = null;

            if (!agv.VdaPaused)
            {
                agv.RunStatus = 2;
            }
        }
        else if (wasDriving && !agv.VdaPaused && !_liftActionQueues.TryGetValue(agv, out _))
        {
            agv.RunStatus = 1;
        }
    }

    /// <summary>
    /// 应用已经到达的节点。
    /// </summary>
    private void ApplyReachedNode(
        AgvModel agv,
        VdaOrderNodeModel node,
        bool applyAngle = true,
        bool applyActions = true)
    {
        if (int.TryParse(node.nodeId, out var positionId))
        {
            agv.PositionID = positionId;
        }

        agv.LastNodeSequenceId = node.sequenceId;

        agv.X = GetNodeX(node);

        agv.Y = GetNodeY(node);

        if (applyAngle && GetNodeAngleDegree(node) is double angleDegree)
        {
            if (!double.IsNaN(angleDegree) && !double.IsInfinity(angleDegree))
            {
                agv.SetBodyAngleDegree(angleDegree);
            }
        }

        if (applyActions)
        {
            ApplyNodeActions(agv, node);
        }
    }

    /// <summary>
    /// 构建动作执行结果描述。
    /// </summary>
    private static string BuildActionResultDescription(AgvActionModel action)
    {
        var actionType = action.actionType;

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
        if (actions == null || actions.Count <= 0)
        {
            return;
        }

        // 注意：这里不能 Clear actionStates。
        // actionStates 只在新订单开始时清一次，后续边动作/节点动作都用 Upsert 保留多个 actionId 的完成状态。
        foreach (var action in actions)
        {
            if (action == null)
            {
                continue;
            }

            if (IsLiftAction(action))
            {
                EnqueueLiftAction(agv, action);

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
    /// 同一台 AGV 按收到的顺序执行升降动作。
    /// </summary>
    private void EnqueueLiftAction(
        AgvModel agv,
        AgvActionModel action,
        double? targetHeight = null,
        string? singleNodeId = null)
    {
        if (!string.IsNullOrWhiteSpace(action.actionId) && agv.actionStates.Any(state =>
                string.Equals(state.actionId, action.actionId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var liftHeight = targetHeight ?? GetLiftTargetHeight(action);
        var queue = _liftActionQueues.GetValue(agv, _ => new Queue<LiftActionRuntimeModel>());
        var status = queue.Count == 0 ? "INITIALIZING" : "WAITING";

        queue.Enqueue(new LiftActionRuntimeModel
        {
            Action = action,
            SingleNodeId = singleNodeId,
            TargetHeight = liftHeight
        });

        UpsertActionState(agv, action, status, string.Empty);
        agv.RunStatus = 2;
    }

    /// <summary>
    /// 按周期推进托举/货叉动作。
    /// </summary>
    private bool StepAgvLiftActions(AgvModel agv, Queue<LiftActionRuntimeModel> queue, double deltaSeconds)
    {
        var runtime = queue.Peek();
        var previousStatus = GetLiftActionProgressStatus(runtime.ElapsedSeconds);
        runtime.ElapsedSeconds += deltaSeconds;

        if (runtime.ElapsedSeconds >= DefaultLiftActionDurationSeconds)
        {
            // 同一次状态更新应用最终高度和载货状态，再回报动作完成。
            agv.LiftHeight = runtime.TargetHeight;
            agv.LoadStatus = agv.LiftStatus == 1 ? LoadedStatus : EmptyLoadStatus;
            UpsertActionState(agv, runtime.Action, "FINISHED", string.Empty);

            queue.Dequeue();
            if (queue.Count == 0)
            {
                _liftActionQueues.Remove(agv);
            }
            else
            {
                UpsertActionState(agv, queue.Peek().Action, "INITIALIZING", string.Empty);
            }
        }
        else
        {
            var currentStatus = GetLiftActionProgressStatus(runtime.ElapsedSeconds);
            if (currentStatus == previousStatus && agv.RunStatus == 2)
            {
                return false;
            }

            UpsertActionState(agv, runtime.Action, currentStatus, string.Empty);
        }

        agv.RunStatus = 2;
        return true;
    }

    private static string GetLiftActionProgressStatus(double elapsedSeconds)
    {
        return elapsedSeconds < LiftActionInitializingSeconds ? "INITIALIZING" : "RUNNING";
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
        actionState.actionDescription = action.actionDescription ?? string.Empty;
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
               actionType.Equals("fork_up", StringComparison.OrdinalIgnoreCase) ||
               actionType.Equals("lift", StringComparison.OrdinalIgnoreCase) ||
               IsLoweringAction(actionType);
    }

    private static bool IsLoweringAction(string? actionType)
    {
        return string.Equals(actionType, "tine_down", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(actionType, "fork_down", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(actionType, "lower", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 获取托举/货叉动作目标高度，单位：米。
    /// 按项目约定，height > 0 对应高位带载，height = 0 对应低位空载。
    /// tine_up 携带 height = 0 时也会到低位，回报仍使用订单里的动作名。
    /// </summary>
    private static double GetLiftTargetHeight(AgvActionModel action)
    {
        var height = GetActionParameterDoubleValue(action, "height");

        if (height.HasValue)
        {
            return Math.Max(0, height.Value);
        }

        return IsLoweringAction(action.actionType) ? 0.0 : 0.01;
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
                out var result) &&
            (!double.IsNaN(result) && !double.IsInfinity(result)))
        {
            return result;
        }

        if (double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out result) &&
            (!double.IsNaN(result) && !double.IsInfinity(result)))
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
        var parameter = action.actionParameters
            ?.FirstOrDefault(x => string.Equals(x?.key, key, StringComparison.OrdinalIgnoreCase));

        return parameter?.value?.ToString();
    }

    /// <summary>
    /// 获取AGV移动速度。
    /// 真实基础速度仍参考 AGV.Speed 和 RCS 下发的 edge.maxSpeed，
    /// 但仿真显示会额外套用倍率、最小显示速度和最大显示速度。
    /// </summary>
    private static double GetMoveSpeed(AgvModel agv, VdaOrderEdgeModel? edge)
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

        var edgeMaxSpeed = edge?.maxSpeed ?? 0;

        // RCS 下发了 edge.maxSpeed 时，仍然按 RCS 限基础速度。
        if (edgeMaxSpeed > 0 && speed > edgeMaxSpeed)
        {
            speed = edgeMaxSpeed;
        }

        // 仿真显示加速倍率。
        speed *= SimulationMoveSpeedScale;

        // 关键：防止 edge.maxSpeed 太小，倍率后仍然很慢。
        if (speed < SimulationMinMoveSpeed)
        {
            speed = SimulationMinMoveSpeed;
        }

        // 防止倍率过大导致跳点。
        if (SimulationMaxMoveSpeed > 0 && speed > SimulationMaxMoveSpeed)
        {
            speed = SimulationMaxMoveSpeed;
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

        return
        [
            .. actions
                .Select(x => new AgvActionModel
                {
                    actionId = x.actionId,
                    actionType = x.actionType,
                    actionDescription = x.actionDescription,
                    blockingType = x.blockingType,
                    actionParameters = ToAgvActionParameters(x.actionParameters)
                })
        ];
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

        return
        [
            .. parameters
                .Select(x => new AgvActionParameterModel
                {
                    key = x.key,
                    value = x.value
                })
        ];
    }

    /// <summary>
    /// 角度归一化到 0 到 360。
    /// </summary>
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