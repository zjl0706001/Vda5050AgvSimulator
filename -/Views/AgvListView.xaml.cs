#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using HandyControl.Controls;
using Vda5050AgvSimulator.Helpers;
using Vda5050AgvSimulator.Models.Agv;
using Vda5050AgvSimulator.Services.Agv;
using MessageBox = System.Windows.MessageBox;
using ScrollViewer = System.Windows.Controls.ScrollViewer;

namespace Vda5050AgvSimulator.Views;

public partial class AgvListView
{
    private static ObservableCollection<AgvModel> Agvs => AgvStoreService.Instance.Agvs;

    public ICollectionView AgvView { get; set; }

    public AgvModel SelectedAgv { get; set; }

    /// <summary>
    /// 初始化AGV列表视图，加载AGV配置、绑定筛选和选中事件。
    /// </summary>
    public AgvListView()
    {
        InitializeComponent();

        DataContext = this;

        LoadAgvsFromConfig();

        AgvView = CollectionViewSource.GetDefaultView(Agvs);

        AgvView.Filter = FilterAgvById;

        RefreshStatistics();

        AgvStoreService.Instance.SelectedAgvUpdated += AgvStoreService_SelectedAgvUpdated;
        AgvStoreService.Instance.AgvStateUpdated += AgvStoreService_AgvStateUpdated;
        Unloaded += AgvListView_Unloaded;

        if (Agvs.Count <= 0) return;

        SelectedAgv = Agvs[0];

        AgvCardListBox.SelectedItem = SelectedAgv;

        AgvStoreService.Instance.SetSelectedAgv(SelectedAgv);
    }

    /// <summary>
    /// 从配置文件加载AGV列表。
    /// </summary>
    private void LoadAgvsFromConfig()
    {
        AgvStoreService.Instance.LoadFromConfig();

        AgvView?.Refresh();
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

        AgvStoreService.Instance.SetSelectedAgv(SelectedAgv);
    }

    /// <summary>
    /// 处理外部AGV选中对象更新，保存配置并刷新列表。
    /// </summary>
    private void AgvStoreService_SelectedAgvUpdated(object sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AgvStoreService_SelectedAgvUpdated(sender, e));
            return;
        }

        SaveAgvsToConfig();

        AgvView?.Refresh();

        RefreshStatistics();
    }

    /// <summary>
    /// 处理AGV全局状态更新，刷新列表和统计信息。
    /// </summary>
    private void AgvStoreService_AgvStateUpdated(object sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(new Action(() => AgvStoreService_AgvStateUpdated(sender, e)));
            return;
        }

        AgvView?.Refresh();

        RefreshStatistics();
    }

    /// <summary>
    /// 控件卸载时解绑事件，避免内存泄漏。
    /// </summary>
    private void AgvListView_Unloaded(object sender, RoutedEventArgs e)
    {
        AgvStoreService.Instance.SelectedAgvUpdated -= AgvStoreService_SelectedAgvUpdated;
        AgvStoreService.Instance.AgvStateUpdated -= AgvStoreService_AgvStateUpdated;
        Unloaded -= AgvListView_Unloaded;
    }

    /// <summary>
    /// 打开添加AGV弹窗，并居中显示。
    /// </summary>
    private void AddAgvButton_Click(object sender, RoutedEventArgs e)
    {
        AddAgvPopupView.Request = new AddAgvRequestModel();

        AddAgvPopup.IsOpen = true;

        Dispatcher.BeginInvoke(new Action(() => { PopupHelper.CenterPopupInScreen(AddAgvPopup, this); }));
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
    /// 搜索框清空时恢复显示全部AGV。
    /// </summary>
    private void SearchIdTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SearchIdTextBox.Text))
        {
            SearchAgvById();
        }
    }

    /// <summary>
    /// 根据设备ID刷新AGV列表筛选。
    /// </summary>
    private void SearchAgvById()
    {
        AgvView?.Refresh();
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

        if (SearchIdTextBox == null)
        {
            return true;
        }

        var keyword = SearchIdTextBox.Text;

        if (string.IsNullOrWhiteSpace(keyword))
        {
            return true;
        }

        keyword = keyword.Trim();

        return agv.DeviceID.ToString().Contains(keyword);
    }

    /// <summary>
    /// 处理AGV卡片点击，单击选中，双击打开编辑弹窗。
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

        SelectedAgv = agv;

        AgvCardListBox.SelectedItem = agv;

        AgvStoreService.Instance.SetSelectedAgv(agv);

        if (e.ClickCount == 2)
        {
            OpenEditAgvPopup(agv);
        }

        e.Handled = true;
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

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// 打开编辑AGV弹窗，并居中显示。
    /// </summary>
    private void OpenEditAgvPopup(AgvModel agv)
    {
        EditAgvPopupView.SetAgv(agv);

        EditAgvPopup.IsOpen = true;

        Dispatcher.BeginInvoke(new Action(() => { PopupHelper.CenterPopupInScreen(EditAgvPopup, this); }));
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

    /// <summary>
    /// 关闭添加AGV弹窗。
    /// </summary>
    private void AddAgvPopupView_CloseRequested(object sender, EventArgs e)
    {
        AddAgvPopup.IsOpen = false;
    }

    /// <summary>
    /// 根据添加请求批量创建AGV。
    /// </summary>
    private void AddAgvPopupView_AddRequested(object sender, AddAgvRequestModel request)
    {
        if (request == null)
        {
            return;
        }

        if (request.DeviceID <= 0)
        {
            MessageBox.Show("设备ID必须大于 0。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (request.AGVCount <= 0)
        {
            MessageBox.Show("设备数量必须大于 0。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                agv.RemoteIP = $"ip{string.Join(".", remoteIP)}";
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

        AddAgvPopup.IsOpen = false;
    }

    /// <summary>
    /// 拖动添加AGV弹窗，并限制弹窗不超出屏幕工作区。
    /// </summary>
    private void AddAgvPopupView_PopupDragDelta(object sender, DragDeltaEventArgs e)
    {
        PopupHelper.MovePopupInScreen(AddAgvPopup, this, e.HorizontalChange, e.VerticalChange);
    }

    /// <summary>
    /// 关闭编辑AGV弹窗。
    /// </summary>
    private void EditAgvPopupView_CloseRequested(object sender, EventArgs e)
    {
        EditAgvPopup.IsOpen = false;
    }

    /// <summary>
    /// 保存编辑后的AGV，并刷新列表和统计信息。
    /// </summary>
    private void EditAgvPopupView_SaveRequested(object sender, AgvEditedEventArgs e)
    {
        if (e == null)
        {
            return;
        }

        var replaced = AgvStoreService.Instance.ReplaceAgv(e.SourceAgv, e.EditedAgv);

        if (!replaced)
        {
            MessageBox.Show("设备ID已存在，不能重复。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedAgv = e.EditedAgv;

        AgvCardListBox.SelectedItem = e.EditedAgv;

        AgvStoreService.Instance.SetSelectedAgv(e.EditedAgv);

        SaveAgvsToConfig();

        AgvView?.Refresh();

        RefreshStatistics();

        EditAgvPopup.IsOpen = false;
    }

    /// <summary>
    /// 拖动编辑AGV弹窗，并限制弹窗不超出屏幕工作区。
    /// </summary>
    private void EditAgvPopupView_PopupDragDelta(object sender, DragDeltaEventArgs e)
    {
        PopupHelper.MovePopupInScreen(EditAgvPopup, this, e.HorizontalChange, e.VerticalChange);
    }

    /// <summary>
    /// 自定义鼠标滚轮滚动速度。
    /// </summary>
    private void AgvCardListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(AgvCardListBox);

        if (scrollViewer == null)
        {
            return;
        }

        var scrollSpeed = 0.28;

        var targetOffset = scrollViewer.VerticalOffset - e.Delta * scrollSpeed;

        if (targetOffset < 0)
        {
            targetOffset = 0;
        }

        if (targetOffset > scrollViewer.ScrollableHeight)
        {
            targetOffset = scrollViewer.ScrollableHeight;
        }

        scrollViewer.ScrollToVerticalOffset(targetOffset);

        e.Handled = true;
    }

    /// <summary>
    /// 向下查找指定类型的子级控件。
    /// </summary>
    private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null)
        {
            return null;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(parent);

        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is T result)
            {
                return result;
            }

            var childResult = FindVisualChild<T>(child);

            if (childResult != null)
            {
                return childResult;
            }
        }

        return null;
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
            LoadStatus = 0,

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

        RefreshToggleAllAgvOnlineButton();

        Growl.SuccessGlobal(hasOfflineAgv ? "所有AGV已上线。" : "所有AGV已下线。");
    }

    /// <summary>
    /// 刷新全部上线/全部下线按钮文字。
    /// </summary>
    private void RefreshToggleAllAgvOnlineButton()
    {
        var agvs = AgvStoreService.Instance.Agvs;

        if (agvs.Count == 0)
        {
            ToggleAllAgvOnlineButton.Content = "全部上线";
            return;
        }

        var hasOfflineAgv = agvs.Any(x => x.OnlineStatus != 1);

        ToggleAllAgvOnlineButton.Content = hasOfflineAgv ? "全部上线" : "全部下线";
    }
}