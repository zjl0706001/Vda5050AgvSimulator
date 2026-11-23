#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Vda5050AgvSimulator.Models.Plc;
using Vda5050AgvSimulator.Services.Plc;

namespace Vda5050AgvSimulator.Views;

/// <summary>
/// 全局PLC自动写入规则配置窗口。
/// 规则本体只在全局单例 PlcAutoWriteRuleStoreService 中维护。
/// PLC / DB块 只保存 RuleId 引用。
/// </summary>
public partial class PlcGlobalAutoWriteRuleWindow : INotifyPropertyChanged
{
    public ObservableCollection<PlcAutoWriteRuleModel> Rules { get; } = [];

    public PlcAutoWriteRuleModel SelectedRule
    {
        get;
        set
        {
            field = value;

            if (field != null)
            {
                field.Triggers ??= [];
                field.WriteItems ??= [];

                SelectedTrigger = field.Triggers.FirstOrDefault();
                SelectedWriteItem = field.WriteItems.FirstOrDefault();
            }
            else
            {
                SelectedTrigger = null;
                SelectedWriteItem = null;
            }

            OnPropertyChanged();
        }
    }

    public PlcAutoWriteTriggerModel SelectedTrigger
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public PlcAutoWriteItemModel SelectedWriteItem
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    public event EventHandler RulesApplied;

    internal void SyncRuleEnabled(int ruleId, bool isEnabled)
    {
        var rule = Rules.FirstOrDefault(item => item?.RuleId == ruleId);
        if (rule != null && rule.IsEnabled != isEnabled)
        {
            rule.IsEnabled = isEnabled;
        }
    }

    public PlcGlobalAutoWriteRuleWindow()
    {
        InitializeComponent();

        DataContext = this;

        LoadRules();
    }

    private void LoadRules()
    {
        Rules.Clear();

        PlcAutoWriteRuleStoreService.Instance.EnsureLoadedFromConfig();

        var rules = PlcAutoWriteRuleStoreService.Instance.GetSnapshotList();

        foreach (var rule in rules)
        {
            if (rule == null)
            {
                continue;
            }

            NormalizeRule(rule);

            Rules.Add(rule);
        }

        SelectedRule = Rules.FirstOrDefault();

        OnPropertyChanged(nameof(Rules));
    }

    private void AddRuleButton_Click(object sender, RoutedEventArgs e)
    {
        var ruleId = GetNextRuleId();

        var rule = CreateCommonAutoWriteRule(ruleId);

        Rules.Add(rule);

        SelectedRule = rule;
    }

    private void AddRequestInTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        var ruleId = GetNextRuleId();

        var rule = CreateRequestInAutoWriteRule(ruleId);

        Rules.Add(rule);

        SelectedRule = rule;
    }

    private void RemoveRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRule == null)
        {
            MessageBox.Show("请先选择要删除的全局规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ruleId = SelectedRule.RuleId;

        var result = MessageBox.Show(
            $"确定删除全局规则吗？\r\nRuleId={SelectedRule.RuleId}\r\n{SelectedRule.RuleName}\r\n\r\n注意：删除后，所有PLC的所有DB块中绑定该RuleId的引用也会同步删除。",
            "删除全局规则",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var index = Rules.IndexOf(SelectedRule);

        Rules.Remove(SelectedRule);

        var removedRefCount = RemoveRuleRefsFromAllPlcs(ruleId);

        if (Rules.Count == 0)
        {
            SelectedRule = null;

            MessageBox.Show(
                $"全局规则已删除。\r\n同步移除DB块绑定引用数量：{removedRefCount}",
                "删除完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        SelectedRule = Rules[Math.Min(index, Rules.Count - 1)];

        MessageBox.Show(
            $"全局规则已删除。\r\n同步移除DB块绑定引用数量：{removedRefCount}",
            "删除完成",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private int RemoveRuleRefsFromAllPlcs(int ruleId)
    {
        if (ruleId <= 0)
        {
            return 0;
        }

        PlcStoreService.Instance.EnsureLoadedFromConfig();

        var plcs = PlcStoreService.Instance.GetSnapshotList();

        if (plcs == null || plcs.Count == 0)
        {
            return 0;
        }

        var removedCount = 0;

        foreach (var plc in plcs)
        {
            if (plc == null || plc.DbBlocks == null)
            {
                continue;
            }

            foreach (var dbBlock in plc.DbBlocks)
            {
                if (dbBlock == null || dbBlock.AutoWriteRuleList == null)
                {
                    continue;
                }

                var needRemoveRefs = dbBlock.AutoWriteRuleList
                    .Where(x => x != null && x.RuleId == ruleId)
                    .ToList();

                if (needRemoveRefs.Count == 0)
                {
                    continue;
                }

                foreach (var ruleRef in needRemoveRefs)
                {
                    dbBlock.AutoWriteRuleList.Remove(ruleRef);

                    removedCount++;
                }

                if (dbBlock.AutoWriteRuleList.Count == 0)
                {
                    dbBlock.AutoWriteEnabled = false;
                }
            }
        }

        if (removedCount > 0)
        {
            PlcStoreService.Instance.ReplaceAll(plcs);
        }

        return removedCount;
    }

    private void AddTriggerButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRule == null)
        {
            MessageBox.Show("请先选择全局规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedRule.Triggers ??= [];

        var triggerId = GetNextTriggerId(SelectedRule);

        var trigger = new PlcAutoWriteTriggerModel
        {
            TriggerId = triggerId,
            TriggerName = $"触发条件-{triggerId}",
            IsEnabled = true,
            ReadAddress = "0.0",
            TriggerValue = true,
            DelayTriggerEnabled = false,
            DelayMilliseconds = 0,
            Remark = string.Empty
        };

        SelectedRule.Triggers.Add(trigger);

        SelectedTrigger = trigger;

        OnPropertyChanged(nameof(SelectedRule));
    }

    private void AddDefaultTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRule == null)
        {
            MessageBox.Show("请先选择全局规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selection = ShowDefaultTemplateSelectDialog();

        if (selection == null)
        {
            return;
        }

        var triggerTemplates = selection.TriggerTemplates ?? [];
        var writeTemplates = selection.WriteTemplates ?? [];

        if (triggerTemplates.Count == 0 && writeTemplates.Count == 0)
        {
            return;
        }

        SelectedRule.Triggers ??= [];
        SelectedRule.WriteItems ??= [];

        PlcAutoWriteTriggerModel lastAddedTrigger = null;
        PlcAutoWriteItemModel lastAddedWriteItem = null;

        foreach (var template in triggerTemplates)
        {
            if (template == null)
            {
                continue;
            }

            var triggerId = GetNextTriggerId(SelectedRule);

            var trigger = new PlcAutoWriteTriggerModel
            {
                TriggerId = triggerId,
                TriggerName = string.IsNullOrWhiteSpace(template.Remark) ? $"触发条件-{triggerId}" : template.Remark,
                IsEnabled = true,
                ReadAddress = ConvertTemplateAddressToRuleAddress(template.AddressTemplate),
                TriggerValue = true,
                DelayTriggerEnabled = false,
                DelayMilliseconds = 0,
                Remark = template.DisplayText
            };

            SelectedRule.Triggers.Add(trigger);

            lastAddedTrigger = trigger;
        }

        foreach (var template in writeTemplates)
        {
            if (template == null)
            {
                continue;
            }

            var writeId = GetNextWriteItemId(SelectedRule);

            var writeItem = new PlcAutoWriteItemModel
            {
                WriteId = writeId,
                WriteName = string.IsNullOrWhiteSpace(template.Remark) ? $"写入动作-{writeId}" : template.Remark,
                IsEnabled = true,
                WriteAddress = ConvertTemplateAddressToRuleAddress(template.AddressTemplate),
                WriteValue = true,
                ResetWhenNotTrigger = true,
                ResetValue = false,
                Remark = template.DisplayText
            };

            SelectedRule.WriteItems.Add(writeItem);

            lastAddedWriteItem = writeItem;
        }

        if (lastAddedTrigger != null)
        {
            SelectedTrigger = lastAddedTrigger;
        }

        if (lastAddedWriteItem != null)
        {
            SelectedWriteItem = lastAddedWriteItem;
        }

        OnPropertyChanged(nameof(SelectedRule));
    }

    private void RemoveTriggerButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRule == null || SelectedTrigger == null)
        {
            return;
        }

        var result = MessageBox.Show(
            "确定删除当前触发条件吗？",
            "删除触发条件",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        SelectedRule.Triggers.Remove(SelectedTrigger);

        SelectedTrigger = SelectedRule.Triggers.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedRule));
    }

    private void AddWriteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRule == null)
        {
            MessageBox.Show("请先选择全局规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedRule.WriteItems ??= [];

        var writeId = GetNextWriteItemId(SelectedRule);

        var writeItem = new PlcAutoWriteItemModel
        {
            WriteId = writeId,
            WriteName = $"写入动作-{writeId}",
            IsEnabled = true,
            WriteAddress = "2.0",
            WriteValue = true,
            ResetWhenNotTrigger = true,
            ResetValue = false,
            Remark = string.Empty
        };

        SelectedRule.WriteItems.Add(writeItem);

        SelectedWriteItem = writeItem;

        OnPropertyChanged(nameof(SelectedRule));
    }

    private void RemoveWriteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRule == null || SelectedWriteItem == null)
        {
            return;
        }

        var result = MessageBox.Show(
            "确定删除当前写入动作吗？",
            "删除写入动作",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        SelectedRule.WriteItems.Remove(SelectedWriteItem);

        SelectedWriteItem = SelectedRule.WriteItems.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedRule));
    }


    /// <summary>
    /// 规则列表新增的多选启停操作。与单条开关一致，已保存规则的开关立即生效；
    /// 新增未保存的规则只更改编辑状态，保存后才正式进入全局规则库。
    /// </summary>
    /// <summary>
    /// 通过单个“批量启停”按钮打开四项操作菜单。
    /// 菜单沿用既有事件处理函数，不改变全局启用优先、持久化与多选逻辑。
    /// </summary>
    private void OpenAutoWriteRuleBatchMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu == null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        // 下拉菜单右边缘与按钮右边缘对齐，避免菜单挡住右侧工具按钮。
        var menuWidth = double.IsNaN(button.ContextMenu.Width) ? 256d : button.ContextMenu.Width;
        button.ContextMenu.HorizontalOffset = Math.Min(0d, button.ActualWidth - menuWidth);
        button.ContextMenu.VerticalOffset = 5d;
        button.ContextMenu.IsOpen = true;
    }

    private void SelectAllRuleRowsButton_Click(object sender, RoutedEventArgs e) => RuleListDataGrid.SelectAll();

    private void EnableAllRulesButton_Click(object sender, RoutedEventArgs e) =>
        ApplyRuleEnabledBatch([.. Rules], true, "全部规则");

    private void DisableAllRulesButton_Click(object sender, RoutedEventArgs e) =>
        ApplyRuleEnabledBatch([.. Rules], false, "全部规则");

    private void EnableSelectedRulesButton_Click(object sender, RoutedEventArgs e) =>
        ApplyRuleEnabledBatch(GetSelectedRuleRows(), true, "选中规则");

    private void DisableSelectedRulesButton_Click(object sender, RoutedEventArgs e) =>
        ApplyRuleEnabledBatch(GetSelectedRuleRows(), false, "选中规则");

    private List<PlcAutoWriteRuleModel> GetSelectedRuleRows() =>
    [
        .. RuleListDataGrid.SelectedItems
            .OfType<PlcAutoWriteRuleModel>()
            .Where(x => x != null)
            .GroupBy(x => x.RuleId)
            .Select(x => x.First())
    ];

    private void ApplyRuleEnabledBatch(IEnumerable<PlcAutoWriteRuleModel> rules, bool enabled, string scope)
    {
        var targets = rules?
            .Where(x => x != null)
            .GroupBy(x => x.RuleId)
            .Select(x => x.First())
            .ToList() ?? [];

        if (targets.Count == 0)
        {
            MessageBox.Show(this, "请先选择要操作的规则。", "自动写入规则",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var store = PlcAutoWriteRuleStoreService.Instance;
            store.EnsureLoadedFromConfig();
            var savedRules = store.GetSnapshotList();
            var savedMap = savedRules.Where(x => x != null)
                .GroupBy(x => x.RuleId)
                .ToDictionary(x => x.Key, x => x.First());
            var changedSaved = 0;
            var changedUi = 0;
            var pendingCount = 0;

            foreach (var rule in targets)
            {
                if (rule.IsEnabled != enabled)
                {
                    changedUi++;
                }

                if (savedMap.TryGetValue(rule.RuleId, out var savedRule))
                {
                    if (savedRule.IsEnabled != enabled)
                    {
                        savedRule.IsEnabled = enabled;
                        changedSaved++;
                    }
                }
                else
                {
                    pendingCount++;
                }
            }

            // 合并为一次ReplaceAll：实时持久化开关，但不提交其它未保存的规则编辑。
            if (changedSaved > 0)
            {
                store.ReplaceAll(savedRules);
            }

            foreach (var rule in targets)
            {
                rule.IsEnabled = enabled;
            }

            if (changedSaved > 0)
            {
                RulesApplied?.Invoke(this, EventArgs.Empty);
            }

            if (RuleListDataGrid.Items is IEditableCollectionView
                {
                    IsEditingItem: false, IsAddingNew: false
                })
            {
                // 刷新行显示时保留用户的多选，避免批量操作后自动丢失选中行。
                var selectedRowsBeforeRefresh = RuleListDataGrid.SelectedItems.Cast<object>().ToList();
                RuleListDataGrid.Items.Refresh();
                foreach (var selectedRow in selectedRowsBeforeRefresh)
                {
                    if (RuleListDataGrid.Items.Contains(selectedRow) &&
                        !RuleListDataGrid.SelectedItems.Contains(selectedRow))
                    {
                        RuleListDataGrid.SelectedItems.Add(selectedRow);
                    }
                }
            }

            var state = enabled ? "启用" : "禁用";
            var detail = pendingCount > 0 ? $"\r\n有 {pendingCount} 条新增规则尚未保存，需点击‘保存’才会持久化。" : string.Empty;
            MessageBox.Show(this,
                $"{scope}共 {targets.Count} 条，已{state}；界面变更 {changedUi} 条，其中 {changedSaved} 条已保存规则的开关发生变化。{detail}",
                "批量设置完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "批量修改规则启用状态失败：\r\n" + ex.Message,
                "自动写入规则", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 规则启用状态实时生效。
    /// 这里只同步启用状态，规则名称、地址等其它编辑仍由“保存”按钮统一提交。
    /// </summary>
    private void RuleEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox ||
            checkBox.DataContext is not PlcAutoWriteRuleModel rule)
        {
            return;
        }

        PlcAutoWriteRuleStoreService.Instance.UpdateRuleEnabled(
            rule.RuleId,
            checkBox.IsChecked == true);
        RulesApplied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 触发条件启用状态实时生效。
    /// </summary>
    private void TriggerEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox ||
            checkBox.DataContext is not PlcAutoWriteTriggerModel trigger)
        {
            return;
        }

        var ownerRule = Rules.FirstOrDefault(x =>
            x?.Triggers != null &&
            x.Triggers.Contains(trigger));

        if (ownerRule == null)
        {
            return;
        }

        PlcAutoWriteRuleStoreService.Instance.UpdateTriggerEnabled(
            ownerRule.RuleId,
            trigger.TriggerId,
            checkBox.IsChecked == true);
    }

    /// <summary>
    /// 写入动作启用状态实时生效。
    /// </summary>
    private void WriteItemEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox ||
            checkBox.DataContext is not PlcAutoWriteItemModel writeItem)
        {
            return;
        }

        var ownerRule = Rules.FirstOrDefault(x =>
            x?.WriteItems != null &&
            x.WriteItems.Contains(writeItem));

        if (ownerRule == null)
        {
            return;
        }

        PlcAutoWriteRuleStoreService.Instance.UpdateWriteItemEnabled(
            ownerRule.RuleId,
            writeItem.WriteId,
            checkBox.IsChecked == true);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var grids = new[] { RuleListDataGrid, RuleTriggersDataGrid, RuleWritesDataGrid };
        if (grids.Any(grid => !grid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                              !grid.CommitEdit(DataGridEditingUnit.Row, true)))
        {
            MessageBox.Show(this, "请修正表格中标出的输入项后再保存。", "全局自动写入规则",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!ValidateRules())
        {
            return;
        }

        var snapshot = Rules
            .Where(x => x != null)
            .OrderBy(x => x.RuleId)
            .Select(x =>
            {
                NormalizeRule(x);
                return x.Clone();
            })
            .ToList();

        PlcAutoWriteRuleStoreService.Instance.ReplaceAll(snapshot);
        RulesApplied?.Invoke(this, EventArgs.Empty);

        MessageBox.Show("全局自动写入规则已保存。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private bool ValidateRules()
    {
        NormalizeAllRules();

        var duplicateRuleId = Rules
            .Where(x => x != null)
            .GroupBy(x => x.RuleId)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicateRuleId != null)
        {
            MessageBox.Show($"全局规则ID重复：{duplicateRuleId.Key}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        foreach (var rule in Rules)
        {
            if (rule == null)
            {
                continue;
            }

            if (rule.RuleId <= 0)
            {
                MessageBox.Show("存在规则ID小于等于0的全局规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(rule.RuleName))
            {
                MessageBox.Show($"RuleId={rule.RuleId} 的规则名称不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (rule.DelayMilliseconds < 0)
            {
                MessageBox.Show($"规则延时时间不能小于0：规则={rule.RuleName}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (rule.DelayTriggerEnabled && rule.DelayMilliseconds <= 0)
            {
                MessageBox.Show($"规则启用延时后，延时毫秒必须大于0：规则={rule.RuleName}", "提示", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (!rule.IsEnabled)
            {
                continue;
            }

            if (!ValidateRuleTriggers(rule))
            {
                return false;
            }

            if (!ValidateRuleWriteItems(rule))
            {
                return false;
            }
        }

        return true;
    }

    private bool ValidateRuleTriggers(PlcAutoWriteRuleModel rule)
    {
        if (rule.Triggers == null || rule.Triggers.Count == 0)
        {
            MessageBox.Show($"规则没有配置触发条件：{rule.RuleName}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var enabledTriggers = rule.Triggers
            .Where(x => x != null && x.IsEnabled)
            .ToList();

        if (enabledTriggers.Count == 0)
        {
            MessageBox.Show($"规则没有启用的触发条件：{rule.RuleName}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var duplicateTriggerId = enabledTriggers
            .GroupBy(x => x.TriggerId)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicateTriggerId != null)
        {
            MessageBox.Show($"规则触发条件ID重复：规则={rule.RuleName}，TriggerId={duplicateTriggerId.Key}", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        foreach (var trigger in enabledTriggers)
        {
            if (trigger.TriggerId <= 0)
            {
                MessageBox.Show($"规则存在触发条件ID小于等于0：{rule.RuleName}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(trigger.TriggerName))
            {
                trigger.TriggerName = $"触发条件 {trigger.TriggerId}";
            }

            if (string.IsNullOrWhiteSpace(trigger.ReadAddress))
            {
                MessageBox.Show($"规则触发条件读取地址为空：规则={rule.RuleName}，条件={trigger.TriggerName}", "提示", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (!PlcDbBlockService.Instance.IsBoolDbAddressOrTemplate(trigger.ReadAddress))
            {
                MessageBox.Show(
                    $"规则触发条件读取地址必须是Bool地址，例如 5.0，也兼容 DB{{DB}}.5.0 或 DB2090.5.0：规则={rule.RuleName}，条件={trigger.TriggerName}",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        return true;
    }

    private bool ValidateRuleWriteItems(PlcAutoWriteRuleModel rule)
    {
        if (rule.WriteItems == null || rule.WriteItems.Count == 0)
        {
            MessageBox.Show($"规则没有配置写入动作：{rule.RuleName}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var enabledWriteItems = rule.WriteItems
            .Where(x => x != null && x.IsEnabled)
            .ToList();

        if (enabledWriteItems.Count == 0)
        {
            MessageBox.Show($"规则没有启用的写入动作：{rule.RuleName}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var duplicateWriteId = enabledWriteItems
            .GroupBy(x => x.WriteId)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicateWriteId != null)
        {
            MessageBox.Show($"规则写入动作ID重复：规则={rule.RuleName}，WriteId={duplicateWriteId.Key}", "提示", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        foreach (var writeItem in enabledWriteItems)
        {
            if (writeItem.WriteId <= 0)
            {
                MessageBox.Show($"规则存在写入动作ID小于等于0：{rule.RuleName}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(writeItem.WriteName))
            {
                writeItem.WriteName = $"写入动作 {writeItem.WriteId}";
            }

            if (string.IsNullOrWhiteSpace(writeItem.WriteAddress))
            {
                MessageBox.Show($"规则写入动作地址为空：规则={rule.RuleName}，动作={writeItem.WriteName}", "提示", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (!PlcDbBlockService.Instance.IsBoolDbAddressOrTemplate(writeItem.WriteAddress))
            {
                MessageBox.Show(
                    $"规则写入动作地址必须是Bool地址，例如 5.0，也兼容 DB{{DB}}.5.0 或 DB2090.5.0：规则={rule.RuleName}，动作={writeItem.WriteName}",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        return true;
    }

    private void NormalizeAllRules()
    {
        foreach (var rule in Rules.Where(x => x != null).OrderBy(x => x.RuleId).ToList())
        {
            NormalizeRule(rule);
        }
    }

    private void NormalizeRule(PlcAutoWriteRuleModel rule)
    {
        if (rule == null)
        {
            return;
        }

        if (rule.RuleId <= 0)
        {
            rule.RuleId = GetNextRuleId();
        }

        if (string.IsNullOrWhiteSpace(rule.RuleName))
        {
            rule.RuleName = $"自动写入规则-{rule.RuleId}";
        }

        if (rule.DelayMilliseconds < 0)
        {
            rule.DelayMilliseconds = 0;
        }

        if (rule.DelayMilliseconds == 0)
        {
            rule.DelayTriggerEnabled = false;
        }

        rule.Triggers ??= [];
        rule.WriteItems ??= [];

        var triggerId = 1;

        foreach (var trigger in rule.Triggers.Where(x => x != null))
        {
            if (trigger.TriggerId <= 0)
            {
                trigger.TriggerId = triggerId;
            }

            if (string.IsNullOrWhiteSpace(trigger.TriggerName))
            {
                trigger.TriggerName = $"触发条件 {trigger.TriggerId}";
            }

            triggerId++;
        }

        var writeId = 1;

        foreach (var writeItem in rule.WriteItems.Where(x => x != null))
        {
            if (writeItem.WriteId <= 0)
            {
                writeItem.WriteId = writeId;
            }

            if (string.IsNullOrWhiteSpace(writeItem.WriteName))
            {
                writeItem.WriteName = $"写入动作 {writeItem.WriteId}";
            }

            writeId++;
        }
    }

    private int GetNextRuleId()
    {
        var usedRuleIds = Rules
            .Where(x => x != null)
            .Select(x => x.RuleId)
            .Where(x => x > 0)
            .ToHashSet();

        var nextRuleId = 1;

        while (usedRuleIds.Contains(nextRuleId))
        {
            nextRuleId++;
        }

        return nextRuleId;
    }

    private int GetNextTriggerId(PlcAutoWriteRuleModel rule)
    {
        if (rule == null || rule.Triggers == null || rule.Triggers.Count == 0)
        {
            return 1;
        }

        return rule.Triggers
            .Where(x => x != null)
            .Select(x => x.TriggerId)
            .DefaultIfEmpty(0)
            .Max() + 1;
    }

    private int GetNextWriteItemId(PlcAutoWriteRuleModel rule)
    {
        if (rule == null || rule.WriteItems == null || rule.WriteItems.Count == 0)
        {
            return 1;
        }

        return rule.WriteItems
            .Where(x => x != null)
            .Select(x => x.WriteId)
            .DefaultIfEmpty(0)
            .Max() + 1;
    }

    private DefaultTemplateSelection ShowDefaultTemplateSelectDialog()
    {
        var templates = CreateDefaultRuleAddressTemplates()
            .Where(item => item != null && string.Equals(item.DataType, "Bool", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var triggerTemplates = templates
            .Where(item => string.Equals(item.Direction, "AGV_TO_PLC", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var writeTemplates = templates
            .Where(item => string.Equals(item.Direction, "PLC_TO_AGV", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (triggerTemplates.Count == 0 && writeTemplates.Count == 0)
        {
            MessageBox.Show(this, "没有可用的 Bool 默认模板。", "默认模板", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        DefaultTemplateSelection selectedTemplates = null;
        var dialog = new Window
        {
            Title = "选择自动写入模板",
            Owner = this,
            Width = 1120,
            Height = 640,
            MinWidth = 930,
            MinHeight = 470,
            ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            FontFamily = FontFamily,
            FontSize = FontSize,
            Foreground = Foreground,
            Background = (System.Windows.Media.Brush)FindResource("RulePageBrush"),
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = dialog.Title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = "勾选需要添加的触发条件和写入动作。此处使用 Bool 地址。",
            Style = (Style)FindResource("RuleHintStyle"),
            Margin = new Thickness(0, 4, 0, 0)
        });
        root.Children.Add(new Border
        {
            Style = (Style)FindResource("RuleBandStyle"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = header
        });

        var content = new Grid { Margin = new Thickness(18, 16, 18, 16) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var triggerPanel = CreateDefaultTemplateSidePanel("触发条件 · AGV_TO_PLC", triggerTemplates);
        var writePanel = CreateDefaultTemplateSidePanel("写入动作 · PLC_TO_AGV", writeTemplates);
        Grid.SetColumn(writePanel, 2);
        content.Children.Add(triggerPanel);
        content.Children.Add(writePanel);
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        var cancelButton = CreateTemplateButton("取消", "NormalButtonStyle");
        cancelButton.IsCancel = true;
        var okButton = CreateTemplateButton("添加选中模板", "PrimaryButtonStyle");
        okButton.MinWidth = 124;
        okButton.Margin = new Thickness(0);
        okButton.Click += (_, _) =>
        {
            var checkedTriggers = triggerTemplates.Where(item => item.IsSelected).ToList();
            var checkedWrites = writeTemplates.Where(item => item.IsSelected).ToList();
            if (checkedTriggers.Count == 0 && checkedWrites.Count == 0)
            {
                MessageBox.Show(dialog, "请至少勾选一个触发条件或写入动作。", "默认模板",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            selectedTemplates = new DefaultTemplateSelection
            {
                TriggerTemplates = checkedTriggers,
                WriteTemplates = checkedWrites
            };
            dialog.DialogResult = true;
        };

        var actions = new StackPanel
            { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(cancelButton);
        actions.Children.Add(okButton);
        var footer = new Border
        {
            Style = (Style)FindResource("RuleBandStyle"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = actions
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        dialog.Content = root;
        return dialog.ShowDialog() == true ? selectedTemplates : null;
    }

    private Border CreateDefaultTemplateSidePanel(string title, List<DefaultRuleAddressTemplate> templates)
    {
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("SectionTitleStyle"),
            Margin = new Thickness(0, 0, 0, 12)
        });

        var selectAllButton = CreateTemplateButton("全选", "NormalButtonStyle");
        var inverseButton = CreateTemplateButton("反选", "NormalButtonStyle");
        var clearButton = CreateTemplateButton("清空", "NormalButtonStyle");
        var selectedCount = new TextBlock { Style = (Style)FindResource("RuleHintStyle") };
        void RefreshTemplateCount() => selectedCount.Text = $"已勾选 {templates.Count(item => item.IsSelected)} 项";
        selectAllButton.Click += (_, _) => templates.ForEach(item => item.IsSelected = true);
        inverseButton.Click += (_, _) => templates.ForEach(item => item.IsSelected = !item.IsSelected);
        clearButton.Click += (_, _) => templates.ForEach(item => item.IsSelected = false);
        foreach (var item in templates)
        {
            item.PropertyChanged += (_, _) => RefreshTemplateCount();
        }

        RefreshTemplateCount();
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        toolbar.Children.Add(selectAllButton);
        toolbar.Children.Add(inverseButton);
        toolbar.Children.Add(clearButton);
        toolbar.Children.Add(selectedCount);
        Grid.SetRow(toolbar, 1);
        content.Children.Add(toolbar);

        var table = new DataGrid
        {
            Style = (Style)FindResource("RuleEditorGridStyle"),
            IsReadOnly = true,
            ItemsSource = templates
        };
        var checkBox = new FrameworkElementFactory(typeof(CheckBox));
        checkBox.SetValue(FrameworkElement.StyleProperty, FindResource("TemplateSelectCheckBoxStyle"));
        checkBox.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(DefaultRuleAddressTemplate.IsSelected))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        table.Columns.Add(new DataGridTemplateColumn
        {
            Header = "选择",
            Width = 52,
            CellTemplate = new DataTemplate { VisualTree = checkBox }
        });

        var textStyle = (Style)FindResource("RuleGridTextStyle");
        var addressStyle = (Style)FindResource("RuleGridAddressStyle");
        table.Columns.Add(new DataGridTextColumn
        {
            Header = "名称",
            MinWidth = 90,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(DefaultRuleAddressTemplate.Name)),
            ElementStyle = textStyle
        });
        table.Columns.Add(new DataGridTextColumn
        {
            Header = "偏移",
            Width = 57,
            Binding = new Binding(nameof(DefaultRuleAddressTemplate.RuleAddress)),
            ElementStyle = addressStyle
        });
        table.Columns.Add(new DataGridTextColumn
        {
            Header = "模板地址",
            Width = 109,
            Binding = new Binding(nameof(DefaultRuleAddressTemplate.AddressTemplate)),
            ElementStyle = addressStyle
        });
        table.Columns.Add(new DataGridTextColumn
        {
            Header = "说明",
            MinWidth = 94,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(DefaultRuleAddressTemplate.Remark)),
            ElementStyle = textStyle
        });
        Grid.SetRow(table, 2);
        content.Children.Add(table);
        return new Border { Style = (Style)FindResource("RuleCardStyle"), Child = content };
    }

    private Button CreateTemplateButton(string content, string styleKey) => new()
    {
        Content = content,
        Style = (Style)FindResource(styleKey)
    };

    private static string ConvertTemplateAddressToRuleAddress(string addressTemplate)
    {
        if (string.IsNullOrWhiteSpace(addressTemplate))
        {
            return string.Empty;
        }

        var address = addressTemplate.Trim();
        var dbxIndex = address.IndexOf(".DBX", StringComparison.OrdinalIgnoreCase);

        if (dbxIndex >= 0)
        {
            return address.Substring(dbxIndex + 4);
        }

        var dbPrefixIndex = address.IndexOf("DB", StringComparison.OrdinalIgnoreCase);

        if (dbPrefixIndex >= 0)
        {
            var lastDotIndex = address.LastIndexOf('.');

            if (lastDotIndex >= 0 && lastDotIndex + 1 < address.Length)
            {
                return address.Substring(lastDotIndex + 1);
            }
        }

        return address;
    }

    private static List<DefaultRuleAddressTemplate> CreateDefaultRuleAddressTemplates()
    {
        var items = new List<DefaultRuleAddressTemplate>();

        void AddTemplateItem(string direction, string name, string dataType, string addressTemplate, string remark)
        {
            items.Add(new DefaultRuleAddressTemplate
            {
                Direction = direction,
                Name = name,
                DataType = dataType,
                AddressTemplate = addressTemplate,
                Remark = remark
            });
        }

        #region AGV_TO_PLC

        AddTemplateItem("AGV_TO_PLC", "LifeBit", "Bool", "DB{DB}.DBX0.0", "系统健康");
        AddTemplateItem("AGV_TO_PLC", "Offline", "Bool", "DB{DB}.DBX0.1", "离线模式");
        AddTemplateItem("AGV_TO_PLC", "Online", "Bool", "DB{DB}.DBX0.2", "在线模式");
        AddTemplateItem("AGV_TO_PLC", "Alarm", "Bool", "DB{DB}.DBX0.3", "故障");
        AddTemplateItem("AGV_TO_PLC", "EmergencyStop", "Bool", "DB{DB}.DBX0.4", "急停");
        AddTemplateItem("AGV_TO_PLC", "EnterRequest", "Bool", "DB{DB}.DBX0.5", "请求进入");
        AddTemplateItem("AGV_TO_PLC", "Entering", "Bool", "DB{DB}.DBX0.6", "正在进入");
        AddTemplateItem("AGV_TO_PLC", "ReachedPosition", "Bool", "DB{DB}.DBX0.7", "小车到位");
        AddTemplateItem("AGV_TO_PLC", "Leaving", "Bool", "DB{DB}.DBX1.0", "正在离开");
        AddTemplateItem("AGV_TO_PLC", "BindComplete", "Bool", "DB{DB}.DBX1.1", "绑定完成");
        AddTemplateItem("AGV_TO_PLC", "UnbindComplete", "Bool", "DB{DB}.DBX1.2", "解绑完成");
        AddTemplateItem("AGV_TO_PLC", "WritePackID1Done", "Bool", "DB{DB}.DBX1.3", "写入PackID1完成");
        AddTemplateItem("AGV_TO_PLC", "WriteGuobiaoPackID1Done", "Bool", "DB{DB}.DBX1.4", "写入PackID1_12位码完成");
        AddTemplateItem("AGV_TO_PLC", "WritePackID2Done", "Bool", "DB{DB}.DBX1.5", "写入PackID2完成");
        AddTemplateItem("AGV_TO_PLC", "WriteGuobiaoPackID2Done", "Bool", "DB{DB}.DBX1.6", "写入PackID2_12位码完成");

        AddTemplateItem("AGV_TO_PLC", "AGVID", "Byte", "DB{DB}.DBB2", "AGV小车编号");
        AddTemplateItem("AGV_TO_PLC", "ErrorCode", "Byte", "DB{DB}.DBB3", "报错代码");

        AddTemplateItem("AGV_TO_PLC", "PACKLayers", "Int", "DB{DB}.DBW4", "pack层数：1：第一层；2：第二层；3：第三层");
        AddTemplateItem("AGV_TO_PLC", "AGVModuleType", "Int", "DB{DB}.DBW6",
            "AGV到达公共区域，到位后，请求进站时，发送CheckAGVModuleType信号和AGVModuleType（当前模组类型）进行校验");

        AddTemplateItem("AGV_TO_PLC", "PackID1", "String[40]", "DB{DB}.DBX8", "Pack码1");
        AddTemplateItem("AGV_TO_PLC", "ShippingCode1", "String[40]", "DB{DB}.DBX50", "传输码1");
        AddTemplateItem("AGV_TO_PLC", "PackID2", "String[40]", "DB{DB}.DBX92", "Pack码2");
        AddTemplateItem("AGV_TO_PLC", "ShippingCode2", "String[40]", "DB{DB}.DBX134", "传输码2");

        AddTemplateItem("AGV_TO_PLC", "UPComplete", "Bool", "DB{DB}.DBX176.0", "上升完成");
        AddTemplateItem("AGV_TO_PLC", "DownComplete", "Bool", "DB{DB}.DBX176.1", "下降完成");

        AddTemplateItem("AGV_TO_PLC", "Block1LimitpieceStatus", "Int", "DB{DB}.DBW178", "Block1限位片状态");
        AddTemplateItem("AGV_TO_PLC", "Block2LimitpieceStatus", "Int", "DB{DB}.DBW180", "Block2限位片状态");

        AddTemplateItem("AGV_TO_PLC", "AGVBodyAmount", "Byte", "DB{DB}.DBB177",
            "AGV载体数量，例如涂胶站需要等待入箱模组小车数量，备用，自行修改数据类型");

        #endregion

        #region PLC_TO_AGV

        AddTemplateItem("PLC_TO_AGV", "LifeBit", "Bool", "DB{DB}.DBX200.0", "系统健康");
        AddTemplateItem("PLC_TO_AGV", "Offline", "Bool", "DB{DB}.DBX200.1", "离线模式（非自动状态）");
        AddTemplateItem("PLC_TO_AGV", "Online", "Bool", "DB{DB}.DBX200.2", "在线模式（自动状态）");
        AddTemplateItem("PLC_TO_AGV", "Alarm", "Bool", "DB{DB}.DBX200.3", "故障");
        AddTemplateItem("PLC_TO_AGV", "EmergencyStopOK", "Bool", "DB{DB}.DBX200.4", "急停");
        AddTemplateItem("PLC_TO_AGV", "EnterInterlock", "Bool", "DB{DB}.DBX200.5", "进入互锁");
        AddTemplateItem("PLC_TO_AGV", "EnterAllowed", "Bool", "DB{DB}.DBX200.6", "允许进入");
        AddTemplateItem("PLC_TO_AGV", "LeaveInterlock", "Bool", "DB{DB}.DBX200.7", "离开互锁");
        AddTemplateItem("PLC_TO_AGV", "LeaveRequest", "Bool", "DB{DB}.DBX201.0", "请求离开");
        AddTemplateItem("PLC_TO_AGV", "Bypass", "Bool", "DB{DB}.DBX201.1", "屏蔽");
        AddTemplateItem("PLC_TO_AGV", "RequestWritePackID1", "Bool", "DB{DB}.DBX201.2", "请求写入Pack码1");
        AddTemplateItem("PLC_TO_AGV", "RequestWritePackID1", "Bool", "DB{DB}.DBX201.3", "请求写入传输码1");
        AddTemplateItem("PLC_TO_AGV", "RequestWritePackID1", "Bool", "DB{DB}.DBX201.4", "请求写入Pack码2");
        AddTemplateItem("PLC_TO_AGV", "RequestWritePackID1", "Bool", "DB{DB}.DBX201.5", "请求写入传输码2");
        AddTemplateItem("PLC_TO_AGV", "Route", "Int", "DB{DB}.DBW202", "AGV小车执行的路径");

        AddTemplateItem("PLC_TO_AGV", "PACKLayers", "Int", "DB{DB}.DBW206", "箱体上线工站扫码箱体后，上传MES，记录层数：1：第一层；2：第二层；3：第三层");
        AddTemplateItem("PLC_TO_AGV", "CheckAGVModuleType", "Int", "DB{DB}.DBW208",
            "入箱设备判断箱体类型，输出模组小车类型：1：模组小车类型1；2：模组小车类型2");

        AddTemplateItem("PLC_TO_AGV", "PackID1", "String[40]", "DB{DB}.DBX210", "Pack码1：24位码");
        AddTemplateItem("PLC_TO_AGV", "ShippingCode1", "String[40]", "DB{DB}.DBX252", "传输码1：12位码（总成码）或12位码（PN）");
        AddTemplateItem("PLC_TO_AGV", "PackID2", "String[40]", "DB{DB}.DBX294", "Pack码1：24位码");
        AddTemplateItem("PLC_TO_AGV", "ShippingCode2", "String[40]", "DB{DB}.DBX336", "传输码2：12位码（总成码）或12位码（PN）");

        AddTemplateItem("PLC_TO_AGV", "RequestUP", "Bool", "DB{DB}.DBX378.0", "请求上升");
        AddTemplateItem("PLC_TO_AGV", "RequestDOWN", "Bool", "DB{DB}.DBX378.1", "请求下降");
        AddTemplateItem("PLC_TO_AGV", "Clampingposition", "Bool", "DB{DB}.DBX378.2", "AGV夹紧位置");
        AddTemplateItem("PLC_TO_AGV", "HeatStationRequestEmptyPallet", "Bool", "DB{DB}.DBX378.3", "加热请求回流空车");
        AddTemplateItem("PLC_TO_AGV", "HeatStationErr", "Bool", "DB{DB}.DBX378.4", "加热库故障");

        AddTemplateItem("PLC_TO_AGV", "Block1LimitpieceStatus", "Int", "DB{DB}.DBW380", "Block1限位片状态");
        AddTemplateItem("PLC_TO_AGV", "Block2LimitpieceStatus", "Int", "DB{DB}.DBW382", "Block2限位片状态");
        AddTemplateItem("PLC_TO_AGV", "HeatCycleAgvAmount", "Int", "DB{DB}.DBW384", "加热回流小车数量");

        #endregion

        return items;
    }

    private sealed class DefaultTemplateSelection
    {
        public List<DefaultRuleAddressTemplate> TriggerTemplates { get; set; }

        public List<DefaultRuleAddressTemplate> WriteTemplates { get; set; }
    }

    private sealed class DefaultRuleAddressTemplate : INotifyPropertyChanged
    {
        public string Direction { get; set; }

        public string Name { get; set; }

        public string DataType { get; set; }

        public string AddressTemplate { get; set; }

        public string Remark { get; set; }

        public bool IsSelected
        {
            get;
            set
            {
                if (field == value)
                {
                    return;
                }

                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public string RuleAddress => ConvertTemplateAddressToRuleAddress(AddressTemplate);

        public string DisplayText => $"{Direction} | {Name} | {DataType} | {AddressTemplate} | {Remark}";
    }

    private PlcAutoWriteRuleModel CreateCommonAutoWriteRule(int ruleId)
    {
        var rule = new PlcAutoWriteRuleModel
        {
            RuleId = ruleId,
            RuleName = $"自动写入规则-{ruleId}",
            IsEnabled = true,
            DelayTriggerEnabled = false,
            DelayMilliseconds = 0,
            Remark = "通用自动写入规则。触发条件全部满足后，规则可按配置延时执行写入动作。"
        };

        rule.Triggers.Add(new PlcAutoWriteTriggerModel
        {
            TriggerId = 1,
            TriggerName = "触发条件",
            IsEnabled = true,
            ReadAddress = "0.0",
            TriggerValue = true,
            DelayTriggerEnabled = false,
            DelayMilliseconds = 0,
            Remark = string.Empty
        });

        rule.WriteItems.Add(new PlcAutoWriteItemModel
        {
            WriteId = 1,
            WriteName = "写入动作",
            IsEnabled = true,
            WriteAddress = "2.0",
            WriteValue = true,
            ResetWhenNotTrigger = true,
            ResetValue = false,
            Remark = string.Empty
        });

        return rule;
    }

    private PlcAutoWriteRuleModel CreateRequestInAutoWriteRule(int ruleId)
    {
        var rule = new PlcAutoWriteRuleModel
        {
            RuleId = ruleId,
            RuleName = "请求进入 -> 允许进入",
            IsEnabled = true,
            DelayTriggerEnabled = false,
            DelayMilliseconds = 0,
            Remark = "AGV请求进入条件满足后，写PLC允许进入true；请求消失后自动复位false。"
        };

        rule.Triggers.Add(new PlcAutoWriteTriggerModel
        {
            TriggerId = 1,
            TriggerName = "请求进入",
            IsEnabled = true,
            ReadAddress = "0.5",
            TriggerValue = true,
            DelayTriggerEnabled = false,
            DelayMilliseconds = 0,
            Remark = "AGV_TO_PLC | EnterRequest | Bool | DB{DB}.DBX0.5 | 请求进入"
        });

        rule.WriteItems.Add(new PlcAutoWriteItemModel
        {
            WriteId = 1,
            WriteName = "允许进入",
            IsEnabled = true,
            WriteAddress = "200.6",
            WriteValue = true,
            ResetWhenNotTrigger = true,
            ResetValue = false,
            Remark = "PLC_TO_AGV | EnterAllowed | Bool | DB{DB}.DBX200.6 | 允许进入"
        });

        return rule;
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}