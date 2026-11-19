#nullable disable

using System;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Vda5050AgvSimulator.Models.Plc;
using Vda5050AgvSimulator.Services.Plc;
using Growl = HandyControl.Controls.Growl;

namespace Vda5050AgvSimulator.Views;

/// <summary>
/// 多PLC配置窗口。
/// 负责PLC列表配置、DB块配置、自动写入规则配置、保存配置、启用/停用PLC虚拟服务。
/// </summary>
public partial class PlcConfigWindow : INotifyPropertyChanged
{
    private bool _isApplyingPlcEnableState;

    /// <summary>
    /// 注意：必须是public，否则XAML ItemsSource="{Binding Plcs}" 绑定不到。
    /// </summary>
    public ObservableCollection<PlcConfigModel> Plcs { get; } = [];

    public bool IsServiceRunning => PlcVirtualServerService.Instance.IsRunning;

    /// <summary>
    /// 当前选中的PLC是否允许编辑基础连接参数。
    /// 运行中的PLC不允许修改IP、内部端口、代理端口等连接参数。
    /// DB块和自动写入规则运行中允许编辑，保存后会自动重载运行中的PLC。
    /// </summary>
    public bool CanEditSelectedPlc =>
        SelectedPlc == null ||
        !PlcVirtualServerService.Instance.IsPlcRunning(SelectedPlc.PlcId);

    /// <summary>
    /// 当前选中的PLC是否可以查看DB块位值。
    /// 只有PLC正在运行时才允许查看。
    /// </summary>
    public bool CanViewDbBlockBits =>
        SelectedPlc != null &&
        PlcVirtualServerService.Instance.IsPlcRunning(SelectedPlc.PlcId);

    /// <summary>
    /// 运行中也允许编辑DB块和自动写入规则。
    /// 注意：保存后会自动重载运行中的PLC，DB内存会重新初始化。
    /// </summary>
    public bool CanEditRuntimeConfig => true;

    /// <summary>
    /// 全局自动写入规则快照。
    /// 页面只从全局规则库中选择规则，DB块只保存 RuleId 引用。
    /// </summary>
    public ObservableCollection<PlcAutoWriteRuleModel> GlobalAutoWriteRules { get; } = [];

    /// <summary>
    /// 启用的全局自动写入规则。 
    /// </summary>
    public IEnumerable<PlcAutoWriteRuleModel> EnabledGlobalAutoWriteRules =>
        GlobalAutoWriteRules
            .Where(x => x != null)
            .Where(x => x.IsEnabled);

    public PlcAutoWriteRuleModel SelectedGlobalRule
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public PlcDbBlockAutoWriteRuleRefModel SelectedDbBlockRuleRef
    {
        get;
        set
        {
            field = value;
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

    public PlcConfigModel SelectedPlc
    {
        get;
        set
        {
            field = value;

            if (field != null)
            {
                field.DbBlocks ??= [];

                SelectedDbBlock = field.DbBlocks.FirstOrDefault();
            }
            else
            {
                SelectedDbBlock = null;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(CanEditSelectedPlc));
            OnPropertyChanged(nameof(CanViewDbBlockBits));
            OnPropertyChanged(nameof(CanEditRuntimeConfig));

            RefreshPlcServerUi();
        }
    }

    public PlcDbBlockModel SelectedDbBlock
    {
        get;
        set
        {
            field = value;

            RefreshSelectedDbBlockRuleNames();

            SelectedDbBlockRuleRef = field?.AutoWriteRuleList?.FirstOrDefault();

            OnPropertyChanged();
        }
    }

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

    /// <summary>
    /// 保留该属性，兼容之前外部可能还在读取ResultPlcs的代码。
    /// </summary>
    public List<PlcConfigModel> ResultPlcs { get; private set; } = [];

    public event PropertyChangedEventHandler PropertyChanged;

    public PlcConfigWindow()
    {
        InitializeComponent();

        DataContext = this;

        PlcStoreService.Instance.EnsureLoadedFromConfig();

        PlcAutoWriteRuleStoreService.Instance.EnsureLoadedFromConfig();

        LoadGlobalAutoWriteRules();

        LoadPlcs(PlcStoreService.Instance.GetSnapshotList());

        SyncPlcEnabledStateWithRunningService();

        RefreshPlcServerUi();
    }

    private void LoadPlcs(IEnumerable<PlcConfigModel> plcs)
    {
        Plcs.Clear();

        if (plcs != null)
        {
            foreach (var plc in plcs)
            {
                if (plc == null)
                {
                    continue;
                }

                var clonedPlc = plc.Clone();

                NormalizePlc(clonedPlc);

                Plcs.Add(clonedPlc);
            }
        }

        SelectedPlc = Plcs.FirstOrDefault();

        OnPropertyChanged(nameof(Plcs));
    }


    private void LoadGlobalAutoWriteRules()
    {
        GlobalAutoWriteRules.Clear();

        PlcAutoWriteRuleStoreService.Instance.EnsureLoadedFromConfig();

        var rules = PlcAutoWriteRuleStoreService.Instance.GetSnapshotList();

        foreach (var rule in rules)
        {
            if (rule == null)
            {
                continue;
            }

            GlobalAutoWriteRules.Add(rule);
        }

        SelectedGlobalRule = EnabledGlobalAutoWriteRules.FirstOrDefault();

        OnPropertyChanged(nameof(GlobalAutoWriteRules));
        OnPropertyChanged(nameof(EnabledGlobalAutoWriteRules));
    }

    private Dictionary<int, PlcAutoWriteRuleModel> GetGlobalRuleMap()
    {
        PlcAutoWriteRuleStoreService.Instance.EnsureLoadedFromConfig();

        return PlcAutoWriteRuleStoreService.Instance
            .GetSnapshotList()
            .Where(x => x != null)
            .GroupBy(x => x.RuleId)
            .ToDictionary(x => x.Key, x => x.First());
    }

    private void RefreshSelectedDbBlockRuleNames()
    {
        if (SelectedDbBlock == null)
        {
            return;
        }

        RefreshRuleRefNames(SelectedDbBlock);
    }

    private void RefreshAllDbBlockRuleNames(PlcConfigModel plc)
    {
        if (plc == null || plc.DbBlocks == null)
        {
            return;
        }

        foreach (var dbBlock in plc.DbBlocks)
        {
            RefreshRuleRefNames(dbBlock);
        }
    }

    private void RefreshRuleRefNames(PlcDbBlockModel dbBlock)
    {
        if (dbBlock == null || dbBlock.AutoWriteRuleList == null)
        {
            return;
        }

        var ruleMap = GetGlobalRuleMap();

        foreach (var ruleRef in dbBlock.AutoWriteRuleList)
        {
            if (ruleRef == null)
            {
                continue;
            }

            if (ruleRef.RuleId <= 0)
            {
                ruleRef.RuleName = string.Empty;
                continue;
            }

            ruleRef.RuleName = ruleMap.TryGetValue(ruleRef.RuleId, out var rule)
                ? rule.RuleName
                : $"规则不存在：{ruleRef.RuleId}";
        }
    }

    private void NormalizeDbBlockRuleRefs(PlcConfigModel plc)
    {
        if (plc == null || plc.DbBlocks == null)
        {
            return;
        }

        foreach (var dbBlock in plc.DbBlocks)
        {
            if (dbBlock == null)
            {
                continue;
            }

            dbBlock.AutoWriteRuleList ??= [];

            var usedRuleIds = new HashSet<int>();

            foreach (var ruleRef in dbBlock.AutoWriteRuleList.ToList())
            {
                if (ruleRef == null)
                {
                    dbBlock.AutoWriteRuleList.Remove(ruleRef);
                    continue;
                }

                if (ruleRef.RuleRefId <= 0)
                {
                    ruleRef.RuleRefId = dbBlock.GetNextRuleRefId();
                }

                if (ruleRef.SortIndex <= 0)
                {
                    ruleRef.SortIndex = dbBlock.GetNextSortIndex();
                }

                if (ruleRef.RuleId > 0 && usedRuleIds.Contains(ruleRef.RuleId))
                {
                    dbBlock.AutoWriteRuleList.Remove(ruleRef);
                    continue;
                }

                if (ruleRef.RuleId > 0)
                {
                    usedRuleIds.Add(ruleRef.RuleId);
                }
            }

            RefreshRuleRefNames(dbBlock);
        }
    }

    private void MigrateLegacyAutoWriteRulesToGlobalRules(PlcConfigModel plc)
    {
        if (plc == null || plc.AutoWriteRules == null || plc.AutoWriteRules.Count == 0)
        {
            return;
        }

        PlcAutoWriteRuleStoreService.Instance.EnsureLoadedFromConfig();

        var globalRules = PlcAutoWriteRuleStoreService.Instance.GetSnapshotList();

        foreach (var legacyRule in plc.AutoWriteRules.ToList())
        {
            if (legacyRule == null)
            {
                continue;
            }

            NormalizeAutoWriteRule(legacyRule);

            var globalRule = FindSameGlobalRule(globalRules, legacyRule);

            if (globalRule == null)
            {
                globalRule = PlcAutoWriteRuleStoreService.Instance.AddRule(legacyRule);
                globalRules = PlcAutoWriteRuleStoreService.Instance.GetSnapshotList();
            }

            if (globalRule == null)
            {
                continue;
            }

            EnsureRuleDbBlocks(plc, globalRule);

            var ownerDbNumber = GetOwnerDbNumber(globalRule);

            var targetDbBlock = ownerDbNumber > 0
                ? plc.DbBlocks?.FirstOrDefault(x => x != null && x.DbNumber == ownerDbNumber)
                : plc.DbBlocks?.FirstOrDefault(x => x != null);

            if (targetDbBlock == null)
            {
                continue;
            }

            if (!targetDbBlock.ContainsRule(globalRule.RuleId))
            {
                targetDbBlock.AddRuleRef(globalRule.RuleId, globalRule.RuleName);
            }
        }

        plc.AutoWriteRules.Clear();

        LoadGlobalAutoWriteRules();
    }

    private PlcAutoWriteRuleModel FindSameGlobalRule(IEnumerable<PlcAutoWriteRuleModel> globalRules,
        PlcAutoWriteRuleModel rule)
    {
        if (globalRules == null || rule == null)
        {
            return null;
        }

        var targetSignature = CreateRuleSignature(rule);

        return globalRules.FirstOrDefault(x =>
            x != null &&
            CreateRuleSignature(x) == targetSignature);
    }

    private string CreateRuleSignature(PlcAutoWriteRuleModel rule)
    {
        if (rule == null)
        {
            return string.Empty;
        }

        var ruleDelay = $"{rule.DelayTriggerEnabled}:{rule.DelayMilliseconds}";

        var triggers = rule.Triggers == null
            ? string.Empty
            : string.Join("|", rule.Triggers
                .Where(x => x != null)
                .OrderBy(x => x.TriggerId)
                .Select(x => $"{x.IsEnabled}:{x.ReadAddress?.Trim()}:{x.TriggerValue}"));

        var writes = rule.WriteItems == null
            ? string.Empty
            : string.Join("|", rule.WriteItems
                .Where(x => x != null)
                .OrderBy(x => x.WriteId)
                .Select(x =>
                    $"{x.IsEnabled}:{x.WriteAddress?.Trim()}:{x.WriteValue}:{x.ResetWhenNotTrigger}:{x.ResetValue}"));

        return $"{rule.RuleName?.Trim()}::{ruleDelay}::{triggers}::{writes}";
    }

    private int GetOwnerDbNumber(PlcAutoWriteRuleModel rule)
    {
        if (rule == null)
        {
            return 0;
        }

        var writeAddress = rule.WriteItems?
            .Where(x => x != null)
            .Where(x => !string.IsNullOrWhiteSpace(x.WriteAddress))
            .OrderBy(x => x.WriteId)
            .Select(x => x.WriteAddress)
            .FirstOrDefault();

        if (PlcDbBlockService.Instance.TryParseDbAddress(writeAddress, out var writeDbNumber, out _, out _))
        {
            return writeDbNumber;
        }

        var readAddress = rule.Triggers?
            .Where(x => x != null)
            .Where(x => !string.IsNullOrWhiteSpace(x.ReadAddress))
            .OrderBy(x => x.TriggerId)
            .Select(x => x.ReadAddress)
            .FirstOrDefault();

        if (PlcDbBlockService.Instance.TryParseDbAddress(readAddress, out var readDbNumber, out _, out _))
        {
            return readDbNumber;
        }

        return 0;
    }

    private void RefreshGlobalRulesButton_Click(object sender, RoutedEventArgs e)
    {
        LoadGlobalAutoWriteRules();

        foreach (var plc in Plcs)
        {
            RefreshAllDbBlockRuleNames(plc);
        }

        Growl.SuccessGlobal("全局自动写入规则已刷新。");
    }

    private void ManageGlobalAutoWriteRulesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var existingWindow = Application.Current.Windows
                .OfType<PlcGlobalAutoWriteRuleWindow>()
                .FirstOrDefault();

            if (existingWindow != null)
            {
                if (existingWindow.WindowState == WindowState.Minimized)
                {
                    existingWindow.WindowState = WindowState.Maximized;
                }

                existingWindow.Activate();
                existingWindow.Focus();
                return;
            }

            var window = new PlcGlobalAutoWriteRuleWindow();

            window.Closed += (_, _) =>
            {
                LoadGlobalAutoWriteRules();

                foreach (var plc in Plcs)
                {
                    RefreshAllDbBlockRuleNames(plc);
                }

                OnPropertyChanged(nameof(SelectedDbBlock));
            };

            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "打开全局自动写入规则窗口失败：\r\n" + ex.Message,
                "异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private List<PlcAutoWriteRuleModel> GetSelectedGlobalRules()
    {
        var selectedRules = GlobalAutoWriteRulesDataGrid?
            .SelectedItems
            .OfType<PlcAutoWriteRuleModel>()
            .Where(x => x != null)
            .ToList() ?? [];

        if (selectedRules.Count == 0 && SelectedGlobalRule != null)
        {
            selectedRules.Add(SelectedGlobalRule);
        }

        return selectedRules
            .Where(x => x != null)
            .GroupBy(x => x.RuleId)
            .Select(x => x.First())
            .ToList();
    }

    private List<PlcDbBlockAutoWriteRuleRefModel> GetSelectedDbBlockRuleRefs()
    {
        var selectedRuleRefs = DbBlockRuleRefsDataGrid?
            .SelectedItems
            .OfType<PlcDbBlockAutoWriteRuleRefModel>()
            .Where(x => x != null)
            .ToList() ?? [];

        if (selectedRuleRefs.Count == 0 && SelectedDbBlockRuleRef != null)
        {
            selectedRuleRefs.Add(SelectedDbBlockRuleRef);
        }

        return selectedRuleRefs
            .Where(x => x != null)
            .Distinct()
            .ToList();
    }

    private void BindSelectedGlobalRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlc == null)
        {
            MessageBox.Show("请先选择PLC。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (SelectedDbBlock == null)
        {
            MessageBox.Show("请先选择DB块。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selectedRules = GetSelectedGlobalRules();

        if (selectedRules.Count == 0)
        {
            MessageBox.Show("请先选择全局自动写入规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedDbBlock.AutoWriteRuleList ??= [];

        var addedRuleRefs = new List<PlcDbBlockAutoWriteRuleRefModel>();

        var existedRuleNames = new List<string>();

        foreach (var rule in selectedRules)
        {
            if (rule == null)
            {
                continue;
            }

            if (SelectedDbBlock.ContainsRule(rule.RuleId))
            {
                existedRuleNames.Add(rule.RuleName);
                continue;
            }

            var ruleRef = SelectedDbBlock.AddRuleRef(rule.RuleId, rule.RuleName);

            addedRuleRefs.Add(ruleRef);

            EnsureRuleDbBlock(SelectedPlc, SelectedDbBlock, rule);
        }

        if (addedRuleRefs.Count == 0)
        {
            var existedText = existedRuleNames.Count > 0
                ? string.Join("、", existedRuleNames)
                : "所选规则";

            Growl.WarningGlobal($"DB{SelectedDbBlock.DbNumber} 已经绑定：{existedText}");
            return;
        }

        SelectedDbBlock.AutoWriteEnabled = true;

        RefreshAllDbBlockRuleNames(SelectedPlc);

        SelectedDbBlockRuleRef = addedRuleRefs.LastOrDefault();

        OnPropertyChanged(nameof(SelectedDbBlock));

        if (existedRuleNames.Count > 0)
        {
            Growl.WarningGlobal($"已绑定 {addedRuleRefs.Count} 条规则，跳过已存在 {existedRuleNames.Count} 条。");
            return;
        }

        Growl.SuccessGlobal($"已给 DB{SelectedDbBlock.DbNumber} 绑定 {addedRuleRefs.Count} 条规则。");
    }

    private void RemoveSelectedRuleRefButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDbBlock == null)
        {
            Growl.WarningGlobal("请先选择DB块。");
            return;
        }

        var selectedRuleRefs = GetSelectedDbBlockRuleRefs();

        if (selectedRuleRefs.Count == 0)
        {
            Growl.WarningGlobal("请先选择要移除的DB块规则引用。");
            return;
        }

        var ruleNames = string.Join("、", selectedRuleRefs.Select(x => x.RuleName));

        var result = MessageBox.Show(
            $"确定移除当前DB块选中的 {selectedRuleRefs.Count} 条规则绑定吗？\r\n{ruleNames}",
            "移除规则绑定",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var ruleRef in selectedRuleRefs)
        {
            if (ruleRef == null)
            {
                continue;
            }

            if (SelectedDbBlock.AutoWriteRuleList == null)
            {
                continue;
            }

            if (!SelectedDbBlock.AutoWriteRuleList.Contains(ruleRef))
            {
                continue;
            }

            SelectedDbBlock.RemoveRuleRef(ruleRef);
        }

        SelectedDbBlockRuleRef = SelectedDbBlock.AutoWriteRuleList?.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedDbBlock));

        Growl.SuccessGlobal($"已移除 {selectedRuleRefs.Count} 条DB块规则绑定。");
    }

    private void SyncPlcEnabledStateWithRunningService()
    {
        _isApplyingPlcEnableState = true;

        try
        {
            foreach (var plc in Plcs)
            {
                if (plc == null)
                {
                    continue;
                }

                plc.IsEnabled = PlcVirtualServerService.Instance.IsPlcRunning(plc.PlcId);
            }
        }
        finally
        {
            _isApplyingPlcEnableState = false;
        }
    }

    private void NormalizePlc(PlcConfigModel plc)
    {
        if (plc == null)
        {
            return;
        }

        if (plc.PlcId <= 0)
        {
            plc.PlcId = GetNextPlcId();
        }

        plc.PlcName = string.IsNullOrWhiteSpace(plc.PlcName)
            ? $"PLC-{plc.PlcId}"
            : plc.PlcName;

        plc.PlcType = string.IsNullOrWhiteSpace(plc.PlcType)
            ? "S1200"
            : plc.PlcType;

        plc.IpAddress = string.IsNullOrWhiteSpace(plc.IpAddress)
            ? "127.0.0.1"
            : plc.IpAddress.Trim();

        if (plc.ProxyPort <= 0)
        {
            plc.ProxyPort = 102;
        }

        if (plc.Port <= 0 || plc.Port == plc.ProxyPort)
        {
            plc.Port = GetNextAvailablePort(plc.ProxyPort);
        }

        if (plc.ConnectTimeOut <= 0)
        {
            plc.ConnectTimeOut = 3000;
        }

        if (plc.ReceiveTimeOut <= 0)
        {
            plc.ReceiveTimeOut = 3000;
        }

        if (plc.PollIntervalMs <= 0)
        {
            plc.PollIntervalMs = 300;
        }

        plc.DbBlocks ??= [];

        MigrateLegacyAutoWriteRulesToGlobalRules(plc);

        NormalizeDbBlockRuleRefs(plc);

        NormalizeDbBlockResetItems(plc);

        NormalizeDbBlockProtocolItems(plc);

        PlcDbBlockService.Instance.NormalizeDbBlocks(plc);

        RefreshAllDbBlockRuleNames(plc);

        OnPropertyChanged(nameof(SelectedPlc));
    }

    private void NormalizeDbBlockResetItems(PlcConfigModel plc)
    {
        if (plc == null || plc.DbBlocks == null)
        {
            return;
        }

        foreach (var dbBlock in plc.DbBlocks)
        {
            if (dbBlock == null)
            {
                continue;
            }

            dbBlock.ResetItems ??= [];

            var nextResetItemId = 1;

            foreach (var resetItem in dbBlock.ResetItems.ToList())
            {
                if (resetItem == null)
                {
                    dbBlock.ResetItems.Remove(resetItem);
                    continue;
                }

                if (resetItem.ResetItemId <= 0)
                {
                    resetItem.ResetItemId = nextResetItemId;
                }

                if (resetItem.ResetItemId >= nextResetItemId)
                {
                    nextResetItemId = resetItem.ResetItemId + 1;
                }

                if (string.IsNullOrWhiteSpace(resetItem.ResetName))
                {
                    resetItem.ResetName = $"复位项-{resetItem.ResetItemId}";
                }

                resetItem.ResetAddress = resetItem.ResetAddress?.Trim() ?? string.Empty;

                resetItem.Remark ??= string.Empty;
            }
        }
    }

    private void NormalizeDbBlockProtocolItems(PlcConfigModel plc)
    {
        if (plc == null || plc.DbBlocks == null)
        {
            return;
        }

        foreach (var dbBlock in plc.DbBlocks)
        {
            if (dbBlock == null)
            {
                continue;
            }

            dbBlock.ProtocolItems ??= [];

            var nextProtocolItemId = 1;

            foreach (var protocolItem in dbBlock.ProtocolItems.ToList())
            {
                if (protocolItem == null)
                {
                    dbBlock.ProtocolItems.Remove(protocolItem);
                    continue;
                }

                if (protocolItem.ProtocolItemId <= 0)
                {
                    protocolItem.ProtocolItemId = nextProtocolItemId;
                }

                if (protocolItem.ProtocolItemId >= nextProtocolItemId)
                {
                    nextProtocolItemId = protocolItem.ProtocolItemId + 1;
                }

                if (string.IsNullOrWhiteSpace(protocolItem.SignalName))
                {
                    protocolItem.SignalName = $"信号-{protocolItem.ProtocolItemId}";
                }

                if (string.IsNullOrWhiteSpace(protocolItem.DataType))
                {
                    protocolItem.DataType = "Bool";
                }

                protocolItem.OffsetAddress = protocolItem.OffsetAddress?.Trim() ?? string.Empty;

                protocolItem.Remark ??= string.Empty;
            }
        }
    }

    private void PlcListDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshPlcServerUi();
    }

    private void AddPlcButton_Click(object sender, RoutedEventArgs e)
    {
        var nextId = GetNextPlcId();

        var plc = CreateDefaultPlc(nextId);

        Plcs.Add(plc);

        SelectedPlc = plc;

        OnPropertyChanged(nameof(Plcs));

        SaveCurrentPlcsToStore(false);

        Growl.SuccessGlobal($"已新增 {plc.PlcName}，默认未启用。配置完成后勾选启用即可启动。");

        RefreshPlcServerUi();
    }

    private void RemovePlcButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedPlcs = PlcListDataGrid
            .SelectedItems
            .OfType<PlcConfigModel>()
            .Where(x => x != null)
            .Distinct()
            .ToList();

        if (selectedPlcs.Count == 0 && SelectedPlc != null)
        {
            selectedPlcs.Add(SelectedPlc);
        }

        if (selectedPlcs.Count == 0)
        {
            Growl.WarningGlobal("请先选择要删除的PLC。");
            return;
        }

        var runningPlcs = selectedPlcs
            .Where(x => PlcVirtualServerService.Instance.IsPlcRunning(x.PlcId))
            .ToList();

        if (runningPlcs.Count > 0)
        {
            var runningNames = string.Join("、", runningPlcs.Select(x => x.PlcName));

            Growl.WarningGlobal($"以下PLC正在运行，不能删除，请先取消启用：{runningNames}");

            return;
        }

        var deleteNames = string.Join("、", selectedPlcs.Select(x => x.PlcName));

        var result = MessageBox.Show(
            $"确定删除选中的 {selectedPlcs.Count} 台PLC配置吗？\r\n{deleteNames}",
            "删除PLC",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var firstIndex = selectedPlcs
            .Select(x => Plcs.IndexOf(x))
            .Where(x => x >= 0)
            .DefaultIfEmpty(0)
            .Min();

        foreach (var plc in selectedPlcs)
        {
            Plcs.Remove(plc);
        }

        if (Plcs.Count > 0)
        {
            var nextIndex = Math.Min(firstIndex, Plcs.Count - 1);

            SelectedPlc = Plcs[nextIndex];
        }
        else
        {
            SelectedPlc = null;
        }

        OnPropertyChanged(nameof(Plcs));

        SaveCurrentPlcsToStore(false);

        Growl.SuccessGlobal($"已删除 {selectedPlcs.Count} 台PLC配置。");

        RefreshPlcServerUi();
    }

    private void PlcEnableCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingPlcEnableState)
        {
            return;
        }

        if (sender is not CheckBox checkBox)
        {
            return;
        }

        if (checkBox.DataContext is not PlcConfigModel plc)
        {
            return;
        }

        var targetEnabled = checkBox.IsChecked == true;

        Dispatcher.BeginInvoke(new Action(() => { ApplySinglePlcEnabledState(plc, targetEnabled); }));
    }

    private void ApplySinglePlcEnabledState(PlcConfigModel plc, bool isEnabled)
    {
        if (plc == null)
        {
            return;
        }

        if (isEnabled)
        {
            EnableSinglePlc(plc);
            return;
        }

        DisableSinglePlc(plc);
    }

    private void EnableSinglePlc(PlcConfigModel plc)
    {
        if (plc == null)
        {
            return;
        }

        SetPlcEnabledWithoutTrigger(plc, true);

        if (!SaveCurrentPlcsToStore(false))
        {
            SetPlcEnabledWithoutTrigger(plc, false);
            RefreshPlcServerUi();
            return;
        }

        var savedPlc = ResultPlcs.FirstOrDefault(x => x.PlcId == plc.PlcId);

        if (savedPlc == null)
        {
            SetPlcEnabledWithoutTrigger(plc, false);
            SaveCurrentPlcsToStore(false);
            RefreshPlcServerUi();
            return;
        }

        var effectiveBlocks = PlcDbBlockService.Instance.GetEffectiveDbBlocks(savedPlc);

        if (effectiveBlocks.Count == 0)
        {
            SetPlcEnabledWithoutTrigger(plc, false);

            SaveCurrentPlcsToStore(false);

            Growl.WarningGlobal($"{plc.PlcName} 没有配置任何DB块，无法启动。请先新增DB块或给DB块绑定全局自动写入规则。");

            RefreshPlcServerUi();

            return;
        }

        var started = PlcVirtualServerService.Instance.StartOrReloadPlc(savedPlc);

        if (!started)
        {
            PlcVirtualServerService.Instance.StopPlc(plc.PlcId);

            SetPlcEnabledWithoutTrigger(plc, false);

            SaveCurrentPlcsToStore(false);

            Growl.WarningGlobal($"{plc.PlcName} 启动失败，已自动取消启用，请检查端口、代理端口、权限或DB配置。");

            RefreshPlcServerUi();

            return;
        }

        Growl.SuccessGlobal($"{plc.PlcName} 已启用并启动。");

        RefreshPlcServerUi();
    }

    private void DisableSinglePlc(PlcConfigModel plc)
    {
        if (plc == null)
        {
            return;
        }

        SetPlcEnabledWithoutTrigger(plc, false);

        PlcVirtualServerService.Instance.StopPlc(plc.PlcId);

        SaveCurrentPlcsToStore(false);

        Growl.WarningGlobal($"{plc.PlcName} 已停用并停止。");

        RefreshPlcServerUi();
    }

    private void SetPlcEnabledWithoutTrigger(PlcConfigModel plc, bool isEnabled)
    {
        if (plc == null)
        {
            return;
        }

        _isApplyingPlcEnableState = true;

        try
        {
            plc.IsEnabled = isEnabled;
        }
        finally
        {
            _isApplyingPlcEnableState = false;
        }
    }

    private void ToggleAllPlcsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Plcs.Count == 0)
        {
            Growl.WarningGlobal("当前没有PLC配置，请先新增PLC。");
            return;
        }

        var allRunning = Plcs
            .Where(x => x != null)
            .All(x => x.IsEnabled && PlcVirtualServerService.Instance.IsPlcRunning(x.PlcId));

        if (allRunning)
        {
            DisableAllPlcs();
            return;
        }

        EnableAllPlcs();
    }

    private void EnableAllPlcs()
    {
        var oldEnabledStates = Plcs
            .Where(x => x != null)
            .ToDictionary(x => x.PlcId, x => x.IsEnabled);

        SetAllPlcsEnabledWithoutTrigger(true);

        if (!SaveCurrentPlcsToStore(false))
        {
            RestorePlcEnabledStates(oldEnabledStates);
            RefreshPlcServerUi();
            return;
        }

        var successIds = new HashSet<int>();

        var failedNames = new List<string>();

        foreach (var plc in ResultPlcs.Where(x => x != null && x.IsEnabled))
        {
            if (PlcVirtualServerService.Instance.IsPlcRunning(plc.PlcId))
            {
                successIds.Add(plc.PlcId);
                continue;
            }

            var effectiveBlocks = PlcDbBlockService.Instance.GetEffectiveDbBlocks(plc);

            if (effectiveBlocks.Count == 0)
            {
                failedNames.Add($"{plc.PlcName}(无DB块)");
                continue;
            }

            var started = PlcVirtualServerService.Instance.StartOrReloadPlc(plc);

            if (started)
            {
                successIds.Add(plc.PlcId);
                continue;
            }

            failedNames.Add(plc.PlcName);

            PlcVirtualServerService.Instance.StopPlc(plc.PlcId);
        }

        _isApplyingPlcEnableState = true;

        try
        {
            foreach (var plc in Plcs)
            {
                if (plc == null)
                {
                    continue;
                }

                plc.IsEnabled = successIds.Contains(plc.PlcId);
            }
        }
        finally
        {
            _isApplyingPlcEnableState = false;
        }

        SaveCurrentPlcsToStore(false);

        if (successIds.Count <= 0)
        {
            Growl.WarningGlobal("全部启用失败，没有任何PLC启动成功。");
        }
        else if (failedNames.Count > 0)
        {
            Growl.WarningGlobal($"部分PLC启动失败，成功 {successIds.Count} 台，失败：{string.Join("、", failedNames)}");
        }
        else
        {
            Growl.SuccessGlobal($"全部启用完成，当前运行 {successIds.Count} 台PLC。");
        }

        RefreshPlcServerUi();
    }

    private void DisableAllPlcs()
    {
        var result = MessageBox.Show(
            "确定要停用全部PLC吗？这会停止所有正在运行的虚拟PLC。",
            "全部停用PLC",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        SetAllPlcsEnabledWithoutTrigger(false);

        PlcVirtualServerService.Instance.StopAllPlcs();

        SaveCurrentPlcsToStore(false);

        Growl.WarningGlobal("已全部停用PLC。");

        RefreshPlcServerUi();
    }

    private void SetAllPlcsEnabledWithoutTrigger(bool isEnabled)
    {
        _isApplyingPlcEnableState = true;

        try
        {
            foreach (var plc in Plcs)
            {
                if (plc == null)
                {
                    continue;
                }

                plc.IsEnabled = isEnabled;
            }
        }
        finally
        {
            _isApplyingPlcEnableState = false;
        }
    }

    private void RestorePlcEnabledStates(Dictionary<int, bool> states)
    {
        if (states == null)
        {
            return;
        }

        _isApplyingPlcEnableState = true;

        try
        {
            foreach (var plc in Plcs)
            {
                if (plc == null)
                {
                    continue;
                }

                if (states.TryGetValue(plc.PlcId, out var isEnabled))
                {
                    plc.IsEnabled = isEnabled;
                }
            }
        }
        finally
        {
            _isApplyingPlcEnableState = false;
        }
    }

    private void AddDbBlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlc == null)
        {
            MessageBox.Show("请先选择PLC。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedPlc.DbBlocks ??= [];

        var dbNumber = GetNextDbNumber(SelectedPlc);

        var dbBlock = new PlcDbBlockModel
        {
            DbNumber = dbNumber,
            Length = 512,
            IsEnabled = true,
            BlockName = $"DB{dbNumber}",
            Remark = "手动新增DB块。"
        };

        SelectedPlc.DbBlocks.Add(dbBlock);

        SelectedDbBlock = dbBlock;
    }

    private void AddDefaultDbBlocksButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlc == null)
        {
            MessageBox.Show("请先选择PLC。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedPlc.DbBlocks ??= [];

        AddOrExpandDbBlock(SelectedPlc, 1500, 512, "DB1500", "默认工站交互DB块。");

        AddOrExpandDbBlock(SelectedPlc, 2090, 512, "DB2090", "默认读取信号DB块。");

        SelectedDbBlock = SelectedPlc.DbBlocks.FirstOrDefault(x => x.DbNumber == 1500)
                          ?? SelectedPlc.DbBlocks.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedPlc));
    }

    private void RemoveDbBlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlc == null || SelectedDbBlock == null)
        {
            return;
        }

        var result = MessageBox.Show(
            "确定删除当前DB块配置吗？",
            "删除DB块",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        SelectedPlc.DbBlocks.Remove(SelectedDbBlock);

        SelectedDbBlock = SelectedPlc.DbBlocks.FirstOrDefault();
    }

    private void ResetDbBlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlc == null)
        {
            MessageBox.Show("请先选择PLC。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (SelectedDbBlock == null)
        {
            MessageBox.Show("请先选择要复位的DB块。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!SelectedDbBlock.IsEnabled)
        {
            MessageBox.Show("当前DB块未启用，不能复位。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!PlcVirtualServerService.Instance.IsPlcRunning(SelectedPlc.PlcId))
        {
            MessageBox.Show("当前PLC模拟服务未启动，不能复位DB块。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var enabledResetItemCount = SelectedDbBlock.ResetItems?
            .Count(x => x != null && x.IsEnabled && !string.IsNullOrWhiteSpace(x.ResetAddress)) ?? 0;

        var confirmText = enabledResetItemCount > 0
            ? $"确定按复位配置复位 {SelectedPlc.PlcName} 的 DB{SelectedDbBlock.DbNumber} 吗？\r\n将写入 {enabledResetItemCount} 个复位项。"
            : $"当前DB块没有配置启用的复位项。\r\n确定清零 {SelectedPlc.PlcName} 的 DB{SelectedDbBlock.DbNumber} 整个DB块吗？";

        var result = MessageBox.Show(
            confirmText,
            "复位DB块",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (!PlcVirtualServerService.Instance.TryResetDbBlockByConfig(
                SelectedPlc.PlcId,
                SelectedDbBlock,
                out var message))
        {
            Growl.WarningGlobal(message);
            return;
        }

        Growl.SuccessGlobal(message);
    }

    private void OpenDbBlockResetConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlc == null)
        {
            MessageBox.Show("请先选择PLC。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (sender is FrameworkElement { DataContext: PlcDbBlockModel clickedDbBlock })
        {
            SelectedDbBlock = clickedDbBlock;
        }

        if (SelectedDbBlock == null)
        {
            MessageBox.Show("请先选择DB块。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var window = new PlcDbBlockResetConfigWindow(SelectedDbBlock)
            {
                Owner = this
            };

            var result = window.ShowDialog();

            if (result != true)
            {
                return;
            }

            SelectedDbBlock = SelectedDbBlock;

            OnPropertyChanged(nameof(SelectedDbBlock));
            OnPropertyChanged(nameof(SelectedPlc));

            Growl.SuccessGlobal($"DB{SelectedDbBlock.DbNumber} 复位配置已更新，请点击保存配置持久化。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "打开DB块复位配置窗口失败：\r\n" +
                ex.GetType().FullName + "\r\n" +
                ex.Message + "\r\n\r\n" +
                ex,
                "异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenProtocolBoardConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlc == null)
        {
            MessageBox.Show("请先选择PLC。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (SelectedDbBlock == null)
        {
            MessageBox.Show("请先选择DB块。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var existingWindow = Application.Current.Windows
                .OfType<PlcDbBlockProtocolBoardConfigWindow>()
                .FirstOrDefault(x =>
                    x.PlcId == SelectedPlc.PlcId &&
                    x.DbNumber == SelectedDbBlock.DbNumber);

            if (existingWindow != null)
            {
                if (existingWindow.WindowState == WindowState.Minimized)
                {
                    existingWindow.WindowState = WindowState.Normal;
                }

                existingWindow.Activate();
                existingWindow.Focus();
                return;
            }

            var window = new PlcDbBlockProtocolBoardConfigWindow(
                SelectedPlc.PlcId,
                SelectedDbBlock);

            window.Closed += (_, _) =>
            {
                OnPropertyChanged(nameof(SelectedDbBlock));
                OnPropertyChanged(nameof(SelectedPlc));
            };

            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "打开协议看板配置窗口失败：\r\n" +
                "类型：" + ex.GetType().FullName + "\r\n" +
                "消息：" + ex.Message + "\r\n\r\n" +
                ex,
                "异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ViewDbBlockBitsButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlc == null)
        {
            MessageBox.Show("请先选择PLC。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (element.DataContext is not PlcDbBlockModel dbBlock)
        {
            MessageBox.Show("未获取到当前DB块。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedDbBlock = dbBlock;

        if (!dbBlock.IsEnabled)
        {
            MessageBox.Show("当前DB块未启用，不能查看位值。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!PlcVirtualServerService.Instance.IsPlcRunning(SelectedPlc.PlcId))
        {
            MessageBox.Show("当前PLC模拟服务未启动，请先启动PLC模拟服务。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var existingWindow = Application.Current.Windows
            .OfType<DbBlockBitsWindow>()
            .FirstOrDefault(x =>
                x.PlcId == SelectedPlc.PlcId &&
                x.DbNumber == dbBlock.DbNumber);

        if (existingWindow != null)
        {
            if (existingWindow.WindowState == WindowState.Minimized)
            {
                existingWindow.WindowState = WindowState.Normal;
            }

            existingWindow.Activate();
            existingWindow.Focus();
            return;
        }

        var length = dbBlock.Length <= 0 ? 256 : dbBlock.Length;

        if (!PlcVirtualServerService.Instance.TryReadDbBytes(
                SelectedPlc.PlcId,
                dbBlock.DbNumber,
                length,
                out var bytes,
                out var message))
        {
            MessageBox.Show(
                "读取DB块失败：" + message,
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var window = new DbBlockBitsWindow(
                SelectedPlc.PlcId,
                SelectedPlc.PlcName,
                dbBlock.DbNumber,
                length,
                bytes);

            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "打开DB块位值窗口失败：\r\n" +
                "类型：" + ex.GetType().FullName + "\r\n" +
                "消息：" + ex.Message,
                "异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void AddRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlc == null)
        {
            MessageBox.Show("请先选择PLC。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedPlc.AutoWriteRules ??= [];

        var nextId = GetNextRuleId(SelectedPlc);

        var rule = CreateCommonAutoWriteRule(nextId);

        SelectedPlc.AutoWriteRules.Add(rule);

        EnsureRuleDbBlocks(SelectedPlc, rule);

        SelectedRule = rule;

        OnPropertyChanged(nameof(SelectedPlc));
    }

    private void AddRequestInTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlc == null)
        {
            MessageBox.Show("请先选择PLC。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedPlc.AutoWriteRules ??= [];

        var nextId = GetNextRuleId(SelectedPlc);

        var rule = CreateRequestInAutoWriteRule(nextId);

        SelectedPlc.AutoWriteRules.Add(rule);

        EnsureRuleDbBlocks(SelectedPlc, rule);

        SelectedRule = rule;

        OnPropertyChanged(nameof(SelectedPlc));
    }

    private void RemoveRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlc == null || SelectedRule == null)
        {
            return;
        }

        SelectedPlc.AutoWriteRules.Remove(SelectedRule);

        SelectedRule = SelectedPlc.AutoWriteRules.FirstOrDefault();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveCurrentPlcsToStore(false))
        {
            return;
        }

        if (PlcVirtualServerService.Instance.IsRunning)
        {
            PlcVirtualServerService.Instance.ReloadEnabledPlcsFromStore();

            SyncPlcEnabledStateWithRunningService();

            Growl.SuccessGlobal("PLC配置已保存，运行中的PLC已按新配置重载。注意：DB内存会重新初始化。");
        }
        else
        {
            Growl.SuccessGlobal("PLC配置已保存。");
        }

        RefreshPlcServerUi();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private bool SaveCurrentPlcsToStore(bool requireEnabledPlc)
    {
        if (!ValidatePlcs(requireEnabledPlc))
        {
            return false;
        }

        ResultPlcs = Plcs
            .Where(x => x != null)
            .Select(x =>
            {
                NormalizePlc(x);
                return x.Clone();
            })
            .ToList();

        PlcStoreService.Instance.ReplaceAll(ResultPlcs);

        return true;
    }

    private void RefreshPlcServerUi()
    {
        var runningCount = PlcVirtualServerService.Instance.RunningCount;

        var isRunning = runningCount > 0;

        OnPropertyChanged(nameof(IsServiceRunning));
        OnPropertyChanged(nameof(CanEditSelectedPlc));
        OnPropertyChanged(nameof(CanViewDbBlockBits));
        OnPropertyChanged(nameof(CanEditRuntimeConfig));

        if (isRunning)
        {
            PlcServerStatusEllipse.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));

            PlcServerStatusTextBlock.Text = $"PLC模拟服务：运行中（{runningCount}台）";

            PlcServerStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));

            PlcServerStatusHintTextBlock.Text = "勾选PLC即启动，取消勾选即停止；运行中可编辑DB块和自动写入规则，保存后会自动重载PLC。";
        }
        else
        {
            PlcServerStatusEllipse.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));

            PlcServerStatusTextBlock.Text = "PLC模拟服务：未启动";

            PlcServerStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));

            PlcServerStatusHintTextBlock.Text = "外部访问地址=IP地址:代理端口；内部服务监听=127.0.0.1:内部端口。";
        }

        var allRunning = Plcs.Count > 0 &&
                         Plcs
                             .Where(x => x != null)
                             .All(x => x.IsEnabled && PlcVirtualServerService.Instance.IsPlcRunning(x.PlcId));

        ToggleAllPlcsButton.Content = allRunning
            ? "全部停用"
            : "全部启用";

        var canEditSelectedPlc = CanEditSelectedPlc;

        AddPlcButton.IsEnabled = true;

        var selectedPlcs = PlcListDataGrid?
            .SelectedItems
            .OfType<PlcConfigModel>()
            .Where(x => x != null)
            .Distinct()
            .ToList() ?? [];

        if (selectedPlcs.Count == 0 && SelectedPlc != null)
        {
            selectedPlcs.Add(SelectedPlc);
        }

        var hasSelectedRunningPlc = selectedPlcs
            .Any(x => PlcVirtualServerService.Instance.IsPlcRunning(x.PlcId));

        RemovePlcButton.IsEnabled =
            selectedPlcs.Count > 0 &&
            !hasSelectedRunningPlc;

        PlcDetailEditPanel.IsEnabled = canEditSelectedPlc;

        DbBlockEditPanel.IsEnabled = true;

        AutoWriteRuleEditPanel.IsEnabled = true;
    }

    private PlcConfigModel CreateDefaultPlc(int plcId)
    {
        var proxyPort = 102;

        var plc = new PlcConfigModel
        {
            PlcId = plcId,
            PlcName = $"PLC-{plcId}",
            IsEnabled = false,
            PlcType = "S1200",
            IpAddress = "127.0.0.1",
            ProxyPort = proxyPort,
            Port = GetNextAvailablePort(proxyPort),
            Rack = 0,
            Slot = 1,
            ConnectTimeOut = 3000,
            ReceiveTimeOut = 3000,
            PollIntervalMs = 300,
            AutoWriteEnabled = true,
            DbBlocks = [],
            AutoWriteRules = []
        };

        return plc;
    }

    private void AddOrExpandDbBlock(
        PlcConfigModel plc,
        int dbNumber,
        int length,
        string blockName,
        string remark)
    {
        if (plc == null)
        {
            return;
        }

        plc.DbBlocks ??= [];

        var target = plc.DbBlocks.FirstOrDefault(x => x.DbNumber == dbNumber);

        if (target == null)
        {
            target = new PlcDbBlockModel
            {
                DbNumber = dbNumber,
                Length = length,
                IsEnabled = true,
                BlockName = blockName,
                Remark = remark
            };

            plc.DbBlocks.Add(target);

            return;
        }

        target.IsEnabled = true;

        if (target.Length < length)
        {
            target.Length = length;
        }

        if (string.IsNullOrWhiteSpace(target.BlockName))
        {
            target.BlockName = blockName;
        }

        if (string.IsNullOrWhiteSpace(target.Remark))
        {
            target.Remark = remark;
        }
    }

    private bool ValidatePlcs(bool requireEnabledPlc)
    {
        if (Plcs.Count == 0)
        {
            if (requireEnabledPlc)
            {
                MessageBox.Show("启动PLC模拟服务前，至少需要配置一台PLC。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        foreach (var plc in Plcs.Where(x => x != null))
        {
            NormalizePlc(plc);
        }

        if (requireEnabledPlc && Plcs.All(x => x == null || !x.IsEnabled))
        {
            MessageBox.Show("启动PLC模拟服务前，至少需要启用一台PLC。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var duplicatePlcId = Plcs
            .Where(x => x != null)
            .GroupBy(x => x.PlcId)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicatePlcId != null)
        {
            MessageBox.Show($"PLC ID重复：{duplicatePlcId.Key}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var duplicateInternalPort = Plcs
            .Where(x => x != null)
            .Where(x => x.IsEnabled)
            .GroupBy(x => x.Port)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicateInternalPort != null)
        {
            MessageBox.Show(
                $"启用的PLC内部端口重复：{duplicateInternalPort.Key}。每台虚拟PLC内部端口必须不同。",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var duplicateExternalEndpoint = Plcs
            .Where(x => x != null)
            .Where(x => x.IsEnabled)
            .GroupBy(x => $"{x.IpAddress?.Trim()}:{x.ProxyPort}")
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicateExternalEndpoint != null)
        {
            MessageBox.Show(
                $"启用的PLC外部代理地址重复：{duplicateExternalEndpoint.Key}。同一个外部IP和代理端口只能配置一台。",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        foreach (var plc in Plcs)
        {
            if (plc == null)
            {
                continue;
            }

            if (plc.PlcId <= 0)
            {
                MessageBox.Show($"{plc.PlcName} 的PLC ID必须大于0。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(plc.PlcName))
            {
                MessageBox.Show("PLC名称不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(plc.PlcType))
            {
                MessageBox.Show($"{plc.PlcName} 的PLC类型不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(plc.IpAddress))
            {
                MessageBox.Show($"{plc.PlcName} 的IP地址不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!IPAddress.TryParse(plc.IpAddress.Trim(), out _))
            {
                MessageBox.Show($"{plc.PlcName} 的IP地址格式不正确：{plc.IpAddress}", "提示", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (plc.ProxyPort <= 0 || plc.ProxyPort > 65535)
            {
                MessageBox.Show($"{plc.PlcName} 的代理端口范围不正确。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (plc.Port <= 0 || plc.Port > 65535)
            {
                MessageBox.Show($"{plc.PlcName} 的内部端口范围不正确。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (plc.Port == plc.ProxyPort)
            {
                MessageBox.Show(
                    $"{plc.PlcName} 的内部端口不能和代理端口相同。建议：代理端口=102，内部端口=1101/1102/1103。",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (plc.Rack < 0)
            {
                MessageBox.Show($"{plc.PlcName} 的Rack不能小于0。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (plc.Slot < 0)
            {
                MessageBox.Show($"{plc.PlcName} 的Slot不能小于0。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (plc.ConnectTimeOut <= 0)
            {
                MessageBox.Show($"{plc.PlcName} 的连接超时必须大于0。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (plc.ReceiveTimeOut <= 0)
            {
                MessageBox.Show($"{plc.PlcName} 的接收超时必须大于0。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (plc.PollIntervalMs <= 0)
            {
                MessageBox.Show($"{plc.PlcName} 的轮询间隔必须大于0。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!ValidateDbBlocks(plc))
            {
                return false;
            }

            if (!ValidateDbBlockRuleRefs(plc))
            {
                return false;
            }

            if (!ValidateDbBlockResetItems(plc))
            {
                return false;
            }

            if (!ValidateDbBlockProtocolItems(plc))
            {
                return false;
            }
        }

        return true;
    }

    private bool ValidateDbBlocks(PlcConfigModel plc)
    {
        plc.DbBlocks ??= [];

        var duplicateDbBlock = plc.DbBlocks
            .Where(x => x != null)
            .Where(x => x.IsEnabled)
            .GroupBy(x => x.DbNumber)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicateDbBlock != null)
        {
            MessageBox.Show(
                $"{plc.PlcName} 的DB块号重复：DB{duplicateDbBlock.Key}",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        foreach (var dbBlock in plc.DbBlocks)
        {
            if (dbBlock == null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(dbBlock.BlockName) && dbBlock.DbNumber > 0)
            {
                dbBlock.BlockName = $"DB{dbBlock.DbNumber}";
            }

            if (!dbBlock.IsEnabled)
            {
                continue;
            }

            if (dbBlock.DbNumber <= 0)
            {
                MessageBox.Show($"{plc.PlcName} 存在DB块号小于等于0。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (dbBlock.Length <= 0)
            {
                MessageBox.Show(
                    $"{plc.PlcName} 的DB{dbBlock.DbNumber} 长度必须大于0。",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
        }

        return true;
    }

    private bool ValidateDbBlockRuleRefs(PlcConfigModel plc)
    {
        if (plc == null || plc.DbBlocks == null)
        {
            return true;
        }

        var ruleMap = GetGlobalRuleMap();

        foreach (var dbBlock in plc.DbBlocks)
        {
            if (dbBlock == null)
            {
                continue;
            }

            dbBlock.AutoWriteRuleList ??= [];

            var duplicateRuleId = dbBlock.AutoWriteRuleList
                .Where(x => x != null)
                .Where(x => x.RuleId > 0)
                .GroupBy(x => x.RuleId)
                .FirstOrDefault(x => x.Count() > 1);

            if (duplicateRuleId != null)
            {
                MessageBox.Show(
                    $"{plc.PlcName} 的 DB{dbBlock.DbNumber} 重复绑定全局规则：RuleId={duplicateRuleId.Key}",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            foreach (var ruleRef in dbBlock.AutoWriteRuleList)
            {
                if (ruleRef == null)
                {
                    continue;
                }

                if (ruleRef.RuleRefId <= 0)
                {
                    ruleRef.RuleRefId = dbBlock.GetNextRuleRefId();
                }

                if (ruleRef.SortIndex <= 0)
                {
                    ruleRef.SortIndex = dbBlock.GetNextSortIndex();
                }

                if (!ruleRef.IsEnabled)
                {
                    continue;
                }

                if (ruleRef.RuleId <= 0)
                {
                    MessageBox.Show(
                        $"{plc.PlcName} 的 DB{dbBlock.DbNumber} 存在未选择全局规则的启用引用。",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }

                if (!ruleMap.TryGetValue(ruleRef.RuleId, out var globalRule))
                {
                    MessageBox.Show(
                        $"{plc.PlcName} 的 DB{dbBlock.DbNumber} 引用的全局规则不存在：RuleId={ruleRef.RuleId}",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }

                ruleRef.RuleName = globalRule.RuleName;
            }
        }

        return true;
    }

    private bool ValidateDbBlockResetItems(PlcConfigModel plc)
    {
        if (plc == null || plc.DbBlocks == null)
        {
            return true;
        }

        foreach (var dbBlock in plc.DbBlocks)
        {
            if (dbBlock == null)
            {
                continue;
            }

            dbBlock.ResetItems ??= [];

            var enabledResetItems = dbBlock.ResetItems
                .Where(x => x != null)
                .Where(x => x.IsEnabled)
                .ToList();

            var duplicateResetItemId = enabledResetItems
                .Where(x => x.ResetItemId > 0)
                .GroupBy(x => x.ResetItemId)
                .FirstOrDefault(x => x.Count() > 1);

            if (duplicateResetItemId != null)
            {
                MessageBox.Show(
                    $"{plc.PlcName} 的 DB{dbBlock.DbNumber} 复位项ID重复：ResetItemId={duplicateResetItemId.Key}",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            foreach (var resetItem in enabledResetItems)
            {
                if (resetItem.ResetItemId <= 0)
                {
                    resetItem.ResetItemId = dbBlock.GetNextResetItemId();
                }

                if (string.IsNullOrWhiteSpace(resetItem.ResetName))
                {
                    resetItem.ResetName = $"复位项-{resetItem.ResetItemId}";
                }

                if (string.IsNullOrWhiteSpace(resetItem.ResetAddress))
                {
                    MessageBox.Show(
                        $"{plc.PlcName} 的 DB{dbBlock.DbNumber} 复位项地址不能为空：{resetItem.ResetName}",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }

                if (!PlcDbBlockService.Instance.IsBoolDbAddressOrTemplate(resetItem.ResetAddress))
                {
                    MessageBox.Show(
                        $"{plc.PlcName} 的 DB{dbBlock.DbNumber} 复位项地址必须是Bool地址，例如 5.0，也兼容 DB{{DB}}.5.0 或 DB2090.5.0：{resetItem.ResetName}",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }
            }
        }

        return true;
    }

    private bool ValidateDbBlockProtocolItems(PlcConfigModel plc)
    {
        if (plc == null || plc.DbBlocks == null)
        {
            return true;
        }

        foreach (var dbBlock in plc.DbBlocks)
        {
            if (dbBlock == null)
            {
                continue;
            }

            dbBlock.ProtocolItems ??= [];

            var enabledProtocolItems = dbBlock.ProtocolItems
                .Where(x => x != null)
                .Where(x => x.IsEnabled)
                .ToList();

            var duplicateProtocolItemId = enabledProtocolItems
                .Where(x => x.ProtocolItemId > 0)
                .GroupBy(x => x.ProtocolItemId)
                .FirstOrDefault(x => x.Count() > 1);

            if (duplicateProtocolItemId != null)
            {
                MessageBox.Show(
                    $"{plc.PlcName} 的 DB{dbBlock.DbNumber} 协议看板项ID重复：ProtocolItemId={duplicateProtocolItemId.Key}",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            foreach (var protocolItem in enabledProtocolItems)
            {
                if (protocolItem.ProtocolItemId <= 0)
                {
                    protocolItem.ProtocolItemId = dbBlock.GetNextProtocolItemId();
                }

                if (string.IsNullOrWhiteSpace(protocolItem.SignalName))
                {
                    protocolItem.SignalName = $"信号-{protocolItem.ProtocolItemId}";
                }

                if (string.IsNullOrWhiteSpace(protocolItem.DataType))
                {
                    protocolItem.DataType = "Bool";
                }

                if (string.IsNullOrWhiteSpace(protocolItem.OffsetAddress))
                {
                    MessageBox.Show(
                        $"{plc.PlcName} 的 DB{dbBlock.DbNumber} 协议看板偏移量地址不能为空：{protocolItem.SignalName}",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }

                protocolItem.OffsetAddress = protocolItem.OffsetAddress.Trim();

                protocolItem.Remark ??= string.Empty;
            }
        }

        return true;
    }

    private bool ValidateAutoWriteRules(PlcConfigModel plc)
    {
        if (plc == null)
        {
            return true;
        }

        plc.AutoWriteRules ??= [];

        var duplicateRuleId = plc.AutoWriteRules
            .Where(x => x != null)
            .GroupBy(x => x.RuleId)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicateRuleId != null)
        {
            MessageBox.Show(
                $"{plc.PlcName} 的规则ID重复：{duplicateRuleId.Key}",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        foreach (var rule in plc.AutoWriteRules)
        {
            if (rule == null)
            {
                continue;
            }

            NormalizeAutoWriteRule(rule);

            if (!rule.IsEnabled)
            {
                continue;
            }

            if (rule.RuleId <= 0)
            {
                MessageBox.Show($"{plc.PlcName} 存在规则ID小于等于0。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(rule.RuleName))
            {
                MessageBox.Show($"{plc.PlcName} 存在规则名称为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (rule.DelayMilliseconds < 0)
            {
                MessageBox.Show(
                    $"{plc.PlcName} 的规则延时时间不能小于0：规则={rule.RuleName}",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (rule.DelayTriggerEnabled && rule.DelayMilliseconds <= 0)
            {
                MessageBox.Show(
                    $"{plc.PlcName} 的规则启用延时后，延时毫秒必须大于0：规则={rule.RuleName}",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (!ValidateAutoWriteRuleTriggers(plc, rule))
            {
                return false;
            }

            if (!ValidateAutoWriteRuleWriteItems(plc, rule))
            {
                return false;
            }

            EnsureRuleDbBlocks(plc, rule);
        }

        return true;
    }

    private bool ValidateAutoWriteRuleTriggers(PlcConfigModel plc, PlcAutoWriteRuleModel rule)
    {
        if (rule.Triggers == null || rule.Triggers.Count == 0)
        {
            MessageBox.Show(
                $"{plc.PlcName} 的规则没有配置触发条件：{rule.RuleName}",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var enabledTriggers = rule.Triggers
            .Where(x => x != null && x.IsEnabled)
            .ToList();

        if (enabledTriggers.Count == 0)
        {
            MessageBox.Show(
                $"{plc.PlcName} 的规则没有启用的触发条件：{rule.RuleName}",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var duplicateTriggerId = enabledTriggers
            .GroupBy(x => x.TriggerId)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicateTriggerId != null)
        {
            MessageBox.Show(
                $"{plc.PlcName} 的规则触发条件ID重复：规则={rule.RuleName}，TriggerId={duplicateTriggerId.Key}",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        foreach (var trigger in enabledTriggers)
        {
            if (trigger.TriggerId <= 0)
            {
                MessageBox.Show(
                    $"{plc.PlcName} 的规则存在触发条件ID小于等于0：{rule.RuleName}",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(trigger.TriggerName))
            {
                trigger.TriggerName = $"触发条件 {trigger.TriggerId}";
            }

            if (string.IsNullOrWhiteSpace(trigger.ReadAddress))
            {
                MessageBox.Show(
                    $"{plc.PlcName} 的规则触发条件读取地址为空：规则={rule.RuleName}，条件={trigger.TriggerName}",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (!PlcDbBlockService.Instance.IsBoolDbAddressOrTemplate(trigger.ReadAddress))
            {
                MessageBox.Show(
                    $"{plc.PlcName} 的规则触发条件读取地址必须是Bool地址，例如 5.0，也兼容 DB{{DB}}.5.0 或 DB2090.5.0：规则={rule.RuleName}，条件={trigger.TriggerName}",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            EnsureAddressDbBlock(plc, trigger.ReadAddress);
        }

        return true;
    }

    private void AddTriggerButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRule == null)
        {
            MessageBox.Show("请先选择自动写入规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedRule.Triggers ??= [];

        var nextId = GetNextTriggerId(SelectedRule);

        var trigger = new PlcAutoWriteTriggerModel
        {
            TriggerId = nextId,
            TriggerName = $"触发条件-{nextId}",
            IsEnabled = true,
            ReadAddress = "0.0",
            TriggerValue = true,
            DelayTriggerEnabled = false,
            DelayMilliseconds = 0,
            Remark = string.Empty
        };

        SelectedRule.Triggers.Add(trigger);

        SelectedTrigger = trigger;

        if (SelectedPlc != null)
        {
            EnsureAddressDbBlock(SelectedPlc, trigger.ReadAddress);
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
            MessageBox.Show("请先选择自动写入规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedRule.WriteItems ??= [];

        var nextId = GetNextWriteItemId(SelectedRule);

        var writeItem = new PlcAutoWriteItemModel
        {
            WriteId = nextId,
            WriteName = $"写入动作-{nextId}",
            IsEnabled = true,
            WriteAddress = "2.0",
            WriteValue = true,
            ResetWhenNotTrigger = true,
            ResetValue = false,
            Remark = string.Empty
        };

        SelectedRule.WriteItems.Add(writeItem);

        SelectedWriteItem = writeItem;

        if (SelectedPlc != null)
        {
            EnsureAddressDbBlock(SelectedPlc, writeItem.WriteAddress);
        }

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

    private bool ValidateAutoWriteRuleWriteItems(PlcConfigModel plc, PlcAutoWriteRuleModel rule)
    {
        if (rule.WriteItems == null || rule.WriteItems.Count == 0)
        {
            MessageBox.Show(
                $"{plc.PlcName} 的规则没有配置写入动作：{rule.RuleName}",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var enabledWriteItems = rule.WriteItems
            .Where(x => x != null && x.IsEnabled)
            .ToList();

        if (enabledWriteItems.Count == 0)
        {
            MessageBox.Show(
                $"{plc.PlcName} 的规则没有启用的写入动作：{rule.RuleName}",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var duplicateWriteId = enabledWriteItems
            .GroupBy(x => x.WriteId)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicateWriteId != null)
        {
            MessageBox.Show(
                $"{plc.PlcName} 的规则写入动作ID重复：规则={rule.RuleName}，WriteId={duplicateWriteId.Key}",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        foreach (var writeItem in enabledWriteItems)
        {
            if (writeItem.WriteId <= 0)
            {
                MessageBox.Show(
                    $"{plc.PlcName} 的规则存在写入动作ID小于等于0：{rule.RuleName}",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(writeItem.WriteName))
            {
                writeItem.WriteName = $"写入动作 {writeItem.WriteId}";
            }

            if (string.IsNullOrWhiteSpace(writeItem.WriteAddress))
            {
                MessageBox.Show(
                    $"{plc.PlcName} 的规则写入动作地址为空：规则={rule.RuleName}，动作={writeItem.WriteName}",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (!PlcDbBlockService.Instance.IsBoolDbAddressOrTemplate(writeItem.WriteAddress))
            {
                MessageBox.Show(
                    $"{plc.PlcName} 的规则写入动作地址必须是Bool地址，例如 5.0，也兼容 DB{{DB}}.5.0 或 DB2090.5.0：规则={rule.RuleName}，动作={writeItem.WriteName}",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            EnsureAddressDbBlock(plc, writeItem.WriteAddress);
        }

        return true;
    }

    private void NormalizeAutoWriteRules(PlcConfigModel plc)
    {
        if (plc == null)
        {
            return;
        }

        plc.AutoWriteRules ??= [];

        var ruleId = 1;

        foreach (var rule in plc.AutoWriteRules.Where(x => x != null).OrderBy(x => x.RuleId).ToList())
        {
            if (rule.RuleId <= 0)
            {
                rule.RuleId = ruleId;
            }

            NormalizeAutoWriteRule(rule);

            ruleId++;
        }
    }

    private void NormalizeAutoWriteRule(PlcAutoWriteRuleModel rule)
    {
        if (rule == null)
        {
            return;
        }

        rule.Triggers ??= [];

        rule.WriteItems ??= [];

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

    private void EnsureRuleDbBlock(PlcConfigModel plc, PlcDbBlockModel dbBlock, PlcAutoWriteRuleModel rule)
    {
        if (plc == null || dbBlock == null || rule == null)
        {
            return;
        }

        if (dbBlock.DbNumber <= 0)
        {
            return;
        }

        if (rule.Triggers != null)
        {
            foreach (var trigger in rule.Triggers)
            {
                if (trigger == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(trigger.ReadAddress))
                {
                    continue;
                }

                EnsureAddressDbBlock(plc, dbBlock.DbNumber, trigger.ReadAddress);
            }
        }

        if (rule.WriteItems != null)
        {
            foreach (var writeItem in rule.WriteItems)
            {
                if (writeItem == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(writeItem.WriteAddress))
                {
                    continue;
                }

                EnsureAddressDbBlock(plc, dbBlock.DbNumber, writeItem.WriteAddress);
            }
        }
    }

    private void EnsureRuleDbBlocks(PlcConfigModel plc, PlcAutoWriteRuleModel rule)
    {
        if (plc == null || rule == null)
        {
            return;
        }

        if (rule.Triggers != null)
        {
            foreach (var trigger in rule.Triggers)
            {
                if (trigger == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(trigger.ReadAddress))
                {
                    continue;
                }

                EnsureAddressDbBlock(plc, trigger.ReadAddress);
            }
        }

        if (rule.WriteItems != null)
        {
            foreach (var writeItem in rule.WriteItems)
            {
                if (writeItem == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(writeItem.WriteAddress))
                {
                    continue;
                }

                EnsureAddressDbBlock(plc, writeItem.WriteAddress);
            }
        }
    }

    private void EnsureAddressDbBlock(PlcConfigModel plc, string address)
    {
        if (plc == null)
        {
            return;
        }

        if (PlcDbBlockService.Instance.TryParseDbAddress(address, out var dbNumber, out _, out _))
        {
            EnsureAddressDbBlock(plc, dbNumber, address);
            return;
        }

        var fallbackDbNumber = SelectedDbBlock?.DbNumber
                               ?? plc.DbBlocks?.FirstOrDefault(x => x != null && x.DbNumber > 0)?.DbNumber
                               ?? 0;

        if (fallbackDbNumber <= 0)
        {
            return;
        }

        EnsureAddressDbBlock(plc, fallbackDbNumber, address);
    }

    private void EnsureAddressDbBlock(PlcConfigModel plc, int dbNumber, string address)
    {
        if (plc == null || dbNumber <= 0)
        {
            return;
        }

        if (!PlcDbBlockService.Instance.TryParseDbAddressOrTemplate(address, dbNumber, out _, out var byteOffset,
                out _))
        {
            return;
        }

        var requiredLength = byteOffset + 32;

        AddOrExpandDbBlock(
            plc,
            dbNumber,
            requiredLength,
            $"DB{dbNumber}",
            "根据当前DB块绑定的全局自动写入规则地址自动补充。");
    }

    private int GetNextPlcId()
    {
        if (Plcs.Count == 0)
        {
            return 1;
        }

        return Plcs.Max(x => x.PlcId) + 1;
    }

    private int GetNextAvailablePort()
    {
        return GetNextAvailablePort(102);
    }

    private int GetNextAvailablePort(int proxyPort)
    {
        var usedPorts = Plcs
            .Where(x => x != null)
            .SelectMany(x => new[]
            {
                x.Port,
                x.ProxyPort
            })
            .Where(x => x > 0)
            .ToHashSet();

        if (proxyPort > 0)
        {
            usedPorts.Add(proxyPort);
        }

        var port = 1101;

        while (usedPorts.Contains(port))
        {
            port++;
        }

        return port;
    }

    private int GetNextDbNumber(PlcConfigModel plc)
    {
        if (plc == null || plc.DbBlocks == null || plc.DbBlocks.Count == 0)
        {
            return 1500;
        }

        if (plc.DbBlocks.All(x => x.DbNumber != 1500))
        {
            return 1500;
        }

        if (plc.DbBlocks.All(x => x.DbNumber != 2090))
        {
            return 2090;
        }

        return plc.DbBlocks.Max(x => x.DbNumber) + 1;
    }

    private int GetNextRuleId(PlcConfigModel plc)
    {
        if (plc == null || plc.AutoWriteRules == null || plc.AutoWriteRules.Count == 0)
        {
            return 1;
        }

        return plc.AutoWriteRules.Max(x => x.RuleId) + 1;
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}