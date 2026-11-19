using System;
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
        var bodyTheta = agv.BodyTheta;

        var turntableTheta = agv.TurntableTheta;

        var driving = IsDriving(agv);

        var message = new
        {
            agv.actionStates,

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

            avoidAreaId = 0,

            batteryState = new
            {
                batteryCharge = agv.Equantity,
                batteryHealth = 0,
                batteryVoltage = 0.0,
                charging = IsCharging(agv),
                reach = 0
            },

            distanceSinceLastNode = 0,

            driving,

            edgeStates = Array.Empty<object>(),

            errors = Array.Empty<object>(),

            headerId,

            information = Array.Empty<object>(),

            interactionZones = Array.Empty<object>(),

            lastNodeId = string.IsNullOrWhiteSpace(agv.CurrentOrderId)
                ? ""
                : agv.PositionID.ToString(),

            lastNodeSequenceId = string.IsNullOrWhiteSpace(agv.CurrentOrderId)
                ? 0
                : agv.LastNodeSequenceId,

            loads = agv.LoadStatus == 1
                ? new object[]
                {
                    new
                    {
                        boundingBoxReference = new
                        {
                            x = 0,
                            y = 0,
                            z = 0
                        },
                        loadDimensions = new
                        {
                            height = agv.LiftHeight,
                            length = 0,
                            width = 0
                        },
                        loadId = "DUMMY",
                        loadPosition = "",
                        loadType = "",
                        weight = 1000
                    }
                }
                : Array.Empty<object>(),

            manufacturer = config.Manufacturer,

            newBaseRequest = false,

            nodeStates = Array.Empty<object>(),

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

        return JsonConvert.SerializeObject(message);
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
        var bodyTheta = agv.BodyTheta;

        var driving = IsDriving(agv);

        var message = new
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

        return JsonConvert.SerializeObject(message);
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