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
/// 显示共享布局在当前块的工作行，并可单独编辑原有的本块自定义点位。
/// 同时支持读取当前运行中的虚拟PLC内存值，并允许直接写入修改。
/// </summary>
public partial class PlcDbBlockProtocolBoardConfigWindow : INotifyPropertyChanged
{
    private readonly int _plcId;

    private readonly PlcDbBlockModel _targetDbBlock;

    private readonly DispatcherTimer _refreshTimer;

    private bool _isRefreshing;

    private readonly ObservableCollection<PlcProtocolBoardRowModel> _localRows = [];
    private bool _openedLocalEditor;

    public bool IsEditingLocal { get; private set; }

    public int PlcId => _plcId;

    public int DbNumber => _targetDbBlock?.DbNumber ?? 0;

    public ObservableCollection<PlcProtocolBoardRowModel> ProtocolRows { get; private set; } = [];

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
        : "PLC未运行：可查看协议及编辑自定义项";

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
        foreach (var item in PlcProtocolTemplate.Basic.GetLocalItems(_targetDbBlock.ProtocolItems))
        {
            _localRows.Add(PlcProtocolBoardRowModel.FromProtocolItem(item));
        }
        ShowRows(false);
    }

    private ObservableCollection<PlcProtocolBoardRowModel> BuildWorkingRows()
    {
        var settings = _localRows.Select(row => row.ToProtocolItem());
        var effective = PlcProtocolTemplate.Basic.GetEffectiveItems(_targetDbBlock, settings);
        return new ObservableCollection<PlcProtocolBoardRowModel>(effective.Select(PlcProtocolBoardRowModel.FromProtocolItem));
    }

    private void ShowRows(bool local)
    {
        IsEditingLocal = local;
        ProtocolRows = local ? _localRows : BuildWorkingRows();
        foreach (var column in new DataGridColumn[] { EnabledColumn, IdColumn, NameColumn, DataTypeColumn, AddressColumn, RemarkColumn })
        {
            column.IsReadOnly = !local;
        }
        OnPropertyChanged(nameof(IsEditingLocal));
        OnPropertyChanged(nameof(ProtocolRows));
        SelectedProtocolRow = ProtocolRows.FirstOrDefault();
        ModeHintText.Text = local
            ? "这里仅保存本块的自定义项。与共享项同名、同偏移的设置在本块优先。"
            : "当前值来自所选 PLC / DB。超出本块长度的共享项显示为停用。";
    }

    private void LocalModeBox_Click(object sender, RoutedEventArgs e)
    {
        if (!ProtocolDataGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !ProtocolDataGrid.CommitEdit(DataGridEditingUnit.Row, true) ||
            (IsEditingLocal && !ValidateProtocolRows()))
        {
            LocalModeBox.IsChecked = IsEditingLocal;
            return;
        }
        var local = LocalModeBox.IsChecked == true;
        if (local) _openedLocalEditor = true;
        ShowRows(local);
        RefreshValues();
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
            panel = new PlcDbBlockProtocolPanelWindow(PlcId, DbNumber, IsEditingLocal ? BuildWorkingRows() : ProtocolRows)
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
        if (!IsEditingLocal) return;
        var nextId = GetNextProtocolItemId();

        var row = new PlcProtocolBoardRowModel
        {
            ProtocolItemId = nextId,
            IsEnabled = true,
            SignalName = $"信号-{nextId}",
            DataType = "Bool",
            OffsetAddress = "DBX0.0",
            Remark = string.Empty
        };

        ProtocolRows.Add(row);

        SelectedProtocolRow = row;
    }

    /// <summary>供已有调用方枚举内置的双向工站协议。</summary>
    public static void AppendBasicProtocolTemplate(Action<string, string, string, string> addItem)
    {
        if (addItem == null) throw new ArgumentNullException(nameof(addItem));
        PlcProtocolTemplate.Basic.AppendDefinitions(addItem);
    }

    private void RemoveProtocolItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsEditingLocal) return;
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
        if (!IsEditingLocal) return;
        if (ProtocolRows.Count == 0)
        {
            return;
        }

        var result = MessageBox.Show(
            "确定清空当前 DB 块的自定义项，恢复共享协议的默认设置吗？",
            "清空自定义项",
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
        if (!ProtocolDataGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !ProtocolDataGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            return;
        }

        if (_openedLocalEditor)
        {
            if (!ValidateProtocolRows()) return;
            NormalizeProtocolRows();
            var local = _localRows.OrderBy(row => row.ProtocolItemId).Select(row => row.ToProtocolItem());
            var settings = PlcProtocolTemplate.Basic.GetLocalItems(local).ToList();
            _targetDbBlock.ProtocolItems ??= [];
            _targetDbBlock.ProtocolItems.Clear();
            foreach (var item in settings) _targetDbBlock.ProtocolItems.Add(item);
        }

        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private bool ValidateProtocolRows()
    {
        var duplicateId = _localRows
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

        foreach (var row in _localRows.Where(x => x != null))
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
        var usedIds = _localRows
            .Where(x => x != null)
            .Where(x => x.ProtocolItemId > 0)
            .GroupBy(x => x.ProtocolItemId)
            .Where(x => x.Count() == 1)
            .Select(x => x.Key)
            .ToHashSet();

        var nextId = 1;

        foreach (var row in _localRows.Where(x => x != null).ToList())
        {
            if (row.ProtocolItemId <= 0 ||
                _localRows.Count(x => x != null && x.ProtocolItemId == row.ProtocolItemId) > 1)
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
        var maxId = _localRows
            .Where(x => x != null)
            .Select(x => x.ProtocolItemId)
            .DefaultIfEmpty(0)
            .Max();

        if (maxId == int.MaxValue)
        {
            var id = PlcProtocolTemplate.Basic.Count + 1;
            var used = _localRows.Select(row => row.ProtocolItemId).ToHashSet();
            while (used.Contains(id)) id++;
            return id;
        }
        return Math.Max(PlcProtocolTemplate.Basic.Count, maxId) + 1;
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
