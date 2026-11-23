#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Vda5050AgvSimulator.Models.Plc;
using Vda5050AgvSimulator.Services.Plc;

namespace Vda5050AgvSimulator.Views;

public partial class PlcPointChangeMonitorWindow : Window
{
    private const int SampleIntervalMs = 300;
    private const int DisplayedRecordLimit = 1000;

    private static readonly Dictionary<string, string> ChineseSignalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LifeBit"] = "系统健康",
        ["Offline"] = "离线模式",
        ["Online"] = "在线模式",
        ["Alarm"] = "故障",
        ["EmergencyStop"] = "急停",
        ["EnterRequest"] = "请求进入",
        ["Entering"] = "正在进入",
        ["ReachedPosition"] = "小车到位",
        ["Leaving"] = "正在离开",
        ["BindComplete"] = "绑定完成",
        ["UnbindComplete"] = "解绑完成",
        ["WritePackID1Done"] = "Pack码1写入完成",
        ["WriteGuobiaoPackID1Done"] = "12位码1写入完成",
        ["WritePackID2Done"] = "Pack码2写入完成",
        ["WriteGuobiaoPackID2Done"] = "12位码2写入完成",
        ["AGVID"] = "小车编号",
        ["ErrorCode"] = "报错代码",
        ["PACKLayers"] = "Pack层数",
        ["AGVModuleType"] = "小车模组类型",
        ["PackID1"] = "Pack码1",
        ["ShippingCode1"] = "传输码1",
        ["PackID2"] = "Pack码2",
        ["ShippingCode2"] = "传输码2",
        ["UPComplete"] = "上升完成",
        ["DownComplete"] = "下降完成",
        ["Block1LimitpieceStatus"] = "1号限位片状态",
        ["Block2LimitpieceStatus"] = "2号限位片状态",
        ["AGVBodyAmount"] = "小车载体数量",
        ["EmergencyStopOK"] = "急停正常",
        ["EnterInterlock"] = "进入互锁",
        ["EnterAllowed"] = "允许进入",
        ["LeaveInterlock"] = "离开互锁",
        ["LeaveRequest"] = "请求离开",
        ["Bypass"] = "屏蔽",
        ["RequestWritePackID1"] = "请求写入Pack码1",
        ["Route"] = "小车执行路径",
        ["CheckAGVModuleType"] = "检查小车模组类型",
        ["RequestUP"] = "请求上升",
        ["RequestDOWN"] = "请求下降",
        ["Clampingposition"] = "小车夹紧位置",
        ["HeatStationRequestEmptyPallet"] = "加热请求回流空车",
        ["HeatStationErr"] = "加热库故障",
        ["HeatCycleAgvAmount"] = "加热回流小车数量"
    };

    private static readonly char[] RemarkSeparators = ['：', ':', '，', ',', '。', '；', ';', '（', '(', '\r', '\n'];

    private static readonly string[] DataTypes =
        { "Bool", "Byte", "Word", "Int", "DInt", "Real", "String", "String[40]", "ByteArray", "ByteArray[15]" };

    private readonly PlcVirtualServerService _server = PlcVirtualServerService.Instance;
    private readonly DispatcherTimer _timer;
    private readonly List<TargetChoice> _targets;
    private readonly ObservableCollection<WatchPointRow> _points = new();
    private readonly ObservableCollection<DisplayedChange> _changes = new();
    private readonly ICollectionView _changeView;

    public PlcPointChangeMonitorWindow(IEnumerable<PlcConfigModel> plcs, int preferredPlcId, int preferredDbNumber)
    {
        _targets = BuildTargets(plcs);

        _changeView = CollectionViewSource.GetDefaultView(_changes);
        InitializeComponent();

        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(SampleIntervalMs)
        };
        _timer.Tick += (_, _) => PollPoints();

        WatchPointsGrid.ItemsSource = _points;
        ChangesGrid.ItemsSource = _changeView;
        TargetPicker.ItemsSource = _targets;
        TargetPicker.SelectedItem = _targets.FirstOrDefault(x => x.PlcId == preferredPlcId && x.DbNumber == preferredDbNumber)
                                    ?? _targets.FirstOrDefault(x => x.PlcId == preferredPlcId)
                                    ?? _targets.FirstOrDefault();
        PointTypePicker.ItemsSource = DataTypes;
        PointTypePicker.Text = "Bool";

        LoadWatchPoints();
        Loaded += (_, _) => UpdateTopmostButton();
        Closed += (_, _) => _timer.Stop();
    }

    private static List<TargetChoice> BuildTargets(IEnumerable<PlcConfigModel> plcs)
    {
        var choices = new List<TargetChoice>();
        var keys = new HashSet<(int PlcId, int DbNumber)>();
        foreach (var plc in plcs.Where(x => x != null && x.PlcId > 0).OrderBy(x => x.PlcId))
        {
            foreach (var dbBlock in (plc.DbBlocks?.Where(x => x != null) ?? Enumerable.Empty<PlcDbBlockModel>())
                         .OrderBy(x => x.DbNumber))
            {
                if (dbBlock.DbNumber is > 0 and <= ushort.MaxValue && keys.Add((plc.PlcId, dbBlock.DbNumber)))
                {
                    choices.Add(new TargetChoice(plc.PlcId, plc.PlcName, dbBlock));
                }
            }
        }

        return choices;
    }

    private void LoadWatchPoints()
    {
        try
        {
            foreach (var setting in PlcPointWatchFileStore.LoadSettings())
            {
                _points.Add(CreateWatchRow(setting));
            }
        }
        catch (Exception ex)
        {
            PointStatusText.Text = "读取监听点位失败，" + ex.Message;
        }
    }

    private WatchPointRow CreateWatchRow(PlcPointWatchSetting setting)
    {
        var target = _targets.FirstOrDefault(x => x.PlcId == setting.PlcId && x.DbNumber == setting.DbNumber);
        var protocolPoint = target?.ProtocolPoints.FirstOrDefault(point =>
            string.Equals(point.OriginalName, setting.Name, StringComparison.OrdinalIgnoreCase) &&
            PlcProtocolTemplate.SameAddress(point.Address, setting.Address, setting.DbNumber) &&
            string.Equals(point.DataType, setting.DataType, StringComparison.OrdinalIgnoreCase));
        if (protocolPoint != null && HasChineseCharacters(protocolPoint.Name) &&
            !string.Equals(setting.Name, protocolPoint.Name, StringComparison.Ordinal))
        {
            setting = new PlcPointWatchSetting(setting.PlcId, setting.DbNumber, protocolPoint.Name,
                setting.Address, setting.DataType, setting.IsEnabled);
        }

        return new WatchPointRow(setting, target?.DisplayName ?? $"PLC-{setting.PlcId} / DB{setting.DbNumber}");
    }

    private static string GetPointDisplayName(PlcDbBlockProtocolItemModel item, int dbNumber)
    {
        var name = item.SignalName?.Trim() ?? string.Empty;
        if (HasChineseCharacters(name))
        {
            return name;
        }

        // 旧模板的四个写码请求使用相同英文名，按字节和位号确定中文名。
        if (name.Equals("RequestWritePackID1", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.DataType?.Trim(), "Bool", StringComparison.OrdinalIgnoreCase))
        {
            var address = item.OffsetAddress.Split(':')[0].Trim().Replace(" ", string.Empty)
                .TrimStart('%').ToUpperInvariant().Replace("DBX", string.Empty);
            if (PlcDbBlockService.Instance.TryParseDbAddressOrTemplate(address, dbNumber,
                    out _, out var byteOffset, out var bitOffset) && byteOffset == 201)
            {
                var requestName = bitOffset switch
                {
                    2 => "请求写入Pack码1",
                    3 => "请求写入传输码1",
                    4 => "请求写入Pack码2",
                    5 => "请求写入传输码2",
                    _ => null
                };
                if (requestName != null)
                {
                    return requestName;
                }
            }
        }

        if (ChineseSignalNames.TryGetValue(name, out var chineseName))
        {
            return chineseName;
        }

        var remark = item.Remark?.Trim() ?? string.Empty;
        var separator = remark.IndexOfAny(RemarkSeparators);
        var remarkName = separator >= 0 ? remark.Substring(0, separator).Trim() : remark;
        if (HasChineseCharacters(remarkName))
        {
            return remarkName;
        }

        return name.Length > 0 ? name : $"点位 {item.ProtocolItemId}";
    }

    private static bool HasChineseCharacters(string text)
    {
        return text.Any(character => character is >= '\u3400' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF');
    }

    private void TargetPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProtocolPointPicker == null || !ReferenceEquals(e.OriginalSource, sender))
        {
            return;
        }

        ProtocolPointPicker.ItemsSource = (TargetPicker.SelectedItem as TargetChoice)?.ProtocolPoints;
        ProtocolPointPicker.SelectedItem = null;
    }

    private void ProtocolPointPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender) && sender is ComboBox { SelectedItem: ProtocolPointChoice point })
        {
            PointNameBox.Text = point.Name;
            PointAddressBox.Text = point.Address;
            PointTypePicker.Text = point.DataType;
        }
    }

    private void WatchPointsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender))
        {
            return;
        }

        var selected = WatchPointsGrid.SelectedItem as WatchPointRow;
        SavePointButton.IsEnabled = selected != null;
        SelectedOnlyCheckBox.IsEnabled = selected != null;
        if (selected == null)
        {
            SelectedOnlyCheckBox.IsChecked = false;
        }

        RefreshChangeFilter();
        RemovePointButton.IsEnabled = selected != null;
        if (selected == null)
        {
            return;
        }

        ShowPointInEditor(selected);
    }

    private void ShowPointInEditor(WatchPointRow selected)
    {
        var point = selected.Setting;
        TargetPicker.SelectedItem = _targets.FirstOrDefault(x => x.PlcId == point.PlcId && x.DbNumber == point.DbNumber);
        ProtocolPointPicker.SelectedItem = null;
        PointNameBox.Text = point.Name;
        PointAddressBox.Text = point.Address;
        PointTypePicker.Text = point.DataType;
        PointEnabledCheckBox.IsChecked = point.IsEnabled;
    }

    private void AddPointButton_Click(object sender, RoutedEventArgs e)
    {
        SaveEditorPoint(null);
    }

    private void SavePointButton_Click(object sender, RoutedEventArgs e)
    {
        if (WatchPointsGrid.SelectedItem is WatchPointRow selected)
        {
            SaveEditorPoint(selected);
        }
    }

    private void SaveEditorPoint(WatchPointRow? editing)
    {
        if (TargetPicker.SelectedItem is not TargetChoice target)
        {
            PointStatusText.Text = "请选择要监听的 PLC / DB。";
            return;
        }

        if (string.IsNullOrWhiteSpace(PointAddressBox.Text) || string.IsNullOrWhiteSpace(PointTypePicker.Text))
        {
            PointStatusText.Text = "请填写点位地址和数据类型。";
            return;
        }

        var setting = new PlcPointWatchSetting(target.PlcId, target.DbNumber, PointNameBox.Text,
            PointAddressBox.Text, PointTypePicker.Text, PointEnabledCheckBox.IsChecked == true);
        var row = CreateWatchRow(setting);
        var duplicate = editing != null && SameWatchPoint(editing.Setting, row.Setting)
            ? null
            : _points.FirstOrDefault(point => !ReferenceEquals(point, editing) && SameWatchPoint(point.Setting, row.Setting));
        if (duplicate != null)
        {
            PointStatusText.Text = $"列表中已有 {duplicate.Target} 的 {duplicate.Name}（{duplicate.Address}）。";
            return;
        }

        var index = editing == null ? -1 : _points.IndexOf(editing);
        var settings = _points.Select(point => point.Setting).ToList();
        if (index >= 0)
        {
            settings[index] = row.Setting;
        }
        else
        {
            settings.Add(row.Setting);
        }

        if (!TrySaveSettings(settings))
        {
            return;
        }

        var singlePointMode = SelectedOnlyCheckBox.IsChecked == true;
        if (index >= 0)
        {
            _points[index] = row;
        }
        else
        {
            _points.Add(row);
        }

        if (editing == null && SelectedOnlyCheckBox.IsChecked == true &&
            WatchPointsGrid.SelectedItem is WatchPointRow focused)
        {
            ShowPointInEditor(focused);
        }
        else
        {
            WatchPointsGrid.SelectedItem = row;
            WatchPointsGrid.ScrollIntoView(row);
            if (singlePointMode)
            {
                SelectedOnlyCheckBox.IsChecked = true;
                RefreshChangeFilter();
            }
        }

        PointStatusText.Text = editing == null ? $"已添加点位 {row.Name}。" : $"已保存修改 {row.Name}。";
    }

    private void RemovePointButton_Click(object sender, RoutedEventArgs e)
    {
        if (WatchPointsGrid.SelectedItem is not WatchPointRow selected ||
            !TrySaveSettings(_points.Where(row => !ReferenceEquals(row, selected)).Select(row => row.Setting)))
        {
            return;
        }

        SelectedOnlyCheckBox.IsChecked = false;
        _points.Remove(selected);
        ResetEditor();
        PointStatusText.Text = $"已删除点位 {selected.Name}。";
    }

    private bool TrySaveSettings(IEnumerable<PlcPointWatchSetting> settings)
    {
        try
        {
            PlcPointWatchFileStore.SaveSettings(settings);
            return true;
        }
        catch (Exception ex)
        {
            PointStatusText.Text = "保存监听点位失败，" + ex.Message;
            return false;
        }
    }

    private static bool SameWatchPoint(PlcPointWatchSetting left, PlcPointWatchSetting right)
    {
        return ReferenceEquals(left, right) ||
               (left.PlcId == right.PlcId && left.DbNumber == right.DbNumber &&
                string.Equals(left.DataType, right.DataType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(AddressSuffix(left.Address), AddressSuffix(right.Address), StringComparison.OrdinalIgnoreCase) &&
                PlcProtocolTemplate.SameAddress(left.Address, right.Address, left.DbNumber));
    }

    private static string AddressSuffix(string address)
    {
        var index = address.IndexOf(':');
        return index < 0 ? string.Empty : address.Substring(index + 1).Trim();
    }

    private void SelectedOnlyCheckBox_Click(object sender, RoutedEventArgs e)
    {
        RefreshChangeFilter();
    }

    private void RefreshChangeFilter()
    {
        if (SelectedOnlyCheckBox.IsChecked == true && WatchPointsGrid.SelectedItem is WatchPointRow point)
        {
            _changeView.Filter = item => item is DisplayedChange change && SameWatchPoint(change.Point, point.Setting);
        }
        else
        {
            _changeView.Filter = null;
        }
    }

    private void ResetEditor()
    {
        WatchPointsGrid.SelectedItem = null;
        ProtocolPointPicker.SelectedItem = null;
        PointNameBox.Clear();
        PointAddressBox.Clear();
        PointTypePicker.Text = "Bool";
        PointEnabledCheckBox.IsChecked = true;
    }

    private void ToggleMonitorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_timer.IsEnabled)
        {
            _timer.Stop();
            foreach (var row in _points)
            {
                row.SetReadState(null, null, row.Setting.IsEnabled ? "已暂停" : "未启用");
            }

            MonitorStateText.Text = "已暂停";
            ToggleMonitorButton.Content = "开始监听";
            return;
        }

        MonitorStateText.Text = $"监听中 · {SampleIntervalMs} ms";
        ToggleMonitorButton.Content = "暂停";
        _timer.Start();
        PollPoints();
    }

    private void PollPoints()
    {
        var timestamp = DateTimeOffset.Now;
        var batch = new List<DisplayedChange>();
        foreach (var row in _points)
        {
            var point = row.Setting;
            if (!point.IsEnabled)
            {
                continue;
            }

            if (!_server.IsPlcRunning(point.PlcId))
            {
                row.SetReadState(null, null, "PLC未运行");
                continue;
            }

            if (!_server.TryReadProtocolItemValue(point.PlcId, point.DbNumber, row.ProtocolItem,
                    out var actualAddress, out var valueText, out var message))
            {
                row.SetReadState(null, actualAddress, "读取失败，" + message);
                continue;
            }

            var previous = row.LastValue;
            var current = valueText ?? string.Empty;
            if (row.IsBoolean && bool.TryParse(current, out var bit))
            {
                current = bit ? "1" : "0";
            }

            row.SetReadState(current, actualAddress, "正常");
            if (previous != null && !string.Equals(previous, current, StringComparison.Ordinal))
            {
                var record = new PlcPointChangeRecord(timestamp, row.Target, point, row.Address, previous, current);
                batch.Add(new DisplayedChange(point, record));
            }
        }

        if (batch.Count > 0)
        {
            RecordChanges(timestamp, batch);
        }
    }

    private void RecordChanges(DateTimeOffset timestamp, IReadOnlyList<DisplayedChange> batch)
    {
        foreach (var change in batch)
        {
            _changes.Insert(0, change);
        }

        while (_changes.Count > DisplayedRecordLimit)
        {
            _changes.RemoveAt(_changes.Count - 1);
        }

        try
        {
            PlcPointWatchFileStore.AppendChanges(timestamp, batch.Select(change => change.Record).ToList());
            LogStatusText.Text = $"最近记录 {timestamp:HH:mm:ss.fff}";
        }
        catch (Exception ex)
        {
            LogStatusText.Text = "日志写入失败，" + ex.Message;
        }
    }

    private void OpenLogDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(PlcPointWatchFileStore.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = PlcPointWatchFileStore.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LogStatusText.Text = "打开日志目录失败，" + ex.Message;
        }
    }

    private void TopmostButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        UpdateTopmostButton();
    }

    private void UpdateTopmostButton()
    {
        TopmostButton.Content = Topmost ? "取消置顶" : "置顶";
    }

    private sealed class DisplayedChange
    {
        internal PlcPointWatchSetting Point { get; }
        public PlcPointChangeRecord Record { get; }

        internal DisplayedChange(PlcPointWatchSetting point, PlcPointChangeRecord record)
        {
            Point = point;
            Record = record;
        }
    }

    public sealed class TargetChoice
    {
        public int PlcId { get; }
        public int DbNumber { get; }
        public string DisplayName { get; }
        private readonly Lazy<IReadOnlyList<ProtocolPointChoice>> _protocolPoints;
        public IReadOnlyList<ProtocolPointChoice> ProtocolPoints => _protocolPoints.Value;

        internal TargetChoice(int plcId, string? plcName, PlcDbBlockModel dbBlock)
        {
            PlcId = plcId;
            DbNumber = dbBlock.DbNumber;
            var idLabel = $"PLC-{plcId}";
            var name = plcName?.Trim();
            var label = name is { Length: > 0 } ? name : idLabel;
            if (!label.Equals(idLabel, StringComparison.OrdinalIgnoreCase) &&
                !label.Equals($"PLC{plcId}", StringComparison.OrdinalIgnoreCase))
            {
                label = $"{label}（{idLabel}）";
            }

            DisplayName = $"{label} / DB{DbNumber}";
            _protocolPoints = new Lazy<IReadOnlyList<ProtocolPointChoice>>(() =>
                PlcProtocolTemplate.Basic.GetEffectiveItems(dbBlock)
                    .Where(item => item.IsEnabled && !string.IsNullOrWhiteSpace(item.OffsetAddress))
                    .Select(item => new ProtocolPointChoice(item, DbNumber)).ToList());
        }
    }

    public sealed class ProtocolPointChoice
    {
        internal string OriginalName { get; }
        public string Name { get; }
        public string Address { get; }
        public string DataType { get; }
        public string DisplayName => $"{Name} · {Address}";

        internal ProtocolPointChoice(PlcDbBlockProtocolItemModel item, int dbNumber)
        {
            Address = item.OffsetAddress;
            var signalName = item.SignalName?.Trim();
            OriginalName = signalName is { Length: > 0 } ? signalName : Address.Trim();
            Name = GetPointDisplayName(item, dbNumber);
            var dataType = item.DataType?.Trim();
            DataType = dataType is { Length: > 0 } ? dataType : "Bool";
        }
    }

    public sealed class WatchPointRow : INotifyPropertyChanged
    {
        private string? _actualAddress;

        internal PlcPointWatchSetting Setting { get; }
        internal PlcDbBlockProtocolItemModel ProtocolItem { get; }
        internal string? LastValue { get; private set; }
        internal bool IsBoolean => Setting.DataType.Equals("Bool", StringComparison.OrdinalIgnoreCase);

        public string Target { get; }
        public string Name => Setting.Name;
        public string Address => _actualAddress ?? Setting.Address;
        public string DataType => Setting.DataType;
        public string CurrentValue => LastValue == null ? "—" : LastValue.Length == 0 ? "（空）" : LastValue;
        public string Status { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal WatchPointRow(PlcPointWatchSetting setting, string target)
        {
            Setting = setting;
            Target = target;
            Status = setting.IsEnabled ? "待采样" : "未启用";
            ProtocolItem = new PlcDbBlockProtocolItemModel
            {
                IsEnabled = setting.IsEnabled,
                SignalName = setting.Name,
                OffsetAddress = setting.Address,
                DataType = setting.DataType
            };
        }

        internal void SetReadState(string? value, string? actualAddress, string status)
        {
            if (LastValue != value)
            {
                LastValue = value;
                OnPropertyChanged(nameof(CurrentValue));
            }

            if (!string.IsNullOrEmpty(actualAddress) && _actualAddress != actualAddress)
            {
                _actualAddress = actualAddress;
                OnPropertyChanged(nameof(Address));
            }

            if (Status != status)
            {
                Status = status;
                OnPropertyChanged(nameof(Status));
            }
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
