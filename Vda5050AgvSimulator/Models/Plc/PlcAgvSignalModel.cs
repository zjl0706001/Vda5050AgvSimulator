#nullable disable

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Vda5050AgvSimulator.Models.Plc;

/// <summary>
/// AGV 与 PLC 的 DB1500 信号交互模型。
/// 
/// 左侧：AGV_TO_PLC，AGV 写给 PLC。
/// 右侧：PLC_TO_AGV，PLC 写给 AGV。
/// 
/// 注意：
/// Siemens String[40] 实际占用 42 字节：
/// 第 1 字节 = 最大长度
/// 第 2 字节 = 当前长度
/// 后 40 字节 = 字符内容
/// </summary>
public class PlcAgvSignalModel : INotifyPropertyChanged
{
    /// <summary>
    /// DB 块号，默认 DB1500。
    /// </summary>
    public int DbNumber
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AgvToPlc));
            OnPropertyChanged(nameof(PlcToAgv));
        }
    } = 1500;

    /// <summary>
    /// AGV 写给 PLC 的信号。
    /// </summary>
    public AgvToPlcSignalModel AgvToPlc { get; set; } = new();

    /// <summary>
    /// PLC 写给 AGV 的信号。
    /// </summary>
    public PlcToAgvSignalModel PlcToAgv { get; set; } = new();

    public event PropertyChangedEventHandler PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// AGV_TO_PLC：AGV 写入 PLC 的信号区。
/// 地址范围：DB1500.DBX0.0 起。
/// </summary>
public class AgvToPlcSignalModel : INotifyPropertyChanged
{
    [PlcAddress("DBX0.0", PlcDataType.Bool, "系统健康")]
    public bool LifeBit
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX0.1", PlcDataType.Bool, "离线模式")]
    public bool Offline
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX0.2", PlcDataType.Bool, "在线模式")]
    public bool Online
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX0.3", PlcDataType.Bool, "故障")]
    public bool Alarm
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX0.4", PlcDataType.Bool, "急停")]
    public bool EmergencyStop
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX0.5", PlcDataType.Bool, "请求进入")]
    public bool EnterRequest
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX0.6", PlcDataType.Bool, "正在进入")]
    public bool Entering
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX0.7", PlcDataType.Bool, "小车到位")]
    public bool ReachedPosition
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX1.0", PlcDataType.Bool, "正在离开")]
    public bool Leaving
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX1.1", PlcDataType.Bool, "绑定完成")]
    public bool BindComplete
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX1.2", PlcDataType.Bool, "解绑完成")]
    public bool UnbindComplete
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX1.3", PlcDataType.Bool, "写入PackID1完成")]
    public bool WritePackID1Done
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX1.4", PlcDataType.Bool, "写入PackID1_12位码完成")]
    public bool WriteGuobiaoPackID1Done
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX1.5", PlcDataType.Bool, "写入PackID2完成")]
    public bool WritePackID2Done
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX1.6", PlcDataType.Bool, "写入PackID2_12位码完成")]
    public bool WriteGuobiaoPackID2Done
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX1.7", PlcDataType.Bool, "备用")]
    public bool Spare6
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBB2", PlcDataType.Byte, "AGV小车编号")]
    public byte AGVID
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBB3", PlcDataType.Byte, "报错代码")]
    public byte ErrorCode
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBW4", PlcDataType.Int, "Pack层数：1第一层，2第二层，3第三层")]
    public short PACKLayers
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBW6", PlcDataType.Int, "AGV模组类型")]
    public short AGVModuleType
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX8.0", PlcDataType.String, "Pack码1", StringLength = 40)]
    public string PackID1
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    [PlcAddress("DBX50.0", PlcDataType.String, "传输码1", StringLength = 40)]
    public string ShippingCode1
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    [PlcAddress("DBX92.0", PlcDataType.String, "Pack码2", StringLength = 40)]
    public string PackID2
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    [PlcAddress("DBX134.0", PlcDataType.String, "传输码2", StringLength = 40)]
    public string ShippingCode2
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    [PlcAddress("DBX176.0", PlcDataType.Bool, "上升完成")]
    public bool UPComplete
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX176.1", PlcDataType.Bool, "下降完成")]
    public bool DownComplete
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX176.2", PlcDataType.Bool, "备用")]
    public bool Spare3
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX176.3", PlcDataType.Bool, "备用")]
    public bool Spare4
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX176.4", PlcDataType.Bool, "备用")]
    public bool Spare5
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX176.5", PlcDataType.Bool, "备用")]
    public bool Spare6_176_5
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX176.6", PlcDataType.Bool, "备用")]
    public bool Spare7
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX176.7", PlcDataType.Bool, "备用")]
    public bool Spare8
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBB177", PlcDataType.Byte, "备用")]
    public byte Spare9
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBW178", PlcDataType.Int, "Block1限位片状态")]
    public short Block1LimitpieceStatus
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBW180", PlcDataType.Int, "Block2限位片状态")]
    public short Block2LimitpieceStatus
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBW182", PlcDataType.Int, "备用")]
    public short SpareInt3
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX184.0", PlcDataType.ByteArray, "备用字节数组", ByteLength = 15)]
    public byte[] SpareByte
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = new byte[15];

    public event PropertyChangedEventHandler PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// PLC_TO_AGV：PLC 写入 AGV 的信号区。
/// 地址范围：DB1500.DBX200.0 起。
/// </summary>
public class PlcToAgvSignalModel : INotifyPropertyChanged
{
    [PlcAddress("DBX200.0", PlcDataType.Bool, "系统健康")]
    public bool LifeBit
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX200.1", PlcDataType.Bool, "离线模式，非自动状态")]
    public bool Offline
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX200.2", PlcDataType.Bool, "在线模式，自动状态")]
    public bool Online
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX200.3", PlcDataType.Bool, "故障")]
    public bool Alarm
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX200.4", PlcDataType.Bool, "急停正常")]
    public bool EmergencyStopOK
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX200.5", PlcDataType.Bool, "进入互锁")]
    public bool EnterInterlock
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX200.6", PlcDataType.Bool, "允许进入")]
    public bool EnterAllowed
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX200.7", PlcDataType.Bool, "离开互锁")]
    public bool LeaveInterlock
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX201.0", PlcDataType.Bool, "请求离开")]
    public bool LeaveRequest
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX201.1", PlcDataType.Bool, "屏蔽")]
    public bool Bypass
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX201.2", PlcDataType.Bool, "请求写入Pack码1/模组码1")]
    public bool RequestWritePackID1
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX201.3", PlcDataType.Bool, "请求写入传输码1")]
    public bool Spare2
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX201.4", PlcDataType.Bool, "请求写入Pack码2/模组码2")]
    public bool Spare3
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX201.5", PlcDataType.Bool, "请求写入传输码2")]
    public bool Spare4
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX201.6", PlcDataType.Bool, "备用")]
    public bool Spare5
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX201.7", PlcDataType.Bool, "备用")]
    public bool Spare6
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBW202", PlcDataType.Int, "AGV小车执行的路径")]
    public short Route
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBW204", PlcDataType.Int, "备用")]
    public short SpareInt1
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBW206", PlcDataType.Int, "Pack层数")]
    public short PACKLayers
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBW208", PlcDataType.Int, "检查AGV模组类型")]
    public short CheckAGVModuleType
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX210.0", PlcDataType.String, "Pack码1", StringLength = 40)]
    public string PackID1
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    [PlcAddress("DBX252.0", PlcDataType.String, "传输码1", StringLength = 40)]
    public string ShippingCode1
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    [PlcAddress("DBX294.0", PlcDataType.String, "Pack码2", StringLength = 40)]
    public string PackID2
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    [PlcAddress("DBX336.0", PlcDataType.String, "传输码2", StringLength = 40)]
    public string ShippingCode2
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    [PlcAddress("DBX378.0", PlcDataType.Bool, "请求上升")]
    public bool RequestUP
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX378.1", PlcDataType.Bool, "请求下降")]
    public bool RequestDOWN
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX378.2", PlcDataType.Bool, "AGV夹紧位置")]
    public bool Clampingposition
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX378.3", PlcDataType.Bool, "加热请求回流空车")]
    public bool HeatStationRequestEmptyPallet
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX378.4", PlcDataType.Bool, "加热库故障")]
    public bool HeatStationErr
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX378.5", PlcDataType.Bool, "备用")]
    public bool Spare6_378_5
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX378.6", PlcDataType.Bool, "备用")]
    public bool Spare7
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX378.7", PlcDataType.Bool, "备用")]
    public bool Spare8
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBB379", PlcDataType.Byte, "备用")]
    public byte Spare9
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBW380", PlcDataType.Int, "Block1限位片状态")]
    public short Block1LimitpieceStatus
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBW382", PlcDataType.Int, "Block2限位片状态")]
    public short Block2LimitpieceStatus
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBW384", PlcDataType.Int, "加热回流小车数量")]
    public short HeatCycleAgvAmount
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    [PlcAddress("DBX386.0", PlcDataType.ByteArray, "备用字节数组", ByteLength = 15)]
    public byte[] SpareByte
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = new byte[15];

    public event PropertyChangedEventHandler PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// PLC 点位地址标记。
/// 后续可以通过反射自动生成点位表、读写映射、界面表格。
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class PlcAddressAttribute : Attribute
{
    public PlcAddressAttribute(string address, PlcDataType dataType, string remark = "")
    {
        Address = address;
        DataType = dataType;
        Remark = remark;
    }

    /// <summary>
    /// 相对地址，例如 DBX0.0、DBW4、DBB2。
    /// DB块号由 PlcAgvSignalModel.DbNumber 统一决定。
    /// </summary>
    public string Address { get; }

    /// <summary>
    /// 数据类型。
    /// </summary>
    public PlcDataType DataType { get; }

    /// <summary>
    /// 备注。
    /// </summary>
    public string Remark { get; }

    /// <summary>
    /// Siemens String 长度。
    /// 例如 String[40]，这里填 40。
    /// </summary>
    public int StringLength { get; set; }

    /// <summary>
    /// 字节数组长度。
    /// 例如 Array[1..15] of Byte，填 15。
    /// </summary>
    public int ByteLength { get; set; }
}

/// <summary>
/// PLC 数据类型。
/// </summary>
public enum PlcDataType
{
    Bool,

    Byte,

    /// <summary>
    /// Siemens Int，2字节，有符号 short。
    /// </summary>
    Int,

    /// <summary>
    /// Siemens String[n]。
    /// 实际占用 n + 2 字节。
    /// </summary>
    String,

    ByteArray
}