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
                RefreshRowValue(row);
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void RefreshRowValue(PlcProtocolBoardRowModel row)
    {
        if (row == null)
        {
            return;
        }

        if (!row.IsEnabled)
        {
            row.CurrentValue = string.Empty;
            row.LastMessage = "未启用";
            return;
        }

        if (!IsPlcRunning)
        {
            row.CurrentValue = string.Empty;
            row.LastMessage = "PLC未运行";
            return;
        }

        var protocolItem = row.ToProtocolItem();

        if (!PlcVirtualServerService.Instance.TryReadProtocolItemValue(
                _plcId,
                DbNumber,
                protocolItem,
                out var actualAddress,
                out var valueText,
                out var message))
        {
            row.ActualAddress = actualAddress;
            row.CurrentValue = string.Empty;
            row.LastMessage = "读取失败：" + message;
            return;
        }

        row.ActualAddress = actualAddress;
        row.CurrentValue = valueText;

        if (string.IsNullOrWhiteSpace(row.WriteValue))
        {
            row.WriteValue = valueText;
        }

        row.LastMessage = "读取成功";
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
        ProtocolDataGrid?.CommitEdit(DataGridEditingUnit.Cell, true);
        ProtocolDataGrid?.CommitEdit(DataGridEditingUnit.Row, true);

        if (row == null)
        {
            MessageBox.Show("请先选择要写入的协议项。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!row.IsEnabled)
        {
            row.LastMessage = "未启用，不能写入";
            return;
        }

        if (!IsPlcRunning)
        {
            row.LastMessage = "PLC未运行，不能写入";
            MessageBox.Show("当前PLC模拟服务未运行，不能写入协议值。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var protocolItem = row.ToProtocolItem();

        if (!PlcVirtualServerService.Instance.TryWriteProtocolItemValue(
                _plcId,
                DbNumber,
                protocolItem,
                row.WriteValue,
                out var actualAddress,
                out var message))
        {
            row.ActualAddress = actualAddress;
            row.LastMessage = "写入失败：" + message;
            MessageBox.Show(row.LastMessage, "写入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        row.ActualAddress = actualAddress;
        row.LastMessage = "写入成功";

        RefreshRowValue(row);
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
            CurrentValue = string.Empty,
            WriteValue = string.Empty,
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

        AddTemplateItem("LifeBit", "Bool", "DB{DB}.DBX0.0", "系统健康");
        AddTemplateItem("Offline", "Bool", "DB{DB}.DBX0.1", "离线模式");
        AddTemplateItem("Online", "Bool", "DB{DB}.DBX0.2", "在线模式");
        AddTemplateItem("Alarm", "Bool", "DB{DB}.DBX0.3", "故障");
        AddTemplateItem("EmergencyStop", "Bool", "DB{DB}.DBX0.4", "急停");
        AddTemplateItem("EnterRequest", "Bool", "DB{DB}.DBX0.5", "请求进入");

        SelectedProtocolRow = ProtocolRows.LastOrDefault();

        RefreshValues();
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
            if (row == null)
            {
                continue;
            }

            _targetDbBlock.ProtocolItems.Add(row.ToProtocolItem());
        }

        DialogResult = true;

        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;

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
}