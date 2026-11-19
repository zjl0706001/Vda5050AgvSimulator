using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Newtonsoft.Json;
using Vda5050AgvSimulator.Helpers;
using Vda5050AgvSimulator.Models.Vda;

// ReSharper disable UnusedAutoPropertyAccessor.Global

// ReSharper disable InconsistentNaming
// ReSharper disable RedundantDefaultMemberInitializer

namespace Vda5050AgvSimulator.Models.Agv;

public abstract class NotifyPropertyChangedBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;

        OnPropertyChanged(propertyName);
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class AgvModel : NotifyPropertyChangedBase
{
    // 仿真更新和 MQTT state 构建共用该锁，确保同一帧读取到一致的升降、载货和动作状态。
    [JsonIgnore]
    internal object SimulationStateLock { get; } = new();

    [JsonIgnore]
    internal long LastPublishedStateSampleAt { get; set; }

    [JsonIgnore]
    internal volatile bool IsRemovedFromStore;

    private const int RunStatusChanged = 1;
    private const int SpeedChanged = 2;
    private const int OnlineStatusChanged = 4;
    private const int PositionIdChanged = 8;
    private const int XChanged = 16;
    private const int YChanged = 32;
    private const int AngleChanged = 64;
    private const int PalletAngleChanged = 128;
    private int _pendingPropertyChanges;
    private AgvRouteRuntimeModel? _routeRuntime;
    private int _positionId = 239;
    private double _x = 23.72;
    private double _y = -45.115;
    private double _angle = 270.0;
    private double _palletWorldAngle = 90.0;

    protected override void OnPropertyChanged(string? propertyName)
    {
        // 仿真及订单处理持有单车锁时，只记录变化。由界面刷新回调统一触发绑定事件。
        if (Monitor.IsEntered(SimulationStateLock))
        {
            var change = propertyName switch
            {
                nameof(RunStatus) => RunStatusChanged,
                nameof(Speed) => SpeedChanged,
                nameof(OnlineStatus) => OnlineStatusChanged,
                nameof(PositionID) => PositionIdChanged,
                nameof(X) => XChanged,
                nameof(Y) => YChanged,
                nameof(Angle) => AngleChanged,
                nameof(PalletWorldAngle) => PalletAngleChanged,
                _ => 0
            };

            if (change != 0)
            {
                int pending;
                do
                {
                    pending = Volatile.Read(ref _pendingPropertyChanges);
                }
                while (Interlocked.CompareExchange(ref _pendingPropertyChanges, pending | change, pending) != pending);

                return;
            }
        }

        base.OnPropertyChanged(propertyName);
        if (propertyName == nameof(Angle))
        {
            base.OnPropertyChanged(nameof(BodyTheta));
        }

        if (propertyName == nameof(Angle) || propertyName == nameof(PalletWorldAngle))
        {
            base.OnPropertyChanged(nameof(TurntableAngle));
            base.OnPropertyChanged(nameof(TurntableTheta));
        }
    }

    internal void FlushSimulationPropertyChanges()
    {
        var changes = Interlocked.Exchange(ref _pendingPropertyChanges, 0);
        if ((changes & RunStatusChanged) != 0)
        {
            base.OnPropertyChanged(nameof(RunStatus));
        }

        if ((changes & SpeedChanged) != 0)
        {
            base.OnPropertyChanged(nameof(Speed));
        }

        if ((changes & OnlineStatusChanged) != 0)
        {
            base.OnPropertyChanged(nameof(OnlineStatus));
        }

        if ((changes & PositionIdChanged) != 0)
        {
            base.OnPropertyChanged(nameof(PositionID));
        }

        if ((changes & XChanged) != 0)
        {
            base.OnPropertyChanged(nameof(X));
        }

        if ((changes & YChanged) != 0)
        {
            base.OnPropertyChanged(nameof(Y));
        }

        if ((changes & AngleChanged) != 0)
        {
            base.OnPropertyChanged(nameof(Angle));
            base.OnPropertyChanged(nameof(BodyTheta));
        }

        if ((changes & PalletAngleChanged) != 0)
        {
            base.OnPropertyChanged(nameof(PalletWorldAngle));
        }

        if ((changes & (AngleChanged | PalletAngleChanged)) != 0)
        {
            base.OnPropertyChanged(nameof(TurntableAngle));
            base.OnPropertyChanged(nameof(TurntableTheta));
        }
    }

    /// <summary>
    /// AGV数量，通常用于批量生成或配置显示。
    /// </summary>
    public int AGVCount { get; set; } = 1;

    /// <summary>
    /// 设备ID，也是当前模拟器中常用的AGV编号。
    /// 一般会映射到VDA5050的serialNumber。
    /// </summary>
    public int DeviceID { get; set; } = 1;

    /// <summary>
    /// 设备类型。
    /// 例如：潜伏式、叉车式、辊筒式等，可按项目内部枚举定义。
    /// </summary>
    public int DeviceType { get; set; } = 3;

    /// <summary>
    /// 本地IP。
    /// 传统TCP/UDP模拟时使用，MQTT模式下通常不是核心字段。
    /// </summary>
    public string LocalIP { get; set; } = "127.0.0.1";

    /// <summary>
    /// 本地端口。
    /// 传统TCP/UDP模拟时使用。
    /// </summary>
    public int LocalPort { get; set; } = 4001;

    /// <summary>
    /// 远程IP。
    /// 传统TCP/UDP模拟时表示RCS或服务端IP。
    /// </summary>
    public string RemoteIP { get; set; } = "127.0.0.1";

    /// <summary>
    /// 远程端口。
    /// 传统TCP/UDP模拟时表示RCS或服务端端口。
    /// </summary>
    public int RemotePort { get; set; } = 4002;

    /// <summary>
    /// 当前点位ID。
    /// 可映射到VDA5050中的lastNodeId或当前地图地标ID。
    /// </summary>
    public int PositionID
    {
        get => _positionId;
        set => SetProperty(ref _positionId, value);
    }

    /// <summary>
    /// 当前X坐标。
    /// 对应VDA5050 agvPosition.x。
    /// </summary>
    public double X
    {
        get => _x;
        set => SetProperty(ref _x, value);
    }

    /// <summary>
    /// 当前Y坐标。
    /// 对应VDA5050 agvPosition.y。
    /// </summary>
    public double Y
    {
        get => _y;
        set => SetProperty(ref _y, value);
    }

    /// <summary>
    /// 当前车头角度。
    /// 单位：角度制。
    /// 对应VDA5050 state.agvPosition.theta / visualization.agvPosition.theta。
    /// 注意：这里表示AGV车体在地图坐标系里的朝向，不表示托盘/货叉/转盘角度。
    /// </summary>
    public double Angle
    {
        get => _angle;
        set => SetProperty(ref _angle, value);
    }

    /// <summary>
    /// 托盘/货物在地图坐标系里的绝对姿态。
    /// 单位：角度制。
    /// 当前项目约定：托盘全程姿态保持不变，只转车头。
    /// 例如托盘绝对姿态固定为90度时：
    /// 车头90度 => turntableState.theta=0；
    /// 车头0度 => turntableState.theta=90度；
    /// 车头180度 => turntableState.theta=-90度。
    /// </summary>
    public double PalletWorldAngle
    {
        get => _palletWorldAngle;
        set => SetProperty(ref _palletWorldAngle, value);
    }

    /// <summary>
    /// AGV车头角度，单位弧度。
    /// 发布VDA5050 agvPosition.theta时建议使用该值。
    /// </summary>
    [JsonIgnore]
    public double BodyTheta => DegreeToRadian(NormalizeAngleDegree(Angle));

    /// <summary>
    /// 托盘/转盘相对AGV车体的角度，单位角度制。
    /// 公式：托盘相对角度 = 托盘地图绝对角度 - 车头地图角度。
    /// </summary>
    [JsonIgnore]
    public double TurntableAngle => NormalizeSignedAngleDegree(PalletWorldAngle - Angle);

    /// <summary>
    /// 托盘/转盘相对AGV车体的角度，单位弧度。
    /// 发布VDA5050 state.turntableState.theta时建议使用该值。
    /// </summary>
    [JsonIgnore]
    public double TurntableTheta => DegreeToRadian(TurntableAngle);

    /// <summary>
    /// 设置车头角度，自动归一化到0到360。
    /// </summary>
    public void SetBodyAngleDegree(double angleDegree)
    {
        Angle = NormalizeAngleDegree(angleDegree);
    }

    /// <summary>
    /// 设置托盘/货物地图绝对姿态，自动归一化到0到360。
    /// </summary>
    public void SetPalletWorldAngleDegree(double angleDegree)
    {
        PalletWorldAngle = NormalizeAngleDegree(angleDegree);
    }

    /// <summary>
    /// 角度转弧度。
    /// </summary>
    private static double DegreeToRadian(double angleDegree)
    {
        return angleDegree * Math.PI / 180.0;
    }

    /// <summary>
    /// 角度归一化到0到360。
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
    /// 角度归一化到-180到180。
    /// VDA5050 turntableState.theta通常使用有符号角度更直观。
    /// </summary>
    private static double NormalizeSignedAngleDegree(double angleDegree)
    {
        var result = NormalizeAngleDegree(angleDegree);

        if (result > 180.0)
        {
            result -= 360.0;
        }

        return result;
    }

    /// <summary>
    /// 电量百分比。
    /// 对应VDA5050 batteryState.batteryCharge。
    /// </summary>
    public double Equantity { get; set; } = 100.0;

    /// <summary>
    /// 运行状态。
    /// 可定义为：停止、运行中、精准停车等。
    /// </summary>
    public int RunStatus
    {
        get;
        set => SetProperty(ref field, value);
    } = 0;

    /// <summary>
    /// 运行方向。
    /// 可定义为：未知、前进、后退。
    /// </summary>
    public int RunDirection { get; set; } = 0;

    /// <summary>
    /// 当前速度，单位建议使用m/s。
    /// 对应VDA5050 velocity.vx/vy或仿真移动速度。
    /// </summary>
    public double Speed
    {
        get;
        set => SetProperty(ref field, value);
    } = 0.5;

    /// <summary>
    /// 避障区域。
    /// 可用于模拟不同避障区编号，例如1到16。
    /// </summary>
    public int DodgeArea { get; set; } = 0;

    /// <summary>
    /// 在线状态。
    /// 可定义为：未知、在线、离线。
    /// </summary>
    public int OnlineStatus
    {
        get;
        set => SetProperty(ref field, value);
    } = 1;

    /// <summary>
    /// 管制状态。
    /// 可定义为：未知、放行、管制。
    /// </summary>
    public int TrafficStatus { get; set; } = 0;

    /// <summary>
    /// 充电状态。
    /// 可定义为：未知、开始充电、停止充电等。
    /// </summary>
    public int ChargeStatus { get; set; } = 2;

    /// <summary>
    /// 平移动作状态。
    /// 用于模拟横移、平移机构等状态。
    /// </summary>
    public int TranslationStatus { get; set; } = 0;

    /// <summary>
    /// 旋转动作状态。
    /// 用于模拟车体旋转或旋转机构状态。
    /// </summary>
    public int RotateStatus { get; set; } = 0;

    /// <summary>
    /// 岔道状态。
    /// 用于模拟分岔、道岔或路径分支状态。
    /// </summary>
    public int BranchStatus { get; set; } = 0;

    /// <summary>
    /// 插销1状态。
    /// </summary>
    public int Pin1Status { get; set; } = 0;

    /// <summary>
    /// 插销2状态。
    /// </summary>
    public int Pin2Status { get; set; } = 0;

    /// <summary>
    /// 平台1状态。
    /// </summary>
    public int Plat1Status { get; set; } = 0;

    /// <summary>
    /// 平台2状态。
    /// </summary>
    public int Plat2Status { get; set; } = 0;

    /// <summary>
    /// 治具1状态。
    /// </summary>
    public int Jig1Status { get; set; } = 0;

    /// <summary>
    /// 治具2状态。
    /// </summary>
    public int Jig2Status { get; set; } = 0;

    /// <summary>
    /// 辊筒1状态。
    /// </summary>
    public int Roller1Status { get; set; } = 0;

    /// <summary>
    /// 辊筒2状态。
    /// </summary>
    public int Roller2Status { get; set; } = 0;

    /// <summary>
    /// 辊筒3状态。
    /// </summary>
    public int Roller3Status { get; set; } = 0;

    /// <summary>
    /// 辊筒4状态。
    /// </summary>
    public int Roller4Status { get; set; } = 0;

    /// <summary>
    /// 载货状态。
    /// 可定义为：UNLOAD（已确认空载）、LOADED（已确认载货）、UNKNOWN（无法判断）。
    /// 对应VDA5050 state.loads：UNKNOWN时不发送loads字段，UNLOAD时发送空数组。
    /// </summary>
    public string LoadStatus { get; set; } = "UNLOAD";

    /// <summary>
    /// 当前载荷长度，单位：米。
    /// 对应VDA5050 loads[].loadDimensions.length；DUMMY载荷的默认值为0。
    /// </summary>
    public double LoadDimensionLength { get; set; } = 0.0;

    /// <summary>
    /// 当前载荷宽度，单位：米。
    /// 对应VDA5050 loads[].loadDimensions.width；DUMMY载荷的默认值为0。
    /// </summary>
    public double LoadDimensionWidth { get; set; } = 0.0;

    /// <summary>
    /// 当前载荷高度，单位：米。
    /// 对应VDA5050 loads[].loadDimensions.height；DUMMY载荷的默认值为0。
    /// 注意：这是货物自身尺寸，不是货叉/顶升机构高度。
    /// </summary>
    public double LoadDimensionHeight { get; set; } = 0.0;

    /// <summary>
    /// 报警码。
    /// 为0通常表示无报警。
    /// </summary>
    public int AlarmCode { get; set; } = 0;

    /// <summary>
    /// 辊筒1载货状态。
    /// </summary>
    public int Roller1LoadStatus { get; set; } = 0;

    /// <summary>
    /// 辊筒2载货状态。
    /// </summary>
    public int Roller2LoadStatus { get; set; } = 0;

    /// <summary>
    /// 辊筒3载货状态。
    /// </summary>
    public int Roller3LoadStatus { get; set; } = 0;

    /// <summary>
    /// 辊筒4载货状态。
    /// </summary>
    public int Roller4LoadStatus { get; set; } = 0;

    /// <summary>
    /// 音乐或蜂鸣器状态。
    /// </summary>
    public int MusicStatus { get; set; }

    /// <summary>
    /// 急停按钮状态。
    /// 可用于映射VDA5050 safetyState.eStop。
    /// </summary>
    public int StopButtonStatus { get; set; } = 0;

    /// <summary>
    /// 控制状态。
    /// 可表示自动、手动、远程、RCS控制等。
    /// </summary>
    public int ControlStatus { get; set; } = AgvHelper.AgvControlStatus.Automatic;

    /// <summary>
    /// 放行RFID。
    /// 传统协议中用于表示允许放行的点位。
    /// </summary>
    public int ReleaseRFID { get; set; } = 0;

    /// <summary>
    /// AGV输出信号1。
    /// </summary>
    public int AGVOutputSignal1 { get; set; } = 0;

    /// <summary>
    /// AGV输出信号2。
    /// </summary>
    public int AGVOutputSignal2 { get; set; } = 0;

    /// <summary>
    /// 是否首次上电。
    /// </summary>
    public bool FirstPoweron { get; set; } = false;

    /// <summary>
    /// 是否按钮放行。
    /// </summary>
    public bool ButtonRelease { get; set; } = false;

    /// <summary>
    /// 电气状态或电气故障标记。
    /// 具体含义按项目协议定义。
    /// </summary>
    public int Electric { get; set; } = 0;

    /// <summary>
    /// 顶升/货叉机构当前高度，单位：米。
    /// 由VDA订单动作参数height驱动仿真，保留小数精度。
    /// </summary>
    public double LiftHeight { get; set; } = 0.0;

    /// <summary>
    /// 项目扩展字段：升降机构状态。
    /// 0=低位，1=高位。VDA5050 2.0.0标准state中没有liftStatus字段，
    /// 这里仅作为项目扩展上报，不替代标准loads字段。
    /// </summary>
    [JsonIgnore]
    public int LiftStatus => LiftHeight > 0 ? 1 : 0;

    /// <summary>
    /// 是否按下关机按钮。
    /// </summary>
    public bool ButtonShutdown { get; set; } = false;

    /// <summary>
    /// 是否按下休眠按钮。
    /// </summary>
    public bool ButtonSleep { get; set; } = false;

    /// <summary>
    /// 任务是否发生变化。
    /// </summary>
    public bool TaskChange { get; set; } = false;

    /// <summary>
    /// 当前任务号。
    /// </summary>
    public int TaskNo { get; set; } = 0;

    /// <summary>
    /// RCS输出信号1。
    /// </summary>
    public int RCSOutputSignal1 { get; set; } = 0;

    /// <summary>
    /// RCS输出信号2。
    /// </summary>
    public int RCSOutputSignal2 { get; set; } = 0;

    /// <summary>
    /// 到下一点位的距离。
    /// 可用于传统协议或界面显示。
    /// </summary>
    public int NextDistance { get; set; } = 0;

    /// <summary>
    /// 管制RFID。
    /// 用于表示当前受控或需要管制的点位。
    /// </summary>
    public int ControlRFID { get; set; } = 0;

    /// <summary>
    /// 仿真启动按钮状态。
    /// </summary>
    public bool SimulationButtonStart { get; set; } = false;

    /// <summary>
    /// 当前动作ID。
    /// </summary>
    public int ActionID { get; set; } = 0;

    /// <summary>
    /// 放行RFID1。
    /// </summary>
    public int ReleaseRFID1 { get; set; } = 0;

    /// <summary>
    /// 放行RFID2。
    /// </summary>
    public int ReleaseRFID2 { get; set; } = 0;

    /// <summary>
    /// 放行RFID3。
    /// </summary>
    public int ReleaseRFID3 { get; set; } = 0;

    /// <summary>
    /// 模拟量信号1。
    /// </summary>
    public int AnalogSignal1 { get; set; } = 0;

    /// <summary>
    /// 模拟量信号2。
    /// </summary>
    public int AnalogSignal2 { get; set; } = 0;

    /// <summary>
    /// 事务命令。
    /// 项目自定义协议字段。
    /// </summary>
    public int tr_cmd { get; set; } = 0;

    /// <summary>
    /// 事务ID索引。
    /// 项目自定义协议字段。
    /// </summary>
    public int tr_id_index { get; set; } = 0;

    /// <summary>
    /// 事务ID小时。
    /// 项目自定义协议字段。
    /// </summary>
    public int tr_id_hour { get; set; } = 0;

    /// <summary>
    /// 事务ID日期。
    /// 项目自定义协议字段。
    /// </summary>
    public int tr_id_day { get; set; } = 0;

    /// <summary>
    /// 事务ID月份。
    /// 项目自定义协议字段。
    /// </summary>
    public int tr_id_month { get; set; } = 0;

    /// <summary>
    /// 事务ID年份。
    /// 项目自定义协议字段。
    /// </summary>
    public int tr_id_year { get; set; } = 0;

    /// <summary>
    /// 事务发送方。
    /// 项目自定义协议字段。
    /// </summary>
    public int tr_sender { get; set; } = 0;

    /// <summary>
    /// 事务类型。
    /// 项目自定义协议字段。
    /// </summary>
    public int tr_type { get; set; } = 0;

    /// <summary>
    /// 事务步长。
    /// 项目自定义协议字段。
    /// </summary>
    public int tr_step_size { get; set; } = 0;

    /// <summary>
    /// 事务启动延时。
    /// 项目自定义协议字段。
    /// </summary>
    public int tr_start_delay { get; set; } = 0;

    /// <summary>
    /// 当前订单中的动作列表。
    /// 对应VDA5050 order中的actions。
    /// </summary>
    public List<AgvActionModel>? actions { get; set; }

    /// <summary>
    /// 当前动作状态列表。
    /// 对应VDA5050 state中的actionStates。
    /// </summary>
    public List<AgvActionStateModel> actionStates { get; set; } = [];

    /// <summary>
    /// VDA5050 协议错误（例如在无活动订单时收到 cancelOrder）。
    /// 属于运行时状态，不写入 AGV 静态配置。
    /// </summary>
    [JsonIgnore]
    public List<AgvVdaErrorModel> VdaErrors { get; set; } = [];

    /// <summary>
    /// 当前VDA5050订单ID。
    /// 对应orderId。
    /// </summary>
    public string CurrentOrderId { get; set; } = string.Empty;

    /// <summary>
    /// 当前VDA5050订单更新ID。
    /// 对应orderUpdateId。
    /// </summary>
    public int CurrentOrderUpdateId { get; set; } = 0;

    /// <summary>
    /// 最后经过的节点序号。
    /// 对应lastNodeSequenceId。
    /// </summary>
    public int LastNodeSequenceId { get; set; } = 0;

    /// <summary>
    /// VDA5050暂停状态。
    /// 对应state.paused。
    /// </summary>
    public bool VdaPaused { get; set; } = false;

    /// <summary>
    /// Factsheet配置。
    /// 用于回复VDA5050 factsheet消息，描述AGV的静态能力、尺寸、速度、载重和轮廓。
    /// </summary>
    public AgvFactSheetConfigModel FactSheet { get; set; } = new();

    [JsonIgnore]
    public AgvRouteRuntimeModel? RouteRuntime
    {
        get => Volatile.Read(ref _routeRuntime);
        set => Volatile.Write(ref _routeRuntime, value);
    }
}

public class AgvActionModel
{
    /// <summary>
    /// 动作ID。
    /// 对应VDA5050 action.actionId。
    /// </summary>
    public string actionId { get; set; } = "";

    /// <summary>
    /// 动作类型。
    /// 例如：tine_up、tine_down、load、unload等。
    /// </summary>
    public string actionType { get; set; } = "";

    /// <summary>
    /// 动作描述。
    /// </summary>
    public string actionDescription { get; set; } = "";

    /// <summary>
    /// 阻塞类型。
    /// 对应VDA5050 blockingType。
    /// 常见值可按项目定义为 NONE、SOFT、HARD。
    /// </summary>
    public string blockingType { get; set; } = "";

    /// <summary>
    /// 动作参数列表。
    /// </summary>
    public List<AgvActionParameterModel> actionParameters { get; set; } = [];
}

public class AgvActionParameterModel
{
    /// <summary>
    /// 参数名称。
    /// </summary>
    public string key { get; set; } = "";

    /// <summary>
    /// 参数值。
    /// 可能是字符串、数字、布尔值等。
    /// </summary>
    public object? value { get; set; }
}

public class AgvActionStateModel
{
    /// <summary>
    /// 动作ID。
    /// 需要和AgvActionModel.actionId对应。
    /// </summary>
    public string actionId { get; set; } = "";

    /// <summary>
    /// 动作类型。
    /// </summary>
    public string actionType { get; set; } = "";

    /// <summary>
    /// 动作描述。
    /// </summary>
    public string actionDescription { get; set; } = "";

    /// <summary>
    /// 动作执行状态。
    /// 常见值可定义为：WAITING、INITIALIZING、RUNNING、FINISHED、FAILED。
    /// </summary>
    public string actionStatus { get; set; } = "";

    /// <summary>
    /// 动作结果描述。
    /// 例如失败原因、完成说明等。
    /// </summary>
    public string? resultDescription { get; set; }
}

/// <summary>
/// VDA5050 state.errors 项。
/// </summary>
public class AgvVdaErrorModel
{
    public string errorType { get; set; } = string.Empty;

    public string errorLevel { get; set; } = "WARNING";

    public string errorDescription { get; set; } = string.Empty;

    public List<AgvVdaErrorReferenceModel> errorReferences { get; set; } = [];
}

public class AgvVdaErrorReferenceModel
{
    public string referenceKey { get; set; } = string.Empty;

    public string referenceValue { get; set; } = string.Empty;
}

public class AgvFactSheetConfigModel
{
    /// <summary>
    /// AGV运动学类型。
    /// 用于Factsheet typeSpecification.agvKinematic。
    /// 具体枚举值可按项目或VDA5050约定解释。
    /// </summary>
    public int AgvKinematic { get; set; } = 0;

    /// <summary>
    /// AGV类别。
    /// 用于Factsheet typeSpecification.agvClass。
    /// 具体枚举值可按项目或VDA5050约定解释。
    /// </summary>
    public int AgvClass { get; set; } = 0;

    /// <summary>
    /// 最大载重，单位建议kg。
    /// 用于Factsheet typeSpecification.maxLoadMass。
    /// </summary>
    public int MaxLoadMass { get; set; } = 0;

    /// <summary>
    /// AGV最小速度，单位m/s。
    /// 用于Factsheet physicalParameters.speedMin。
    /// </summary>
    public double SpeedMin { get; set; } = 0.0;

    /// <summary>
    /// AGV最大速度，单位m/s。
    /// 用于Factsheet physicalParameters.speedMax。
    /// </summary>
    public double SpeedMax { get; set; } = 1.0;

    /// <summary>
    /// 最大加速度，单位m/s²。
    /// 用于Factsheet physicalParameters.accelerationMax。
    /// </summary>
    public double AccelerationMax { get; set; } = 0.5;

    /// <summary>
    /// 最大减速度，单位m/s²。
    /// 用于Factsheet physicalParameters.decelerationMax。
    /// </summary>
    public double DecelerationMax { get; set; } = 0.5;

    /// <summary>
    /// 车体长度，单位m。
    /// 用于Factsheet physicalParameters.length。
    /// </summary>
    public double Length { get; set; } = 1.2;

    /// <summary>
    /// 车体宽度，单位m。
    /// 用于Factsheet physicalParameters.width。
    /// </summary>
    public double Width { get; set; } = 0.8;

    /// <summary>
    /// 最小高度，单位m。
    /// 用于Factsheet physicalParameters.heightMin。
    /// </summary>
    public double HeightMin { get; set; } = 0.3;

    /// <summary>
    /// 最大高度，单位m。
    /// 用于Factsheet physicalParameters.heightMax。
    /// </summary>
    public double HeightMax { get; set; } = 0.5;

    /// <summary>
    /// AGV车体轮廓点。
    /// 可用于Factsheet agvGeometry.envelopes或自定义footprint。
    /// </summary>
    public List<AgvFootprintPointModel> AgvFootprint { get; set; } =
    [
        new() { X = 0.6, Y = 0.4 },
        new() { X = 0.6, Y = -0.4 },
        new() { X = -0.6, Y = -0.4 },
        new() { X = -0.6, Y = 0.4 }
    ];
}

public class AgvRouteRuntimeModel
{
    private List<VdaOrderEdgeModel> _edges = [];
    private volatile bool _edgesAreSorted;

    public string OrderId { get; set; } = string.Empty;

    public int OrderUpdateId { get; set; }

    public List<VdaOrderNodeModel> Nodes { get; set; } = [];

    public List<VdaOrderEdgeModel> Edges
    {
        get
        {
            // 对外仍提供可编辑的列表，外部访问后按普通列表查找，兼容原地修改。
            _edgesAreSorted = false;
            return _edges;
        }
        set
        {
            _edges = value;
            _edgesAreSorted = false;
        }
    }

    internal List<VdaOrderEdgeModel> RouteEdges => _edges;

    internal void SetSortedEdges(List<VdaOrderEdgeModel> edges)
    {
        _edges = edges;
        _edgesAreSorted = true;
    }

    // 返回排序路线中第一个序号不小于给定值的边。外部直接赋值的路线从头扫描。
    internal int GetFirstEdgeIndex(int sequenceId)
    {
        var start = 0;
        var end = _edges.Count;
        while (start < end)
        {
            var middle = start + (end - start) / 2;
            if (_edges[middle].sequenceId < sequenceId)
            {
                start = middle + 1;
            }
            else
            {
                end = middle;
            }
        }

        return start;
    }

    internal bool EdgesAreSorted => _edgesAreSorted;

    public int TargetNodeIndex { get; set; }

    public bool IsCompleted { get; set; }

    public bool IsRotating { get; set; }

    public VdaOrderNodeModel? RotatingNode { get; set; }

    public double RotateTargetAngleDegree { get; set; }

    public RotateMode RotateMode { get; set; } = RotateMode.None;

    public HashSet<int> FinishedEdgeActionSequenceIds { get; set; } = [];
    public AgvTurnHelper.TurnDirection RotateDirection { get; set; } = AgvTurnHelper.TurnDirection.NoTurn;
}

/// <summary>
/// 旋转模式。
/// </summary>
public enum RotateMode
{
    None = 0,

    BeforeMove = 1,

    AfterArrive = 2,

    EdgeAction = 3
}

public class AgvFootprintPointModel
{
    /// <summary>
    /// 轮廓点X坐标，单位m。
    /// 通常以车体中心为原点。
    /// </summary>
    public double X { get; set; }

    /// <summary>
    /// 轮廓点Y坐标，单位m。
    /// 通常以车体中心为原点。
    /// </summary>
    public double Y { get; set; }
}
