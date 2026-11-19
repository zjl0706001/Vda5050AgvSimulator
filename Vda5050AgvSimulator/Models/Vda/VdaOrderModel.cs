#nullable disable
// ReSharper disable InconsistentNaming
using System.Collections.Generic;

namespace Vda5050AgvSimulator.Models.Vda;

/// <summary>
/// VDA5050 order 订单模型。
/// RCS 下发给 AGV 的任务数据。
/// </summary>
public class VdaOrderModel
{
    public string orderId { get; set; } = "";

    public int orderUpdateId { get; set; }

    public List<VdaOrderNodeModel> nodes { get; set; } = [];

    public List<VdaOrderEdgeModel> edges { get; set; } = [];

    public long headerId { get; set; }

    public string timestamp { get; set; } = "";

    public string version { get; set; } = "";

    public string manufacturer { get; set; } = "";

    public string serialNumber { get; set; } = "";
}

/// <summary>
/// VDA5050 订单节点。
/// </summary>
public class VdaOrderNodeModel
{
    public string nodeId { get; set; } = "";

    public int sequenceId { get; set; }

    public string nodeDescription { get; set; } = "";

    public bool released { get; set; }

    public VdaOrderNodePositionModel nodePosition { get; set; } = new();

    public List<VdaOrderActionModel> actions { get; set; } = [];
}

/// <summary>
/// VDA5050 订单节点坐标。
/// </summary>
public class VdaOrderNodePositionModel
{
    public double x { get; set; }

    public double y { get; set; }

    public double theta { get; set; }

    public double allowedDeviationXY { get; set; }

    public double allowedDeviationTheta { get; set; }

    public string mapId { get; set; } = "";
}

/// <summary>
/// VDA5050 订单边。
/// </summary>
public class VdaOrderEdgeModel
{
    public string edgeId { get; set; } = "";

    public int sequenceId { get; set; }

    public string edgeDescription { get; set; } = "";

    public bool released { get; set; }

    public string startNodeId { get; set; } = "";

    public string endNodeId { get; set; } = "";

    public double maxSpeed { get; set; }

    public double maxHeight { get; set; }

    public double minHeight { get; set; }

    public double orientation { get; set; }

    public string orientationType { get; set; } = "";

    public string direction { get; set; } = "";

    public bool rotationAllowed { get; set; }

    public double maxRotationSpeed { get; set; }

    public double length { get; set; }

    public List<VdaOrderActionModel> actions { get; set; } = [];

    public int securityId { get; set; }

    public bool needQrFinishTask { get; set; }
}

/// <summary>
/// VDA5050 动作模型。
/// nodes 和 edges 里面的 actions 都可以用这个。
/// </summary>
public class VdaOrderActionModel
{
    public string actionType { get; set; } = "";

    public string actionId { get; set; } = "";

    public string actionDescription { get; set; } = "";

    public string blockingType { get; set; } = "NONE";

    public List<VdaOrderActionParameterModel> actionParameters { get; set; } = [];
}

/// <summary>
/// VDA5050 动作参数。
/// </summary>
public class VdaOrderActionParameterModel
{
    public string key { get; set; } = "";

    public string value { get; set; } = "";
}