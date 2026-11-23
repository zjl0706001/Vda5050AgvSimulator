#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandyControl.Controls;
using Microsoft.Win32;
using Vda5050AgvSimulator.Models.Agv;
using Vda5050AgvSimulator.Services.Agv;
using MessageBox = System.Windows.MessageBox;

namespace Vda5050AgvSimulator.Views;

public partial class AgvListView
{
    private const double MinimumCardWidth = 228;
    private const double CardHorizontalSpacing = 12;
    private const double CompactLayoutWidth = 600;

    private static readonly DependencyPropertyKey CardColumnCountPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(CardColumnCount), typeof(int), typeof(AgvListView), new PropertyMetadata(1));

    private static readonly DependencyPropertyKey IsCompactLayoutPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsCompactLayout), typeof(bool), typeof(AgvListView), new PropertyMetadata(false));

    public static readonly DependencyProperty CardColumnCountProperty = CardColumnCountPropertyKey.DependencyProperty;
    public static readonly DependencyProperty IsCompactLayoutProperty = IsCompactLayoutPropertyKey.DependencyProperty;

    private readonly DispatcherTimer _searchTimer;
    private readonly CollectionViewSource _agvViewSource = new();
    private readonly HashSet<FrameworkElement> _telemetryElements = [];
    private string _searchKeyword = string.Empty;
    private bool _storeEventsSubscribed;

    private static ObservableCollection<AgvModel> Agvs => AgvStoreService.Instance.Agvs;

    public int CardColumnCount => (int)GetValue(CardColumnCountProperty);

    public bool IsCompactLayout => (bool)GetValue(IsCompactLayoutProperty);

    public ICollectionView AgvView { get; set; }

    public AgvModel SelectedAgv { get; set; }

    /// <summary>
    /// 初始化AGV列表视图，加载AGV配置、绑定筛选和选中事件。
    /// </summary>
    public AgvListView()
    {
        _searchTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(160)
        };
        _searchTimer.Tick += SearchTimer_Tick;

        InitializeComponent();

        AgvStoreService.Instance.EnsureLoadedFromConfig();

        _agvViewSource.Source = Agvs;
        AgvView = _agvViewSource.View;

        AgvView.Filter = FilterAgvById;

        DataContext = this;
        Loaded += AgvListView_Loaded;
        Unloaded += AgvListView_Unloaded;

        AgvCardListBox.SelectedItem = AgvStoreService.Instance.SelectedAgv ?? Agvs.FirstOrDefault();
        RefreshStatistics();
    }

    /// <summary>
    /// 根据列表可用宽度计算列数，末行与前面的卡片对齐。
    /// </summary>
    private void AgvCardListBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
        {
            return;
        }

        var width = e.NewSize.Width;
        var columns = Math.Max(1, (int)(width / (MinimumCardWidth + CardHorizontalSpacing)));
        SetValue(CardColumnCountPropertyKey, columns);
        SetValue(IsCompactLayoutPropertyKey, width < CompactLayoutWidth);
    }

    /// <summary>
    /// 保存当前AGV列表到配置文件。
    /// </summary>
    private void SaveAgvsToConfig()
    {
        AgvStoreService.Instance.SaveToConfig();
    }

    /// <summary>
    /// 处理AGV卡片选中变化，并同步到全局选中服务。
    /// </summary>
    private void AgvCardListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedAgv = AgvCardListBox.SelectedItem as AgvModel;

        if (!ReferenceEquals(SelectedAgv, AgvStoreService.Instance.SelectedAgv))
        {
            AgvStoreService.Instance.SetSelectedAgv(SelectedAgv);
        }
    }

    /// <summary>
    /// 处理外部AGV选中对象更新，保存配置并刷新列表。
    /// </summary>
    private void AgvStoreService_SelectedAgvUpdated(
        object sender,
        EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                new Action(() =>
                    AgvStoreService_SelectedAgvUpdated(sender, e)));

            return;
        }

        if (!_storeEventsSubscribed)
        {
            return;
        }

        SaveAgvsToConfig();
        AgvView?.Refresh();
        RefreshStatistics();
    }

    /// <summary>
    /// 更新卡片数值和统计，避免仿真帧触发集合重置和整页重新布局。
    /// </summary>
    private void AgvStoreService_AgvStateUpdated(
        object sender,
        EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                new Action(() =>
                    AgvStoreService_AgvStateUpdated(sender, e)));

            return;
        }

        if (!_storeEventsSubscribed)
        {
            return;
        }

        foreach (var element in _telemetryElements)
        {
            // 旧版 AGV 模型的坐标、电量等属性可能没有 PropertyChanged。
            var property = element is TextBlock ? TextBlock.TextProperty : RangeBase.ValueProperty;
            element.GetBindingExpression(property)?.UpdateTarget();
        }

        RefreshStatistics();
    }

    private void AgvListView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_storeEventsSubscribed)
        {
            return;
        }

        AgvStoreService.Instance.SelectedAgvUpdated += AgvStoreService_SelectedAgvUpdated;
        AgvStoreService.Instance.AgvStateUpdated += AgvStoreService_AgvStateUpdated;
        _storeEventsSubscribed = true;
        var currentAgv = AgvStoreService.Instance.SelectedAgv;
        if (currentAgv != null && !currentAgv.DeviceID.ToString().Contains(SearchIdTextBox.Text.Trim()))
        {
            SearchIdTextBox.Clear();
        }
        SearchAgvById();
        AgvCardListBox.SelectedItem = currentAgv;
        RefreshStatistics();
    }

    /// <summary>
    /// 控件卸载时解绑事件，避免内存泄漏。
    /// </summary>
    private void AgvListView_Unloaded(object sender, RoutedEventArgs e)
    {
        _searchTimer.Stop();
        AgvStoreService.Instance.SelectedAgvUpdated -= AgvStoreService_SelectedAgvUpdated;
        AgvStoreService.Instance.AgvStateUpdated -= AgvStoreService_AgvStateUpdated;
        _storeEventsSubscribed = false;
        _telemetryElements.Clear();
    }

    private void CardTelemetry_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            _telemetryElements.Add(element);
        }
    }

    private void CardTelemetry_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            _telemetryElements.Remove(element);
        }
    }

    /// <summary>
    /// 打开添加AGV表单。
    /// </summary>
    private void AddAgvButton_Click(object sender, RoutedEventArgs e)
    {
        AddAgvEditor.Request = new AddAgvRequestModel();

        ShowAgvPage(AddAgvEditor);
    }

    /// <summary>
    /// 打开批量修改AGV地标表单。
    /// </summary>
    private void BatchPositionButton_Click(object sender, RoutedEventArgs e)
    {
        if (Agvs.Count <= 0)
        {
            Growl.WarningGlobal("当前没有AGV。");
            return;
        }

        var minDeviceId = Agvs.Min(x => x.DeviceID);
        var maxDeviceId = Agvs.Max(x => x.DeviceID);
        var defaultPositionId =
            SelectedAgv?.PositionID.ToString() ?? Agvs.FirstOrDefault()?.PositionID.ToString() ?? "0";
        var defaultAngle = SelectedAgv?.Angle.ToString("0.##") ?? Agvs.FirstOrDefault()?.Angle.ToString("0.##") ?? "0";

        BatchStartAgvIdTextBox.Text = minDeviceId.ToString();
        BatchEndAgvIdTextBox.Text = maxDeviceId.ToString();
        BatchStartPositionIdTextBox.Text = defaultPositionId;
        BatchPositionStepTextBox.Text = "0";
        BatchAngleTextBox.Text = "0";
        BatchLoadStatusComboBox.SelectedIndex = 0;

        BatchValidationText.Text = string.Empty;
        ShowAgvPage(BatchPositionEditor);
    }

    /// <summary>
    /// 关闭批量修改AGV地标表单。
    /// </summary>
    private void BatchPositionCancelButton_Click(object sender, RoutedEventArgs e)
    {
        ShowAgvPage(AgvPageScrollViewer);
    }

    /// <summary>
    /// 确认批量修改AGV地标。
    /// </summary>
    private void BatchPositionConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(BatchStartAgvIdTextBox.Text?.Trim(), out var startDeviceId))
        {
            BatchValidationText.Text = "起始AGV ID必须是整数。";
            return;
        }

        if (!int.TryParse(BatchEndAgvIdTextBox.Text?.Trim(), out var endDeviceId))
        {
            BatchValidationText.Text = "结束AGV ID必须是整数。";
            return;
        }

        if (!int.TryParse(BatchStartPositionIdTextBox.Text?.Trim(), out var startPositionId))
        {
            BatchValidationText.Text = "起始地标必须是整数。";
            return;
        }

        if (!int.TryParse(BatchPositionStepTextBox.Text?.Trim(), out var step))
        {
            BatchValidationText.Text = "地标递增必须是整数。";
            return;
        }

        if (!double.TryParse(BatchAngleTextBox.Text?.Trim(), out var angle))
        {
            BatchValidationText.Text = "角度必须是数字。";
            return;
        }

        if (startDeviceId <= 0 || endDeviceId <= 0)
        {
            BatchValidationText.Text = "AGV ID必须大于 0。";
            return;
        }

        if (startPositionId < 0)
        {
            BatchValidationText.Text = "起始地标不能小于 0。";
            return;
        }

        if (step < 0)
        {
            BatchValidationText.Text = "地标递增不能小于 0。";
            return;
        }

        if (double.IsNaN(angle) || angle < 0 || angle > 360)
        {
            BatchValidationText.Text = "角度必须在 0 - 360 之间。";
            return;
        }

        var minDeviceId = Math.Min(startDeviceId, endDeviceId);
        var maxDeviceId = Math.Max(startDeviceId, endDeviceId);
        var loadStatus = BatchLoadStatusComboBox.SelectedValue as string;

        var updatedCount = AgvStoreService.Instance.BatchUpdateAgvPositionId(
            startDeviceId,
            endDeviceId,
            startPositionId,
            step,
            angle,
            loadStatus,
            out var updateMessage);

        if (updatedCount < 0)
        {
            BatchValidationText.Text = updateMessage;
            return;
        }

        if (updatedCount <= 0)
        {
            BatchValidationText.Text = string.IsNullOrWhiteSpace(updateMessage) ? "没有找到符合范围的AGV。" : updateMessage;
            return;
        }

        var firstUpdatedAgv = Agvs
            .Where(x => x.DeviceID >= minDeviceId && x.DeviceID <= maxDeviceId)
            .OrderBy(x => x.DeviceID)
            .FirstOrDefault();

        if (firstUpdatedAgv != null)
        {
            SelectedAgv = firstUpdatedAgv;
            AgvCardListBox.SelectedItem = firstUpdatedAgv;
            AgvCardListBox.ScrollIntoView(firstUpdatedAgv);
            AgvStoreService.Instance.SetSelectedAgv(firstUpdatedAgv);
        }

        SaveAgvsToConfig();

        AgvView?.Refresh();

        RefreshStatistics();

        AgvStoreService.Instance.NotifyAgvStateUpdated();

        ShowAgvPage(AgvPageScrollViewer);

        var loadStatusMessage = loadStatus switch
        {
            "LOADED" => "，设为带载",
            "UNLOAD" => "，设为空载",
            _ => string.Empty
        };
        Growl.SuccessGlobal($"已更新 {updatedCount} 台 AGV 的地标、坐标和角度{loadStatusMessage}。");
    }

    /// <summary>
    /// 选择并导入地图文件，文件读取和校验在后台执行。
    /// </summary>
    private async void ImportMapButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入地图",
            Filter = "地图 JSON (*.json)|*.json",
            DefaultExt = ".json",
            CheckFileExists = true
        };

        var owner = System.Windows.Window.GetWindow(this);
        if ((owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true)
        {
            return;
        }

        var filePath = dialog.FileName;
        ImportMapButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() =>
            {
                var success = ConfigStoreService.Instance.TryImportMapConfig(filePath, out var map, out var message);
                return (Success: success, PointCount: map.points.Count, Message: message);
            });

            if (result.Success)
            {
                Growl.SuccessGlobal($"地图导入成功，已加载 {result.PointCount} 个地标。");
            }
            else
            {
                Growl.WarningGlobal(result.Message);
            }
        }
        catch (Exception ex)
        {
            Growl.WarningGlobal($"导入地图时发生错误。{ex.Message}");
        }
        finally
        {
            ImportMapButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 点击搜索按钮时刷新AGV筛选结果。
    /// </summary>
    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        SearchAgvById();
    }

    /// <summary>
    /// 搜索框按下回车时执行搜索。
    /// </summary>
    private void SearchIdTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SearchAgvById();

            e.Handled = true;
        }
    }

    /// <summary>
    /// 连续输入时合并筛选请求，清空后立即恢复全部卡片。
    /// </summary>
    private void SearchIdTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (AgvView == null)
        {
            return;
        }

        _searchTimer.Stop();
        if (string.IsNullOrWhiteSpace(SearchIdTextBox.Text))
        {
            SearchAgvById();
        }
        else
        {
            _searchTimer.Start();
        }
    }

    private void SearchTimer_Tick(object sender, EventArgs e)
    {
        SearchAgvById();
    }

    /// <summary>
    /// 根据设备ID刷新AGV列表筛选。
    /// </summary>
    private void SearchAgvById()
    {
        _searchTimer.Stop();
        var keyword = SearchIdTextBox.Text.Trim();
        if (keyword == _searchKeyword)
        {
            return;
        }

        _searchKeyword = keyword;
        AgvView?.Refresh();
        AgvEmptyStateTextBlock.Text = keyword.Length == 0 ? "暂无 AGV" : "未找到匹配的 AGV";
        AgvPageScrollViewer.ScrollToTop();
    }

    /// <summary>
    /// 根据搜索框内容过滤AGV设备ID。
    /// </summary>
    private bool FilterAgvById(object obj)
    {
        if (obj is not AgvModel agv)
        {
            return false;
        }

        return _searchKeyword.Length == 0 || agv.DeviceID.ToString().Contains(_searchKeyword);
    }

    /// <summary>
    /// 处理AGV卡片点击，单击选中，双击打开编辑表单。
    /// </summary>
    private void AgvCardBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseEventFromButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var element = sender as FrameworkElement;

        if (element?.DataContext is not AgvModel agv)
        {
            return;
        }

        AgvCardListBox.SelectedItem = agv;

        if (e.ClickCount == 2)
        {
            OpenAgvEditor(agv);
        }

        e.Handled = true;
    }

    private void EditAgvButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgvModel agv)
        {
            AgvCardListBox.SelectedItem = agv;
            OpenAgvEditor(agv);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 判断鼠标事件是否来源于按钮，避免按钮点击触发卡片选中逻辑。
    /// </summary>
    private bool IsMouseEventFromButton(DependencyObject source)
    {
        if (source == null)
        {
            return false;
        }

        var button = FindVisualParent<Button>(source);

        return button != null;
    }

    /// <summary>
    /// 向上查找指定类型的父级控件。
    /// </summary>
    private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var current = child;

        while (current != null)
        {
            if (current is T result)
            {
                return result;
            }

            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// 打开编辑AGV表单。
    /// </summary>
    private void OpenAgvEditor(AgvModel agv)
    {
        if (agv == null)
        {
            return;
        }

        EditAgvEditor.SetAgv(agv);

        ShowAgvPage(EditAgvEditor);
    }

    /// <summary>
    /// 删除当前按钮对应的AGV。
    /// </summary>
    private void DeleteAgvButton_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;

        if (button?.DataContext is not AgvModel agv)
        {
            return;
        }

        var result = MessageBox.Show(
            $"确定删除 AGV {agv.DeviceID} 吗？",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var removed = AgvStoreService.Instance.RemoveAgv(agv);

        if (!removed) return;

        if (SelectedAgv == agv)
        {
            SelectedAgv = Agvs.FirstOrDefault();

            AgvCardListBox.SelectedItem = SelectedAgv;

            AgvStoreService.Instance.SetSelectedAgv(SelectedAgv);
        }

        SaveAgvsToConfig();

        AgvView?.Refresh();

        RefreshStatistics();

        e.Handled = true;
    }

    private void ShowAgvPage(FrameworkElement page)
    {
        AgvPageScrollViewer.Visibility = page == AgvPageScrollViewer ? Visibility.Visible : Visibility.Collapsed;
        AddAgvEditor.Visibility = page == AddAgvEditor ? Visibility.Visible : Visibility.Collapsed;
        EditAgvEditor.Visibility = page == EditAgvEditor ? Visibility.Visible : Visibility.Collapsed;
        BatchPositionEditor.Visibility = page == BatchPositionEditor ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AgvEditor_CloseRequested(object sender, EventArgs e)
    {
        ShowAgvPage(AgvPageScrollViewer);
    }

    /// <summary>
    /// 根据添加请求批量创建AGV。
    /// </summary>
    private void AddAgvEditor_AddRequested(object sender, AddAgvRequestModel request)
    {
        if (request == null)
        {
            return;
        }

        if (request.DeviceID <= 0)
        {
            AddAgvEditor.ShowValidationError("设备ID必须大于 0。");
            return;
        }

        if (request.AGVCount <= 0)
        {
            AddAgvEditor.ShowValidationError("设备数量必须大于 0。");
            return;
        }

        var startDeviceId = request.DeviceID;
        List<int> agvDeviceIdList = [];

        for (var i = startDeviceId;; i++)
        {
            if (Agvs.Any(x => x.DeviceID == i)) continue;

            agvDeviceIdList.Add(i);

            if (agvDeviceIdList.Count >= request.AGVCount) break;
        }

        AgvModel firstAddedAgv = null;

        for (var i = 0; i < agvDeviceIdList.Count; i++)
        {
            var deviceId = agvDeviceIdList[i];
            var agv = CreateAgvFromRequest(request, deviceId, i);
            var remoteIP = request.RemoteIP.Split('.');
            if (remoteIP.Length == 4)
            {
                remoteIP[remoteIP.Length - 1] = deviceId.ToString();
                agv.RemoteIP = $"{string.Join(".", remoteIP)}";
            }

            var added = AgvStoreService.Instance.AddAgv(agv);
            if (!added)
                continue;

            firstAddedAgv ??= agv;
        }

        AgvStoreService.Instance.SortAgvsByDeviceId();

        SaveAgvsToConfig();

        AgvView?.Refresh();

        if (firstAddedAgv != null)
        {
            SelectedAgv = firstAddedAgv;

            AgvCardListBox.SelectedItem = firstAddedAgv;

            AgvCardListBox.ScrollIntoView(firstAddedAgv);

            AgvStoreService.Instance.SetSelectedAgv(firstAddedAgv);
        }

        RefreshStatistics();

        ShowAgvPage(AgvPageScrollViewer);
    }

    /// <summary>
    /// 保存编辑后的AGV，并刷新列表和统计信息。
    /// </summary>
    private void EditAgvEditor_SaveRequested(object sender, AgvEditedEventArgs e)
    {
        if (e == null)
        {
            return;
        }

        var replaced = AgvStoreService.Instance.ReplaceAgv(e.SourceAgv, e.EditedAgv);

        if (!replaced)
        {
            EditAgvEditor.ShowValidationError("设备ID已存在，不能重复。");
            return;
        }

        SelectedAgv = e.EditedAgv;

        AgvCardListBox.SelectedItem = e.EditedAgv;

        AgvStoreService.Instance.SetSelectedAgv(e.EditedAgv);

        SaveAgvsToConfig();

        AgvView?.Refresh();

        RefreshStatistics();

        // 通知地图、详情页和其他订阅者刷新坐标与角度。
        AgvStoreService.Instance.NotifyAgvStateUpdated();

        ShowAgvPage(AgvPageScrollViewer);

        Growl.SuccessGlobal($"AGV {e.EditedAgv.DeviceID} 参数修改成功。");
    }

    /// <summary>
    /// 获取下一个可用的设备ID。
    /// </summary>
    private int GetNextDeviceId()
    {
        return AgvStoreService.Instance.GetNextDeviceId();
    }

    /// <summary>
    /// 根据添加请求创建AGV模型。
    /// </summary>
    private static AgvModel CreateAgvFromRequest(AddAgvRequestModel request, int deviceId, int index)
    {
        var agv = new AgvModel
        {
            AGVCount = request.AGVCount,

            DeviceID = deviceId,

            DeviceType = 3,

            LocalIP = request.LocalIP,
            LocalPort = request.LocalPort + index,

            RemoteIP = request.RemoteIP,
            RemotePort = request.RemotePort + index,

            PositionID = request.PositionID,

            X = request.X,
            Y = request.Y,
            Angle = request.Angle,

            Equantity = request.Equantity,

            RunStatus = 0,
            RunDirection = 0,
            Speed = 0,
            DodgeArea = 0,

            OnlineStatus = 1,
            TrafficStatus = 0,
            ChargeStatus = 0,
            LoadStatus = "UNLOAD",

            AlarmCode = 0,
            ControlStatus = 1,

            ReleaseRFID = 0,
            ControlRFID = 0,

            AGVOutputSignal1 = 0,
            AGVOutputSignal2 = 0,

            FirstPoweron = false,
            ButtonRelease = false,
            Electric = 0,
            LiftHeight = 0,
            ButtonShutdown = false,
            ButtonSleep = false,
            TaskChange = false,
            TaskNo = 0,

            RCSOutputSignal1 = 0,
            RCSOutputSignal2 = 0,

            NextDistance = 0,

            SimulationButtonStart = false,

            ActionID = 0,

            ReleaseRFID1 = 0,
            ReleaseRFID2 = 0,
            ReleaseRFID3 = 0,

            AnalogSignal1 = 0,
            AnalogSignal2 = 0,

            tr_cmd = 0,
            tr_id_index = 0,
            tr_id_hour = 0,
            tr_id_day = 0,
            tr_id_month = 0,
            tr_id_year = 0,
            tr_sender = 0,
            tr_type = 0,
            tr_step_size = 0,
            tr_start_delay = 0
        };

        return agv;
    }

    /// <summary>
    /// 刷新AGV总数、在线数和离线数。
    /// </summary>
    private void RefreshStatistics()
    {
        var totalCount = Agvs.Count;
        var onlineCount = Agvs.Count(x => x.OnlineStatus == 1);
        var offlineCount = totalCount - onlineCount;
        TotalCountTextBlock.Text = totalCount.ToString();
        OnlineCountTextBlock.Text = onlineCount.ToString();
        OfflineCountTextBlock.Text = offlineCount.ToString();
        ToggleAllAgvOnlineButton.Content = totalCount > 0 && offlineCount == 0 ? "全部下线" : "全部上线";
        ToggleAllAgvOnlineButton.IsEnabled = totalCount > 0;
        BatchPositionButton.IsEnabled = totalCount > 0;
    }

    /// <summary>
    /// 点击全部上线/全部下线按钮。
    /// </summary>
    private void ToggleAllAgvOnlineButton_Click(object sender, RoutedEventArgs e)
    {
        var agvs = AgvStoreService.Instance.Agvs;

        if (agvs.Count == 0)
        {
            Growl.WarningGlobal("当前没有AGV。");
            return;
        }


        var hasOfflineAgv = agvs.Any(x => x.OnlineStatus != 1);

        var newOnlineStatus = hasOfflineAgv ? 1 : 2;

        foreach (var agv in agvs)
        {
            agv.OnlineStatus = newOnlineStatus;
        }

        AgvStoreService.Instance.NotifySelectedAgvUpdated();

        RefreshStatistics();

        Growl.SuccessGlobal(hasOfflineAgv ? "所有AGV已上线。" : "所有AGV已下线。");
    }
}