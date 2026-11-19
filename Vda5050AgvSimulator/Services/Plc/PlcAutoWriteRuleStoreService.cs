#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Vda5050AgvSimulator.Models.Plc;
using Vda5050AgvSimulator.Services.File;

namespace Vda5050AgvSimulator.Services.Plc;

/// <summary>
/// PLC自动写入规则存储服务。
/// 单例服务。
/// 
/// 设计说明：
/// 1. 全局自动写入规则只在这里维护。
/// 2. PLC / DB块 不保存规则实体，只保存 RuleId 引用。
/// 3. DB块执行自动写入时，通过 PlcAutoWriteRuleStoreService.Instance 根据 RuleId 获取规则。
/// 4. RuleId 一旦生成，不允许随便重排，否则 DB块引用会失效。
/// 5. 当前自动写入规则统一按“全部触发条件满足”执行，不再维护触发关系属性。
/// </summary>
public class PlcAutoWriteRuleStoreService
{
    private static readonly Lazy<PlcAutoWriteRuleStoreService> LazyInstance =
        new(() => new PlcAutoWriteRuleStoreService());

    public static PlcAutoWriteRuleStoreService Instance => LazyInstance.Value;

    private readonly object _lockObj = new();

    private bool _isLoaded;

    private ObservableCollection<PlcAutoWriteRuleModel> Rules { get; } = [];

    public event EventHandler PlcAutoWriteRuleChanged;

    private PlcAutoWriteRuleStoreService()
    {
    }

    /// <summary>
    /// 确保PLC自动写入规则已经从配置文件加载。
    /// </summary>
    public void EnsureLoadedFromConfig()
    {
        var needNotify = false;

        lock (_lockObj)
        {
            if (_isLoaded)
            {
                return;
            }

            LoadFromConfigCore();

            needNotify = true;
        }

        if (needNotify)
        {
            NotifyPlcAutoWriteRuleChanged();
        }
    }

    /// <summary>
    /// 从配置文件加载PLC自动写入规则。
    /// 注意：调用方必须持有_lockObj锁。
    /// </summary>
    private void LoadFromConfigCore()
    {
        Rules.Clear();

        var rules = ConfigFileService.LoadPlcAutoWriteRules();

        foreach (var rule in rules)
        {
            if (rule == null)
            {
                continue;
            }

            NormalizeRule(rule);

            Rules.Add(rule.Clone());
        }

        if (Rules.Count == 0)
        {
            var defaultRule = PlcAutoWriteRuleModel.CreateDefault();
            defaultRule.RuleId = 1;

            NormalizeRule(defaultRule);

            Rules.Add(defaultRule);
        }

        NormalizeRuleIdsKeepStable();

        _isLoaded = true;
    }

    /// <summary>
    /// 保存当前PLC自动写入规则到配置文件。
    /// </summary>
    public void SaveToConfig()
    {
        EnsureLoadedFromConfig();

        ObservableCollection<PlcAutoWriteRuleModel> snapshot;

        lock (_lockObj)
        {
            NormalizeRuleIdsKeepStable();

            snapshot = CreateSnapshotCollection();
        }

        ConfigFileService.SavePlcAutoWriteRules(snapshot);

        NotifyPlcAutoWriteRuleChanged();
    }

    /// <summary>
    /// 获取PLC自动写入规则快照。
    /// 外部拿到的是Clone副本，不能直接修改内部集合。
    /// </summary>
    public List<PlcAutoWriteRuleModel> GetSnapshotList()
    {
        EnsureLoadedFromConfig();

        lock (_lockObj)
        {
            return Rules
                .Where(x => x != null)
                .OrderBy(x => x.RuleId)
                .Select(x => x.Clone())
                .ToList();
        }
    }

    /// <summary>
    /// 获取启用的全局规则快照。
    /// </summary>
    public List<PlcAutoWriteRuleModel> GetEnabledSnapshotList()
    {
        EnsureLoadedFromConfig();

        lock (_lockObj)
        {
            return Rules
                .Where(x => x != null)
                .Where(x => x.IsEnabled)
                .OrderBy(x => x.RuleId)
                .Select(x => x.Clone())
                .ToList();
        }
    }

    /// <summary>
    /// 根据RuleId获取全局规则快照。
    /// </summary>
    public PlcAutoWriteRuleModel GetRuleById(int ruleId)
    {
        if (ruleId <= 0)
        {
            return null;
        }

        EnsureLoadedFromConfig();

        lock (_lockObj)
        {
            var rule = Rules.FirstOrDefault(x => x != null && x.RuleId == ruleId);

            return rule?.Clone();
        }
    }

    /// <summary>
    /// 根据RuleId获取启用的全局规则快照。
    /// </summary>
    public PlcAutoWriteRuleModel GetEnabledRuleById(int ruleId)
    {
        if (ruleId <= 0)
        {
            return null;
        }

        EnsureLoadedFromConfig();

        lock (_lockObj)
        {
            var rule = Rules.FirstOrDefault(x =>
                x != null &&
                x.IsEnabled &&
                x.RuleId == ruleId);

            return rule?.Clone();
        }
    }

    /// <summary>
    /// 获取启用规则字典。
    /// Key = RuleId。
    /// </summary>
    public Dictionary<int, PlcAutoWriteRuleModel> GetEnabledRuleDictionary()
    {
        EnsureLoadedFromConfig();

        lock (_lockObj)
        {
            return Rules
                .Where(x => x != null)
                .Where(x => x.IsEnabled)
                .GroupBy(x => x.RuleId)
                .ToDictionary(
                    x => x.Key,
                    x => x.First().Clone());
        }
    }

    /// <summary>
    /// 根据DB块规则引用列表，获取实际要执行的全局规则。
    /// 按规则引用 SortIndex 排序。
    /// </summary>
    public List<PlcAutoWriteRuleModel> GetEnabledRulesByRefs(
        IEnumerable<PlcDbBlockAutoWriteRuleRefModel> ruleRefs)
    {
        EnsureLoadedFromConfig();

        if (ruleRefs == null)
        {
            return [];
        }

        var refs = ruleRefs
            .Where(x => x != null)
            .Where(x => x.IsEnabled)
            .Where(x => x.RuleId > 0)
            .OrderBy(x => x.SortIndex)
            .ThenBy(x => x.RuleRefId)
            .ToList();

        if (refs.Count == 0)
        {
            return [];
        }

        lock (_lockObj)
        {
            var ruleMap = Rules
                .Where(x => x != null)
                .Where(x => x.IsEnabled)
                .GroupBy(x => x.RuleId)
                .ToDictionary(x => x.Key, x => x.First());

            var result = new List<PlcAutoWriteRuleModel>();

            foreach (var ruleRef in refs)
            {
                if (!ruleMap.TryGetValue(ruleRef.RuleId, out var rule))
                {
                    continue;
                }

                result.Add(rule.Clone());
            }

            return result;
        }
    }

    /// <summary>
    /// 判断指定RuleId是否存在。
    /// </summary>
    public bool ContainsRule(int ruleId)
    {
        if (ruleId <= 0)
        {
            return false;
        }

        EnsureLoadedFromConfig();

        lock (_lockObj)
        {
            return Rules.Any(x => x != null && x.RuleId == ruleId);
        }
    }

    /// <summary>
    /// 判断指定RuleId是否存在且启用。
    /// </summary>
    public bool ContainsEnabledRule(int ruleId)
    {
        if (ruleId <= 0)
        {
            return false;
        }

        EnsureLoadedFromConfig();

        lock (_lockObj)
        {
            return Rules.Any(x =>
                x != null &&
                x.IsEnabled &&
                x.RuleId == ruleId);
        }
    }

    /// <summary>
    /// 实时更新全局规则启用状态，并立即保存。
    /// 只更新启用状态，不覆盖规则窗口中尚未点击保存的其它编辑内容。
    /// </summary>
    public bool UpdateRuleEnabled(int ruleId, bool isEnabled)
    {
        if (ruleId <= 0)
        {
            return false;
        }

        EnsureLoadedFromConfig();

        ObservableCollection<PlcAutoWriteRuleModel> snapshot;

        lock (_lockObj)
        {
            var rule = Rules.FirstOrDefault(x => x != null && x.RuleId == ruleId);

            if (rule == null)
            {
                return false;
            }

            rule.IsEnabled = isEnabled;
            snapshot = CreateSnapshotCollection();
        }

        ConfigFileService.SavePlcAutoWriteRules(snapshot);
        NotifyPlcAutoWriteRuleChanged();

        return true;
    }

    /// <summary>
    /// 实时更新指定触发条件启用状态，并立即保存。
    /// </summary>
    public bool UpdateTriggerEnabled(int ruleId, int triggerId, bool isEnabled)
    {
        if (ruleId <= 0 || triggerId <= 0)
        {
            return false;
        }

        EnsureLoadedFromConfig();

        ObservableCollection<PlcAutoWriteRuleModel> snapshot;

        lock (_lockObj)
        {
            var rule = Rules.FirstOrDefault(x => x != null && x.RuleId == ruleId);
            var trigger = rule?.Triggers?.FirstOrDefault(x => x != null && x.TriggerId == triggerId);

            if (trigger == null)
            {
                return false;
            }

            trigger.IsEnabled = isEnabled;
            snapshot = CreateSnapshotCollection();
        }

        ConfigFileService.SavePlcAutoWriteRules(snapshot);
        NotifyPlcAutoWriteRuleChanged();

        return true;
    }

    /// <summary>
    /// 实时更新指定写入动作启用状态，并立即保存。
    /// </summary>
    public bool UpdateWriteItemEnabled(int ruleId, int writeId, bool isEnabled)
    {
        if (ruleId <= 0 || writeId <= 0)
        {
            return false;
        }

        EnsureLoadedFromConfig();

        ObservableCollection<PlcAutoWriteRuleModel> snapshot;

        lock (_lockObj)
        {
            var rule = Rules.FirstOrDefault(x => x != null && x.RuleId == ruleId);
            var writeItem = rule?.WriteItems?.FirstOrDefault(x => x != null && x.WriteId == writeId);

            if (writeItem == null)
            {
                return false;
            }

            writeItem.IsEnabled = isEnabled;
            snapshot = CreateSnapshotCollection();
        }

        ConfigFileService.SavePlcAutoWriteRules(snapshot);
        NotifyPlcAutoWriteRuleChanged();

        return true;
    }

    /// <summary>
    /// 替换全部PLC自动写入规则，并保存到配置文件。
    /// 注意：保留已有RuleId，不做全量重排，避免DB块引用失效。
    /// </summary>
    public void ReplaceAll(IEnumerable<PlcAutoWriteRuleModel> rules)
    {
        if (rules == null)
        {
            return;
        }

        ObservableCollection<PlcAutoWriteRuleModel> snapshot;

        lock (_lockObj)
        {
            Rules.Clear();

            foreach (var rule in rules)
            {
                if (rule == null)
                {
                    continue;
                }

                NormalizeRule(rule);

                Rules.Add(rule.Clone());
            }

            if (Rules.Count == 0)
            {
                var defaultRule = PlcAutoWriteRuleModel.CreateDefault();
                defaultRule.RuleId = 1;

                NormalizeRule(defaultRule);

                Rules.Add(defaultRule);
            }

            NormalizeRuleIdsKeepStable();

            _isLoaded = true;

            snapshot = CreateSnapshotCollection();
        }

        ConfigFileService.SavePlcAutoWriteRules(snapshot);

        NotifyPlcAutoWriteRuleChanged();
    }

    /// <summary>
    /// 添加一条默认自动写入规则，并保存到配置文件。
    /// </summary>
    public PlcAutoWriteRuleModel AddDefaultRule()
    {
        PlcAutoWriteRuleModel result;

        ObservableCollection<PlcAutoWriteRuleModel> snapshot;

        lock (_lockObj)
        {
            EnsureLoadedFromConfigInner();

            var nextRuleId = GetNextRuleIdCore();

            var rule = PlcAutoWriteRuleModel.CreateDefault();
            rule.RuleId = nextRuleId;
            rule.RuleName = $"自动写入规则 {nextRuleId}";

            NormalizeRule(rule);

            Rules.Add(rule);

            snapshot = CreateSnapshotCollection();

            result = rule.Clone();
        }

        ConfigFileService.SavePlcAutoWriteRules(snapshot);

        NotifyPlcAutoWriteRuleChanged();

        return result;
    }

    /// <summary>
    /// 添加指定规则，并保存到配置文件。
    /// 如果RuleId小于等于0或重复，则自动分配新的RuleId。
    /// </summary>
    public PlcAutoWriteRuleModel AddRule(PlcAutoWriteRuleModel rule)
    {
        if (rule == null)
        {
            return null;
        }

        PlcAutoWriteRuleModel result;

        ObservableCollection<PlcAutoWriteRuleModel> snapshot;

        lock (_lockObj)
        {
            EnsureLoadedFromConfigInner();

            var newRule = rule.Clone();

            if (newRule.RuleId <= 0 || Rules.Any(x => x != null && x.RuleId == newRule.RuleId))
            {
                newRule.RuleId = GetNextRuleIdCore();
            }

            NormalizeRule(newRule);

            Rules.Add(newRule);

            snapshot = CreateSnapshotCollection();

            result = newRule.Clone();
        }

        ConfigFileService.SavePlcAutoWriteRules(snapshot);

        NotifyPlcAutoWriteRuleChanged();

        return result;
    }

    /// <summary>
    /// 删除指定自动写入规则，并保存到配置文件。
    /// 注意：删除规则后，不会重排其他规则ID。
    /// </summary>
    public bool RemoveRule(PlcAutoWriteRuleModel rule)
    {
        if (rule == null)
        {
            return false;
        }

        return RemoveRuleById(rule.RuleId);
    }

    /// <summary>
    /// 根据RuleId删除规则。
    /// 注意：删除规则后，不会重排其他规则ID。
    /// </summary>
    public bool RemoveRuleById(int ruleId)
    {
        if (ruleId <= 0)
        {
            return false;
        }

        bool removed;

        ObservableCollection<PlcAutoWriteRuleModel> snapshot;

        lock (_lockObj)
        {
            EnsureLoadedFromConfigInner();

            var target = Rules.FirstOrDefault(x => x != null && x.RuleId == ruleId);

            if (target == null)
            {
                return false;
            }

            removed = Rules.Remove(target);

            snapshot = CreateSnapshotCollection();
        }

        if (removed)
        {
            ConfigFileService.SavePlcAutoWriteRules(snapshot);

            NotifyPlcAutoWriteRuleChanged();
        }

        return removed;
    }

    /// <summary>
    /// 清空所有自动写入规则，并保存到配置文件。
    /// </summary>
    public void ClearAll()
    {
        ObservableCollection<PlcAutoWriteRuleModel> snapshot;

        lock (_lockObj)
        {
            EnsureLoadedFromConfigInner();

            Rules.Clear();

            snapshot = CreateSnapshotCollection();
        }

        ConfigFileService.SavePlcAutoWriteRules(snapshot);

        NotifyPlcAutoWriteRuleChanged();
    }

    /// <summary>
    /// 恢复默认自动写入规则，并保存到配置文件。
    /// </summary>
    public void ResetDefault()
    {
        ObservableCollection<PlcAutoWriteRuleModel> snapshot;

        lock (_lockObj)
        {
            Rules.Clear();

            var defaultRule = PlcAutoWriteRuleModel.CreateDefault();
            defaultRule.RuleId = 1;

            NormalizeRule(defaultRule);

            Rules.Add(defaultRule);

            _isLoaded = true;

            snapshot = CreateSnapshotCollection();
        }

        ConfigFileService.SavePlcAutoWriteRules(snapshot);

        NotifyPlcAutoWriteRuleChanged();
    }

    /// <summary>
    /// 获取下一个规则ID。
    /// </summary>
    public int GetNextRuleId()
    {
        EnsureLoadedFromConfig();

        lock (_lockObj)
        {
            return GetNextRuleIdCore();
        }
    }

    /// <summary>
    /// 创建用于保存或读取的PLC自动写入规则快照集合。
    /// 注意：调用方必须持有_lockObj锁。
    /// </summary>
    private ObservableCollection<PlcAutoWriteRuleModel> CreateSnapshotCollection()
    {
        return new ObservableCollection<PlcAutoWriteRuleModel>(
            Rules
                .Where(x => x != null)
                .OrderBy(x => x.RuleId)
                .Select(x =>
                {
                    NormalizeRule(x);

                    return x.Clone();
                }));
    }

    /// <summary>
    /// 内部确保加载。
    /// 注意：调用方必须持有_lockObj锁。
    /// </summary>
    private void EnsureLoadedFromConfigInner()
    {
        if (_isLoaded)
        {
            return;
        }

        LoadFromConfigCore();
    }

    /// <summary>
    /// 获取下一个规则ID。
    /// 注意：调用方必须持有_lockObj锁。
    /// </summary>
    private int GetNextRuleIdCore()
    {
        var maxRuleId = Rules
            .Where(x => x != null)
            .Select(x => x.RuleId)
            .DefaultIfEmpty(0)
            .Max();

        return maxRuleId + 1;
    }

    /// <summary>
    /// 规则ID规整。
    /// 
    /// 重要：
    /// 这里不会把所有RuleId重新排列为1、2、3。
    /// 因为DB块保存的是 RuleId 引用。
    /// 如果重排RuleId，DB块引用会错乱。
    /// 
    /// 这里只处理：
    /// 1. RuleId 小于等于0。
    /// 2. RuleId 重复。
    /// </summary>
    private void NormalizeRuleIdsKeepStable()
    {
        var usedRuleIds = new HashSet<int>();

        foreach (var rule in Rules.Where(x => x != null).OrderBy(x => x.RuleId).ToList())
        {
            if (rule.RuleId <= 0 || usedRuleIds.Contains(rule.RuleId))
            {
                rule.RuleId = GetNextAvailableRuleId(usedRuleIds);
            }

            usedRuleIds.Add(rule.RuleId);

            NormalizeRule(rule);
        }
    }

    /// <summary>
    /// 获取当前可用的RuleId。
    /// 注意：调用方必须持有_lockObj锁。
    /// </summary>
    private int GetNextAvailableRuleId(HashSet<int> usedRuleIds)
    {
        var ruleId = 1;

        while (usedRuleIds.Contains(ruleId) || Rules.Any(x => x != null && x.RuleId == ruleId))
        {
            ruleId++;
        }

        return ruleId;
    }

    /// <summary>
    /// 规整单条规则，防止配置文件为空集合或旧配置反序列化异常。
    /// 当前规则统一采用“全部触发条件满足”，不再维护触发关系属性。
    /// </summary>
    private void NormalizeRule(PlcAutoWriteRuleModel rule)
    {
        if (rule == null)
        {
            return;
        }

        if (rule.RuleId <= 0)
        {
            rule.RuleId = 0;
        }

        if (string.IsNullOrWhiteSpace(rule.RuleName))
        {
            rule.RuleName = rule.RuleId > 0
                ? $"自动写入规则 {rule.RuleId}"
                : "自动写入规则";
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

            if (trigger.DelayMilliseconds < 0)
            {
                trigger.DelayMilliseconds = 0;
            }

            if (trigger.DelayMilliseconds == 0)
            {
                trigger.DelayTriggerEnabled = false;
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

    /// <summary>
    /// 通知PLC自动写入规则发生变化。
    /// </summary>
    private void NotifyPlcAutoWriteRuleChanged()
    {
        PlcAutoWriteRuleChanged?.Invoke(this, EventArgs.Empty);
    }
}