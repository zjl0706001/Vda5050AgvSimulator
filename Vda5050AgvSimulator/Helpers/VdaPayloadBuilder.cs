using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;
using Vda5050AgvSimulator.Models.Agv;
using Vda5050AgvSimulator.Models.Mqtt;
using Vda5050AgvSimulator.Models.Vda;

namespace Vda5050AgvSimulator.Helpers;

public static class VdaPayloadBuilder
{
    /// <summary>
    /// 构建 VDA5050 state 报文。
    /// </summary>
    public static string BuildStatePayload(MqttConfigModel config, AgvModel agv, int headerId)
    {
        return SerializeStateSnapshot(CaptureStateSnapshot(config, agv), headerId);
    }

    internal static StateSnapshot CaptureStateSnapshot(MqttConfigModel config, AgvModel agv)
    {
        lock (agv.SimulationStateLock)
        {
            return CaptureState(config, agv);
        }
    }

    internal static string SerializeStateSnapshot(StateSnapshot snapshot, int headerId)
    {
        snapshot.HeaderId = headerId;
        return JsonConvert.SerializeObject(snapshot, Formatting.None);
    }

    internal sealed class StateSnapshot
    {
        internal long CapturedAt { get; } = Stopwatch.GetTimestamp();
        public List<AgvActionStateModel?>? actionStates { get; set; }
        public object agvPosition { get; set; } = null!;
        public int avoidAreaId => 0;
        public object batteryState { get; set; } = null!;
        public int distanceSinceLastNode => 0;
        public bool driving { get; set; }
        public VdaEdgeStateModel[] edgeStates { get; set; } = [];
        public AgvVdaErrorModel?[] errors { get; set; } = [];
        [JsonProperty("headerId")] public int HeaderId { get; set; }
        public object[] information => Array.Empty<object>();
        public object[] interactionZones => Array.Empty<object>();
        public string lastNodeId { get; set; } = "";
        public int lastNodeSequenceId { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public object[]? loads { get; set; }

        public int liftStatus { get; set; }
        public string manufacturer { get; set; } = "";
        public bool newBaseRequest => false;
        public VdaNodeStateModel[] nodeStates { get; set; } = [];
        public string operatingMode { get; set; } = "";
        public string orderId { get; set; } = "";
        public int orderUpdateId { get; set; }
        public bool paused { get; set; }
        public object safetyState { get; set; } = null!;
        public string serialNumber { get; set; } = "";
        public string timestamp { get; set; } = "";
        public object turntableState { get; set; } = null!;
        public object velocity { get; set; } = null!;
        public string version { get; set; } = "";
        public string zoneSetId { get; set; } = "";
    }

    private static StateSnapshot CaptureState(MqttConfigModel config, AgvModel agv)
    {
        var bodyTheta = agv.BodyTheta;

        var turntableTheta = agv.TurntableTheta;

        var driving = IsDriving(agv);

        // VDA5050 2.0.0：已知空载发送空loads数组；已知载货发送载荷对象；
        // 无法判断载货状态时，整个loads字段不发送。
        var loads = BuildLoads(agv);

        // 已知载荷时，liftStatus 和 loads 使用同一个载货状态。手动编辑和旧配置可能只设置 LoadStatus。
        var reportedLiftStatus = agv.LiftStatus;
        if (loads != null)
        {
            reportedLiftStatus = loads.Length > 0 ? 1 : 0;
        }

        // 路线执行中上报剩余已释放节点/边；路线完成后 RouteRuntime 为 null，自然上报空列表。
        // 即使订单已经完成，orderId 与最后到达的节点仍按 VDA5050 2.0.0 保留。
        var runtime = agv.RouteRuntime;

        var pendingNodeStates = Array.Empty<VdaNodeStateModel>();
        var pendingEdgeStates = Array.Empty<VdaEdgeStateModel>();
        if (runtime is { IsCompleted: false })
        {
            var nodeStart = Math.Min(runtime.Nodes.Count, Math.Max(0, runtime.TargetNodeIndex));
            var nodeCount = runtime.Nodes.Count - nodeStart;
            var firstPendingEdgeSequenceId = nodeCount > 0
                ? runtime.Nodes[nodeStart].sequenceId - 1
                : int.MaxValue;
            if (nodeCount > 0)
            {
                pendingNodeStates = new VdaNodeStateModel[nodeCount];
                for (var index = 0; index < nodeCount; index++)
                {
                    var node = runtime.Nodes[nodeStart + index];
                    var position = node.nodePosition;
                    pendingNodeStates[index] = new VdaNodeStateModel
                    {
                        nodeId = node.nodeId,
                        sequenceId = node.sequenceId,
                        released = node.released,
                        nodePosition = new VdaNodePositionModel
                        {
                            x = position?.x ?? 0,
                            y = position?.y ?? 0,
                            theta = position?.theta ?? 0,
                            mapId = string.IsNullOrWhiteSpace(position?.mapId)
                                ? config.MapId
                                : position?.mapId,
                            allowedDeviationXY = position?.allowedDeviationXY ?? 0,
                            allowedDeviationTheta = position?.allowedDeviationTheta ?? 0
                        }
                    };
                }
            }

            if (runtime.EdgesAreSorted)
            {
                var edgeStart = runtime.GetFirstEdgeIndex(firstPendingEdgeSequenceId);
                if (edgeStart < runtime.RouteEdges.Count)
                {
                    pendingEdgeStates = new VdaEdgeStateModel[runtime.RouteEdges.Count - edgeStart];
                    for (var index = 0; index < pendingEdgeStates.Length; index++)
                    {
                        pendingEdgeStates[index] = CaptureEdgeState(runtime.RouteEdges[edgeStart + index]);
                    }
                }
            }
            else if (runtime.RouteEdges.Count > 0)
            {
                pendingEdgeStates = runtime.RouteEdges
                    .FindAll(edge => edge.sequenceId >= firstPendingEdgeSequenceId)
                    .ConvertAll(CaptureEdgeState).ToArray();
            }
        }

        return new StateSnapshot
        {
            actionStates = agv.actionStates?.ConvertAll(action => action == null
                ? null
                : new AgvActionStateModel
                {
                    actionId = action.actionId,
                    actionType = action.actionType,
                    actionDescription = action.actionDescription,
                    actionStatus = action.actionStatus,
                    resultDescription = action.resultDescription
                }),

            agvPosition = new
            {
                deviationRange = 0,
                localizationScore = 0,
                mapDescription = "",
                mapId = config.MapId,
                mapVersion = "V44",
                positionInitialized = true,
                theta = bodyTheta,
                x = agv.X,
                y = agv.Y
            },

            batteryState = new
            {
                batteryCharge = agv.Equantity,
                batteryHealth = 0,
                batteryVoltage = 0.0,
                charging = IsCharging(agv),
                reach = 0
            },

            driving = driving,

            edgeStates = pendingEdgeStates,

            errors = agv.VdaErrors.ConvertAll(error => error == null
                ? null
                : new AgvVdaErrorModel
                {
                    errorType = error.errorType,
                    errorLevel = error.errorLevel,
                    errorDescription = error.errorDescription,
                    errorReferences = error.errorReferences?.ConvertAll(reference =>
                        reference == null
                            ? null!
                            : new AgvVdaErrorReferenceModel
                            {
                                referenceKey = reference.referenceKey,
                                referenceValue = reference.referenceValue
                            })!
                }).ToArray(),

            lastNodeId = agv.PositionID > 0
                ? agv.PositionID.ToString()
                : "",

            lastNodeSequenceId = agv.PositionID > 0
                ? agv.LastNodeSequenceId
                : 0,

            loads = loads,

            // liftStatus不是VDA5050 2.0.0标准字段，是当前项目约定的扩展字段。
            liftStatus = reportedLiftStatus,

            manufacturer = config.Manufacturer,

            nodeStates = pendingNodeStates,

            operatingMode = AgvHelper.AgvControlStatus.GetVdaOperatingMode(agv),

            orderId = agv.CurrentOrderId,

            orderUpdateId = agv.CurrentOrderUpdateId,

            paused = agv.VdaPaused || agv.ControlStatus == AgvHelper.AgvControlStatus.Manual,

            safetyState = new
            {
                eStop = agv.StopButtonStatus == 0 ? "NONE" : "AUTOACK",
                fieldViolation = false
            },

            serialNumber = agv.DeviceID.ToString(),

            timestamp = VdaTimeHelper.GetUtcTimestamp(),

            turntableState = new
            {
                theta = turntableTheta
            },

            velocity = new
            {
                omega = 0.0,
                vx = driving ? agv.Speed * Math.Cos(bodyTheta) : 0.0,
                vy = driving ? agv.Speed * Math.Sin(bodyTheta) : 0.0
            },

            version = config.ProtocolVersion,

            zoneSetId = config.ZoneId
        };
    }

    private static VdaEdgeStateModel CaptureEdgeState(VdaOrderEdgeModel edge)
    {
        return new VdaEdgeStateModel
        {
            edgeId = edge.edgeId,
            sequenceId = edge.sequenceId,
            released = edge.released
        };
    }

    /// <summary>
    /// 根据AGV载货状态构建VDA5050 loads字段。
    /// UNKNOWN/未识别状态返回null，由调用方省略整个loads字段。
    /// </summary>
    private static object[]? BuildLoads(AgvModel agv)
    {
        var loadStatus = agv.LoadStatus?.Trim();

        if (string.Equals(loadStatus, "UNLOAD", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<object>();
        }

        if (!string.Equals(loadStatus, "LOADED", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var load = new
        {
            loadId = "DUMMY",
            boundingBoxReference = new
            {
                x = 0.0,
                y = 0.0,
                z = 0.0
            },
            // DUMMY 载荷未配置尺寸时，按实车报文填 0。
            loadDimensions = new
            {
                height = NormalizeLoadDimension(agv.LoadDimensionHeight),
                length = NormalizeLoadDimension(agv.LoadDimensionLength),
                width = NormalizeLoadDimension(agv.LoadDimensionWidth)
            },
            loadPosition = "",
            loadType = "",
            weight = 1000
        };

        return [load];
    }

    private static double NormalizeLoadDimension(double dimension)
    {
        return dimension > 0 && !double.IsInfinity(dimension) ? dimension : 0.0;
    }

    /// <summary>
    /// 构建 VDA5050 connection 报文。
    /// </summary>
    public static string BuildConnectionPayload(
        MqttConfigModel config,
        AgvModel agv,
        int connectionState,
        int headerId)
    {
        var message = new
        {
            connectionState,
            headerId,
            timestamp = VdaTimeHelper.GetUtcTimestamp(),
            version = config.ProtocolVersion,
            manufacturer = config.Manufacturer,
            serialNumber = agv.DeviceID.ToString()
        };

        return JsonConvert.SerializeObject(message);
    }

    /// <summary>
    /// 构建 VDA5050 visualization 报文。
    /// </summary>
    public static string BuildVisualizationPayload(MqttConfigModel config, AgvModel agv, int headerId)
    {
        object message;
        lock (agv.SimulationStateLock)
        {
            message = CaptureVisualization(config, agv, headerId);
        }

        return JsonConvert.SerializeObject(message);
    }

    private static object CaptureVisualization(MqttConfigModel config, AgvModel agv, int headerId)
    {
        var bodyTheta = agv.BodyTheta;

        var driving = IsDriving(agv);

        return new
        {
            agvPosition = new
            {
                positionInitialized = true,
                localizationScore = 100.0,
                x = agv.X,
                y = agv.Y,
                theta = bodyTheta,
                mapId = config.MapId,
                mapDescription = ""
            },

            velocity = new
            {
                vx = driving ? agv.Speed * Math.Cos(bodyTheta) : 0.0,
                vy = driving ? agv.Speed * Math.Sin(bodyTheta) : 0.0,
                omega = 0.0
            },

            headerId,
            timestamp = VdaTimeHelper.GetUtcTimestamp(),
            version = config.ProtocolVersion,
            manufacturer = config.Manufacturer,
            serialNumber = agv.DeviceID.ToString()
        };
    }

    /// <summary>
    /// 构建 VDA5050 factsheet 报文。
    /// </summary>
    public static string BuildFactSheetPayload(MqttConfigModel config, AgvModel agv, int headerId)
    {
        var factSheetConfig = config.FactSheetConfig;

        var message = new VdaFactSheetModel
        {
            TypeSpecification = new TypeSpecificationModel
            {
                AgvKinematic = factSheetConfig.AgvKinematic,
                AgvClass = factSheetConfig.AgvClass,
                MaxLoadMass = factSheetConfig.MaxLoadMass
            },

            HeaderId = headerId,
            Timestamp = VdaTimeHelper.GetUtcTimestamp(),
            Version = config.ProtocolVersion,
            Manufacturer = config.Manufacturer,
            SerialNumber = agv.DeviceID.ToString()
        };

        return JsonConvert.SerializeObject(message);
    }

    /// <summary>
    /// 判断AGV是否正在行驶。
    /// </summary>
    private static bool IsDriving(AgvModel agv)
    {
        /*
         * 这里按你前面说的状态理解：
         * RunStatus = 1 表示运行中。
         * 如果你的实际枚举不是 1，后面只改这里即可。
         */
        return agv.RunStatus == 1;
    }

    /// <summary>
    /// 判断AGV是否正在充电。
    /// </summary>
    private static bool IsCharging(AgvModel agv)
    {
        /*
         * 这里按你前面说的充电状态理解：
         * ChargeStatus = 1 表示充电启动。
         * ChargeStatus = 2 表示充电停止。
         */
        return agv.ChargeStatus == 1;
    }
}