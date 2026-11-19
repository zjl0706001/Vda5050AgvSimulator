#nullable disable

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Vda5050AgvSimulator.Models.Plc;

public enum PlcConnectionMode
{
    PortProxy = 1,
    Direct = 2
}

/// <summary>
/// PLC配置模型。
/// 一条记录代表一台PLC。
/// </summary>
public class PlcConfigModel : INotifyPropertyChanged
{
    private PlcConnectionMode? _connectionMode;
    private int? _directListenPort;

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
    /// 全局PLC复位配置。
    /// 所有PLC、所有DB块共用同一份复位模板；执行复位时，地址会按当前目标DB块解析。
    ///
    /// 说明：
    /// 1. 该字段会随PLC配置一起持久化，PlcStoreService 会负责在各PLC配置之间同步。
    /// 2. PlcDbBlockModel.ResetItems 仅保留用于兼容旧配置，运行时不再直接读取DB块自己的复位列表。
    /// </summary>
    public ObservableCollection<PlcDbBlockResetItemModel> GlobalResetItems { get; set; } = [];

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

    /// <summary>
    /// 旧配置没有连接方式时，按上一版直连地址判断。
    /// </summary>
    public PlcConnectionMode ConnectionMode
    {
        get => _connectionMode ?? (string.IsNullOrWhiteSpace(DirectListenAddress)
            ? PlcConnectionMode.PortProxy
            : PlcConnectionMode.Direct);
        set
        {
            _connectionMode = value == PlcConnectionMode.Direct
                ? PlcConnectionMode.Direct
                : PlcConnectionMode.PortProxy;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 直连方式绑定的本机 IPv4 地址。
    /// </summary>
    public string DirectListenAddress
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            if (!_connectionMode.HasValue)
            {
                OnPropertyChanged(nameof(ConnectionMode));
            }
        }
    } = string.Empty;

    /// <summary>
    /// 直连监听端口。旧配置沿用原有 Port 作为初始值。
    /// </summary>
    public int DirectListenPort
    {
        get => _directListenPort ?? Port;
        set
        {
            _directListenPort = value;
            OnPropertyChanged();
        }
    }

    public int GetListeningPort()
    {
        return ConnectionMode == PlcConnectionMode.Direct ? DirectListenPort : Port;
    }

    internal IPEndPoint GetListeningEndPoint()
    {
        var port = GetListeningPort();
        if (port <= 0 || port > 65535)
        {
            return null;
        }

        return ConnectionMode == PlcConnectionMode.Direct
            ? ParseIpv4EndPoint(DirectListenAddress, port)
            : new IPEndPoint(IPAddress.Loopback, port);
    }

    internal IPEndPoint GetProxyEndPoint()
    {
        return ParseIpv4EndPoint(IpAddress, ProxyPort > 0 ? ProxyPort : 102);
    }

    private static IPEndPoint ParseIpv4EndPoint(string address, int port)
    {
        return port > 0 && port <= 65535 &&
               IPAddress.TryParse(address?.Trim(), out var ip) &&
               ip.AddressFamily == AddressFamily.InterNetwork
            ? new IPEndPoint(ip, port)
            : null;
    }

    /// <summary>
    /// 本机回环地址上的仿真监听端口。每台启用的仿真 PLC 使用独立端口。
    /// </summary>
    public int Port
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            if (!_directListenPort.HasValue)
            {
                OnPropertyChanged(nameof(DirectListenPort));
            }
        }
    } = 1101;

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
            DirectListenAddress = DirectListenAddress,
            DirectListenPort = DirectListenPort,
            ConnectionMode = ConnectionMode,
            Port = Port,
            ProxyPort = ProxyPort,
            Rack = Rack,
            Slot = Slot,
            ConnectTimeOut = ConnectTimeOut,
            ReceiveTimeOut = ReceiveTimeOut,
            PollIntervalMs = PollIntervalMs,
            AutoWriteEnabled = AutoWriteEnabled,
            AutoWriteRules = new ObservableCollection<PlcAutoWriteRuleModel>(
                AutoWriteRules == null
                    ? []
                    : AutoWriteRules.Where(x => x != null).Select(x => x.Clone())),
            GlobalResetItems = new ObservableCollection<PlcDbBlockResetItemModel>(
                GlobalResetItems == null
                    ? []
                    : GlobalResetItems.Where(x => x != null).Select(x => x.Clone())),
            DbBlocks = new ObservableCollection<PlcDbBlockModel>(
                DbBlocks == null
                    ? []
                    : DbBlocks.Where(x => x != null).Select(x => x.Clone()))
        };
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
