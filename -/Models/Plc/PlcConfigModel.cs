#nullable disable

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Vda5050AgvSimulator.Models.Plc;

/// <summary>
/// PLC配置模型。
/// 一条记录代表一台PLC。
/// </summary>
public class PlcConfigModel : INotifyPropertyChanged
{
    public int PlcId
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = 1;

    public string PlcName
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "PLC-1";

    /// <summary>
    /// 虚拟PLC内部DB块列表。
    /// 例如 DB1500、DB2090。
    /// </summary>
    public ObservableCollection<PlcDbBlockModel> DbBlocks { get; set; } = [];

    /// <summary>
    /// 是否启用该PLC。
    /// </summary>
    public bool IsEnabled
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = true;

    public string PlcType
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "S1200";

    public string IpAddress
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "127.0.0.1";

    public int Port
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = 102;

    /// <summary>
    /// 外部代理端口。
    /// RCS / 上位机实际连接的端口，例如 102。
    /// </summary>
    public int ProxyPort
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = 102;

    public int Rack
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = 0;

    public int Slot
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = 2;

    public int ConnectTimeOut
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = 3000;

    public int ReceiveTimeOut
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = 3000;

    /// <summary>
    /// 自动写入轮询间隔。
    /// </summary>
    public int PollIntervalMs
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = 1000;

    /// <summary>
    /// 是否启用该PLC的自动写入功能。
    /// 注意：PLC勾选 + 该字段勾选 + 规则勾选，三者都满足才执行。
    /// </summary>
    public bool AutoWriteEnabled
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = true;

    /// <summary>
    /// 自动写入规则列表。
    /// </summary>
    public ObservableCollection<PlcAutoWriteRuleModel> AutoWriteRules { get; set; } = [];

    public PlcConfigModel Clone()
    {
        return new PlcConfigModel
        {
            PlcId = PlcId,
            PlcName = PlcName,
            IsEnabled = IsEnabled,
            PlcType = PlcType,
            IpAddress = IpAddress,
            Port = Port,
            ProxyPort = ProxyPort,
            Rack = Rack,
            Slot = Slot,
            ConnectTimeOut = ConnectTimeOut,
            ReceiveTimeOut = ReceiveTimeOut,
            PollIntervalMs = PollIntervalMs,
            AutoWriteEnabled = AutoWriteEnabled,
            AutoWriteRules = new ObservableCollection<PlcAutoWriteRuleModel>(
                AutoWriteRules.Select(x => x.Clone())),
            DbBlocks = new ObservableCollection<PlcDbBlockModel>(
                DbBlocks == null
                    ? []
                    : DbBlocks.Select(x => x.Clone()))
        };
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}