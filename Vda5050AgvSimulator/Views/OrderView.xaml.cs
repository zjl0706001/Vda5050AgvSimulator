#nullable disable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Vda5050AgvSimulator.Models.Agv;
using Vda5050AgvSimulator.Services.Agv;

namespace Vda5050AgvSimulator.Views;

public partial class OrderView
{
    private const int UpcomingNodeLimit = 8;
    private readonly DispatcherTimer _refreshTimer;
    private bool _listening;
    private AgvModel _displayedAgv;
    private string _displayedOrderId;

    public OrderView()
    {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _refreshTimer.Tick += (_, _) => RefreshIfVisible();

        Loaded += OrderView_Loaded;
        Unloaded += OrderView_Unloaded;
        IsVisibleChanged += OrderView_IsVisibleChanged;
    }

    private void OrderView_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_listening)
        {
            AgvStoreService.Instance.SelectedAgvChanged += AgvStoreService_SelectedAgvChanged;
            _listening = true;
        }

        UpdateRefreshTimer();
    }

    private void OrderView_Unloaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Stop();
        AgvStoreService.Instance.SelectedAgvChanged -= AgvStoreService_SelectedAgvChanged;
        _listening = false;
        _displayedAgv = null;
        _displayedOrderId = null;
    }

    private void OrderView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateRefreshTimer();
    }

    private void UpdateRefreshTimer()
    {
        if (_listening && IsVisible)
        {
            _refreshTimer.Start();
            RefreshIfVisible();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    private void AgvStoreService_SelectedAgvChanged(object sender, AgvModel agv)
    {
        if (Dispatcher.CheckAccess())
        {
            RefreshIfVisible();
        }
        else if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshIfVisible));
        }
    }

    private void RefreshIfVisible()
    {
        if (!_listening || !IsVisible)
        {
            return;
        }

        var agv = AgvStoreService.Instance.SelectedAgv;
        if (agv == null)
        {
            ShowEmpty("选择一台 AGV 查看订单");
            return;
        }

        // 避免界面线程等待背景仿真或 MQTT 线程持有的单车锁。
        if (!Monitor.TryEnter(agv.SimulationStateLock))
        {
            if (!ReferenceEquals(_displayedAgv, agv))
            {
                ShowEmpty($"正在读取 AGV {agv.DeviceID} 的订单");
            }

            return;
        }

        OrderSnapshot snapshot;
        try
        {
            snapshot = CaptureSnapshot(agv);
        }
        finally
        {
            Monitor.Exit(agv.SimulationStateLock);
        }

        _displayedAgv = agv;
        _displayedOrderId = snapshot.OrderId;
        EmptyOrderPanel.Visibility = Visibility.Collapsed;
        OrderContentPanel.Visibility = Visibility.Visible;
        OrderContentPanel.DataContext = snapshot;

        RoutePanel.Visibility = snapshot.HasRoute ? Visibility.Visible : Visibility.Collapsed;
        MoreNodesTextBlock.Text = snapshot.HiddenNodeCount > 0 ? $"另有 {snapshot.HiddenNodeCount} 个节点" : string.Empty;
        MoreNodesTextBlock.Visibility = snapshot.HiddenNodeCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarningsPanel.Visibility = snapshot.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateRows(RouteNodesItemsControl, snapshot.UpcomingNodes,
            (previous, next) => previous.NodeId == next.NodeId && previous.NodeHint == next.NodeHint);
        UpdateRows(OrderActionsItemsControl, snapshot.Actions,
            (previous, next) => previous.ActionId == next.ActionId && previous.ActionType == next.ActionType &&
                                previous.StatusCode == next.StatusCode && previous.Description == next.Description);
        UpdateRows(OrderWarningsItemsControl, snapshot.Warnings,
            (previous, next) => previous.Title == next.Title && previous.Description == next.Description);
    }

    private void ShowEmpty(string text)
    {
        _displayedAgv = null;
        _displayedOrderId = null;
        EmptyOrderTextBlock.Text = text;
        EmptyOrderPanel.Visibility = Visibility.Visible;
        OrderContentPanel.Visibility = Visibility.Collapsed;
        OrderContentPanel.DataContext = null;
        RouteNodesItemsControl.ItemsSource = null;
        OrderActionsItemsControl.ItemsSource = null;
        OrderWarningsItemsControl.ItemsSource = null;
    }

    private static OrderSnapshot CaptureSnapshot(AgvModel agv)
    {
        var orderId = string.IsNullOrWhiteSpace(agv.CurrentOrderId) ? null : agv.CurrentOrderId;
        var runtime = agv.RouteRuntime;
        var matchesOrder = runtime != null && orderId != null &&
                           string.Equals(orderId, runtime.OrderId, StringComparison.OrdinalIgnoreCase) &&
                           agv.CurrentOrderUpdateId == runtime.OrderUpdateId;
        var totalNodes = matchesOrder ? runtime.Nodes?.Count ?? 0 : 0;
        var targetIndex = matchesOrder ? Math.Max(0, Math.Min(totalNodes, runtime.TargetNodeIndex)) : 0;
        var hasRoute = matchesOrder && !runtime.IsCompleted && targetIndex < totalNodes;
        var remainingNodes = hasRoute ? totalNodes - targetIndex : 0;
        var visibleNodes = Math.Min(remainingNodes, UpcomingNodeLimit);

        var snapshot = new OrderSnapshot
        {
            VehicleLabel = $"AGV {agv.DeviceID}",
            OrderId = orderId,
            UpdateIdText = orderId == null ? "—" : agv.CurrentOrderUpdateId.ToString(),
            LastNodeText = agv.PositionID > 0 ? agv.PositionID.ToString() : "—",
            LastSequenceText = orderId != null && agv.PositionID > 0 ? agv.LastNodeSequenceId.ToString() : "—",
            HasRoute = hasRoute,
            TotalNodes = totalNodes,
            ReachedNodes = targetIndex,
            TargetNodeText = hasRoute ? runtime.Nodes[targetIndex]?.nodeId ?? "—" : "—",
            HiddenNodeCount = remainingNodes - visibleNodes,
            RemainingNodesText = $"待经过节点 · {remainingNodes}",
            ProgressText = $"{targetIndex} / {totalNodes}"
        };

        if (hasRoute)
        {
            for (var index = 0; index < visibleNodes; index++)
            {
                var node = runtime.Nodes[targetIndex + index];
                var sequence = node?.sequenceId.ToString() ?? "—";
                snapshot.UpcomingNodes.Add(new NodeRow
                {
                    NodeId = node?.nodeId ?? "—",
                    NodeHint = index == 0 ? $"目标 · seq {sequence}" : $"seq {sequence}"
                });
            }
        }

        var hasActiveAction = false;
        if (agv.actionStates != null)
        {
            foreach (var action in agv.actionStates)
            {
                if (action == null)
                {
                    continue;
                }

                var statusCode = action.actionStatus?.Trim().ToUpperInvariant() ?? string.Empty;
                var row = new ActionRow
                {
                    ActionId = action.actionId ?? string.Empty,
                    ActionType = string.IsNullOrWhiteSpace(action.actionType) ? "未命名动作" : action.actionType,
                    StatusCode = statusCode,
                    Description = !string.IsNullOrWhiteSpace(action.resultDescription)
                        ? action.resultDescription
                        : action.actionDescription ?? string.Empty
                };

                SetActionStatus(row);
                hasActiveAction |= statusCode is "WAITING" or "INITIALIZING" or "RUNNING" or "PAUSED";
                snapshot.Actions.Add(row);
            }
        }

        if (agv.VdaErrors != null)
        {
            foreach (var warning in agv.VdaErrors)
            {
                if (warning == null)
                {
                    continue;
                }

                var title = string.IsNullOrWhiteSpace(warning.errorType) ? "未知告警" : warning.errorType;
                snapshot.Warnings.Add(new WarningRow
                {
                    Title = string.IsNullOrWhiteSpace(warning.errorLevel) ? title : $"{title} · {warning.errorLevel}",
                    Description = warning.errorDescription ?? string.Empty
                });
            }
        }

        if (hasRoute || hasActiveAction)
        {
            if (agv.OnlineStatus != 1)
            {
                snapshot.StatusText = agv.OnlineStatus == 2 ? "车辆离线" : "连接未知";
                snapshot.Tone = "Paused";
            }
            else if (agv.StopButtonStatus != 0)
            {
                snapshot.StatusText = "急停";
                snapshot.Tone = "Error";
            }
            else if (agv.VdaPaused)
            {
                snapshot.StatusText = "已暂停";
                snapshot.Tone = "Paused";
            }
            else
            {
                snapshot.StatusText = hasRoute ? (runtime.IsRotating ? "转向中" : "执行中") : "有待执行动作";
                snapshot.Tone = "Active";
            }
        }
        else if (matchesOrder && runtime.IsCompleted)
        {
            snapshot.StatusText = "已完成";
            snapshot.Tone = "Done";
        }
        else
        {
            // 路线运行对象释放后只保留订单头，可能是完结、取消或仅接收到订单头。
            snapshot.StatusText = orderId == null ? "无订单" : "无活动路线";
        }

        return snapshot;
    }

    private static void SetActionStatus(ActionRow row)
    {
        switch (row.StatusCode)
        {
            case "WAITING":
                row.StatusText = "等待";
                break;
            case "INITIALIZING":
                row.StatusText = "初始化";
                row.Tone = "Active";
                break;
            case "RUNNING":
                row.StatusText = "执行中";
                row.Tone = "Active";
                break;
            case "PAUSED":
                row.StatusText = "已暂停";
                row.Tone = "Paused";
                break;
            case "FINISHED":
                row.StatusText = "完成";
                row.Tone = "Done";
                break;
            case "FAILED":
                row.StatusText = "失败";
                row.Tone = "Error";
                break;
            default:
                row.StatusText = string.IsNullOrEmpty(row.StatusCode) ? "未知" : row.StatusCode;
                break;
        }
    }

    private static void UpdateRows<T>(ItemsControl control, List<T> next, Func<T, T, bool> equal)
    {
        var previous = control.ItemsSource as List<T>;
        if (previous != null && previous.Count == next.Count)
        {
            var index = 0;
            while (index < next.Count && equal(previous[index], next[index]))
            {
                index++;
            }

            if (index == next.Count)
            {
                return;
            }
        }

        control.ItemsSource = next;
    }

    private void CopyOrderIdButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_displayedOrderId))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_displayedOrderId);
        }
        catch (ExternalException)
        {
            MessageBox.Show("剪贴板暂时不可用，请稍后再试。", "复制订单号", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private sealed class OrderSnapshot
    {
        public string VehicleLabel { get; set; }
        public string OrderId { get; set; }
        public bool HasOrder => OrderId != null;
        public string StatusText { get; set; }
        public string Tone { get; set; }
        public string UpdateIdText { get; set; }
        public string LastNodeText { get; set; }
        public string LastSequenceText { get; set; }
        public string TargetNodeText { get; set; }
        public bool HasRoute { get; set; }
        public int TotalNodes { get; set; }
        public int ReachedNodes { get; set; }
        public int HiddenNodeCount { get; set; }
        public string RemainingNodesText { get; set; }
        public string ProgressText { get; set; }
        public List<NodeRow> UpcomingNodes { get; } = [];
        public List<ActionRow> Actions { get; } = [];
        public List<WarningRow> Warnings { get; } = [];
    }

    private sealed class NodeRow
    {
        public string NodeId { get; set; }
        public string NodeHint { get; set; }
    }

    private sealed class ActionRow
    {
        public string ActionType { get; set; }
        public string ActionId { get; set; }
        public string ActionIdLabel => string.IsNullOrEmpty(ActionId) ? "—" : $"ID {ActionId}";
        public string StatusCode { get; set; }
        public string StatusText { get; set; }
        public string Tone { get; set; }
        public string Description { get; set; }
    }

    private sealed class WarningRow
    {
        public string Title { get; set; }
        public string Description { get; set; }
    }
}