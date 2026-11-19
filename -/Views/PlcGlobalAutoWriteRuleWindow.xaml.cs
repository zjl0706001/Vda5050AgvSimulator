#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
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

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
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
                MessageBox.Show($"规则启用延时后，延时毫秒必须大于0：规则={rule.RuleName}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            Remark = "请求进入条件满足后，按规则延时配置写允许进入true；请求消失后自动复位false。"
        };

        rule.Triggers.Add(new PlcAutoWriteTriggerModel
        {
            TriggerId = 1,
            TriggerName = "请求进入",
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
            WriteName = "允许进入",
            IsEnabled = true,
            WriteAddress = "2.0",
            WriteValue = true,
            ResetWhenNotTrigger = true,
            ResetValue = false,
            Remark = string.Empty
        });

        return rule;
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}