#nullable disable

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Vda5050AgvSimulator.Models.Plc;
using Vda5050AgvSimulator.Services.Plc;

namespace Vda5050AgvSimulator.Views;

/// <summary>
/// DB块协议看板配置窗口。
/// 每个DB块维护自己的协议项列表，用于配置“名称 / 数据类型 / 偏移量地址 / 注释”。
/// 同时支持读取当前运行中的虚拟PLC内存值，并允许直接写入修改。
/// </summary>
public partial class PlcDbBlockProtocolBoardConfigWindow : INotifyPropertyChanged
{
    private readonly int _plcId;

    private readonly PlcDbBlockModel _targetDbBlock;

    private readonly DispatcherTimer _refreshTimer;

    private bool _isRefreshing;

    public int PlcId => _plcId;

    public int DbNumber => _targetDbBlock?.DbNumber ?? 0;

    public ObservableCollection<PlcProtocolBoardRowModel> ProtocolRows { get; } = [];

    public string[] DataTypes { get; } =
    [
        "Bool",
        "Byte",
        "Word",
        "Int",
        "DInt",
        "Real",
        "String",
        "String[40]",
        "ByteArray",
        "ByteArray[15]"
    ];

    public bool IsPlcRunning => PlcVirtualServerService.Instance.IsPlcRunning(_plcId);

    public string RunningStatusText => IsPlcRunning
        ? "PLC运行中：可读取/写入当前值"
        : "PLC未运行：只能编辑协议配置，不能读取/写入值";

    public Brush RunningStatusForeground => IsPlcRunning
        ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
        : new SolidColorBrush(Color.FromRgb(239, 68, 68));

    public bool AutoRefreshEnabled
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();

            if (_refreshTimer == null)
            {
                return;
            }

            if (field)
            {
                _refreshTimer.Start();
                RefreshValues();
                return;
            }

            _refreshTimer.Stop();
        }
    } = true;

    public PlcProtocolBoardRowModel SelectedProtocolRow
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public PlcDbBlockProtocolBoardConfigWindow(int plcId, PlcDbBlockModel dbBlock)
    {
        if (dbBlock == null)
        {
            throw new ArgumentNullException(nameof(dbBlock));
        }

        _plcId = plcId;
        _targetDbBlock = dbBlock;

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        _refreshTimer.Tick += (_, _) => RefreshValues();

        InitializeComponent();

        DataContext = this;

        LoadFromTarget();

        Loaded += (_, _) =>
        {
            RefreshValues();

            if (AutoRefreshEnabled)
            {
                _refreshTimer.Start();
            }
        };

        Closed += (_, _) => _refreshTimer.Stop();
    }

    private void LoadFromTarget()
    {
        ProtocolRows.Clear();

        _targetDbBlock.ProtocolItems ??= [];

        foreach (var protocolItem in _targetDbBlock.ProtocolItems)
        {
            if (protocolItem == null)
            {
                continue;
            }

            ProtocolRows.Add(PlcProtocolBoardRowModel.FromProtocolItem(protocolItem));
        }

        NormalizeProtocolRows();

        SelectedProtocolRow = ProtocolRows.FirstOrDefault();
    }

    private void RefreshValuesButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshValues();
    }

    private void OpenOperationPanelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ProtocolDataGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !ProtocolDataGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            return;
        }

        var panel = Application.Current.Windows.OfType<PlcDbBlockProtocolPanelWindow>()
            .FirstOrDefault(window => window.PlcId == PlcId && window.DbNumber == DbNumber);

        if (panel == null)
        {
            panel = new PlcDbBlockProtocolPanelWindow(PlcId, DbNumber, ProtocolRows)
            {
                Topmost = this.Topmost
            };
            panel.Show();
            return;
        }

        panel.SelectInitialTarget();
        if (panel.WindowState == WindowState.Minimized)
        {
            panel.WindowState = WindowState.Normal;
        }

        panel.Activate();
    }

    private void RefreshValues()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;

        try
        {
            OnPropertyChanged(nameof(IsPlcRunning));
            OnPropertyChanged(nameof(RunningStatusText));
            OnPropertyChanged(nameof(RunningStatusForeground));

            foreach (var row in ProtocolRows.Where(x => x != null))
            {
                PlcProtocolBoardValueService.TryRefreshRowValue(PlcId, DbNumber, row);
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void WriteSelectedValueButton_Click(object sender, RoutedEventArgs e)
    {
        WriteRowValue(SelectedProtocolRow);
    }

    private void WriteRowValueButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlcProtocolBoardRowModel row })
        {
            WriteRowValue(row);
        }
    }

    private void WriteRowValue(PlcProtocolBoardRowModel row)
    {
        if (!ProtocolDataGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !ProtocolDataGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            return;
        }

        if (row == null)
        {
            MessageBox.Show("请先选择要写入的协议项。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!PlcProtocolBoardValueService.TryWriteRowValue(PlcId, DbNumber, row) && row.IsEnabled)
        {
            MessageBox.Show(row.LastMessage, "写入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddProtocolItemButton_Click(object sender, RoutedEventArgs e)
    {
        var nextId = GetNextProtocolItemId();

        var row = new PlcProtocolBoardRowModel
        {
            ProtocolItemId = nextId,
            IsEnabled = true,
            SignalName = $"信号-{nextId}",
            DataType = "Bool",
            OffsetAddress = "DB{DB}.DBX0.0",
            Remark = string.Empty
        };

        ProtocolRows.Add(row);

        SelectedProtocolRow = row;
    }

    private void ImportBasicTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProtocolRows.Count > 0)
        {
            var result = MessageBox.Show(
                "导入基础模板会追加协议项，不会自动删除已有配置。是否继续？",
                "导入基础模板",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        AppendBasicProtocolTemplate(AddTemplateItem);

        SelectedProtocolRow = ProtocolRows.LastOrDefault();

        RefreshValues();
    }

    /// <summary>
    /// 公共基础协议模板定义，单DB窗口和PLC批量导入使用同一份模板。
    /// 由调用方决定去重和写入的目标，避免重复维护两套协议项。
    /// </summary>
    public static void AppendBasicProtocolTemplate(Action<string, string, string, string> addItem)
    {
        if (addItem == null)
        {
            throw new ArgumentNullException(nameof(addItem));
        }

        #region AGV_TO_PLC

        addItem("LifeBit", "Bool", "DB{DB}.DBX0.0", "系统健康");
        addItem("Offline", "Bool", "DB{DB}.DBX0.1", "离线模式");
        addItem("Online", "Bool", "DB{DB}.DBX0.2", "在线模式");
        addItem("Alarm", "Bool", "DB{DB}.DBX0.3", "故障");
        addItem("EmergencyStop", "Bool", "DB{DB}.DBX0.4", "急停");
        addItem("EnterRequest", "Bool", "DB{DB}.DBX0.5", "请求进入");
        addItem("Entering", "Bool", "DB{DB}.DBX0.6", "正在进入");
        addItem("ReachedPosition", "Bool", "DB{DB}.DBX0.7", "小车到位");
        addItem("Leaving", "Bool", "DB{DB}.DBX1.0", "正在离开");
        addItem("BindComplete", "Bool", "DB{DB}.DBX1.1", "绑定完成");
        addItem("UnbindComplete", "Bool", "DB{DB}.DBX1.2", "解绑完成");
        addItem("WritePackID1Done", "Bool", "DB{DB}.DBX1.3", "写入PackID1完成");
        addItem("WriteGuobiaoPackID1Done", "Bool", "DB{DB}.DBX1.4", "写入PackID1_12位码完成");
        addItem("WritePackID2Done", "Bool", "DB{DB}.DBX1.5", "写入PackID2完成");
        addItem("WriteGuobiaoPackID2Done", "Bool", "DB{DB}.DBX1.6", "写入PackID2_12位码完成");

        addItem("AGVID", "Byte", "DB{DB}.DBB2", "AGV小车编号");
        addItem("ErrorCode", "Byte", "DB{DB}.DBB3", "报错代码");

        addItem("PACKLayers", "Int", "DB{DB}.DBW4", "pack层数：1：第一层；2：第二层；3：第三层");
        addItem("AGVModuleType", "Int", "DB{DB}.DBW6",
            "AGV到达公共区域，到位后，请求进站时，发送CheckAGVModuleType信号和AGVModuleType（当前模组类型）进行校验");

        addItem("PackID1", "String[40]", "DB{DB}.DBX8", "Pack码1");
        addItem("ShippingCode1", "String[40]", "DB{DB}.DBX50", "传输码1");
        addItem("PackID2", "String[40]", "DB{DB}.DBX92", "Pack码2");
        addItem("ShippingCode2", "String[40]", "DB{DB}.DBX134", "传输码2");

        addItem("UPComplete", "Bool", "DB{DB}.DBX176.0", "上升完成");
        addItem("DownComplete", "Bool", "DB{DB}.DBX176.1", "下降完成");

        addItem("Block1LimitpieceStatus", "Int", "DB{DB}.DBW178", "Block1限位片状态");
        addItem("Block2LimitpieceStatus", "Int", "DB{DB}.DBW180", "Block2限位片状态");

        addItem("AGVBodyAmount", "Byte", "DB{DB}.DBB177", "AGV载体数量，例如涂胶站需要等待入箱模组小车数量，备用，自行修改数据类型");

        #endregion

        #region PLC_TO_AGV

        addItem("LifeBit", "Bool", "DB{DB}.DBX200.0", "系统健康");
        addItem("Offline", "Bool", "DB{DB}.DBX200.1", "离线模式（非自动状态）");
        addItem("Online", "Bool", "DB{DB}.DBX200.2", "在线模式（自动状态）");
        addItem("Alarm", "Bool", "DB{DB}.DBX200.3", "故障");
        addItem("EmergencyStopOK", "Bool", "DB{DB}.DBX200.4", "急停");
        addItem("EnterInterlock", "Bool", "DB{DB}.DBX200.5", "进入互锁");
        addItem("EnterAllowed", "Bool", "DB{DB}.DBX200.6", "允许进入");
        addItem("LeaveInterlock", "Bool", "DB{DB}.DBX200.7", "离开互锁");
        addItem("LeaveRequest", "Bool", "DB{DB}.DBX201.0", "请求离开");
        addItem("Bypass", "Bool", "DB{DB}.DBX201.1", "屏蔽");
        addItem("RequestWritePackID1", "Bool", "DB{DB}.DBX201.2", "请求写入Pack码1");
        addItem("RequestWritePackID1", "Bool", "DB{DB}.DBX201.3", "请求写入传输码1");
        addItem("RequestWritePackID1", "Bool", "DB{DB}.DBX201.4", "请求写入Pack码2");
        addItem("RequestWritePackID1", "Bool", "DB{DB}.DBX201.5", "请求写入传输码2");
        addItem("Route", "Int", "DB{DB}.DBW202", "AGV小车执行的路径");

        addItem("PACKLayers", "Int", "DB{DB}.DBW206", "箱体上线工站扫码箱体后，上传MES，记录层数：1：第一层；2：第二层；3：第三层");
        addItem("CheckAGVModuleType", "Int", "DB{DB}.DBW208", "入箱设备判断箱体类型，输出模组小车类型：1：模组小车类型1；2：模组小车类型2");

        addItem("PackID1", "String[40]", "DB{DB}.DBX210", "Pack码1：24位码");
        addItem("ShippingCode1", "String[40]", "DB{DB}.DBX252", "传输码1：12位码（总成码）或12位码（PN）");
        addItem("PackID2", "String[40]", "DB{DB}.DBX294", "Pack码1：24位码");
        addItem("ShippingCode2", "String[40]", "DB{DB}.DBX336", "传输码2：12位码（总成码）或12位码（PN）");

        addItem("RequestUP", "Bool", "DB{DB}.DBX378.0", "请求上升");
        addItem("RequestDOWN", "Bool", "DB{DB}.DBX378.1", "请求下降");
        addItem("Clampingposition", "Bool", "DB{DB}.DBX378.2", "AGV夹紧位置");
        addItem("HeatStationRequestEmptyPallet", "Bool", "DB{DB}.DBX378.3", "加热请求回流空车");
        addItem("HeatStationErr", "Bool", "DB{DB}.DBX378.4", "加热库故障");

        addItem("Block1LimitpieceStatus", "Int", "DB{DB}.DBW380", "Block1限位片状态");
        addItem("Block2LimitpieceStatus", "Int", "DB{DB}.DBW382", "Block2限位片状态");
        addItem("HeatCycleAgvAmount", "Int", "DB{DB}.DBW384", "加热回流小车数量");

        #endregion

    }

    private void AddTemplateItem(string signalName, string dataType, string offsetAddress, string remark)
    {
        var exists = ProtocolRows.Any(x =>
            x != null &&
            string.Equals(x.SignalName?.Trim(), signalName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.OffsetAddress?.Trim(), offsetAddress, StringComparison.OrdinalIgnoreCase));

        if (exists)
        {
            return;
        }

        ProtocolRows.Add(new PlcProtocolBoardRowModel
        {
            ProtocolItemId = GetNextProtocolItemId(),
            IsEnabled = true,
            SignalName = signalName,
            DataType = dataType,
            OffsetAddress = offsetAddress,
            Remark = remark
        });
    }

    private void RemoveProtocolItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProtocolRow == null)
        {
            MessageBox.Show("请先选择要删除的协议项。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            $"确定删除协议项吗？\r\n{SelectedProtocolRow.SignalName}",
            "删除协议项",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var index = ProtocolRows.IndexOf(SelectedProtocolRow);

        ProtocolRows.Remove(SelectedProtocolRow);

        SelectedProtocolRow = ProtocolRows.Count == 0
            ? null
            : ProtocolRows[Math.Min(index, ProtocolRows.Count - 1)];
    }

    private void ClearProtocolItemsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProtocolRows.Count == 0)
        {
            return;
        }

        var result = MessageBox.Show(
            "确定清空当前DB块的所有协议看板配置吗？",
            "清空协议看板配置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        ProtocolRows.Clear();

        SelectedProtocolRow = null;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        ProtocolDataGrid?.CommitEdit(DataGridEditingUnit.Cell, true);
        ProtocolDataGrid?.CommitEdit(DataGridEditingUnit.Row, true);

        if (!ValidateProtocolRows())
        {
            return;
        }

        NormalizeProtocolRows();

        _targetDbBlock.ProtocolItems ??= [];
        _targetDbBlock.ProtocolItems.Clear();

        foreach (var row in ProtocolRows.OrderBy(x => x.ProtocolItemId))
        {
            _targetDbBlock.ProtocolItems.Add(row.ToProtocolItem());
        }


        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private bool ValidateProtocolRows()
    {
        var duplicateId = ProtocolRows
            .Where(x => x != null)
            .Where(x => x.ProtocolItemId > 0)
            .GroupBy(x => x.ProtocolItemId)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicateId != null)
        {
            MessageBox.Show(
                $"协议项ID重复：{duplicateId.Key}",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        foreach (var row in ProtocolRows.Where(x => x != null))
        {
            if (row.ProtocolItemId <= 0)
            {
                row.ProtocolItemId = GetNextProtocolItemId();
            }

            if (string.IsNullOrWhiteSpace(row.SignalName))
            {
                row.SignalName = $"信号-{row.ProtocolItemId}";
            }

            if (string.IsNullOrWhiteSpace(row.DataType))
            {
                row.DataType = "Bool";
            }

            row.OffsetAddress = row.OffsetAddress?.Trim() ?? string.Empty;
            row.Remark ??= string.Empty;

            if (!row.IsEnabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.OffsetAddress))
            {
                MessageBox.Show(
                    $"启用的协议项偏移量地址不能为空：{row.SignalName}",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
        }

        return true;
    }

    private void NormalizeProtocolRows()
    {
        var usedIds = ProtocolRows
            .Where(x => x != null)
            .Where(x => x.ProtocolItemId > 0)
            .GroupBy(x => x.ProtocolItemId)
            .Where(x => x.Count() == 1)
            .Select(x => x.Key)
            .ToHashSet();

        var nextId = 1;

        foreach (var row in ProtocolRows.Where(x => x != null).ToList())
        {
            if (row.ProtocolItemId <= 0 ||
                ProtocolRows.Count(x => x != null && x.ProtocolItemId == row.ProtocolItemId) > 1)
            {
                while (usedIds.Contains(nextId))
                {
                    nextId++;
                }

                row.ProtocolItemId = nextId;
                usedIds.Add(nextId);
                nextId++;
            }

            if (string.IsNullOrWhiteSpace(row.SignalName))
            {
                row.SignalName = $"信号-{row.ProtocolItemId}";
            }

            if (string.IsNullOrWhiteSpace(row.DataType))
            {
                row.DataType = "Bool";
            }

            row.OffsetAddress = row.OffsetAddress?.Trim() ?? string.Empty;
            row.Remark ??= string.Empty;
        }
    }

    private int GetNextProtocolItemId()
    {
        var maxId = ProtocolRows
            .Where(x => x != null)
            .Select(x => x.ProtocolItemId)
            .DefaultIfEmpty(0)
            .Max();

        return maxId + 1;
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void TopmostButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;

        TopmostButton.Content = Topmost ? "取消置顶" : "置顶";
    }
}