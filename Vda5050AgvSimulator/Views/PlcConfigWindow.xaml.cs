#nullable disable

using System;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Vda5050AgvSimulator.Helpers;
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
    // 保存配置时，DB块列表/下拉框可能因绑定刷新暂时清空选择。
    // 该标记只影响保存阶段，不干扰用户正常切换DB块。
    private bool _isSavingPlcConfig;
    private string _plcSearchText = string.Empty;
    private int _plcModeFilterIndex;
    private readonly DispatcherTimer _plcConnectionTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(1)
    };

    /// <summary>
    /// 虚拟PLC本机辅助IP使用的子网掩码。
    /// 现场不是24位网段时在这里修改。
    /// </summary>
    private const string PlcLocalSubnetMask = "255.255.255.0";

    /// <summary>
    /// 当前已打开的子弹窗。
    /// 同一时间只允许存在一个子弹窗，避免多个窗口同时打开导致句柄、焦点、悬浮异常。
    /// </summary>
    private Window _currentPopupWindow;

    /// <summary>
    /// 注意：必须是public，否则XAML ItemsSource="{Binding Plcs}" 绑定不到。
    /// </summary>
    public ObservableCollection<PlcConfigModel> Plcs { get; } = [];

    public ICollectionView PlcListView { get; }

    public IReadOnlyDictionary<int, int> PlcConnectionCounts { get; private set; } =
        new Dictionary<int, int>();

    public string PlcSearchText
    {
        get => _plcSearchText;
        set
        {
            value ??= string.Empty;
            if (_plcSearchText == value)
            {
                return;
            }

            _plcSearchText = value;
            OnPropertyChanged();
            RefreshPlcList();
        }
    }

    public int PlcModeFilterIndex
    {
        get => _plcModeFilterIndex;
        set
        {
            if (_plcModeFilterIndex == value)
            {
                return;
            }

            _plcModeFilterIndex = value;
            OnPropertyChanged();
            RefreshPlcList();
        }
    }

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
    /// 保存后会自动重载运行中的PLC；重载过程会先备份并恢复已有DB块数据。
    /// </summary>
    public bool CanEditRuntimeConfig => true;

    /// <summary>
    /// 全局自动写入规则快照。
    /// 页面只从全局规则库中选择规则，DB块只保存 RuleId 引用。
    /// </summary>
    public ObservableCollection<PlcAutoWriteRuleModel> GlobalAutoWriteRules { get; } = [];

    public IReadOnlyDictionary<int, bool> GlobalRuleEnabledStates { get; private set; } =
        new Dictionary<int, bool>();

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
            }

            OnPropertyChanged();
            SelectedDbBlock = field?.DbBlocks.FirstOrDefault();
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
            // 保存期间 SelectedPlc.DbBlocks 绑定刷新会产生临时 null，
            // 只要之前的DB仍属于当前PLC，就不让这个中间状态覆盖当前选择。
            if (_isSavingPlcConfig && value == null && field != null &&
                SelectedPlc?.DbBlocks?.Contains(field) == true)
            {
                return;
            }

            field = value;

            RefreshSelectedDbBlockRuleNames();

            SelectedDbBlockRuleRef = field?.AutoWriteRuleList?.FirstOrDefault();

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 把所有已启用PLC的IP添加为电脑网卡辅助IP。
    /// 127.0.0.1会自动跳过，已经存在的IP不会重复添加。
    /// </summary>
    private static void EnsureEnabledPlcIpAddresses(
        IEnumerable<PlcConfigModel> plcs)
    {
        if (plcs == null)
        {
            return;
        }

        var ipAddresses = plcs
            .Where(x =>
                x != null &&
                x.IsEnabled &&
                x.ConnectionMode == PlcConnectionMode.PortProxy &&
                !string.IsNullOrWhiteSpace(x.IpAddress))
            .Select(x => x.IpAddress.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var ipAddress in ipAddresses)
        {
            WindowsNetworkAddressService.Instance
                .EnsureLocalIpv4Address(
                    ipAddress,
                    PlcLocalSubnetMask);
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
        PlcListView = new ListCollectionView(Plcs) { Filter = MatchesPlcFilter };

        InitializeComponent();

        DataContext = this;

        PlcStoreService.Instance.EnsureLoadedFromConfig();

        PlcAutoWriteRuleStoreService.Instance.EnsureLoadedFromConfig();

        LoadGlobalAutoWriteRules();

        LoadPlcs(PlcStoreService.Instance.GetSnapshotList());

        // 启动配置页时同步全局停用的规则，防止历史配置中仍保留启用引用。
        DisableBindingsForGloballyDisabledRules();

        SyncPlcEnabledStateWithRunningService();

        _plcConnectionTimer.Tick += (_, _) => RefreshPlcConnectionCounts();
        Loaded += (_, _) =>
        {
            RefreshPlcConnectionCounts();
            _plcConnectionTimer.Start();
        };
        Closed += (_, _) => _plcConnectionTimer.Stop();

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

        SelectedGlobalRule = EnabledGlobalAutoWriteRules.FirstOrDefault() ?? GlobalAutoWriteRules.FirstOrDefault();

        OnPropertyChanged(nameof(GlobalAutoWriteRules));
        PublishGlobalRuleEnabledStates();
    }

    private void PublishGlobalRuleEnabledStates()
    {
        GlobalRuleEnabledStates = GlobalAutoWriteRules
            .GroupBy(rule => rule.RuleId)
            .ToDictionary(group => group.Key, group => group.First().IsEnabled);

        OnPropertyChanged(nameof(GlobalRuleEnabledStates));
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

    /// <summary>
    /// 全局规则库未启用或不存在的规则不允许在PLC本地被启用。
    /// </summary>
    private bool IsGlobalRuleEnabled(int ruleId)
    {
        return ruleId > 0 &&
               GetGlobalRuleMap().TryGetValue(ruleId, out var globalRule) &&
               globalRule != null && globalRule.IsEnabled;
    }

    /// <summary>
    /// 全局停用优先：取消页面中对应规则引用的启用状态，同时同步存储快照，
    /// 使运行中的PLC立即看到本地关闭结果，不需要重载或清空DB内存。
    /// 只处理明确存在且已停用的全局规则；不存在的规则保留原引用供验证时提示。
    /// </summary>
    private int DisableBindingsForGloballyDisabledRules()
    {
        var disabledIds = GetGlobalRuleMap()
            .Where(pair => pair.Value != null && !pair.Value.IsEnabled)
            .Select(pair => pair.Key)
            .ToHashSet();

        if (disabledIds.Count == 0)
        {
            return 0;
        }

        var disabledCount = 0;
        foreach (var plc in Plcs.Where(x => x?.DbBlocks != null))
        {
            foreach (var dbBlock in plc.DbBlocks.Where(x => x?.AutoWriteRuleList != null))
            {
                foreach (var ruleRef in dbBlock.AutoWriteRuleList.Where(x => x != null))
                {
                    if (ruleRef.IsEnabled && disabledIds.Contains(ruleRef.RuleId))
                    {
                        ruleRef.IsEnabled = false;
                        disabledCount++;
                    }
                }
            }
        }

        // 不持久化页面的其它未保存字段，仅同步全局已停用规则的运行时引用开关。
        // 无变化时跳过ReplaceAll，减少对运行中的多个PLC产生不必要的持久化操作。
        var needsStoredUpdate = PlcStoreService.Instance.GetSnapshotList()
            .Where(x => x?.DbBlocks != null)
            .SelectMany(x => x.DbBlocks.Where(db => db?.AutoWriteRuleList != null))
            .SelectMany(db => db.AutoWriteRuleList)
            .Any(ruleRef => ruleRef != null && ruleRef.IsEnabled && disabledIds.Contains(ruleRef.RuleId));

        if (needsStoredUpdate)
        {
            UpdateStoredPlcRuntimeState(storedPlcs =>
            {
                foreach (var plc in storedPlcs.Where(x => x?.DbBlocks != null))
                {
                    foreach (var dbBlock in plc.DbBlocks.Where(x => x?.AutoWriteRuleList != null))
                    {
                        foreach (var ruleRef in dbBlock.AutoWriteRuleList.Where(x => x != null))
                        {
                            if (ruleRef.IsEnabled && disabledIds.Contains(ruleRef.RuleId))
                            {
                                ruleRef.IsEnabled = false;
                            }
                        }
                    }
                }
            });
        }

        var boundRuleView = DbBlockRuleRefsDataGrid?.Items;
        if (disabledCount > 0 &&
            boundRuleView is IEditableCollectionView { IsEditingItem: false, IsAddingNew: false })
        {
            boundRuleView.Refresh();
        }

        return disabledCount;
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
        var disabledCount = RefreshGlobalRulesAndBindings();
        if (disabledCount > 0)
        {
            Growl.WarningGlobal($"全局规则库已刷新，并停用 {disabledCount} 条全局规则已关闭的 PLC 绑定。");
            return;
        }

        Growl.SuccessGlobal("全局自动写入规则已刷新。");
    }

    private int RefreshGlobalRulesAndBindings()
    {
        LoadGlobalAutoWriteRules();
        foreach (var plc in Plcs)
        {
            RefreshAllDbBlockRuleNames(plc);
        }

        return DisableBindingsForGloballyDisabledRules();
    }

    /// <summary>
    /// 判断是否已有子弹窗打开。
    /// 有则激活当前弹窗，并阻止继续打开新弹窗。
    /// </summary>
    private bool TryActivateCurrentPopupWindow()
    {
        var popupWindow = _currentPopupWindow;

        if (popupWindow == null || !Application.Current.Windows.OfType<Window>().Contains(popupWindow))
        {
            popupWindow = Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(x =>
                    x != null &&
                    x != this &&
                    IsManagedPopupWindow(x) &&
                    (x.Owner == this || x is PlcDbBlockProtocolBoardConfigWindow));
        }

        if (popupWindow == null)
        {
            _currentPopupWindow = null;
            return false;
        }

        _currentPopupWindow = popupWindow;

        ActivatePopupWindow(popupWindow);

        Growl.WarningGlobal("请先关闭当前已打开的弹窗。");

        return true;
    }

    /// <summary>
    /// 注册当前子弹窗。
    /// 子弹窗关闭后自动释放，允许再次打开其它弹窗。
    /// 注意：协议看板窗口不设置Owner，避免当前PLC配置页面最小化时一起最小化。
    /// </summary>
    private void RegisterCurrentPopupWindow(Window window)
    {
        if (window == null)
        {
            return;
        }

        if (window.Owner == null && window is not PlcDbBlockProtocolBoardConfigWindow)
        {
            window.Owner = this;
        }

        _currentPopupWindow = window;

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_currentPopupWindow, window))
            {
                _currentPopupWindow = null;
            }
        };
    }

    /// <summary>
    /// 判断是否属于当前页面统一管理的子弹窗。
    /// </summary>
    private bool IsManagedPopupWindow(Window window)
    {
        return window is PlcGlobalAutoWriteRuleWindow ||
               window is PlcDbBlockResetConfigWindow ||
               window is PlcDbBlockProtocolBoardConfigWindow ||
               window is DbBlockBitsWindow;
    }

    /// <summary>
    /// 激活并置顶当前子弹窗。
    /// </summary>
    private void ActivatePopupWindow(Window window)
    {
        if (window == null)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Focus();
    }

    private void ManageGlobalAutoWriteRulesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (TryActivateCurrentPopupWindow())
            {
                return;
            }

            var window = new PlcGlobalAutoWriteRuleWindow();

            RegisterCurrentPopupWindow(window);

            void RefreshAfterRuleEdit()
            {
                var disabledCount = RefreshGlobalRulesAndBindings();
                if (disabledCount > 0)
                {
                    Growl.WarningGlobal($"全局规则已停用，自动停用 {disabledCount} 条 PLC 规则绑定。");
                }

                OnPropertyChanged(nameof(SelectedDbBlock));
            }

            window.RulesApplied += (_, _) => RefreshAfterRuleEdit();
            window.Closed += (_, _) => RefreshAfterRuleEdit();

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

    private void GlobalRuleEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox ||
            checkBox.DataContext is not PlcAutoWriteRuleModel rule)
        {
            return;
        }

        var requestedState = checkBox.IsChecked == true;
        try
        {
            PlcAutoWriteRuleStoreService.Instance.UpdateRuleEnabled(rule.RuleId, requestedState);
            if (!GetGlobalRuleMap().TryGetValue(rule.RuleId, out var storedRule))
            {
                RefreshGlobalRulesAndBindings();
                Growl.WarningGlobal("这条规则已从全局规则库移除。");
                return;
            }

            rule.IsEnabled = storedRule.IsEnabled;
            PublishGlobalRuleEnabledStates();
            if (_currentPopupWindow is PlcGlobalAutoWriteRuleWindow editor)
            {
                editor.SyncRuleEnabled(rule.RuleId, rule.IsEnabled);
            }

            if (rule.IsEnabled != requestedState)
            {
                Growl.WarningGlobal($"规则「{rule.RuleName}」的启用状态未保存。");
                return;
            }

            var disabledCount = requestedState ? 0 : DisableBindingsForGloballyDisabledRules();
            var state = requestedState ? "已启用" : "已停用";
            Growl.SuccessGlobal(disabledCount > 0
                ? $"{state}「{rule.RuleName}」，并停用 {disabledCount} 条 PLC 绑定。"
                : $"{state}「{rule.RuleName}」。");
        }
        catch (Exception ex)
        {
            Growl.WarningGlobal("同步全局规则启用状态失败：" + ex.Message);
        }
        finally
        {
            checkBox.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget();
        }
    }


    /// <summary>
    /// 全局自动写入规则批量启停：仅更新已存储规则的开关，不保存页面其它尚未提交的PLC配置。
    /// 全局禁用后，按现有规则同步关停所有PLC/DB块对应绑定；全局重新启用不会反向强行开启绑定。
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

    private void EnableAllGlobalRulesButton_Click(object sender, RoutedEventArgs e) =>
        ApplyGlobalRulesEnabledBatch(GlobalAutoWriteRules.ToList(), true, "全部全局规则");

    private void DisableAllGlobalRulesButton_Click(object sender, RoutedEventArgs e) =>
        ApplyGlobalRulesEnabledBatch(GlobalAutoWriteRules.ToList(), false, "全部全局规则");

    private void EnableSelectedGlobalRulesButton_Click(object sender, RoutedEventArgs e) =>
        ApplyGlobalRulesEnabledBatch(GetSelectedGlobalRules(), true, "选中的全局规则");

    private void DisableSelectedGlobalRulesButton_Click(object sender, RoutedEventArgs e) =>
        ApplyGlobalRulesEnabledBatch(GetSelectedGlobalRules(), false, "选中的全局规则");

    private void ApplyGlobalRulesEnabledBatch(IEnumerable<PlcAutoWriteRuleModel> ruleSource, bool enabled, string scope)
    {
        var targets = ruleSource?
            .Where(x => x != null)
            .GroupBy(x => x.RuleId)
            .Select(x => x.First())
            .ToList() ?? new List<PlcAutoWriteRuleModel>();

        if (targets.Count == 0)
        {
            Growl.WarningGlobal("请先选择要操作的全局规则。");
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
            var changedCount = 0;
            var missedCount = 0;

            foreach (var rule in targets)
            {
                if (!savedMap.TryGetValue(rule.RuleId, out var savedRule))
                {
                    missedCount++;
                    continue;
                }

                if (savedRule.IsEnabled != enabled)
                {
                    savedRule.IsEnabled = enabled;
                    changedCount++;
                }
            }

            // 一次性提交全部开关，避免为每条规则反复保存配置。
            if (changedCount > 0)
            {
                store.ReplaceAll(savedRules);
            }

            foreach (var rule in targets)
            {
                if (!savedMap.TryGetValue(rule.RuleId, out var savedRule))
                {
                    continue;
                }

                rule.IsEnabled = savedRule.IsEnabled;
                if (_currentPopupWindow is PlcGlobalAutoWriteRuleWindow editor)
                {
                    editor.SyncRuleEnabled(rule.RuleId, rule.IsEnabled);
                }
            }

            PublishGlobalRuleEnabledStates();
            var disabledBindings = enabled ? 0 : DisableBindingsForGloballyDisabledRules();
            if (GlobalAutoWriteRulesDataGrid.Items is IEditableCollectionView
                { IsEditingItem: false, IsAddingNew: false })
            {
                // 刷新行显示时保留用户的多选，避免批量操作后自动丢失选中行。
                var selectedRowsBeforeRefresh = GlobalAutoWriteRulesDataGrid.SelectedItems.Cast<object>().ToList();
                GlobalAutoWriteRulesDataGrid.Items.Refresh();
                foreach (var selectedRow in selectedRowsBeforeRefresh)
                {
                    if (GlobalAutoWriteRulesDataGrid.Items.Contains(selectedRow) &&
                        !GlobalAutoWriteRulesDataGrid.SelectedItems.Contains(selectedRow))
                    {
                        GlobalAutoWriteRulesDataGrid.SelectedItems.Add(selectedRow);
                    }
                }
            }

            var action = enabled ? "启用" : "禁用";
            var skipped = missedCount > 0 ? $"，跳过已删除 {missedCount} 条" : string.Empty;
            var suffix = disabledBindings > 0 ? $"，同步禁用 PLC 规则绑定 {disabledBindings} 条" : string.Empty;
            Growl.SuccessGlobal($"{scope}共 {targets.Count} 条，已{action}（开关变更 {changedCount} 条）{suffix}{skipped}。");
        }
        catch (Exception ex)
        {
            Growl.WarningGlobal("批量切换全局规则失败：" + ex.Message);
            RefreshGlobalRulesAndBindings();
        }
    }

    /// <summary>
    /// 当前DB块引用批量启停：只处理当前PLC、当前DB块中对应规则引用。
    /// 启用时严格执行全局开关优先，禁用时不受全局开关限制。
    /// </summary>
    private void EnableAllDbBlockRuleRefsButton_Click(object sender, RoutedEventArgs e) =>
        ApplyDbBlockRuleRefsEnabledBatch(SelectedDbBlock?.AutoWriteRuleList, true, "全部绑定规则");

    private void DisableAllDbBlockRuleRefsButton_Click(object sender, RoutedEventArgs e) =>
        ApplyDbBlockRuleRefsEnabledBatch(SelectedDbBlock?.AutoWriteRuleList, false, "全部绑定规则");

    private void EnableSelectedDbBlockRuleRefsButton_Click(object sender, RoutedEventArgs e) =>
        ApplyDbBlockRuleRefsEnabledBatch(GetSelectedDbBlockRuleRefs(), true, "选中的绑定规则");

    private void DisableSelectedDbBlockRuleRefsButton_Click(object sender, RoutedEventArgs e) =>
        ApplyDbBlockRuleRefsEnabledBatch(GetSelectedDbBlockRuleRefs(), false, "选中的绑定规则");

    private void ApplyDbBlockRuleRefsEnabledBatch(
        IEnumerable<PlcDbBlockAutoWriteRuleRefModel> source, bool enabled, string scope)
    {
        var plc = SelectedPlc;
        var dbBlock = SelectedDbBlock;
        if (plc == null || dbBlock == null)
        {
            Growl.WarningGlobal("请先选择PLC和DB块。");
            return;
        }

        var targets = source?
            .Where(x => x != null)
            .GroupBy(x => x.RuleId)
            .Select(x => x.First())
            .ToList() ?? new List<PlcDbBlockAutoWriteRuleRefModel>();
        if (targets.Count == 0)
        {
            Growl.WarningGlobal("请先选择要操作的DB块规则绑定。");
            return;
        }

        try
        {
            var globalMap = enabled ? GetGlobalRuleMap() : null;
            var allowed = new List<PlcDbBlockAutoWriteRuleRefModel>();
            var skipped = 0;
            foreach (var ruleRef in targets)
            {
                if (enabled && (!globalMap.TryGetValue(ruleRef.RuleId, out var globalRule) || !globalRule.IsEnabled))
                {
                    skipped++;
                    continue;
                }

                allowed.Add(ruleRef);
            }

            if (allowed.Count == 0)
            {
                Growl.WarningGlobal("选中的规则全部被全局规则库禁用或已删除，不能在DB块中启用。");
                return;
            }

            var store = PlcStoreService.Instance;
            store.EnsureLoadedFromConfig();
            var storedPlcs = store.GetSnapshotList();
            var storedDbBlock = storedPlcs
                .FirstOrDefault(x => x != null && x.PlcId == plc.PlcId)?
                .DbBlocks?.FirstOrDefault(x => x != null && x.DbNumber == dbBlock.DbNumber);
            var changedStored = 0;
            var unsaved = 0;
            var changedUi = allowed.Count(x => x.IsEnabled != enabled);

            foreach (var ruleRef in allowed)
            {
                var storedRef = storedDbBlock?.AutoWriteRuleList?
                    .FirstOrDefault(x => x != null && x.RuleId == ruleRef.RuleId);
                if (storedRef == null)
                {
                    unsaved++;
                    continue;
                }

                if (storedRef.IsEnabled != enabled)
                {
                    storedRef.IsEnabled = enabled;
                    changedStored++;
                }
            }

            // 一次性更新运行时PLC绑定快照，不重新加载PLC，也不覆盖尚未保存的其它页面编辑。
            if (changedStored > 0)
            {
                store.ReplaceAll(storedPlcs);
            }

            foreach (var ruleRef in allowed)
            {
                ruleRef.IsEnabled = enabled;
            }

            if (DbBlockRuleRefsDataGrid.Items is IEditableCollectionView
                { IsEditingItem: false, IsAddingNew: false })
            {
                // 刷新行显示时保留用户的多选，避免批量操作后自动丢失选中行。
                var selectedRowsBeforeRefresh = DbBlockRuleRefsDataGrid.SelectedItems.Cast<object>().ToList();
                DbBlockRuleRefsDataGrid.Items.Refresh();
                foreach (var selectedRow in selectedRowsBeforeRefresh)
                {
                    if (DbBlockRuleRefsDataGrid.Items.Contains(selectedRow) &&
                        !DbBlockRuleRefsDataGrid.SelectedItems.Contains(selectedRow))
                    {
                        DbBlockRuleRefsDataGrid.SelectedItems.Add(selectedRow);
                    }
                }
            }

            var action = enabled ? "启用" : "禁用";
            var skipNote = skipped > 0 ? $"，跳过全局未启用 {skipped} 条" : string.Empty;
            var unsavedNote = unsaved > 0 ? $"，有 {unsaved} 条新绑定需点击‘保存配置’" : string.Empty;
            Growl.SuccessGlobal($"DB{dbBlock.DbNumber} {scope}共 {targets.Count} 条，已{action} {allowed.Count} 条（变更 {changedUi} 条）{skipNote}{unsavedNote}。");
        }
        catch (Exception ex)
        {
            Growl.WarningGlobal("批量修改DB块规则绑定失败：" + ex.Message);
        }
    }

    private List<PlcAutoWriteRuleModel> GetSelectedGlobalRules() =>
        GlobalAutoWriteRulesDataGrid.SelectedItems
            .OfType<PlcAutoWriteRuleModel>()
            .GroupBy(rule => rule.RuleId)
            .Select(group => group.First())
            .ToList();

    private List<PlcDbBlockAutoWriteRuleRefModel> GetSelectedDbBlockRuleRefs() =>
        DbBlockRuleRefsDataGrid.SelectedItems
            .OfType<PlcDbBlockAutoWriteRuleRefModel>()
            .Distinct()
            .ToList();

    private void BindSelectedGlobalRuleButton_Click(object sender, RoutedEventArgs e)
    {
        var plc = SelectedPlc;
        var dbBlock = SelectedDbBlock;
        if (plc == null || dbBlock == null)
        {
            Growl.WarningGlobal("请先选择 PLC 和 DB 块。");
            return;
        }

        var selectedRules = GetSelectedGlobalRules();
        if (selectedRules.Count == 0)
        {
            Growl.WarningGlobal("请先选择要绑定的全局规则。");
            return;
        }

        var currentGlobalRules = GetGlobalRuleMap();
        var addedRuleRefs = new List<PlcDbBlockAutoWriteRuleRefModel>();
        var alreadyBoundCount = 0;
        var unavailableCount = 0;
        var disabledBindingCount = 0;
        dbBlock.AutoWriteRuleList ??= [];

        foreach (var selectedRule in selectedRules)
        {
            if (selectedRule.RuleId <= 0 || !currentGlobalRules.TryGetValue(selectedRule.RuleId, out var rule))
            {
                unavailableCount++;
                continue;
            }

            if (dbBlock.ContainsRule(rule.RuleId))
            {
                alreadyBoundCount++;
                continue;
            }

            var ruleRef = dbBlock.AddRuleRef(rule.RuleId, rule.RuleName);
            if (!rule.IsEnabled)
            {
                ruleRef.IsEnabled = false;
                disabledBindingCount++;
            }

            addedRuleRefs.Add(ruleRef);
            EnsureRuleDbBlock(plc, dbBlock, rule);
        }

        if (addedRuleRefs.Count > 0)
        {
            if (addedRuleRefs.Count > disabledBindingCount)
            {
                dbBlock.AutoWriteEnabled = true;
            }

            RefreshAllDbBlockRuleNames(plc);
            SelectedDbBlockRuleRef = addedRuleRefs.Last();
            OnPropertyChanged(nameof(SelectedDbBlock));
        }

        var disabledNote = disabledBindingCount > 0 ? $"，其中 {disabledBindingCount} 条为停用绑定" : string.Empty;
        var summary = $"DB{dbBlock.DbNumber} 新增绑定 {addedRuleRefs.Count} 条{disabledNote}。";
        if (addedRuleRefs.Count > 0)
        {
            summary += "点击“保存配置”保存绑定。";
        }

        if (alreadyBoundCount > 0 || unavailableCount > 0)
        {
            Growl.WarningGlobal($"{summary} 跳过已绑定 {alreadyBoundCount} 条、已失效 {unavailableCount} 条。");
            return;
        }

        Growl.SuccessGlobal(summary);
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

        if (plc.ConnectionMode == PlcConnectionMode.PortProxy)
        {
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

    private void RefreshPlcConnectionCounts()
    {
        var counts = new Dictionary<int, int>();
        foreach (var plc in Plcs.Where(x => x != null))
        {
            counts[plc.PlcId] = PlcVirtualServerService.Instance.GetPlcConnectionCount(plc.PlcId);
        }

        if (counts.Count == PlcConnectionCounts.Count && counts.All(entry =>
                PlcConnectionCounts.TryGetValue(entry.Key, out var previous) && previous == entry.Value))
        {
            return;
        }

        PlcConnectionCounts = counts;
        OnPropertyChanged(nameof(PlcConnectionCounts));
    }

    private bool MatchesPlcFilter(object item)
    {
        if (item is not PlcConfigModel plc)
        {
            return false;
        }

        PlcConnectionMode? mode = PlcModeFilterIndex switch
        {
            1 => PlcConnectionMode.PortProxy,
            2 => PlcConnectionMode.Direct,
            _ => null
        };
        if (mode.HasValue && plc.ConnectionMode != mode.Value)
        {
            return false;
        }

        var search = PlcSearchText.Trim();
        if (search.Length == 0)
        {
            return true;
        }

        return plc.PlcId.ToString(CultureInfo.InvariantCulture).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
               (plc.PlcName?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
               $"{plc.IpAddress}:{plc.ProxyPort}".IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
               $"{plc.DirectListenAddress}:{plc.DirectListenPort}".IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void RefreshPlcList()
    {
        var previousPlc = SelectedPlc;
        var previousDbBlock = SelectedDbBlock;
        PlcListView.Refresh();

        if (previousPlc != null && PlcListView.Contains(previousPlc) &&
            !ReferenceEquals(SelectedPlc, previousPlc))
        {
            SelectedPlc = previousPlc;
        }

        if (previousDbBlock != null && ReferenceEquals(SelectedPlc, previousPlc) &&
            previousPlc?.DbBlocks?.Contains(previousDbBlock) == true &&
            !ReferenceEquals(SelectedDbBlock, previousDbBlock))
        {
            SelectedDbBlock = previousDbBlock;
        }

        RefreshPlcServerUi();
    }

    private void ClearPlcFilter()
    {
        _plcSearchText = string.Empty;
        _plcModeFilterIndex = 0;
        OnPropertyChanged(nameof(PlcSearchText));
        OnPropertyChanged(nameof(PlcModeFilterIndex));
        RefreshPlcList();
    }

    private void ClearPlcFilterButton_Click(object sender, RoutedEventArgs e)
    {
        ClearPlcFilter();
        PlcSearchTextBox.Focus();
    }

    private void CopyPlcEndpointButton_Click(object sender, RoutedEventArgs e)
    {
        var plc = SelectedPlc;
        if (plc == null || !PlcListView.Contains(plc))
        {
            return;
        }

        var endpoint = plc.ConnectionMode == PlcConnectionMode.Direct
            ? plc.GetListeningEndPoint()
            : plc.GetProxyEndPoint();
        if (endpoint == null || endpoint.Address.Equals(IPAddress.Any) ||
            endpoint.Address.Equals(IPAddress.Broadcast))
        {
            Growl.WarningGlobal($"{plc.PlcName} 的连接端点无效，请先检查地址和端口。");
            return;
        }

        try
        {
            Clipboard.SetText(endpoint.ToString());
            Growl.SuccessGlobal($"已复制 {plc.PlcName} 的连接端点。");
        }
        catch (ExternalException)
        {
            Growl.WarningGlobal("剪贴板暂时不可用，请重试。");
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

        if (!MatchesPlcFilter(plc))
        {
            ClearPlcFilter();
        }

        Plcs.Add(plc);

        SelectedPlc = plc;

        OnPropertyChanged(nameof(Plcs));

        SaveCurrentPlcsToStore(false);

        Growl.SuccessGlobal($"已新增 {plc.PlcName}，默认未启用。配置完成后勾选启用即可启动。");

        RefreshPlcServerUi();
    }

    /// <summary>
    /// 批量新增PLC时每一行的自定义参数。
    /// 清单的最后一列可覆盖本批默认 DB 块，空列沿用默认值。
    /// </summary>
    private sealed class BatchPlcInput
    {
        public string IpAddress { get; set; } = string.Empty;
        public string PlcName { get; set; } = string.Empty;
        public int? ProxyPort { get; set; }
        public int? ListenPort { get; set; }
        public List<(int DbNumber, int Length)> DbBlocks { get; set; }
    }

    /// <summary>
    /// 批量添加网卡辅助IP时的单条输入。
    /// </summary>
    private sealed class BatchIpv4Input
    {
        public string IpAddress { get; set; } = string.Empty;
        public string SubnetMask { get; set; } = string.Empty;
    }

    private sealed class BatchIpv4Adapter
    {
        public BatchIpv4Adapter(int interfaceIndex, string adapterId)
        {
            InterfaceIndex = interfaceIndex;
            AdapterId = adapterId;
        }

        public int InterfaceIndex { get; }
        public string AdapterId { get; }
    }

    private enum BatchIpv4DeleteState
    {
        Removed,
        AlreadyAbsent,
        Skipped,
        Failed
    }

    private void BatchAddPlcsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowBatchAddPlcsDialog();
    }

    private void BatchAddNetworkIpButton_Click(object sender, RoutedEventArgs e)
    {
        ShowBatchAddNetworkIpDialog();
    }

    /// <summary>
    /// 批量自定义添加PLC窗口。
    /// 支持连续IP生成和逐行自定义IP清单，两种模式共用编号、名称、连接方式、端口及DB模板配置。
    /// </summary>
    private void ShowBatchAddPlcsDialog()
    {
        var dialog = CreateBatchDialog("批量添加 PLC");
        var idStartBox = CreateBatchTextBox(GetNextPlcId().ToString(CultureInfo.InvariantCulture));
        var nameTemplateBox = CreateBatchTextBox("PLC-{id}");
        var connectionModeCombo = CreateBatchComboBox(["端口代理", "直连"], 0);
        var plcTypeBox = CreateBatchTextBox("S1200");
        var proxyPortBox = CreateBatchTextBox("102");
        var portStartBox = CreateBatchTextBox("1101");
        const int customDbPresetIndex = 3;
        var dbPresetCombo = CreateBatchComboBox(["不添加DB块", "仅DB1500", "DB1500 + DB2090", "自定义 DB 块"], 2);
        var customDbBox = CreateBatchTextBox(string.Empty);
        var rackBox = CreateBatchTextBox("0");
        var slotBox = CreateBatchTextBox("1");
        var connectTimeoutBox = CreateBatchTextBox("3000");
        var receiveTimeoutBox = CreateBatchTextBox("3000");
        var pollIntervalBox = CreateBatchTextBox("300");

        var deviceGrid = CreateBatchFormGrid(2);
        AddBatchField(deviceGrid, 0, 0, "PLC 起始编号", idStartBox);
        AddBatchField(deviceGrid, 0, 2, "PLC 类型", plcTypeBox);
        AddBatchField(deviceGrid, 1, 0, "名称模板（{id} / {index} / {ip}）", nameTemplateBox, 3);

        var connectionGrid = CreateBatchFormGrid(3);
        AddBatchField(connectionGrid, 0, 0, "连接方式", connectionModeCombo);
        AddBatchField(connectionGrid, 0, 2, "默认 DB 块", dbPresetCombo);
        AddBatchField(connectionGrid, 1, 0, "代理端口", proxyPortBox);
        var portStartField = CreateBatchFieldPanel("内部端口起始", portStartBox);
        var portStartLabel = (TextBlock)portStartField.Children[0];
        Grid.SetRow(portStartField, 1);
        Grid.SetColumn(portStartField, 2);
        connectionGrid.Children.Add(portStartField);

        var dbListField = CreateBatchFieldPanel("本批默认 DB 块清单", customDbBox);
        dbListField.Children.Add(CreateBatchHint("例如 DB313; DB1500:1024。冒号后为字节数，省略时为 512 字节。"));
        dbListField.Visibility = Visibility.Collapsed;
        Grid.SetRow(dbListField, 2);
        Grid.SetColumnSpan(dbListField, 3);
        connectionGrid.Children.Add(dbListField);
        dbPresetCombo.SelectionChanged += (_, _) =>
        {
            dbListField.Visibility = dbPresetCombo.SelectedIndex == customDbPresetIndex
                ? Visibility.Visible
                : Visibility.Collapsed;
        };

        var communicationGrid = CreateBatchFormGrid(3);
        AddBatchField(communicationGrid, 0, 0, "Rack", rackBox);
        AddBatchField(communicationGrid, 0, 2, "Slot", slotBox);
        AddBatchField(communicationGrid, 1, 0, "连接超时（ms）", connectTimeoutBox);
        AddBatchField(communicationGrid, 1, 2, "接收超时（ms）", receiveTimeoutBox);
        AddBatchField(communicationGrid, 2, 0, "轮询间隔（ms）", pollIntervalBox);

        connectionModeCombo.SelectionChanged += (_, _) =>
        {
            var isDirect = connectionModeCombo.SelectedIndex == 1;
            proxyPortBox.IsEnabled = !isDirect;
            portStartLabel.Text = isDirect ? "直连端口起始" : "内部端口起始";
            System.Windows.Automation.AutomationProperties.SetName(portStartBox, portStartLabel.Text);

            if (isDirect && portStartBox.Text.Trim() == "1101")
            {
                portStartBox.Text = "102";
            }
            else if (!isDirect && portStartBox.Text.Trim() == "102")
            {
                portStartBox.Text = "1101";
            }
        };

        var customFormatText = CreateBatchHint();
        var (tabControl, countBox, startIpBox, rangePreview, customIpListBox, fillExampleButton) =
            CreateBatchAddressEditor(500, customFormatText, "内部端口或直连端口按生成顺序递增。");
        fillExampleButton.Click += (_, _) =>
        {
            customIpListBox.Text = connectionModeCombo.SelectedIndex == 1
                ? "# IP,名称,直连端口,DB块\r\n192.168.1.101,PLC-101,102,DB313;DB1500:1024\r\n192.168.1.102,PLC-102,103"
                : "# IP,名称,代理端口,内部端口,DB块\r\n192.168.1.101,PLC-101,102,1101,DB313;DB1500:1024\r\n192.168.1.102,PLC-102,102,1102";
        };

        void RefreshCustomFormatText()
        {
            var columns = connectionModeCombo.SelectedIndex == 1
                ? "IP,名称,直连端口,DB块。直连 IP 须已绑定到本机。"
                : "IP,名称,代理端口,内部端口,DB块。内部端口须唯一。";
            customFormatText.Text = columns + "除 IP 外均可省略，跳过中间列时保留逗号。"
                + "DB 列留空沿用默认值，填“无”跳过创建，多个块用分号隔开。";
        }

        var left = new StackPanel();
        left.Children.Add(CreateBatchCard(CreateBatchSectionTitle("设备信息"), deviceGrid));
        left.Children.Add(CreateBatchCard(CreateBatchSectionTitle("待添加地址"), tabControl));
        var right = new StackPanel();
        right.Children.Add(CreateBatchCard(CreateBatchSectionTitle("连接与 DB"), connectionGrid));
        right.Children.Add(CreateBatchCard(CreateBatchSectionTitle("通信参数"), communicationGrid));

        var summaryText = CreateBatchHint();
        var addButton = CreateBatchButton("批量添加", "PrimaryButtonStyle", 132);
        SetBatchDialogContent(dialog, "新建 PLC 默认停用，核对连接和地址后可在列表中启用。",
            "M 2,2 H 18 V 16 H 2 Z M 2,7 H 18 M 6,11 H 7 M 10,11 H 11 M 14,11 H 15 M 6,16 V 19 M 14,16 V 19",
            left, right, summaryText, addButton);

        int GetExpectedCount()
        {
            if (tabControl.SelectedIndex == 0)
            {
                return int.TryParse(countBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var count) && count > 0 ? count : 0;
            }

            return customIpListBox.Text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Count(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("#", StringComparison.Ordinal));
        }

        void RefreshSummary()
        {
            var count = GetExpectedCount();
            if (tabControl.SelectedIndex == 0)
            {
                RefreshBatchRangePreview(rangePreview, startIpBox.Text, count, 500);
            }

            var modeText = connectionModeCombo.SelectedIndex == 1 ? "直连" : "端口代理";
            summaryText.Text = count > 0
                ? $"预计添加 {count} 台 PLC · {modeText} · 编号从 {idStartBox.Text.Trim()} 开始"
                : "请填写有效的添加数量或自定义IP清单。";
            addButton.Content = count > 0 ? $"添加 {count} 台" : "批量添加";
            RefreshCustomFormatText();
        }

        countBox.TextChanged += (_, _) => RefreshSummary();
        startIpBox.TextChanged += (_, _) => RefreshSummary();
        customIpListBox.TextChanged += (_, _) => RefreshSummary();
        idStartBox.TextChanged += (_, _) => RefreshSummary();
        connectionModeCombo.SelectionChanged += (_, _) => RefreshSummary();
        tabControl.SelectionChanged += (_, _) => RefreshSummary();

        addButton.Click += (_, _) =>
        {
            if (!int.TryParse(idStartBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var startId) || startId <= 0)
            {
                MessageBox.Show(dialog, "PLC起始编号必须是大于0的整数。", "批量添加PLC", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                idStartBox.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(nameTemplateBox.Text))
            {
                MessageBox.Show(dialog, "名称模板不能为空。", "批量添加PLC", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                nameTemplateBox.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(plcTypeBox.Text))
            {
                MessageBox.Show(dialog, "PLC类型不能为空。", "批量添加PLC", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                plcTypeBox.Focus();
                return;
            }

            var isDirect = connectionModeCombo.SelectedIndex == 1;
            var connectionMode = isDirect ? PlcConnectionMode.Direct : PlcConnectionMode.PortProxy;

            if (!TryParseBatchPositiveInt(portStartBox.Text, out var portStart) || portStart > 65535)
            {
                MessageBox.Show(dialog, "端口起始值必须在1至65535之间。", "批量添加PLC", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                portStartBox.Focus();
                return;
            }

            var commonProxyPort = 102;
            if (!isDirect && (!TryParseBatchPositiveInt(proxyPortBox.Text, out commonProxyPort) || commonProxyPort > 65535))
            {
                MessageBox.Show(dialog, "代理端口必须在1至65535之间。", "批量添加PLC", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                proxyPortBox.Focus();
                return;
            }

            if (!TryParseBatchNonNegativeInt(rackBox.Text, out var rack) ||
                !TryParseBatchNonNegativeInt(slotBox.Text, out var slot) ||
                !TryParseBatchPositiveInt(connectTimeoutBox.Text, out var connectTimeout) ||
                !TryParseBatchPositiveInt(receiveTimeoutBox.Text, out var receiveTimeout) ||
                !TryParseBatchPositiveInt(pollIntervalBox.Text, out var pollInterval))
            {
                MessageBox.Show(dialog, "Rack、Slot必须是非负整数；超时和轮询间隔必须是大于0的整数。", "批量添加PLC",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var inputs = new List<BatchPlcInput>();
            if (tabControl.SelectedIndex == 0)
            {
                if (!int.TryParse(countBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var count) || count < 1 || count > 500)
                {
                    MessageBox.Show(dialog, "连续生成数量必须在1至500之间。", "批量添加PLC", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    countBox.Focus();
                    return;
                }

                if (!IPAddress.TryParse(startIpBox.Text.Trim(), out var startAddress) ||
                    startAddress.AddressFamily != AddressFamily.InterNetwork)
                {
                    MessageBox.Show(dialog, "起始IP必须是有效的IPv4地址。", "批量添加PLC", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    startIpBox.Focus();
                    return;
                }

                for (var index = 0; index < count; index++)
                {
                    if (!TryIncrementIpv4Address(startAddress, index, out var ipAddress))
                    {
                        MessageBox.Show(dialog, "IP递增超出IPv4地址范围，请调整起始IP或添加数量。", "批量添加PLC",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    inputs.Add(new BatchPlcInput { IpAddress = ipAddress });
                }
            }
            else
            {
                var lines = customIpListBox.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                {
                    var line = lines[lineIndex].Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parts = line.Split(',');
                    var maxParts = isDirect ? 4 : 5;
                    if (parts.Length > maxParts)
                    {
                        MessageBox.Show(dialog,
                            $"第 {lineIndex + 1} 行字段过多。" +
                            (isDirect ? "直连格式为 IP,名称,直连端口,DB块。" : "代理格式为 IP,名称,代理端口,内部端口,DB块。") +
                            "同一行的多个 DB 块用分号隔开。",
                            "批量添加PLC", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var ipAddress = parts[0].Trim();
                    if (!IPAddress.TryParse(ipAddress, out var parsedIp) ||
                        parsedIp.AddressFamily != AddressFamily.InterNetwork)
                    {
                        MessageBox.Show(dialog, $"第 {lineIndex + 1} 行IP地址无效：{ipAddress}", "批量添加PLC",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var input = new BatchPlcInput
                    {
                        IpAddress = parsedIp.ToString(),
                        PlcName = parts.Length >= 2 ? parts[1].Trim() : string.Empty
                    };

                    if (parts.Length == maxParts && !string.IsNullOrWhiteSpace(parts[maxParts - 1]))
                    {
                        if (!TryParseBatchDbBlocks(parts[maxParts - 1], out var rowDbBlocks, out var rowDbError))
                        {
                            MessageBox.Show(dialog, $"第 {lineIndex + 1} 行的 DB 块设置有误。{rowDbError}",
                                "批量添加 PLC", MessageBoxButton.OK, MessageBoxImage.Warning);
                            customIpListBox.Focus();
                            return;
                        }

                        input.DbBlocks = rowDbBlocks;
                    }

                    if (isDirect)
                    {
                        int? listenPort = null;
                        if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]))
                        {
                            if (!TryParseBatchPositiveInt(parts[2], out var parsedPort) || parsedPort > 65535)
                            {
                                MessageBox.Show(dialog, $"第 {lineIndex + 1} 行直连端口无效：{parts[2]}",
                                    "批量添加PLC", MessageBoxButton.OK, MessageBoxImage.Warning);
                                return;
                            }

                            listenPort = parsedPort;
                        }

                        input.ListenPort = listenPort;
                    }
                    else
                    {
                        int? lineProxyPort = null;
                        int? lineInternalPort = null;
                        if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]))
                        {
                            if (!TryParseBatchPositiveInt(parts[2], out var parsedProxyPort) || parsedProxyPort > 65535)
                            {
                                MessageBox.Show(dialog, $"第 {lineIndex + 1} 行代理端口无效：{parts[2]}",
                                    "批量添加PLC", MessageBoxButton.OK, MessageBoxImage.Warning);
                                return;
                            }

                            lineProxyPort = parsedProxyPort;
                        }

                        if (parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3]))
                        {
                            if (!TryParseBatchPositiveInt(parts[3], out var parsedInternalPort) || parsedInternalPort > 65535)
                            {
                                MessageBox.Show(dialog, $"第 {lineIndex + 1} 行内部端口无效：{parts[3]}",
                                    "批量添加PLC", MessageBoxButton.OK, MessageBoxImage.Warning);
                                return;
                            }

                            lineInternalPort = parsedInternalPort;
                        }

                        input.ProxyPort = lineProxyPort;
                        input.ListenPort = lineInternalPort;
                    }

                    inputs.Add(input);
                    if (inputs.Count > 500)
                    {
                        MessageBox.Show(dialog, "一次最多添加500台PLC。", "批量添加PLC", MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }
                }

                if (inputs.Count == 0)
                {
                    MessageBox.Show(dialog, "自定义IP清单为空，请至少填写一个IPv4地址。", "批量添加PLC",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var defaultDbText = dbPresetCombo.SelectedIndex switch
            {
                0 => "无",
                1 => "DB1500",
                2 => "DB1500;DB2090",
                _ => customDbBox.Text
            };
            List<(int DbNumber, int Length)> defaultDbBlocks = [];
            if (inputs.Any(input => input.DbBlocks == null) &&
                !TryParseBatchDbBlocks(defaultDbText, out defaultDbBlocks, out var defaultDbError))
            {
                MessageBox.Show(dialog, defaultDbError, "批量添加 PLC", MessageBoxButton.OK, MessageBoxImage.Warning);
                customDbBox.Focus();
                return;
            }

            for (var index = 0; index < inputs.Count; index++)
            {
                var effectiveListenPort = inputs[index].ListenPort ?? (portStart + index);
                if (effectiveListenPort < 1 || effectiveListenPort > 65535)
                {
                    MessageBox.Show(dialog,
                        $"第 {index + 1} 台PLC计算得到的监听端口 {effectiveListenPort} 超出1至65535范围，请调整端口起始值或自定义端口。",
                        "批量添加PLC", MessageBoxButton.OK, MessageBoxImage.Warning);
                    portStartBox.Focus();
                    return;
                }
            }

            if ((long)startId + inputs.Count - 1 > int.MaxValue)
            {
                MessageBox.Show(dialog, "PLC编号超出整数范围。", "批量添加PLC", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var existingIds = Plcs.Where(x => x != null).Select(x => x.PlcId).ToHashSet();
            for (var index = 0; index < inputs.Count; index++)
            {
                var plcId = startId + index;
                if (existingIds.Contains(plcId))
                {
                    MessageBox.Show(dialog, $"PLC编号 {plcId} 已存在，请调整起始编号。", "批量添加PLC",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var createdPlcs = new List<PlcConfigModel>();
            for (var index = 0; index < inputs.Count; index++)
            {
                var input = inputs[index];
                var plcId = startId + index;
                var plc = CreateDefaultPlc(plcId);
                plc.PlcName = string.IsNullOrWhiteSpace(input.PlcName)
                    ? FormatBatchPlcName(nameTemplateBox.Text.Trim(), plcId, index + 1, input.IpAddress)
                    : input.PlcName.Trim();
                plc.PlcType = plcTypeBox.Text.Trim();
                plc.ConnectionMode = connectionMode;
                plc.Rack = rack;
                plc.Slot = slot;
                plc.ConnectTimeOut = connectTimeout;
                plc.ReceiveTimeOut = receiveTimeout;
                plc.PollIntervalMs = pollInterval;
                plc.IsEnabled = false;

                if (isDirect)
                {
                    plc.DirectListenAddress = input.IpAddress;
                    plc.DirectListenPort = input.ListenPort ?? (portStart + index);
                }
                else
                {
                    plc.IpAddress = input.IpAddress;
                    plc.ProxyPort = input.ProxyPort ?? commonProxyPort;
                    plc.Port = input.ListenPort ?? (portStart + index);
                }

                foreach (var (dbNumber, length) in input.DbBlocks ?? defaultDbBlocks)
                {
                    var remark = dbNumber switch
                    {
                        1500 => "批量添加时自动创建的默认工站交互DB块。",
                        2090 => "批量添加时自动创建的默认读取信号DB块。",
                        _ => "批量添加时创建的自定义DB块。"
                    };
                    AddOrExpandDbBlock(plc, dbNumber, length, $"DB{dbNumber}", remark);
                }

                createdPlcs.Add(plc);
            }

            var createdNames = createdPlcs.Select(x => x.PlcName).ToList();
            var duplicateName = createdNames
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateName != null)
            {
                MessageBox.Show(dialog, $"生成的PLC名称重复：{duplicateName.Key}。请在名称模板中加入 {{id}} 或 {{index}}。",
                    "批量添加PLC", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var occupiedEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var existingPlc in Plcs.Where(x => x != null))
            {
                var listeningEndpoint = existingPlc.GetListeningEndPoint();
                if (listeningEndpoint != null)
                {
                    occupiedEndpoints.Add(listeningEndpoint.ToString());
                }

                if (existingPlc.ConnectionMode == PlcConnectionMode.PortProxy)
                {
                    var proxyEndpoint = existingPlc.GetProxyEndPoint();
                    if (proxyEndpoint != null)
                    {
                        occupiedEndpoints.Add(proxyEndpoint.ToString());
                    }
                }
            }

            foreach (var plc in createdPlcs)
            {
                var listeningEndpoint = plc.GetListeningEndPoint();
                if (listeningEndpoint != null && !occupiedEndpoints.Add(listeningEndpoint.ToString()))
                {
                    MessageBox.Show(dialog, $"PLC {plc.PlcName} 的监听端点 {listeningEndpoint} 与现有配置或本批次其他PLC冲突。",
                        "批量添加PLC", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (plc.ConnectionMode == PlcConnectionMode.PortProxy)
                {
                    var proxyEndpoint = plc.GetProxyEndPoint();
                    if (proxyEndpoint != null && !occupiedEndpoints.Add(proxyEndpoint.ToString()))
                    {
                        MessageBox.Show(dialog, $"PLC {plc.PlcName} 的代理端点 {proxyEndpoint} 与现有配置或本批次其他PLC冲突。",
                            "批量添加PLC", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
            }

            if (!TrySavePlcBatch(Plcs.Concat(createdPlcs).ToList()))
            {
                return;
            }

            if (createdPlcs.Any(plc => !MatchesPlcFilter(plc)))
            {
                ClearPlcFilter();
            }

            foreach (var plc in createdPlcs)
            {
                Plcs.Add(plc);
            }

            SelectedPlc = createdPlcs.LastOrDefault();
            OnPropertyChanged(nameof(Plcs));

            RefreshPlcServerUi();
            Growl.SuccessGlobal($"批量添加完成：成功添加 {createdPlcs.Count} 台PLC，编号 {createdPlcs.First().PlcId} 至 {createdPlcs.Last().PlcId}。新PLC默认停用。 ");
            dialog.Close();
        };

        RefreshSummary();
        dialog.ShowDialog();
    }

    /// <summary>
    /// 批量给指定网卡添加自定义IPv4辅助地址。
    /// 支持连续IP范围和逐行自定义清单，不修改网卡原有IP、网关或DNS。
    /// </summary>
    private void ShowBatchAddNetworkIpDialog()
    {
        var networkAddressService = WindowsNetworkAddressService.Instance;
        var isAdministrator = networkAddressService.IsAdministrator();
        var isAdding = false;
        var isDeleting = false;
        var dialog = CreateBatchDialog("批量添加网卡 IPv4 地址");
        dialog.Closing += (_, e) => e.Cancel = isAdding || isDeleting;
        var adapterCombo = new ComboBox
        {
            Style = (Style)FindResource("PlcComboStyle"),
            Width = double.NaN,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "选择要添加辅助 IPv4 地址的网卡。"
        };
        System.Windows.Automation.AutomationProperties.SetName(adapterCombo, "目标网卡");

        var refreshAdapterButton = CreateBatchButton("刷新网卡", "NormalButtonStyle", 84);
        var adapterGrid = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = true };
        DockPanel.SetDock(refreshAdapterButton, Dock.Right);
        adapterGrid.Children.Add(refreshAdapterButton);
        adapterGrid.Children.Add(adapterCombo);

        var maskBox = CreateBatchTextBox("255.255.255.0");
        var maskPanel = CreateBatchFieldPanel("默认子网掩码", maskBox);
        var adminHint = CreateBatchHint();
        var permissionNote = CreateBatchNote(adminHint);
        var permissionGranted = (
            Background: CreateBatchBrush(235, 249, 242),
            Border: CreateBatchBrush(193, 228, 211),
            Text: CreateBatchBrush(20, 105, 73));
        var permissionRequired = (
            Background: CreateBatchBrush(255, 247, 231),
            Border: CreateBatchBrush(241, 220, 174),
            Text: CreateBatchBrush(128, 76, 15));

        var listFormat = CreateBatchHint("每行填写 IP 或 IP,子网掩码。省略掩码时使用默认值。");
        var (tabControl, countBox, startIpBox, rangePreview, customIpListBox, fillExampleButton) =
            CreateBatchAddressEditor(1024, listFormat);
        fillExampleButton.Click += (_, _) =>
        {
            customIpListBox.Text =
                "# 每行一个IP；也可以为单个IP单独指定子网掩码\r\n" +
                "192.168.1.101\r\n" +
                "192.168.1.102,255.255.255.0\r\n" +
                "192.168.1.103,255.255.255.128";
        };

        var existingIpv4Rows = new List<BatchIpv4Input>();
        var ipView = new ListCollectionView(existingIpv4Rows);
        var existingIpTitle = CreateBatchSectionTitle("网卡已有 IPv4 地址");
        var copyExistingIpsButton = CreateBatchButton("复制全部", "NormalButtonStyle", 84);
        copyExistingIpsButton.IsEnabled = false;
        copyExistingIpsButton.ToolTip = "复制当前网卡的所有 IPv4 地址，每行一个。";
        var copySelectedIpsButton = CreateBatchButton("复制选中", "AccentButtonStyle", 84);
        copySelectedIpsButton.IsEnabled = false;
        copySelectedIpsButton.ToolTip = "复制选中的 IPv4 地址，每行一个。";
        copySelectedIpsButton.Margin = new Thickness(0, 0, 8, 0);
        var deleteSelectedIpButton = CreateBatchButton("删除选中", "MaintenanceButtonStyle", 84);
        deleteSelectedIpButton.IsEnabled = false;
        deleteSelectedIpButton.ToolTip = "从当前网卡删除选中的静态 IPv4 地址。";
        deleteSelectedIpButton.Margin = new Thickness(8, 0, 0, 0);

        var searchBox = CreateBatchTextBox(string.Empty);
        var searchPanel = CreateBatchFieldPanel("筛选地址或子网掩码", searchBox);
        var ipTable = new DataGrid
        {
            ItemsSource = ipView,
            Style = (Style)FindResource("BatchIpv4TableStyle"),
            ColumnHeaderStyle = (Style)FindResource("BatchIpv4HeaderStyle"),
            CellStyle = (Style)FindResource(typeof(DataGridCell)),
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            IsReadOnly = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Height = 302,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow
        };
        ipTable.Columns.Add(new DataGridTextColumn
        {
            Header = "IPv4 地址",
            Binding = new Binding(nameof(BatchIpv4Input.IpAddress)),
            ElementStyle = (Style)FindResource("BatchIpv4AddressStyle"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        ipTable.Columns.Add(new DataGridTextColumn
        {
            Header = "子网掩码",
            Binding = new Binding(nameof(BatchIpv4Input.SubnetMask)),
            ElementStyle = (Style)FindResource("BatchIpv4MaskStyle"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        var selectAllIpButton = CreateBatchButton("全选", "NormalButtonStyle", 62);
        selectAllIpButton.Command = System.Windows.Input.ApplicationCommands.SelectAll;
        selectAllIpButton.CommandTarget = ipTable;
        selectAllIpButton.ToolTip = "选中当前筛选结果中的所有地址。";
        selectAllIpButton.VerticalAlignment = VerticalAlignment.Bottom;
        selectAllIpButton.Margin = new Thickness(10, 0, 0, 10);
        DockPanel.SetDock(selectAllIpButton, Dock.Right);
        var searchRow = new DockPanel { LastChildFill = true };
        searchRow.Children.Add(selectAllIpButton);
        searchRow.Children.Add(searchPanel);

        var emptyHint = CreateBatchHint("请选择目标网卡。");
        emptyHint.HorizontalAlignment = HorizontalAlignment.Center;
        emptyHint.VerticalAlignment = VerticalAlignment.Center;
        emptyHint.TextAlignment = TextAlignment.Center;
        emptyHint.Margin = new Thickness(12, 38, 12, 0);
        emptyHint.IsHitTestVisible = false;
        var tableGrid = new Grid();
        tableGrid.Children.Add(ipTable);
        tableGrid.Children.Add(emptyHint);
        var tableFrame = new Border
        {
            Style = (Style)FindResource("TableFrameStyle"),
            Child = tableGrid
        };
        var filterCount = CreateBatchHint();
        filterCount.Margin = new Thickness(0, 8, 0, 0);
        var selectedIpCount = CreateBatchHint("已选 0 个");
        selectedIpCount.VerticalAlignment = VerticalAlignment.Center;
        selectedIpCount.Margin = new Thickness(0, 0, 8, 0);
        selectedIpCount.ToolTip = "按住 Ctrl 点选多行，按住 Shift 连续选行，按 Ctrl+A 全选。";
        var copyActions = new StackPanel { Orientation = Orientation.Horizontal };
        copyActions.Children.Add(copySelectedIpsButton);
        copyActions.Children.Add(copyExistingIpsButton);
        copyActions.Children.Add(deleteSelectedIpButton);
        DockPanel.SetDock(copyActions, Dock.Right);
        var addressToolbar = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
        addressToolbar.Children.Add(copyActions);
        addressToolbar.Children.Add(selectedIpCount);
        var emptyAddressMessage = "请选择目标网卡。";

        ipView.Filter = item =>
        {
            var row = (BatchIpv4Input)item;
            var query = searchBox.Text.Trim();
            return query.Length == 0 ||
                   row.IpAddress.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   row.SubnetMask.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        };

        void RefreshExistingIpView()
        {
            ipView.Refresh();
            var total = existingIpv4Rows.Count;
            var shown = ipView.Count;
            copyExistingIpsButton.IsEnabled = total > 0;
            existingIpTitle.Text = total > 0 ? $"网卡已有 IPv4 地址（{total}）" : "网卡已有 IPv4 地址";
            emptyHint.Text = total == 0 ? emptyAddressMessage : "没有匹配的 IPv4 地址。";
            emptyHint.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
            filterCount.Text = $"显示 {shown} / {total} 个 IPv4 地址";
            filterCount.Visibility = total > 0 && !string.IsNullOrWhiteSpace(searchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        void CopyIpv4Addresses(IEnumerable<BatchIpv4Input> rows)
        {
            var addresses = rows.Select(row => row.IpAddress).ToArray();
            if (addresses.Length == 0)
            {
                return;
            }

            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, addresses));
                Growl.SuccessGlobal($"已复制 {addresses.Length} 个 IPv4 地址。");
            }
            catch (ExternalException)
            {
                Growl.WarningGlobal("剪贴板暂时不可用，请重试。");
            }
        }

        void RefreshAddressActions()
        {
            var count = ipTable.SelectedItems.Count;
            selectedIpCount.Text = $"已选 {count} 个";
            copySelectedIpsButton.IsEnabled = count > 0;
            deleteSelectedIpButton.IsEnabled = count > 0 && isAdministrator && !isAdding && !isDeleting;
        }

        searchBox.TextChanged += (_, _) => RefreshExistingIpView();
        ipTable.SelectionChanged += (_, _) => RefreshAddressActions();
        copyExistingIpsButton.Click += (_, _) => CopyIpv4Addresses(existingIpv4Rows);
        copySelectedIpsButton.Click += (_, _) =>
        {
            var selectedRows = ipTable.SelectedItems.OfType<BatchIpv4Input>().ToHashSet();
            CopyIpv4Addresses(ipView.OfType<BatchIpv4Input>().Where(selectedRows.Contains));
        };

        BatchIpv4Adapter SelectedBatchIpv4Adapter() =>
            (adapterCombo.SelectedItem as ComboBoxItem)?.Tag as BatchIpv4Adapter;

        // 切换或刷新网卡时，从 Windows 按接口索引读取当前地址。
        void RefreshSelectedAdapterIpv4Addresses()
        {
            existingIpv4Rows.Clear();
            emptyAddressMessage = "请选择目标网卡。";
            var target = SelectedBatchIpv4Adapter();
            try
            {
                if (target == null)
                {
                    return;
                }

                emptyAddressMessage = "当前网卡尚未绑定 IPv4 地址。";
                var addresses = ReadBatchIpv4Interface(target).UnicastAddresses
                    .Where(x => x.Address != null && x.Address.AddressFamily == AddressFamily.InterNetwork)
                    .OrderBy(x => x.Address.GetAddressBytes()[0])
                    .ThenBy(x => x.Address.GetAddressBytes()[1])
                    .ThenBy(x => x.Address.GetAddressBytes()[2])
                    .ThenBy(x => x.Address.GetAddressBytes()[3]);

                foreach (var addressInfo in addresses)
                {
                    var subnetMask = "未知";
                    try
                    {
                        subnetMask = addressInfo.IPv4Mask?.ToString() ?? "未知";
                    }
                    catch (Exception)
                    {
                        // 某些虚拟网卡不支持读取IPv4Mask，但仍然显示其IP地址。
                    }

                    existingIpv4Rows.Add(new BatchIpv4Input
                    {
                        IpAddress = addressInfo.Address.ToString(),
                        SubnetMask = subnetMask
                    });
                }
            }
            catch (Exception ex)
            {
                existingIpv4Rows.Clear();
                emptyAddressMessage = "读取网卡 IPv4 地址失败：" + ex.Message;
            }
            finally
            {
                RefreshExistingIpView();
            }
        }

        var left = new StackPanel();
        left.Children.Add(CreateBatchCard(CreateBatchSectionTitle("目标网卡"), adapterGrid, maskPanel, permissionNote));
        left.Children.Add(CreateBatchCard(CreateBatchSectionTitle("待添加地址"), tabControl));
        var right = CreateBatchCard(existingIpTitle, searchRow, tableFrame, filterCount, addressToolbar);

        var summaryText = CreateBatchHint();
        var addButton = CreateBatchButton("批量添加 IP", "PrimaryButtonStyle", 132);
        SetBatchDialogContent(dialog, "为选中的网卡添加辅助 IPv4 地址，或管理已绑定的地址。",
            "M 8,1 H 12 V 5 H 8 Z M 1,14 H 5 V 18 H 1 Z M 8,14 H 12 V 18 H 8 Z M 15,14 H 19 V 18 H 15 Z M 10,5 V 14 M 3,14 V 9 H 17 V 14",
            left, right, summaryText, addButton);

        void RefreshAdapterOptions(BatchIpv4Adapter preferredAdapter = null)
        {
            adapterCombo.Items.Clear();
            foreach (var adapter in networkAddressService.GetConfigurableNetworkInterfaces())
            {
                int interfaceIndex;
                try
                {
                    interfaceIndex = adapter.GetIPProperties().GetIPv4Properties()?.Index ?? 0;
                }
                catch (NetworkInformationException)
                {
                    continue;
                }

                if (interfaceIndex <= 0)
                {
                    continue;
                }

                var statusText = adapter.OperationalStatus == OperationalStatus.Up ? "已连接" : "未连接";
                var identity = new BatchIpv4Adapter(interfaceIndex, adapter.Id);
                var item = new ComboBoxItem
                {
                    Content = $"{adapter.Name}  ·  {statusText}",
                    Tag = identity,
                    ToolTip = $"{adapter.Name}  |  InterfaceIndex {interfaceIndex}  |  {adapter.Description}"
                };
                adapterCombo.Items.Add(item);

                if (preferredAdapter?.InterfaceIndex == interfaceIndex &&
                    string.Equals(preferredAdapter.AdapterId, identity.AdapterId, StringComparison.OrdinalIgnoreCase))
                {
                    adapterCombo.SelectedItem = item;
                }
            }

            if (adapterCombo.SelectedItem == null && adapterCombo.Items.Count > 0)
            {
                adapterCombo.SelectedIndex = 0;
            }

            RefreshSummary();
            RefreshSelectedAdapterIpv4Addresses();
        }

        int GetExpectedCount()
        {
            if (tabControl.SelectedIndex == 0)
            {
                return int.TryParse(countBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var count) && count > 0 ? count : 0;
            }

            return customIpListBox.Text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Count(line =>
                {
                    var trimmed = line.Trim();
                    return trimmed.Length > 0 && !trimmed.StartsWith("#", StringComparison.Ordinal);
                });
        }

        void RefreshSummary()
        {
            var selectedAdapter = adapterCombo.SelectedItem as ComboBoxItem;
            var adapterName = selectedAdapter?.Content?.ToString() ?? "未选择网卡";
            var count = GetExpectedCount();
            if (tabControl.SelectedIndex == 0)
            {
                RefreshBatchRangePreview(rangePreview, startIpBox.Text, count, 1024);
            }

            var permissionColors = isAdministrator ? permissionGranted : permissionRequired;
            adminHint.Text = isAdministrator
                ? "已获得管理员权限。"
                : "请以管理员身份重新运行模拟器以添加或删除网卡 IP。";
            adminHint.Foreground = permissionColors.Text;
            permissionNote.Background = permissionColors.Background;
            permissionNote.BorderBrush = permissionColors.Border;
            RefreshAddressActions();
            if (isAdding || isDeleting)
            {
                return;
            }
            summaryText.Text = count > 0
                ? $"目标：{adapterName} · 预计 {count} 个IP · 默认掩码 {maskBox.Text.Trim()}"
                : "请填写有效的连续IP数量或自定义IP清单。";
            addButton.Content = count > 0 ? $"添加 {count} 个IP" : "批量添加 IP";
            addButton.IsEnabled = isAdministrator && selectedAdapter != null;
        }

        refreshAdapterButton.Click += (_, _) =>
        {
            RefreshAdapterOptions(SelectedBatchIpv4Adapter());
        };
        adapterCombo.SelectionChanged += (_, _) => RefreshSummary();
        adapterCombo.SelectionChanged += (_, _) => RefreshSelectedAdapterIpv4Addresses();
        countBox.TextChanged += (_, _) => RefreshSummary();
        startIpBox.TextChanged += (_, _) => RefreshSummary();
        customIpListBox.TextChanged += (_, _) => RefreshSummary();
        maskBox.TextChanged += (_, _) => RefreshSummary();
        tabControl.SelectionChanged += (_, _) => RefreshSummary();

        deleteSelectedIpButton.Click += async (_, _) =>
        {
            if (isAdding || isDeleting || !isAdministrator)
            {
                return;
            }

            var target = SelectedBatchIpv4Adapter();
            var selectedAddresses = ipTable.SelectedItems.OfType<BatchIpv4Input>()
                .Select(row => row.IpAddress)
                .Distinct()
                .ToArray();
            if (target == null || selectedAddresses.Length == 0)
            {
                return;
            }

            var adapterName = (adapterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "目标网卡";
            var addressPreview = string.Join("\r\n", selectedAddresses.Take(10));
            if (selectedAddresses.Length > 10)
            {
                addressPreview += $"\r\n另有 {selectedAddresses.Length - 10} 个地址。";
            }

            var confirm = MessageBox.Show(dialog,
                $"从网卡「{adapterName}」删除选中的 IPv4 地址吗？共 {selectedAddresses.Length} 个。\r\n" +
                $"InterfaceIndex {target.InterfaceIndex}\r\n\r\n{addressPreview}",
                "删除网卡 IPv4 地址", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            isDeleting = true;
            adapterGrid.IsEnabled = false;
            ipTable.IsEnabled = false;
            addButton.IsEnabled = false;
            deleteSelectedIpButton.Content = "正在删除…";
            summaryText.Text = $"正在从 {adapterName} 删除选中的 IPv4 地址…";
            RefreshAddressActions();

            try
            {
                var results = new List<BatchIpv4DeleteState>(selectedAddresses.Length);
                var details = new List<string>();
                foreach (var ip in selectedAddresses)
                {
                    (BatchIpv4DeleteState State, string Detail) result;
                    try
                    {
                        result = await System.Threading.Tasks.Task.Run(() => RemoveBatchIpv4Address(target, ip));
                    }
                    catch (Exception ex)
                    {
                        result = (BatchIpv4DeleteState.Failed, ex.Message);
                    }

                    results.Add(result.State);
                    if (result.State is BatchIpv4DeleteState.Removed or BatchIpv4DeleteState.AlreadyAbsent)
                    {
                        // 当前项已经过 Windows 复查，立即更新右侧视图。
                        existingIpv4Rows.RemoveAll(row => row.IpAddress == ip);
                        RefreshExistingIpView();
                    }
                    else
                    {
                        details.Add($"{ip}：{result.Detail}");
                    }

                    summaryText.Text = $"已处理 {results.Count} / {selectedAddresses.Length} 个 IPv4 地址。";
                }

                RefreshSelectedAdapterIpv4Addresses();
                var removed = results.Count(state => state == BatchIpv4DeleteState.Removed);
                var alreadyAbsent = results.Count(state => state == BatchIpv4DeleteState.AlreadyAbsent);
                var skipped = results.Count(state => state == BatchIpv4DeleteState.Skipped);
                var failed = results.Count(state => state == BatchIpv4DeleteState.Failed);
                var message = $"已删除 {removed} 个，已不存在 {alreadyAbsent} 个，跳过 {skipped} 个，失败 {failed} 个。";
                if (details.Count == 0)
                {
                    Growl.SuccessGlobal(message);
                    return;
                }

                var detailText = string.Join("\r\n", details.Take(20));
                if (details.Count > 20)
                {
                    detailText += $"\r\n另有 {details.Count - 20} 条记录。";
                }

                MessageBox.Show(dialog, message + "\r\n\r\n" + detailText, "删除结果", MessageBoxButton.OK,
                    failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                RefreshSelectedAdapterIpv4Addresses();
                MessageBox.Show(dialog, "批量删除 IPv4 地址时发生异常：\r\n" + ex.Message,
                    "删除网卡 IPv4 地址", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                isDeleting = false;
                adapterGrid.IsEnabled = true;
                ipTable.IsEnabled = true;
                deleteSelectedIpButton.Content = "删除选中";
                RefreshSummary();
            }
        };

        addButton.Click += async (_, _) =>
        {
            if (isAdding || isDeleting)
            {
                return;
            }

            var selectedAdapter = adapterCombo.SelectedItem as ComboBoxItem;
            var target = SelectedBatchIpv4Adapter();
            var adapterId = target?.AdapterId;
            if (string.IsNullOrWhiteSpace(adapterId))
            {
                MessageBox.Show(dialog, "请先选择目标网卡。", "批量添加网卡IP", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!networkAddressService.IsAdministrator())
            {
                MessageBox.Show(dialog, "添加网卡IP需要管理员权限，请以管理员身份重新运行模拟器。", "权限不足",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var defaultMask = maskBox.Text.Trim();
            if (!IPAddress.TryParse(defaultMask, out var parsedDefaultMask) ||
                parsedDefaultMask.AddressFamily != AddressFamily.InterNetwork ||
                !IsValidBatchIpv4SubnetMask(parsedDefaultMask))
            {
                MessageBox.Show(dialog, "默认子网掩码无效，请输入连续的IPv4子网掩码，例如 255.255.255.0。",
                    "批量添加网卡IP", MessageBoxButton.OK, MessageBoxImage.Warning);
                maskBox.Focus();
                return;
            }

            var inputs = new List<BatchIpv4Input>();
            if (tabControl.SelectedIndex == 0)
            {
                if (!int.TryParse(countBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var count) || count < 1 || count > 1024)
                {
                    MessageBox.Show(dialog, "连续生成数量必须在1至1024之间。", "批量添加网卡IP", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    countBox.Focus();
                    return;
                }

                if (!IPAddress.TryParse(startIpBox.Text.Trim(), out var startAddress) ||
                    startAddress.AddressFamily != AddressFamily.InterNetwork)
                {
                    MessageBox.Show(dialog, "起始IP必须是有效的IPv4地址。", "批量添加网卡IP", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    startIpBox.Focus();
                    return;
                }

                for (var index = 0; index < count; index++)
                {
                    if (!TryIncrementIpv4Address(startAddress, index, out var ipAddress))
                    {
                        MessageBox.Show(dialog, "IP递增超出IPv4地址范围，请调整起始IP或添加数量。",
                            "批量添加网卡IP", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    inputs.Add(new BatchIpv4Input { IpAddress = ipAddress, SubnetMask = defaultMask });
                }
            }
            else
            {
                var lines = customIpListBox.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                var uniqueInputs = new Dictionary<string, BatchIpv4Input>(StringComparer.OrdinalIgnoreCase);
                for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                {
                    var line = lines[lineIndex].Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parts = line.Split(',');
                    if (parts.Length > 2)
                    {
                        MessageBox.Show(dialog, $"第 {lineIndex + 1} 行格式错误。格式应为：IP 或 IP,子网掩码。",
                            "批量添加网卡IP", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var ipText = parts[0].Trim();
                    if (!IPAddress.TryParse(ipText, out var parsedIp) ||
                        parsedIp.AddressFamily != AddressFamily.InterNetwork)
                    {
                        MessageBox.Show(dialog, $"第 {lineIndex + 1} 行IP地址无效：{ipText}", "批量添加网卡IP",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var lineMask = parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1])
                        ? parts[1].Trim()
                        : defaultMask;
                    if (!IPAddress.TryParse(lineMask, out var parsedLineMask) ||
                        parsedLineMask.AddressFamily != AddressFamily.InterNetwork ||
                        !IsValidBatchIpv4SubnetMask(parsedLineMask))
                    {
                        MessageBox.Show(dialog, $"第 {lineIndex + 1} 行子网掩码无效：{lineMask}",
                            "批量添加网卡IP", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var normalizedIp = parsedIp.ToString();
                    var normalizedMask = parsedLineMask.ToString();
                    if (uniqueInputs.TryGetValue(normalizedIp, out var previousInput))
                    {
                        if (!string.Equals(previousInput.SubnetMask, normalizedMask, StringComparison.OrdinalIgnoreCase))
                        {
                            MessageBox.Show(dialog, $"IP {normalizedIp} 重复出现，但子网掩码不一致。",
                                "批量添加网卡IP", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        continue;
                    }

                    uniqueInputs.Add(normalizedIp, new BatchIpv4Input
                    {
                        IpAddress = normalizedIp,
                        SubnetMask = normalizedMask
                    });

                    if (uniqueInputs.Count > 1024)
                    {
                        MessageBox.Show(dialog, "一次最多添加1024个IP。", "批量添加网卡IP", MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }
                }

                inputs.AddRange(uniqueInputs.Values);
                if (inputs.Count == 0)
                {
                    MessageBox.Show(dialog, "自定义IP清单为空，请至少填写一个IPv4地址。", "批量添加网卡IP",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var adapterDisplayName = selectedAdapter.Content?.ToString() ?? adapterId;
            var confirmResult = MessageBox.Show(
                dialog,
                $"确定向以下网卡批量添加 {inputs.Count} 个辅助IP吗？\r\n\r\n" +
                $"网卡：{adapterDisplayName}\r\n" +
                $"默认子网掩码：{defaultMask}\r\n\r\n" +
                "已存在于本机其他网卡的IP会跳过；不会修改原有IP、网关或DNS。",
                "确认批量添加网卡IP",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmResult != MessageBoxResult.Yes)
            {
                return;
            }

            isAdding = true;
            adapterGrid.IsEnabled = false;
            ipTable.IsEnabled = false;
            RefreshAddressActions();
            addButton.IsEnabled = false;
            addButton.Content = "正在添加…";
            refreshAdapterButton.IsEnabled = false;
            summaryText.Text = $"正在向 {adapterDisplayName} 添加 {inputs.Count} 个IP，请稍候……";

            try
            {
                var batchResult = await System.Threading.Tasks.Task.Run(() =>
                {
                    var addedCount = 0;
                    var skippedCount = 0;
                    var failedCount = 0;
                    var details = new List<string>();
                    foreach (var input in inputs)
                    {
                        try
                        {
                            if (networkAddressService.TryAddIpv4AddressToInterface(
                                    adapterId,
                                    input.IpAddress,
                                    input.SubnetMask,
                                    out var message))
                            {
                                addedCount++;
                            }
                            else
                            {
                                skippedCount++;
                                if (details.Count < 100)
                                {
                                    details.Add($"[跳过] {input.IpAddress}：{message}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            failedCount++;
                            if (details.Count < 100)
                            {
                                details.Add($"[失败] {input.IpAddress}：{ex.Message}");
                            }
                        }
                    }

                    return (AddedCount: addedCount, SkippedCount: skippedCount, FailedCount: failedCount, Details: details);
                });

                RefreshAdapterOptions(target);
                var resultText =
                    $"批量添加完成。\r\n成功新增：{batchResult.AddedCount} 个\r\n" +
                    $"已存在跳过：{batchResult.SkippedCount} 个\r\n" +
                    $"失败：{batchResult.FailedCount} 个";
                summaryText.Text = resultText.Replace("\r\n", "  ");

                if (batchResult.FailedCount > 0 || batchResult.SkippedCount > 0)
                {
                    var detailText = string.Join("\r\n", batchResult.Details.Take(30));
                    if (batchResult.Details.Count > 30)
                    {
                        detailText += "\r\n……其余明细已省略。";
                    }

                    MessageBox.Show(dialog, resultText + "\r\n\r\n明细：\r\n" + detailText,
                        "批量添加结果", MessageBoxButton.OK,
                        batchResult.FailedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
                }

                if (batchResult.FailedCount == 0)
                {
                    Growl.SuccessGlobal($"网卡IP批量添加完成：新增 {batchResult.AddedCount} 个，已存在跳过 {batchResult.SkippedCount} 个。目标网卡：{adapterDisplayName}");
                    isAdding = false;
                    // dialog.Close();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(dialog, "批量添加网卡IP时发生异常：\r\n" + ex.Message, "批量添加网卡IP",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                isAdding = false;
                if (dialog.IsVisible)
                {
                    refreshAdapterButton.IsEnabled = true;
                    adapterGrid.IsEnabled = true;
                    ipTable.IsEnabled = true;
                    RefreshSummary();
                }
            }
        };


        RefreshAdapterOptions();
        RefreshSummary();
        dialog.ShowDialog();
    }

    private static IPInterfaceProperties ReadBatchIpv4Interface(BatchIpv4Adapter target)
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            // 通过当前 Windows 接口索引定位。网卡 ID 仅用于防止索引被其它设备复用。
            var sameAdapter = string.Equals(adapter.Id, target.AdapterId, StringComparison.OrdinalIgnoreCase);
            IPInterfaceProperties properties;
            try
            {
                if (!adapter.Supports(NetworkInterfaceComponent.IPv4))
                {
                    continue;
                }

                properties = adapter.GetIPProperties();
                if (properties.GetIPv4Properties()?.Index != target.InterfaceIndex)
                {
                    continue;
                }
            }
            catch (NetworkInformationException) when (!sameAdapter)
            {
                continue;
            }

            if (!sameAdapter)
            {
                throw new InvalidOperationException($"接口索引 {target.InterfaceIndex} 已指向其它网卡，请刷新网卡列表。");
            }

            return properties;
        }

        throw new InvalidOperationException($"找不到接口索引 {target.InterfaceIndex} 对应的网卡，请刷新网卡列表。");
    }

    private static UnicastIPAddressInformation ReadBatchIpv4Address(BatchIpv4Adapter target, IPAddress address) =>
        ReadBatchIpv4Interface(target).UnicastAddresses.FirstOrDefault(entry => address.Equals(entry.Address));

    private static bool IsSystemAssignedIpv4Address(UnicastIPAddressInformation entry) =>
        entry.PrefixOrigin is PrefixOrigin.Dhcp or PrefixOrigin.WellKnown or PrefixOrigin.RouterAdvertisement;

    private static (BatchIpv4DeleteState State, string Detail) RemoveBatchIpv4Address(
        BatchIpv4Adapter target, string ip)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return (BatchIpv4DeleteState.Failed, "IPv4 地址无效。");
        }

        var entry = ReadBatchIpv4Address(target, address);
        if (entry == null)
        {
            return (BatchIpv4DeleteState.AlreadyAbsent, null);
        }

        if (IsSystemAssignedIpv4Address(entry))
        {
            return (BatchIpv4DeleteState.Skipped, "由 DHCP 或系统分配。");
        }

        var persistentError = TryDeleteStaticIpv4Address(target.InterfaceIndex, address, activeOnly: false);
        // 包括 Element not found 在内的任何命令结果，都以 Windows 当前地址列表为准。
        entry = ReadBatchIpv4Address(target, address);
        if (entry == null)
        {
            return (persistentError == null ? BatchIpv4DeleteState.Removed : BatchIpv4DeleteState.AlreadyAbsent, null);
        }

        if (IsSystemAssignedIpv4Address(entry))
        {
            return (BatchIpv4DeleteState.Skipped, "当前地址已由 DHCP 或系统分配。");
        }

        // 某些静态地址只存在于 ActiveStore，持久存储删除后仍在时按同一索引和地址处理。
        var activeError = TryDeleteStaticIpv4Address(target.InterfaceIndex, address, activeOnly: true);
        if (ReadBatchIpv4Address(target, address) == null)
        {
            return (persistentError == null || activeError == null
                ? BatchIpv4DeleteState.Removed
                : BatchIpv4DeleteState.AlreadyAbsent, null);
        }

        return (BatchIpv4DeleteState.Failed, activeError ?? persistentError ?? "Windows 仍列出该地址。");
    }

    private static string TryDeleteStaticIpv4Address(int interfaceIndex, IPAddress address, bool activeOnly)
    {
        var store = activeOnly ? "active" : "persistent";
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = System.IO.Path.Combine(Environment.SystemDirectory, "netsh.exe"),
            Arguments = FormattableString.Invariant(
                $"interface ipv4 delete address name={interfaceIndex} address={address} store={store}"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 Windows 网卡地址命令。");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                return "Windows 网卡地址命令执行超时。";
            }

            var outputText = output.GetAwaiter().GetResult();
            var errorText = error.GetAwaiter().GetResult();
            if (process.ExitCode == 0)
            {
                return null;
            }

            var message = (errorText + "\n" + outputText).Trim();
            return message.Length > 0 ? message : $"Windows 返回错误码 {process.ExitCode}。";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static bool IsValidBatchIpv4SubnetMask(IPAddress subnetMask)
    {
        var bytes = subnetMask.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        var zeroBitSeen = false;
        var oneCount = 0;
        foreach (var value in bytes)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var isOne = (value & (1 << bit)) != 0;
                if (isOne)
                {
                    if (zeroBitSeen)
                    {
                        return false;
                    }

                    oneCount++;
                }
                else
                {
                    zeroBitSeen = true;
                }
            }
        }

        return oneCount is >= 1 and <= 32;
    }

    private Window CreateBatchDialog(string title)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 1020,
            Height = 750,
            MinWidth = 660,
            MinHeight = 550,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            ShowInTaskbar = false,
            Background = CreateBatchBrush(242, 245, 250),
            Foreground = Foreground,
            FontFamily = FontFamily,
            FontSize = 13,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        var scrollBarType = typeof(System.Windows.Controls.Primitives.ScrollBar);
        dialog.Resources[scrollBarType] = FindResource(scrollBarType);
        return dialog;
    }

    private void SetBatchDialogContent(Window dialog, string subtitle, string iconGeometry,
        FrameworkElement left, FrameworkElement right, TextBlock summaryText, Button addButton)
    {
        var borderBrush = (Brush)FindResource("PanelBorderBrush");
        var accentBrush = (Brush)FindResource("AccentBrush");
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new DockPanel { LastChildFill = true };
        var mark = new Border
        {
            Width = 44,
            Height = 44,
            Margin = new Thickness(0, 0, 14, 0),
            CornerRadius = new CornerRadius(10),
            Background = CreateBatchBrush(234, 242, 255),
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(iconGeometry),
                Stroke = accentBrush,
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        heading.Children.Add(mark);
        var headingText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        headingText.Children.Add(new TextBlock
        {
            Text = dialog.Title,
            FontSize = 21,
            FontWeight = FontWeights.SemiBold
        });
        var subtitleText = CreateBatchHint(subtitle);
        subtitleText.Margin = new Thickness(0, 4, 0, 0);
        headingText.Children.Add(subtitleText);
        heading.Children.Add(headingText);
        root.Children.Add(new Border
        {
            Background = Brushes.White,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(22, 17, 22, 17),
            Child = heading
        });

        var columns = new Grid { Margin = new Thickness(20, 18, 20, 6) };
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.VerticalAlignment = VerticalAlignment.Top;
        right.VerticalAlignment = VerticalAlignment.Top;
        columns.Children.Add(left);
        columns.Children.Add(right);

        void ArrangeColumns(double width)
        {
            var compact = width < 900;
            columns.ColumnDefinitions[0].Width = new GridLength(compact ? 1 : 54, GridUnitType.Star);
            columns.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 14);
            columns.ColumnDefinitions[2].Width = compact
                ? new GridLength(0)
                : new GridLength(46, GridUnitType.Star);
            Grid.SetColumn(right, compact ? 0 : 2);
            Grid.SetRow(right, compact ? 1 : 0);
        }

        ArrangeColumns(dialog.Width);
        dialog.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
            {
                ArrangeColumns(e.NewSize.Width);
            }
        };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = columns
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var cancelButton = CreateBatchButton("取消", "NormalButtonStyle", 82);
        cancelButton.Margin = new Thickness(0, 0, 8, 0);
        cancelButton.IsCancel = true;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(cancelButton);
        actions.Children.Add(addButton);
        DockPanel.SetDock(actions, Dock.Right);
        var footer = new DockPanel { LastChildFill = true };
        footer.Children.Add(actions);
        summaryText.Margin = new Thickness(0, 0, 16, 0);
        summaryText.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(summaryText);
        var footerFrame = new Border
        {
            Background = Brushes.White,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(22, 12, 22, 12),
            Child = footer
        };
        Grid.SetRow(footerFrame, 2);
        root.Children.Add(footerFrame);
        dialog.Content = root;
    }

    private static SolidColorBrush CreateBatchBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private Border CreateBatchCard(params UIElement[] content)
    {
        var panel = new StackPanel();
        foreach (var element in content)
        {
            panel.Children.Add(element);
        }

        return new Border
        {
            Style = (Style)FindResource("PageCardStyle"),
            Margin = new Thickness(0, 0, 0, 14),
            Child = panel
        };
    }

    private TextBlock CreateBatchSectionTitle(string text) => new()
    {
        Style = (Style)FindResource("SectionTitleStyle"),
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 12)
    };

    private TextBlock CreateBatchHint(string text = "") => new()
    {
        Text = text,
        Foreground = (Brush)FindResource("MutedTextBrush"),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap
    };

    private Border CreateBatchNote(UIElement content) => new()
    {
        Style = (Style)FindResource("FormSectionStyle"),
        CornerRadius = new CornerRadius(8),
        Child = content
    };

    private Button CreateBatchButton(string text, string styleKey, double minWidth) => new()
    {
        Content = text,
        Style = (Style)FindResource(styleKey),
        Height = 38,
        MinWidth = minWidth,
        Margin = new Thickness(0)
    };

    private (TabControl Tabs, TextBox CountBox, TextBox StartIpBox, TextBlock Preview,
        TextBox ListBox, Button FillExampleButton) CreateBatchAddressEditor(
            int maxCount, TextBlock customFormat, string rangeDescription = null)
    {
        var tabs = new TabControl { Style = (Style)FindResource("PlcTabsStyle"), SelectedIndex = 0 };
        var tabStyle = (Style)FindResource("PlcTabStyle");
        var countBox = CreateBatchTextBox("50");
        var startIpBox = CreateBatchTextBox("192.168.1.101");
        var rangeGrid = CreateBatchFormGrid(1);
        AddBatchField(rangeGrid, 0, 0, "起始 IPv4 地址", startIpBox);
        AddBatchField(rangeGrid, 0, 2, $"添加数量（1–{maxCount}）", countBox);

        var preview = CreateBatchHint();
        preview.FontWeight = FontWeights.SemiBold;
        var rangePanel = new StackPanel();
        rangePanel.Children.Add(rangeGrid);
        rangePanel.Children.Add(CreateBatchNote(preview));
        if (!string.IsNullOrWhiteSpace(rangeDescription))
        {
            var rangeHint = CreateBatchHint(rangeDescription);
            rangeHint.Margin = new Thickness(0, 8, 0, 0);
            rangePanel.Children.Add(rangeHint);
        }

        var listBox = new TextBox
        {
            Height = 154,
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10, 8, 10, 8),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontFamily = new System.Windows.Media.FontFamily("Consolas")
        };
        System.Windows.Automation.AutomationProperties.SetName(listBox, "自定义 IP 清单");
        var editorFrameStyle = new Style(typeof(Border), (Style)FindResource("FormSectionStyle"));
        var focused = new Trigger { Property = IsKeyboardFocusWithinProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, FindResource("AccentBrush")));
        editorFrameStyle.Triggers.Add(focused);
        var editorFrame = new Border
        {
            Style = editorFrameStyle,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            Child = listBox
        };

        var fillExampleButton = CreateBatchButton("填入示例", "AccentButtonStyle", 86);
        fillExampleButton.Margin = new Thickness(0, 0, 8, 0);
        var clearButton = CreateBatchButton("清空", "NormalButtonStyle", 70);
        clearButton.Click += (_, _) => listBox.Clear();
        var editorActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        editorActions.Children.Add(fillExampleButton);
        editorActions.Children.Add(clearButton);

        customFormat.Margin = new Thickness(0, 0, 0, 4);
        var commentHint = CreateBatchHint("自动忽略空行和以 # 开头的注释行。");
        commentHint.Margin = new Thickness(0, 0, 0, 10);
        var listPanel = new StackPanel();
        listPanel.Children.Add(customFormat);
        listPanel.Children.Add(commentHint);
        listPanel.Children.Add(editorFrame);
        listPanel.Children.Add(editorActions);

        tabs.Items.Add(new TabItem { Header = "连续生成", Style = tabStyle, Content = rangePanel });
        tabs.Items.Add(new TabItem { Header = "自定义 IP 清单", Style = tabStyle, Content = listPanel });
        tabs.SelectedIndex = 0;
        return (tabs, countBox, startIpBox, preview, listBox, fillExampleButton);
    }

    private static void RefreshBatchRangePreview(TextBlock preview, string startIp, int count, int maxCount)
    {
        if (count < 1 || count > maxCount)
        {
            preview.Text = $"请输入 1–{maxCount} 的添加数量。";
            return;
        }

        if (!IPAddress.TryParse(startIp.Trim(), out var firstAddress) ||
            firstAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            preview.Text = "请输入有效的起始 IPv4 地址。";
            return;
        }

        if (!TryIncrementIpv4Address(firstAddress, count - 1, out var lastAddress))
        {
            preview.Text = "生成范围超出 IPv4 地址上限。";
            return;
        }

        preview.Text = count == 1
            ? $"生成地址  {firstAddress}"
            : $"生成范围  {firstAddress}  –  {lastAddress}";
    }

    private TextBox CreateBatchTextBox(string value) => new()
    {
        Style = (Style)FindResource("PlcTextInputStyle"),
        Text = value,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private ComboBox CreateBatchComboBox(IEnumerable<string> items, int selectedIndex) => new()
    {
        Style = (Style)FindResource("PlcComboStyle"),
        ItemsSource = items,
        SelectedIndex = selectedIndex,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Width = double.NaN
    };

    private static Grid CreateBatchFormGrid(int rows)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < rows; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        return grid;
    }

    private StackPanel CreateBatchFieldPanel(string label, Control control)
    {
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("FieldLabelStyle"),
            Text = label,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(control);
        return panel;
    }

    private void AddBatchField(Grid grid, int row, int column, string label, Control control, int columnSpan = 1)
    {
        var panel = CreateBatchFieldPanel(label, control);
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, column);
        Grid.SetColumnSpan(panel, columnSpan);
        grid.Children.Add(panel);
    }

    private static bool TryParseBatchPositiveInt(string text, out int value)
    {
        return int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    private static bool TryParseBatchDbBlocks(string text, out List<(int DbNumber, int Length)> blocks, out string error)
    {
        blocks = null;
        error = string.Empty;
        if (text?.Trim() == "无")
        {
            blocks = [];
            return true;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "请填写 DB 块号，例如 DB313 或 DB313;DB1500:1024。";
            return false;
        }

        var parsed = new List<(int DbNumber, int Length)>();
        var numbers = new HashSet<int>();
        foreach (var entry in text.Split(new[] { ';', '；', ',', '，', '、', '+' }))
        {
            var parts = entry.Trim().Replace('：', ':').Split(':');
            var numberText = parts[0].Trim();
            if (numberText.StartsWith("DB", StringComparison.OrdinalIgnoreCase))
            {
                numberText = numberText.Substring(2).Trim();
            }

            if (!ushort.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number == 0)
            {
                error = $"“{entry.Trim()}”的 DB 块号无效，请填写 1 至 65535。";
                return false;
            }

            var length = 512;
            if (parts.Length > 2 || (parts.Length == 2 && !TryParseBatchPositiveInt(parts[1], out length)))
            {
                error = $"DB{number} 的长度无效，请用“块号:正整数字节数”，例如 DB{number}:512。";
                return false;
            }

            if (!numbers.Add(number))
            {
                error = $"DB{number} 重复，请为每个块号只填写一项。";
                return false;
            }

            parsed.Add((number, length));
        }

        blocks = parsed;
        return true;
    }

    private static bool TryParseBatchNonNegativeInt(string text, out int value)
    {
        return int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private static string FormatBatchPlcName(string template, int plcId, int index, string ipAddress)
    {
        var name = template ?? string.Empty;
        name = ReplaceBatchPlaceholder(name, "{id}", plcId.ToString(CultureInfo.InvariantCulture));
        name = ReplaceBatchPlaceholder(name, "{index}", index.ToString(CultureInfo.InvariantCulture));
        name = ReplaceBatchPlaceholder(name, "{ip}", ipAddress ?? string.Empty);
        return name.Trim();
    }

    /// <summary>
    /// 兼容 .NET Framework 的不区分大小写占位符替换。
    /// 不依赖 string.Replace(string, string, StringComparison) 重载。
    /// </summary>
    private static string ReplaceBatchPlaceholder(string source, string placeholder, string replacement)
    {
        source ??= string.Empty;
        replacement ??= string.Empty;
        var position = 0;
        while (true)
        {
            var index = source.IndexOf(placeholder, position, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return source;
            }

            source = source.Remove(index, placeholder.Length).Insert(index, replacement);
            position = index + replacement.Length;
        }
    }

    private static bool TryIncrementIpv4Address(IPAddress startAddress, int offset, out string result)
    {
        result = string.Empty;
        if (startAddress == null || startAddress.AddressFamily != AddressFamily.InterNetwork || offset < 0)
        {
            return false;
        }

        var bytes = startAddress.GetAddressBytes();
        var numericAddress = ((long)bytes[0] << 24) |
                             ((long)bytes[1] << 16) |
                             ((long)bytes[2] << 8) |
                             bytes[3];
        numericAddress += offset;
        if (numericAddress > uint.MaxValue)
        {
            return false;
        }

        var nextBytes = new byte[]
        {
            (byte)((numericAddress >> 24) & 0xFF),
            (byte)((numericAddress >> 16) & 0xFF),
            (byte)((numericAddress >> 8) & 0xFF),
            (byte)(numericAddress & 0xFF)
        };
        result = new IPAddress(nextBytes).ToString();
        return true;
    }

    private void RemovePlcButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = PlcListDataGrid.SelectedItems.OfType<PlcConfigModel>()
            .Where(plc => PlcListView.Contains(plc))
            .OrderBy(plc => plc.PlcId)
            .ToList();
        if (selected.Count == 0)
        {
            Growl.WarningGlobal("请先在列表中选择要删除的 PLC。");
            return;
        }

        var server = PlcVirtualServerService.Instance;
        var byRunningState = selected.ToLookup(plc => server.IsPlcRunning(plc.PlcId));
        var toDelete = byRunningState[false].ToList();
        if (toDelete.Count == 0)
        {
            Growl.WarningGlobal("选中的 PLC 均在运行，请先停用需要删除的设备。");
            return;
        }

        var running = byRunningState[true].ToList();
        var keepNote = running.Count == 0 ? string.Empty
            : $"\r\n\r\n运行中保留 {running.Count} 台：\r\n{FormatPlcDeletePreview(running)}";
        var result = MessageBox.Show(this,
            $"已选 {selected.Count} 台，确定删除以下 {toDelete.Count} 台已停用 PLC 的配置吗？\r\n\r\n" +
            FormatPlcDeletePreview(toDelete) + keepNote,
            "删除选中的 PLC", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        // 只从确认过的待删除项中剔除状态变化的设备，不扩大删除范围。
        toDelete.RemoveAll(plc => !PlcListView.Contains(plc) || server.IsPlcRunning(plc.PlcId));
        if (toDelete.Count == 0)
        {
            Growl.WarningGlobal("设备状态已变化，本次可删除的 PLC 为 0 台。");
            RefreshPlcServerUi();
            return;
        }

        var visibleItems = PlcListDataGrid.Items;
        var firstIndex = toDelete.Min(plc => visibleItems.IndexOf(plc));
        var deleted = new HashSet<PlcConfigModel>(toDelete);
        var remaining = Plcs.Where(plc => !deleted.Contains(plc)).ToList();
        if (!TrySavePlcBatch(remaining))
        {
            return;
        }

        foreach (var plc in toDelete)
        {
            Plcs.Remove(plc);
        }

        if (PlcListDataGrid.SelectedItem == null)
        {
            SelectedPlc = visibleItems.Count > 0
                ? (PlcConfigModel)visibleItems[Math.Max(0, Math.Min(firstIndex, visibleItems.Count - 1))]
                : null;
        }

        OnPropertyChanged(nameof(Plcs));
        RefreshPlcServerUi();
        var skipped = selected.Count - toDelete.Count;
        var skipNote = skipped > 0 ? $"，跳过 {skipped} 台" : string.Empty;
        Growl.SuccessGlobal($"已删除 {toDelete.Count} 台 PLC 的配置{skipNote}。");
    }

    private static string FormatPlcDeletePreview(IReadOnlyList<PlcConfigModel> rows)
    {
        const int previewLimit = 8;
        var names = string.Join("\r\n", rows.Take(previewLimit).Select(plc => $"{plc.PlcName}（ID {plc.PlcId}）"));
        return rows.Count > previewLimit ? names + $"\r\n另有 {rows.Count - previewLimit} 台。" : names;
    }

    private void UseProxyEndpointForDirectButton_Click(object sender, RoutedEventArgs e)
    {
        var plc = SelectedPlc;
        if (plc == null || !PlcListView.Contains(plc))
        {
            return;
        }

        if (PlcVirtualServerService.Instance.IsPlcRunning(plc.PlcId))
        {
            Growl.WarningGlobal($"请先停用 {plc.PlcName}，再设置直连。");
            return;
        }

        var endpoint = plc.GetProxyEndPoint();
        if (endpoint == null || endpoint.Address.Equals(IPAddress.Any) ||
            endpoint.Address.Equals(IPAddress.Broadcast))
        {
            Growl.WarningGlobal($"{plc.PlcName} 的原代理 IP 或端口无效，请先核对代理设置。");
            return;
        }

        plc.DirectListenAddress = endpoint.Address.ToString();
        plc.DirectListenPort = endpoint.Port;
        plc.ConnectionMode = PlcConnectionMode.Direct;

        RefreshPlcList();
        Growl.SuccessGlobal($"{plc.PlcName} 已设为沿用原地址直连，保存或启用后生效。");
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

            ShowSinglePlcStartResultGrowl($"{plc.PlcName} 没有配置任何DB块，无法启动。请先新增DB块或给DB块绑定全局自动写入规则。", false);

            RefreshPlcServerUi();

            return;
        }

        var started = PlcVirtualServerService.Instance.StartOrReloadPlc(savedPlc);

        if (!started)
        {
            PlcVirtualServerService.Instance.StopPlc(plc.PlcId);

            SetPlcEnabledWithoutTrigger(plc, false);

            SaveCurrentPlcsToStore(false);

            if (savedPlc.ConnectionMode == PlcConnectionMode.Direct)
            {
                ShowSinglePlcStartResultGrowl(
                    $"{plc.PlcName} 直连启动失败。请检查本机是否绑定 {savedPlc.DirectListenAddress}，以及该地址的 {savedPlc.DirectListenPort} 端口是否被占用。详细原因见 PLC 传输日志。", false);
            }
            else
            {
                ShowSinglePlcStartResultGrowl($"{plc.PlcName} 启动失败，已自动取消启用，请检查端口、代理端口、权限或DB配置。", false);
            }

            RefreshPlcServerUi();

            return;
        }

        ShowSinglePlcStartResultGrowl($"{plc.PlcName} 已启用并启动。", true);

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

    /// <summary>
    /// 显示PLC启动结果提示。
    /// 启动成功或启动失败提示只保留一个，避免多个Growl同时存在导致鼠标悬浮时报无效窗口句柄。
    /// </summary>
    private void ShowSinglePlcStartResultGrowl(string message, bool isSuccess)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        Growl.ClearGlobal();

        if (isSuccess)
        {
            Growl.SuccessGlobal(message);
            return;
        }

        Growl.WarningGlobal(message);
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
            ShowSinglePlcStartResultGrowl("全部启用失败，没有任何PLC启动成功。", false);
        }
        else if (failedNames.Count > 0)
        {
            ShowSinglePlcStartResultGrowl($"部分PLC启动失败，成功 {successIds.Count} 台，失败：{string.Join("、", failedNames)}",
                false);
        }
        else
        {
            ShowSinglePlcStartResultGrowl($"全部启用完成，当前运行 {successIds.Count} 台PLC。", true);
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

    /// <summary>
    /// 复位所有正在运行PLC的全部已启用DB块。
    ///
    /// 复位规则：
    /// 1、配置了启用的复位项时，按照复位配置写入；
    /// 2、没有配置启用的复位项时，清零整个DB块；
    /// 3、未运行的PLC不处理；
    /// 4、未启用的DB块不处理；
    /// 5、单个DB块失败不会中断其他DB块。
    /// </summary>
    private void ResetAllDbBlockButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (Plcs.Count == 0)
        {
            Growl.WarningGlobal("当前没有PLC配置。");
            return;
        }

        var runningPlcs = Plcs
            .Where(plc => plc != null)
            .Where(plc =>
                PlcVirtualServerService.Instance.IsPlcRunning(plc.PlcId))
            .OrderBy(plc => plc.PlcId)
            .ToList();

        if (runningPlcs.Count == 0)
        {
            Growl.WarningGlobal("当前没有正在运行的PLC，不能执行全部复位。");
            return;
        }

        var resetTargets =
            new List<(PlcConfigModel Plc, PlcDbBlockModel DbBlock)>();

        foreach (var plc in runningPlcs)
        {
            if (plc.DbBlocks == null)
            {
                continue;
            }

            foreach (var dbBlock in plc.DbBlocks
                         .Where(dbBlock =>
                             dbBlock != null &&
                             dbBlock.IsEnabled)
                         .OrderBy(dbBlock => dbBlock.DbNumber))
            {
                resetTargets.Add((plc, dbBlock));
            }
        }

        if (resetTargets.Count == 0)
        {
            Growl.WarningGlobal("所有正在运行的PLC都没有已启用的DB块。");
            return;
        }

        var configuredResetBlockCount = resetTargets.Count(target =>
            target.DbBlock.ResetItems?
                .Any(resetItem =>
                    resetItem != null &&
                    resetItem.IsEnabled &&
                    !string.IsNullOrWhiteSpace(resetItem.ResetAddress))
            == true);

        var clearWholeBlockCount =
            resetTargets.Count - configuredResetBlockCount;

        var confirmText =
            $"确定复位所有正在运行PLC的全部DB块吗？\r\n\r\n" +
            $"PLC数量：{runningPlcs.Count}\r\n" +
            $"DB块数量：{resetTargets.Count}\r\n" +
            $"按复位配置写入：{configuredResetBlockCount} 个DB块\r\n" +
            $"整块清零：{clearWholeBlockCount} 个DB块\r\n\r\n" +
            "复位后自动写入规则仍会继续运行；" +
            "如果触发条件仍然成立，对应位可能再次被自动写入。";

        var confirmResult = MessageBox.Show(
            confirmText,
            "复位所有PLC的全部DB块",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        var successCount = 0;

        var failedMessages = new List<string>();

        foreach (var target in resetTargets)
        {
            var plc = target.Plc;

            var dbBlock = target.DbBlock;

            try
            {
                if (!PlcVirtualServerService.Instance
                        .IsPlcRunning(plc.PlcId))
                {
                    failedMessages.Add(
                        $"{plc.PlcName}/DB{dbBlock.DbNumber}：" +
                        "PLC已经停止运行。");

                    continue;
                }

                if (!PlcVirtualServerService.Instance
                        .TryResetDbBlockByConfig(
                            plc.PlcId,
                            dbBlock,
                            out var message))
                {
                    failedMessages.Add(
                        $"{plc.PlcName}/DB{dbBlock.DbNumber}：" +
                        (string.IsNullOrWhiteSpace(message)
                            ? "复位失败。"
                            : message));

                    continue;
                }

                successCount++;
            }
            catch (Exception ex)
            {
                failedMessages.Add(
                    $"{plc.PlcName}/DB{dbBlock.DbNumber}：" +
                    ex.Message);
            }
        }

        if (failedMessages.Count == 0)
        {
            Growl.SuccessGlobal(
                $"全部复位完成，共复位 {runningPlcs.Count} 台PLC、" +
                $"{successCount} 个DB块。");

            return;
        }

        var displayedFailures = failedMessages
            .Take(20)
            .ToList();

        var failedDetail = string.Join(
            "\r\n",
            displayedFailures.Select((message, index) =>
                $"{index + 1}. {message}"));

        if (failedMessages.Count > displayedFailures.Count)
        {
            failedDetail +=
                $"\r\n……另外还有 " +
                $"{failedMessages.Count - displayedFailures.Count} 个失败项。";
        }

        MessageBox.Show(
            $"批量复位执行完成。\r\n\r\n" +
            $"成功：{successCount} 个DB块\r\n" +
            $"失败：{failedMessages.Count} 个DB块\r\n\r\n" +
            $"失败明细：\r\n{failedDetail}",
            "复位所有PLC的全部DB块",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        Growl.WarningGlobal(
            $"批量复位完成：成功 {successCount} 个，" +
            $"失败 {failedMessages.Count} 个。");
    }

    private void OpenSharedProtocolButton_Click(object sender, RoutedEventArgs e)
    {
        new PlcProtocolTemplateImportWindow { Owner = this }.ShowDialog();
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
            if (TryActivateCurrentPopupWindow())
            {
                return;
            }

            var window = new PlcDbBlockResetConfigWindow(SelectedDbBlock)
            {
                Owner = this
            };

            RegisterCurrentPopupWindow(window);

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
        // 从按钮所在的DB块行取得目标，避免只使用上次选中的DB块。
        if (sender is FrameworkElement { DataContext: PlcDbBlockModel clickedDbBlock })
        {
            SelectedDbBlock = clickedDbBlock;
        }

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
            if (TryActivateCurrentPopupWindow())
            {
                return;
            }

            var window = new PlcDbBlockProtocolBoardConfigWindow(
                SelectedPlc.PlcId,
                SelectedDbBlock);

            RegisterCurrentPopupWindow(window);

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

    private void OpenPointChangeMonitorButton_Click(object sender, RoutedEventArgs e)
    {
        var existing = Application.Current.Windows.OfType<PlcPointChangeMonitorWindow>().FirstOrDefault();
        if (existing != null)
        {
            ActivatePopupWindow(existing);
            return;
        }

        try
        {
            var window = new PlcPointChangeMonitorWindow(Plcs, SelectedPlc?.PlcId ?? 0,
                SelectedDbBlock?.DbNumber ?? 0)
            {
                Topmost = this.Topmost
            };
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开点位变化记录失败，" + ex.Message, "异常",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ViewRulePointsButton_Click(object sender, RoutedEventArgs e)
    {
        var plc = SelectedPlc;
        var dbBlock = SelectedDbBlock;
        if (plc == null || dbBlock == null)
        {
            MessageBox.Show(this, "请先选择PLC和DB块。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!dbBlock.IsEnabled || !PlcVirtualServerService.Instance.IsPlcRunning(plc.PlcId))
        {
            MessageBox.Show(this, "请先启用当前DB块并启动PLC模拟服务。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var existing = Application.Current.Windows
            .OfType<PlcAutoWriteRuleMonitorWindow>()
            .FirstOrDefault(window => window.PlcId == plc.PlcId && window.DbNumber == dbBlock.DbNumber);
        if (existing != null)
        {
            ActivatePopupWindow(existing);
            return;
        }

        try
        {
            var window = new PlcAutoWriteRuleMonitorWindow(plc, dbBlock, SelectedDbBlockRuleRef?.RuleId);
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开规则点位监视失败：" + ex.Message, "异常", MessageBoxButton.OK, MessageBoxImage.Error);
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

        if (TryActivateCurrentPopupWindow())
        {
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

            RegisterCurrentPopupWindow(window);

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

    /// <summary>
    /// PLC级自动写入开关实时生效。
    /// 只更新存储服务中的开关，不重载PLC，因此不会清空DB内存。
    /// </summary>
    private void PlcAutoWriteEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox ||
            checkBox.DataContext is not PlcConfigModel plc)
        {
            return;
        }

        UpdateStoredPlcRuntimeState(storedPlcs =>
        {
            var storedPlc = storedPlcs.FirstOrDefault(x => x != null && x.PlcId == plc.PlcId);

            if (storedPlc != null)
            {
                storedPlc.AutoWriteEnabled = checkBox.IsChecked == true;
            }
        });
    }

    /// <summary>
    /// DB块启用状态实时生效。
    /// </summary>
    private void DbBlockEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox ||
            checkBox.DataContext is not PlcDbBlockModel dbBlock)
        {
            return;
        }

        var ownerPlc = Plcs.FirstOrDefault(x =>
            x?.DbBlocks != null &&
            x.DbBlocks.Contains(dbBlock));

        if (ownerPlc == null)
        {
            return;
        }

        UpdateStoredDbBlockRuntimeState(
            ownerPlc.PlcId,
            dbBlock.DbNumber,
            storedDbBlock => storedDbBlock.IsEnabled = checkBox.IsChecked == true);
    }

    /// <summary>
    /// DB块自动写入开关实时生效。
    /// </summary>
    private void DbBlockAutoWriteEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox ||
            checkBox.DataContext is not PlcDbBlockModel dbBlock)
        {
            return;
        }

        var ownerPlc = Plcs.FirstOrDefault(x =>
            x?.DbBlocks != null &&
            x.DbBlocks.Contains(dbBlock));

        if (ownerPlc == null)
        {
            return;
        }

        UpdateStoredDbBlockRuntimeState(
            ownerPlc.PlcId,
            dbBlock.DbNumber,
            storedDbBlock => storedDbBlock.AutoWriteEnabled = checkBox.IsChecked == true);
    }

    /// <summary>
    /// 当前DB块绑定规则的启用状态实时生效。
    /// </summary>
    private void DbBlockRuleRefEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox ||
            checkBox.DataContext is not PlcDbBlockAutoWriteRuleRefModel ruleRef)
        {
            return;
        }

        // 即使通过代码或旧绑定打开复选框，也不能突破全局规则库的禁用状态。
        if (checkBox.IsChecked == true && !IsGlobalRuleEnabled(ruleRef.RuleId))
        {
            ruleRef.IsEnabled = false;
            checkBox.IsChecked = false;
            Growl.WarningGlobal($"规则【{ruleRef.RuleName}】未在全局规则库启用，不能在PLC中单独启用。");
            return;
        }

        PlcConfigModel ownerPlc = null;
        PlcDbBlockModel ownerDbBlock = null;

        foreach (var plc in Plcs.Where(x => x?.DbBlocks != null))
        {
            ownerDbBlock = plc.DbBlocks.FirstOrDefault(x =>
                x?.AutoWriteRuleList != null &&
                x.AutoWriteRuleList.Contains(ruleRef));

            if (ownerDbBlock == null)
            {
                continue;
            }

            ownerPlc = plc;
            break;
        }

        if (ownerPlc == null || ownerDbBlock == null)
        {
            return;
        }

        UpdateStoredDbBlockRuntimeState(
            ownerPlc.PlcId,
            ownerDbBlock.DbNumber,
            storedDbBlock =>
            {
                var storedRuleRef = storedDbBlock.AutoWriteRuleList?
                    .FirstOrDefault(x =>
                        x != null &&
                        (x.RuleRefId == ruleRef.RuleRefId ||
                         x.RuleId == ruleRef.RuleId));

                if (storedRuleRef != null)
                {
                    storedRuleRef.IsEnabled = checkBox.IsChecked == true;
                }
            });
    }

    /// <summary>
    /// 只修改运行时需要立即生效的PLC开关字段。
    /// 使用存储服务快照进行定点修改，避免把页面中其它尚未保存的编辑一并写入。
    /// </summary>
    private void UpdateStoredPlcRuntimeState(Action<List<PlcConfigModel>> updateAction)
    {
        if (updateAction == null)
        {
            return;
        }

        PlcStoreService.Instance.EnsureLoadedFromConfig();

        var storedPlcs = PlcStoreService.Instance.GetSnapshotList();

        if (storedPlcs == null)
        {
            return;
        }

        updateAction(storedPlcs);

        PlcStoreService.Instance.ReplaceAll(storedPlcs);
    }

    private void UpdateStoredDbBlockRuntimeState(
        int plcId,
        int dbNumber,
        Action<PlcDbBlockModel> updateAction)
    {
        if (plcId <= 0 || dbNumber <= 0 || updateAction == null)
        {
            return;
        }

        UpdateStoredPlcRuntimeState(storedPlcs =>
        {
            var storedDbBlock = storedPlcs
                .FirstOrDefault(x => x != null && x.PlcId == plcId)?
                .DbBlocks?
                .FirstOrDefault(x => x != null && x.DbNumber == dbNumber);

            if (storedDbBlock != null)
            {
                updateAction(storedDbBlock);
            }
        });
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // 保存前以逻辑标识保留选择，而不是依赖 DataGrid/ComboBox 的 SelectedItem 对象引用。
        // ValidatePlcs 会 NormalizePlc，并多次触发 SelectedPlc 的绑定刷新。
        var selectedPlcIdBeforeSave = SelectedPlc?.PlcId ?? 0;
        var selectedDbNumberBeforeSave = SelectedDbBlock?.DbNumber ?? 0;
        _isSavingPlcConfig = true;

        try
        {
        if (!SaveCurrentPlcsToStore(false))
        {
            return;
        }

        if (PlcVirtualServerService.Instance.IsRunning)
        {
            PlcVirtualServerService.Instance.ReloadEnabledPlcsFromStore();

            SyncPlcEnabledStateWithRunningService();

            Growl.SuccessGlobal("PLC配置已保存，运行中的PLC已按新配置重载，原DB块数据已保留。");
        }
        else
        {
            Growl.SuccessGlobal("PLC配置已保存。");
        }

        RefreshPlcList();
        }
        finally
        {
            _isSavingPlcConfig = false;
            RestoreSelectedDbBlockAfterSave(selectedPlcIdBeforeSave, selectedDbNumberBeforeSave);

            // 处理 WPF 在本次事件处理结束后才完成的 SelectedItem 双向绑定更新。
            // 如果控件在布局刷新时再把选择清空，下一轮 Dispatcher 会重新选中原DB。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsLoaded && IsVisible)
                {
                    RestoreSelectedDbBlockAfterSave(selectedPlcIdBeforeSave, selectedDbNumberBeforeSave);
                }
            }), DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// 保存配置及PLC重载后，按PLC ID / DB编号恢复原先的选中项。
    /// 仅选择仍然存在的DB块，不会新增或覆盖用户的DB配置。
    /// </summary>
    private void RestoreSelectedDbBlockAfterSave(int plcId, int dbNumber)
    {
        if (plcId <= 0)
        {
            return;
        }

        var plc = Plcs.FirstOrDefault(item => item != null && item.PlcId == plcId);
        if (plc == null || !PlcListView.Contains(plc))
        {
            return;
        }

        if (!ReferenceEquals(SelectedPlc, plc))
        {
            SelectedPlc = plc;
        }

        if (dbNumber <= 0)
        {
            return;
        }

        var dbBlock = plc.DbBlocks?.FirstOrDefault(item => item != null && item.DbNumber == dbNumber);
        if (dbBlock == null)
        {
            // DB确实已被删除或更改编号时，不恢复过期的选中对象。
            return;
        }

        if (!ReferenceEquals(SelectedDbBlock, dbBlock))
        {
            SelectedDbBlock = dbBlock;
        }

        // 即使对象引用未变化，也通知两个绑定控件恢复可见的选中状态。
        OnPropertyChanged(nameof(SelectedDbBlock));
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// 保存当前PLC配置。
    /// 保存前自动把所有已启用PLC的IP添加到电脑网卡。
    /// </summary>
    private bool SaveCurrentPlcsToStore(bool requireEnabledPlc, IList<PlcConfigModel> plcsToSave = null)
    {
        plcsToSave ??= Plcs;
        if (!ValidatePlcs(plcsToSave, requireEnabledPlc))
        {
            return false;
        }

        var snapshot = plcsToSave
            .Where(x => x != null)
            .Select(x =>
            {
                NormalizePlc(x);
                var copy = x.Clone();
                foreach (var block in copy.DbBlocks)
                {
                    PlcProtocolTemplate.Basic.CompactSavedCopy(block);
                }
                return copy;
            })
            .ToList();

        try
        {
            /*
             * 先确保本机网卡具有PLC监听IP，
             * 再保存配置和启动/重载虚拟PLC。
             */
            EnsureEnabledPlcIpAddresses(snapshot);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "自动添加PLC本机IP失败：\r\n" +
                ex.Message +
                "\r\n\r\nPLC配置本次未保存。" +
                "\r\n请确认程序已使用管理员权限运行。",
                "PLC配置",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            return false;
        }

        // 批量操作传入原列表对象的子集或超集。校验和网卡处理耗时，提交前再核对移除项。
        if (!ReferenceEquals(plcsToSave, Plcs) && Plcs.Except(plcsToSave)
                .Any(plc => plc != null && PlcVirtualServerService.Instance.IsPlcRunning(plc.PlcId)))
        {
            Growl.WarningGlobal("有待删除的 PLC 已开始运行，本次删除已取消。");
            return false;
        }

        PlcStoreService.Instance.ReplaceAll(snapshot);
        ResultPlcs = snapshot;

        return true;
    }

    // 批量增删先保存候选列表，成功后再更新界面集合。
    private bool TrySavePlcBatch(IList<PlcConfigModel> plcsToSave)
    {
        try
        {
            return SaveCurrentPlcsToStore(false, plcsToSave);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存 PLC 配置失败。\r\n" + ex.Message,
                "PLC 配置", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void RefreshPlcServerUi()
    {
        if (PlcServerStatusHintTextBlock == null)
        {
            return;
        }

        var runningCount = PlcVirtualServerService.Instance.RunningCount;

        var isRunning = runningCount > 0;

        OnPropertyChanged(nameof(IsServiceRunning));
        OnPropertyChanged(nameof(CanEditSelectedPlc));
        OnPropertyChanged(nameof(CanViewDbBlockBits));
        OnPropertyChanged(nameof(CanEditRuntimeConfig));

        if (isRunning)
        {
            PlcServerStatusEllipse.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));

            PlcServerStatusTextBlock.Text = $"{runningCount} 台运行中";

            PlcServerStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));

            PlcServerStatusHintTextBlock.Text = "停用 PLC 后可修改连接参数。运行中可编辑 DB 块和自动写入规则。";
        }
        else
        {
            PlcServerStatusEllipse.Fill = new SolidColorBrush(Color.FromRgb(100, 116, 139));

            PlcServerStatusTextBlock.Text = "服务未启动";

            PlcServerStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(83, 101, 128));

            PlcServerStatusHintTextBlock.Text = "选择 PLC 编辑连接参数，勾选启用后启动。";
        }

        var allRunning = Plcs.Count > 0 &&
                         Plcs
                             .Where(x => x != null)
                             .All(x => x.IsEnabled && PlcVirtualServerService.Instance.IsPlcRunning(x.PlcId));

        ToggleAllPlcsButton.Content = allRunning
            ? "全部停用"
            : "全部启用";

        var hasSelectedPlc = SelectedPlc != null && PlcListView.Contains(SelectedPlc);
        var canEditSelectedPlc = hasSelectedPlc && CanEditSelectedPlc;
        var selectedRows = PlcListDataGrid.SelectedItems;
        RemovePlcButton.IsEnabled = selectedRows.OfType<PlcConfigModel>()
            .Any(plc => !PlcVirtualServerService.Instance.IsPlcRunning(plc.PlcId));
        PlcDetailEditPanel.IsEnabled = hasSelectedPlc;

        var visibleCount = PlcListDataGrid.Items.Count;
        PlcListSummaryTextBlock.Text = $"显示 {visibleCount} / {Plcs.Count} 台 · 已选 {selectedRows.Count} 台";
        PlcListEmptyTextBlock.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlcListEmptyTextBlock.Text = Plcs.Count == 0 ? "点击「新增」添加 PLC" : "没有匹配的 PLC";
        ClearPlcFilterButton.IsEnabled = PlcSearchText.Length > 0 || PlcModeFilterIndex != 0;
        CopyPlcEndpointButton.IsEnabled = hasSelectedPlc;
        UseProxyEndpointForDirectButton.IsEnabled = canEditSelectedPlc;

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
        string remark,
        bool enableBlock = true)
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
                IsEnabled = enableBlock,
                BlockName = blockName,
                Remark = remark
            };

            plc.DbBlocks.Add(target);

            return;
        }

        if (enableBlock)
        {
            target.IsEnabled = true;
        }

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

    private bool ValidatePlcs(IList<PlcConfigModel> plcsToSave, bool requireEnabledPlc)
    {
        if (plcsToSave.Count == 0)
        {
            if (requireEnabledPlc)
            {
                MessageBox.Show("启动PLC模拟服务前，至少需要配置一台PLC。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        foreach (var plc in plcsToSave.Where(x => x != null))
        {
            NormalizePlc(plc);
        }

        if (requireEnabledPlc && plcsToSave.All(x => x == null || !x.IsEnabled))
        {
            MessageBox.Show("启动PLC模拟服务前，至少需要启用一台PLC。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var duplicatePlcId = plcsToSave
            .Where(x => x != null)
            .GroupBy(x => x.PlcId)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicatePlcId != null)
        {
            MessageBox.Show($"PLC ID重复：{duplicatePlcId.Key}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var duplicateListeningEndPoint = plcsToSave
            .Where(x => x != null)
            .Where(x => x.IsEnabled)
            .GroupBy(x => x.GetListeningEndPoint())
            .FirstOrDefault(x => x.Key != null && x.Count() > 1);

        if (duplicateListeningEndPoint != null)
        {
            MessageBox.Show(
                $"启用的 PLC 监听地址重复（{duplicateListeningEndPoint.Key}）。请检查 IP 和端口。",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var duplicateExternalEndpoint = plcsToSave
            .Where(x => x != null)
            .Where(x => x.IsEnabled)
            .GroupBy(x => x.ConnectionMode == PlcConnectionMode.Direct
                ? x.GetListeningEndPoint()
                : x.GetProxyEndPoint())
            .FirstOrDefault(x => x.Key != null && x.Count() > 1);

        if (duplicateExternalEndpoint != null)
        {
            MessageBox.Show(
                $"启用的PLC外部代理或直连地址重复：{duplicateExternalEndpoint.Key}。同一个外部IP和代理端口（或直连端口）只能配置一台。",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        foreach (var plc in plcsToSave)
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

            var useDirect = plc.ConnectionMode == PlcConnectionMode.Direct;
            if (useDirect &&
                (!IPAddress.TryParse(plc.DirectListenAddress?.Trim(), out var directIp) ||
                 directIp.AddressFamily != AddressFamily.InterNetwork ||
                 directIp.Equals(IPAddress.Any)))
            {
                MessageBox.Show($"{plc.PlcName} 的直连 IP 必须是本机 IPv4 地址。", "提示", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (!useDirect && string.IsNullOrWhiteSpace(plc.IpAddress))
            {
                MessageBox.Show($"{plc.PlcName} 的IP地址不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!useDirect && !IPAddress.TryParse(plc.IpAddress.Trim(), out _))
            {
                MessageBox.Show($"{plc.PlcName} 的IP地址格式不正确：{plc.IpAddress}", "提示", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (!useDirect && (plc.ProxyPort <= 0 || plc.ProxyPort > 65535))
            {
                MessageBox.Show($"{plc.PlcName} 的代理端口范围不正确。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (plc.GetListeningPort() <= 0 || plc.GetListeningPort() > 65535)
            {
                if (useDirect)
                {
                    MessageBox.Show($"{plc.PlcName} 的直连端口必须在 1 至 65535 之间。", "提示",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                MessageBox.Show($"{plc.PlcName} 的内部端口范围不正确。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!useDirect && plc.Port == plc.ProxyPort)
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

                // 保存时再次校验全局规则，兼容旧配置及未经界面直接修改的绑定状态。
                // 全局停用只影响对应绑定，不影响其他规则及DB块的正常保存。
                if (ruleRef.RuleId > 0 &&
                    ruleMap.TryGetValue(ruleRef.RuleId, out var disabledGlobalRule) &&
                    !disabledGlobalRule.IsEnabled)
                {
                    ruleRef.IsEnabled = false;
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

                EnsureAddressDbBlock(plc, dbBlock.DbNumber, trigger.ReadAddress, enableBlock: rule.IsEnabled);
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

                EnsureAddressDbBlock(plc, dbBlock.DbNumber, writeItem.WriteAddress, enableBlock: rule.IsEnabled);
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

    private void EnsureAddressDbBlock(PlcConfigModel plc, int dbNumber, string address, bool enableBlock = true)
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
            "根据当前DB块绑定的全局自动写入规则地址自动补充。",
            enableBlock);
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
                x.ProxyPort,
                x.DirectListenPort
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

    private void OpenOperationPanelButton_Click(object sender, RoutedEventArgs e)
    {
        var plc = SelectedPlc;
        if (plc == null)
        {
            MessageBox.Show(this, "请先选择PLC。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 操作按钮位于DB块表格行中，优先获取当前点击行的DB块，避免打开其他行的操作面板。
        var dbBlock = (sender as FrameworkElement)?.DataContext as PlcDbBlockModel ?? SelectedDbBlock;
        if (dbBlock == null)
        {
            MessageBox.Show(this, "请先选择DB块。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedDbBlock = dbBlock;

        try
        {
            // 同一个PLC的同一个DB块仅复用一个已经打开的操作面板。
            var panel = Application.Current.Windows.OfType<PlcDbBlockProtocolPanelWindow>()
                .FirstOrDefault(window => window.PlcId == plc.PlcId && window.DbNumber == dbBlock.DbNumber);

            if (panel == null)
            {
                var rows = PlcProtocolTemplate.Basic.GetEffectiveItems(dbBlock)
                    .Select(PlcProtocolBoardRowModel.FromProtocolItem);

                panel = new PlcDbBlockProtocolPanelWindow(plc.PlcId, dbBlock.DbNumber, rows)
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
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开DB块操作面板失败：\r\n" + ex,
                "异常", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public sealed class PlcConnectionCountConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        return values.Length >= 2 && values[0] is int plcId &&
               values[1] is IReadOnlyDictionary<int, int> counts && counts.TryGetValue(plcId, out var count)
            ? count.ToString(culture)
            : "0";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}


/// <summary>
/// PLC规则引用的全局开关转换器。依赖窗口全局规则库快照进行显示与禁用控制。
/// 默认返回可启用状态；ConverterParameter=status 时返回文字。
/// </summary>
public sealed class PlcGlobalRuleEnabledConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var ruleId = values.Length >= 1 && values[0] is int id ? id : 0;
        var enabled = false;
        var exists = values.Length >= 2 &&
                     values[1] is IReadOnlyDictionary<int, bool> states &&
                     states.TryGetValue(ruleId, out enabled);

        if (string.Equals(parameter as string, "status", StringComparison.OrdinalIgnoreCase))
        {
            return !exists ? "规则不存在" : enabled ? "全局已启用" : "全局未启用";
        }

        return exists && enabled;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
