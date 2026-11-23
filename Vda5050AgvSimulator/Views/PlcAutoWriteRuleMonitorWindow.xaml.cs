#nullable disable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Vda5050AgvSimulator.Models.Plc;
using Vda5050AgvSimulator.Services.Plc;

namespace Vda5050AgvSimulator.Views;

public partial class PlcAutoWriteRuleMonitorWindow : PlcToolPage
{
    private static readonly Regex ExecutionMarkerName = new(
        @"^规则\s*(?<id>[0-9]+)\s*本轮已执行$",
        RegexOptions.CultureInvariant);

    private readonly PlcVirtualServerService _server = PlcVirtualServerService.Instance;
    private readonly PlcConfigModel _initialPlc;
    private readonly PlcDbBlockModel _initialDbBlock;
    private readonly DispatcherTimer _timer;
    private MonitorTarget _target;
    private bool _updatingTargetPicker;
    private List<RuleRow> _rules = [];
    private List<PointRow> _points = [];
    private int _requestedLength;

    internal int PlcId => _target.PlcId;
    internal int DbNumber => _target.DbNumber;

    public PlcAutoWriteRuleMonitorWindow(PlcConfigModel plc, PlcDbBlockModel dbBlock, int? selectedRuleId)
    {
        _initialPlc = (plc ?? throw new ArgumentNullException(nameof(plc))).Clone();
        _initialDbBlock = (dbBlock ?? throw new ArgumentNullException(nameof(dbBlock))).Clone();
        _target = new MonitorTarget(_initialPlc, _initialDbBlock);

        InitializeComponent();

        UpdateTargetTitle();

        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _timer.Tick += (_, _) => RefreshValues();
        void InitializeTargets(object sender, RoutedEventArgs args)
        {
            Loaded -= InitializeTargets;
            ReloadTargets(selectedRuleId);
        }
        Loaded += InitializeTargets;
        Closed += (_, _) => _timer.Stop();
    }

    private void ReloadTargets(int? selectedRuleId)
    {
        _timer.Stop();
        try
        {
            var plcs = PlcStoreService.Instance.GetSnapshotList();
            var targets = new List<MonitorTarget>();
            var keys = new HashSet<(int PlcId, int DbNumber)>();
            foreach (var plc in plcs)
            {
                if (plc.PlcId <= 0 || plc.DbBlocks == null)
                {
                    continue;
                }

                foreach (var dbBlock in plc.DbBlocks)
                {
                    if (dbBlock != null && dbBlock.DbNumber is > 0 and <= ushort.MaxValue &&
                        keys.Add((plc.PlcId, dbBlock.DbNumber)))
                    {
                        targets.Add(new MonitorTarget(plc, dbBlock));
                    }
                }
            }

            var initial = targets.FirstOrDefault(target =>
                target.PlcId == _initialPlc.PlcId && target.DbNumber == _initialDbBlock.DbNumber);
            if (initial == null)
            {
                var plc = plcs.FirstOrDefault(item => item.PlcId == _initialPlc.PlcId) ?? _initialPlc;
                initial = new MonitorTarget(plc, _initialDbBlock);
                targets.Add(initial);
            }

            var selected = targets.FirstOrDefault(target => target.PlcId == PlcId && target.DbNumber == DbNumber)
                           ?? initial;
            var ruleId = selected.PlcId == PlcId && selected.DbNumber == DbNumber ? selectedRuleId : null;
            _updatingTargetPicker = true;
            try
            {
                PlcTargetPicker.ItemsSource = targets.OrderBy(target => target.PlcId)
                    .ThenBy(target => target.DbNumber).ToList();
                PlcTargetPicker.SelectedItem = selected;
            }
            finally
            {
                _updatingTargetPicker = false;
            }

            ReloadRules(selected, ruleId);
        }
        catch (Exception ex)
        {
            ClearMonitor("读取 PLC 配置失败：" + ex.Message);
        }
    }

    private void PlcTargetPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingTargetPicker && ReferenceEquals(e.OriginalSource, sender) &&
            sender is ComboBox { SelectedItem: MonitorTarget target })
        {
            ReloadRules(target, null);
        }
    }

    private void UpdateTargetTitle()
    {
        Title = $"{_target.DisplayName} 规则点位监视";
        DbTitleText.Text = Title;
    }

    private void ReloadRules(MonitorTarget target, int? selectedRuleId)
    {
        _timer.Stop();
        _target = target;
        UpdateTargetTitle();
        try
        {
            var plc = target.Plc;
            var dbBlock = target.DbBlock;
            var references = dbBlock.AutoWriteRuleList
                ?.Where(reference => reference != null)
                .OrderBy(reference => reference.SortIndex)
                .ThenBy(reference => reference.RuleRefId)
                .ToList() ?? [];
            var definitions = PlcAutoWriteRuleStoreService.Instance.GetSnapshotList()
                .GroupBy(rule => rule.RuleId)
                .ToDictionary(group => group.Key, group => group.First());
            var executionMarkers = new Dictionary<int, PointRow>();
            var rows = new List<RuleRow>();

            foreach (var reference in references)
            {
                definitions.TryGetValue(reference.RuleId, out var rule);
                var enabled = plc.IsEnabled && plc.AutoWriteEnabled &&
                              dbBlock.IsEnabled && dbBlock.AutoWriteEnabled &&
                              reference.IsEnabled && rule?.IsEnabled == true;
                var row = new RuleRow(reference, rule, enabled);
                rows.Add(row);
                if (rule == null)
                {
                    continue;
                }

                foreach (var trigger in rule.Triggers?.Where(trigger => trigger != null)
                                            .OrderBy(trigger => trigger.TriggerId)
                                        ?? Enumerable.Empty<PlcAutoWriteTriggerModel>())
                {
                    row.Points.Add(CreatePoint(target.DbNumber,
                        "触发条件", trigger.TriggerName, trigger.ReadAddress, trigger.IsEnabled,
                        trigger.TriggerValue, null));
                }

                foreach (var item in rule.WriteItems?.Where(item => item != null).OrderBy(item => item.WriteId)
                                     ?? Enumerable.Empty<PlcAutoWriteItemModel>())
                {
                    int? markedRuleId = null;
                    var match = ExecutionMarkerName.Match(item.WriteName?.Trim() ?? string.Empty);
                    if (item.WriteValue && match.Success &&
                        int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture,
                            out var id))
                    {
                        markedRuleId = id;
                    }

                    var point = CreatePoint(target.DbNumber,
                        markedRuleId.HasValue ? "执行标记" : "写入动作",
                        item.WriteName, item.WriteAddress, item.IsEnabled, item.WriteValue,
                        item.ResetWhenNotTrigger ? item.ResetValue : (bool?)null);
                    row.Points.Add(point);

                    if (enabled && markedRuleId.HasValue && item.IsEnabled &&
                        !executionMarkers.ContainsKey(markedRuleId.Value))
                    {
                        executionMarkers.Add(markedRuleId.Value, point);
                    }
                }
            }

            foreach (var row in rows)
            {
                if (!executionMarkers.TryGetValue(row.RuleId, out var marker))
                {
                    continue;
                }

                row.ExecutionMarker = marker;
                if (!row.Points.Contains(marker))
                {
                    row.Points.Add(marker);
                }
            }

            _rules = rows;
            _points = [.. rows.SelectMany(row => row.Points).Distinct()];
            _requestedLength = _points.Select(point => point.ByteOffset + 1).DefaultIfEmpty(0).Max();
            RulesGrid.ItemsSource = rows;
            RulesGrid.SelectedItem = rows.FirstOrDefault(row => row.RuleId == selectedRuleId) ?? rows.FirstOrDefault();
            UpdateSelectedRule();
            EditStatusText.Text = string.Empty;
            RefreshValues();
            _timer.Start();
        }
        catch (Exception ex)
        {
            ClearMonitor("读取规则配置失败：" + ex.Message);
        }
    }

    private void ClearMonitor(string message)
    {
        _rules = [];
        _points = [];
        _requestedLength = 0;
        RulesGrid.ItemsSource = null;
        UpdateSelectedRule();
        EditStatusText.Text = string.Empty;
        ReadStatusText.Text = message;
    }

    private static PointRow CreatePoint(int dbNumber, string kind, string name, string address,
        bool enabled, bool expected, bool? reset)
    {
        var service = PlcDbBlockService.Instance;
        var resolved = service.ResolveDbAddressForDbBlock(address, dbNumber);
        if (service.TryParseDbAddress(resolved, out var resolvedDbNumber, out var offset, out var bit) &&
            resolvedDbNumber == dbNumber && offset >= 0 && offset < ushort.MaxValue && bit is >= 0 and <= 7)
        {
            return new PointRow(kind, name, $"DB{dbNumber}.DBX{offset}.{bit.Value}", enabled, expected, reset, offset,
                bit.Value);
        }

        return new PointRow(kind, name, string.IsNullOrWhiteSpace(address) ? "—" : address, enabled, expected, reset,
            -1, -1);
    }

    private void RefreshValues()
    {
        if (_rules.Count == 0)
        {
            ReadStatusText.Text = "当前 DB 未绑定规则。";
            return;
        }

        var target = _target;
        byte[] bytes = null;
        var ready = _server.TryGetAutoWriteMonitorDbLength(target.PlcId, target.DbNumber, out var length);
        if (ready)
        {
            var readLength = Math.Min(_requestedLength, length);
            if (readLength > 0)
            {
                if (_server.TryReadDbBytes(target.PlcId, target.DbNumber, readLength, out var snapshot,
                        out var message))
                {
                    bytes = snapshot;
                    ReadStatusText.Text = $"最后刷新 {DateTime.Now:HH:mm:ss.fff}";
                }
                else
                {
                    ReadStatusText.Text = "读取 DB 失败：" + message;
                }
            }
            else
            {
                ReadStatusText.Text = "当前规则没有可读取的点位。";
            }
        }
        else
        {
            ReadStatusText.Text = $"DB{target.DbNumber} 未就绪，请检查模拟服务。";
        }

        foreach (var point in _points)
        {
            var offset = point.ByteOffset;
            bool? value = bytes != null && offset >= 0 && offset < bytes.Length
                ? (bytes[offset] & (1 << point.BitOffset)) != 0
                : (bool?)null;
            point.SetValue(value);
        }

        foreach (var rule in _rules)
        {
            if (!rule.HasDefinition)
            {
                rule.SetStatus("缺少规则", $"全局规则库中找不到 ID {rule.RuleId}。", false);
                continue;
            }

            long executionCount = 0;
            DateTime executedAtUtc = default;
            var hasExecution = ready && _server.TryGetAutoWriteRuleExecution(
                target.PlcId, target.DbNumber, rule.RuleId, out executionCount, out executedAtUtc);
            rule.SetExecutionCount(ready ? executionCount : (long?)null);

            if (rule.ExecutionMarker is { } marker)
            {
                var state = marker.Value;
                var text = state == true ? "本轮已执行" : state == false ? "本轮未执行" : "标记不可读";
                rule.SetStatus(text, "执行标记 " + marker.Address, state == true);
                continue;
            }

            if (!ready)
            {
                rule.SetStatus("DB 未就绪", ReadStatusText.Text, false);
                continue;
            }

            if (hasExecution)
            {
                var last = executedAtUtc.ToLocalTime();
                rule.SetStatus("已有执行记录", $"最近执行 {last:yyyy-MM-dd HH:mm:ss.fff}", true);
            }
            else
            {
                rule.SetStatus("暂无执行记录", "当前服务启动后的执行记录。", false);
            }
        }
    }

    private void PointValueCell_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not DataGridCell cell ||
            cell.DataContext is not PointRow point || !point.CanToggle || !_points.Contains(point))
        {
            return;
        }

        e.Handled = true;
        var target = _target;
        var success = _server.TryToggleDbBit(target.PlcId, target.DbNumber, point.ByteOffset, point.BitOffset,
            out _, out var message);
        EditStatusText.Text = success ? message : "写入失败：" + message;
        RefreshValues();
    }

    private void RulesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedRule();
    }

    private void UpdateSelectedRule()
    {
        if (PointsGrid == null || SelectedRuleText == null)
        {
            return;
        }

        var selected = RulesGrid.SelectedItem as RuleRow;
        PointsGrid.ItemsSource = selected?.Points;
        SelectedRuleText.Text = selected?.Name ?? "请选择规则";
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        ReloadTargets((RulesGrid.SelectedItem as RuleRow)?.RuleId);
    }


    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    public sealed class MonitorTarget
    {
        public PlcConfigModel Plc { get; }
        public PlcDbBlockModel DbBlock { get; }
        public int PlcId => Plc.PlcId;
        public int DbNumber => DbBlock.DbNumber;
        public string DisplayName { get; }

        internal MonitorTarget(PlcConfigModel plc, PlcDbBlockModel dbBlock)
        {
            Plc = plc;
            DbBlock = dbBlock;
            var idLabel = $"PLC-{PlcId}";
            var name = string.IsNullOrWhiteSpace(plc.PlcName) ? idLabel : plc.PlcName.Trim();
            if (!name.Equals(idLabel, StringComparison.OrdinalIgnoreCase) &&
                !name.Equals($"PLC{PlcId}", StringComparison.OrdinalIgnoreCase))
            {
                name = $"{name}（{idLabel}）";
            }

            DisplayName = $"{name} / DB{DbNumber}";
        }
    }

    public sealed class RuleRow : INotifyPropertyChanged
    {
        public RuleRow(PlcDbBlockAutoWriteRuleRefModel reference, PlcAutoWriteRuleModel rule, bool enabled)
        {
            SortIndex = reference.SortIndex;
            RuleId = reference.RuleId;
            HasDefinition = rule != null;
            var name = rule?.RuleName ?? reference.RuleName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"规则 {RuleId}";
            }

            Name = HasDefinition && !enabled ? $"{name}（停用）" : name;
        }

        public int SortIndex { get; }
        public int RuleId { get; }
        public bool HasDefinition { get; }
        public string Name { get; }
        public List<PointRow> Points { get; } = [];
        public PointRow ExecutionMarker { get; set; }
        public string Status { get; private set; } = "—";
        public string StatusDetail { get; private set; } = string.Empty;
        public bool IsExecuted { get; private set; }
        public long? ExecutionCount { get; private set; }

        public event PropertyChangedEventHandler PropertyChanged;

        public void SetExecutionCount(long? count)
        {
            if (ExecutionCount == count)
            {
                return;
            }

            ExecutionCount = count;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExecutionCount)));
        }

        public void SetStatus(string status, string detail, bool executed)
        {
            if (Status == status && StatusDetail == detail && IsExecuted == executed)
            {
                return;
            }

            Status = status;
            StatusDetail = detail;
            IsExecuted = executed;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    public sealed class PointRow : INotifyPropertyChanged
    {
        public PointRow(string kind, string name, string address, bool enabled,
            bool expected, bool? reset, int byteOffset, int bitOffset)
        {
            Kind = enabled ? kind : $"{kind}（停用）";
            Name = string.IsNullOrWhiteSpace(name) ? kind : name;
            Address = address;
            ExpectedValue = FormatBit(expected);
            ResetValue = FormatBit(reset);
            ByteOffset = byteOffset;
            BitOffset = bitOffset;
        }

        public string Kind { get; }
        public string Name { get; }
        public string Address { get; }
        public string ExpectedValue { get; }
        public string ResetValue { get; }
        public int ByteOffset { get; }
        public int BitOffset { get; }
        public bool? Value { get; private set; }
        public bool CanToggle => Value.HasValue;
        public string CurrentValue => FormatBit(Value);

        public event PropertyChangedEventHandler PropertyChanged;

        public void SetValue(bool? value)
        {
            if (Value == value)
            {
                return;
            }

            Value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentValue)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanToggle)));
        }

        private static string FormatBit(bool? value)
        {
            return value == true ? "1" : value == false ? "0" : "—";
        }
    }
}