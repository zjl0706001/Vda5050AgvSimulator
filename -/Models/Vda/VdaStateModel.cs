#nullable disable

using System.Collections.Generic;

// ReSharper disable InconsistentNaming

namespace Vda5050AgvSimulator.Models.Vda;

/// <summary>
/// VDA5050 state 状态上报模型。 
/// </summary>
public class VdaStateModel
{
    public List<VdaActionStateModel> actionStates { get; set; } = [];

    public VdaAgvPositionModel agvPosition { get; set; } = new();

    public int avoidAreaId { get; set; }

    public VdaBatteryStateModel batteryState { get; set; } = new();

    public double distanceSinceLastNode { get; set; }

    public bool driving { get; set; }

    public List<VdaEdgeStateModel> edgeStates { get; set; } = [];

    public List<object> errors { get; set; } = [];

    public long headerId { get; set; }

    public List<object> information { get; set; } = [];

    public List<object> interactionZones { get; set; } = [];

    public string lastNodeId { get; set; } = "";

    public int lastNodeSequenceId { get; set; }

    public List<VdaLoadModel> loads { get; set; } = [];

    public string manufacturer { get; set; } = "";

    public bool newBaseRequest { get; set; }

    public List<VdaNodeStateModel> nodeStates { get; set; } = [];

    public string operatingMode { get; set; } = "MANUAL";

    public string orderId { get; set; } = "";

    public int orderUpdateId { get; set; }

    public bool paused { get; set; }

    public VdaSafetyStateModel safetyState { get; set; } = new();

    public string serialNumber { get; set; } = "";

    public string timestamp { get; set; } = "";

    public VdaTurntableStateModel turntableState { get; set; } = new();

    public VdaVelocityModel velocity { get; set; } = new();

    public string version { get; set; } = "";

    public string zoneSetId { get; set; } = "";
}

/// <summary>
/// AGV 位置。
/// </summary>
public class VdaAgvPositionModel
{
    public double deviationRange { get; set; }

    public double localizationScore { get; set; }

    public string mapDescription { get; set; } = "";

    public string mapId { get; set; } = "";

    public string mapVersion { get; set; } = "V44";

    public bool positionInitialized { get; set; } = true;

    public double theta { get; set; }

    public double x { get; set; }

    public double y { get; set; }
}

/// <summary>
/// 电池状态。
/// </summary>
public class VdaBatteryStateModel
{
    public double batteryCharge { get; set; }

    public double batteryHealth { get; set; }

    public double batteryVoltage { get; set; }

    public bool charging { get; set; }

    public double reach { get; set; }
}

/// <summary>
/// 速度状态。
/// </summary>
public class VdaVelocityModel
{
    public double omega { get; set; }

    public double vx { get; set; }

    public double vy { get; set; }
}

/// <summary>
/// 安全状态。
/// </summary>
public class VdaSafetyStateModel
{
    public string eStop { get; set; } = "NONE";

    public bool fieldViolation { get; set; }
}

/// <summary>
/// 转盘状态。
/// </summary>
public class VdaTurntableStateModel
{
    public double theta { get; set; }
}

/// <summary>
/// 负载状态。
/// </summary>
public class VdaLoadModel
{
    public VdaBoundingBoxReferenceModel boundingBoxReference { get; set; } = new();

    public VdaLoadDimensionsModel loadDimensions { get; set; } = new();

    public string loadId { get; set; } = "";

    public string loadPosition { get; set; } = "";

    public string loadType { get; set; } = "";

    public double weight { get; set; }
}

/// <summary>
/// 负载包围盒参考点。
/// </summary>
public class VdaBoundingBoxReferenceModel
{
    public double x { get; set; }

    public double y { get; set; }

    public double z { get; set; }
}

/// <summary>
/// 负载尺寸。
/// </summary>
public class VdaLoadDimensionsModel
{
    public double height { get; set; }

    public double length { get; set; }

    public double width { get; set; }
}

/// <summary>
/// 节点状态。
/// </summary>
public class VdaNodeStateModel
{
    public string nodeId { get; set; } = "";

    public VdaNodePositionModel nodePosition { get; set; } = new();

    public bool released { get; set; }

    public int sequenceId { get; set; }
}

/// <summary>
/// 节点位置。
/// </summary>
public class VdaNodePositionModel
{
    public double allowedDeviationTheta { get; set; }

    public double allowedDeviationXY { get; set; }

    public string mapId { get; set; } = "";

    public double theta { get; set; }

    public double x { get; set; }

    public double y { get; set; }
}

/// <summary>
/// 路径边状态。
/// </summary>
public class VdaEdgeStateModel
{
    public string edgeId { get; set; } = "";

    public bool released { get; set; }

    public int sequenceId { get; set; }
}

/// <summary>
/// 动作状态。
/// </summary>
public class VdaActionStateModel
{
    public string actionDescription { get; set; } = "";

    public string actionId { get; set; } = "";

    public string actionStatus { get; set; } = "";

    public string actionType { get; set; } = "";

    public string resultDescription { get; set; } = "";
}