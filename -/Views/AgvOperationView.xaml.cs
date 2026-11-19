#nullable disable

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Vda5050AgvSimulator.Helpers;
using Vda5050AgvSimulator.Models.Agv;
using Vda5050AgvSimulator.Services.Agv;

namespace Vda5050AgvSimulator.Views;

public partial class AgvOperationView
{
    private AgvModel _selectedAgv;

    public AgvOperationView()
    {
        InitializeComponent();

        Loaded += AgvOperationView_Loaded;
        Unloaded += AgvOperationView_Unloaded;
    }

    #region 生命周期

    private void AgvOperationView_Loaded(object sender, RoutedEventArgs e)
    {
        AgvStoreService.Instance.SelectedAgvChanged -= AgvStoreService_SelectedAgvChanged;
        AgvStoreService.Instance.SelectedAgvUpdated -= AgvStoreService_SelectedAgvUpdated;

        AgvStoreService.Instance.SelectedAgvChanged += AgvStoreService_SelectedAgvChanged;
        AgvStoreService.Instance.SelectedAgvUpdated += AgvStoreService_SelectedAgvUpdated;

        SetSelectedAgv(AgvStoreService.Instance.SelectedAgv);
    }

    private void AgvOperationView_Unloaded(object sender, RoutedEventArgs e)
    {
        AgvStoreService.Instance.SelectedAgvChanged -= AgvStoreService_SelectedAgvChanged;
        AgvStoreService.Instance.SelectedAgvUpdated -= AgvStoreService_SelectedAgvUpdated;

        UnsubscribeSelectedAgvPropertyChanged();
    }

    #endregion

    #region AGV选择与状态监听

    private void AgvStoreService_SelectedAgvChanged(object sender, AgvModel agv)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AgvStoreService_SelectedAgvChanged(sender, agv));
            return;
        }

        SetSelectedAgv(agv);
    }

    private void AgvStoreService_SelectedAgvUpdated(object sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AgvStoreService_SelectedAgvUpdated(sender, e));
            return;
        }

        RefreshSelectedAgvView();
    }

    private void SetSelectedAgv(AgvModel agv)
    {
        UnsubscribeSelectedAgvPropertyChanged();

        _selectedAgv = agv;

        SubscribeSelectedAgvPropertyChanged();

        RefreshSelectedAgvView();
    }

    private void SubscribeSelectedAgvPropertyChanged()
    {
        if (_selectedAgv is INotifyPropertyChanged notifyAgv)
        {
            notifyAgv.PropertyChanged += SelectedAgv_PropertyChanged;
        }
    }

    private void UnsubscribeSelectedAgvPropertyChanged()
    {
        if (_selectedAgv is INotifyPropertyChanged notifyAgv)
        {
            notifyAgv.PropertyChanged -= SelectedAgv_PropertyChanged;
        }
    }

    private void SelectedAgv_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SelectedAgv_PropertyChanged(sender, e));
            return;
        }

        if (!NeedRefreshSelectedAgvView(e.PropertyName))
        {
            return;
        }

        RefreshSelectedAgvView();
    }

    private bool NeedRefreshSelectedAgvView(string propertyName)
    {
        return propertyName switch
        {
            nameof(AgvModel.OnlineStatus) => true,
            nameof(AgvModel.RunStatus) => true,
            nameof(AgvModel.Speed) => true,
            nameof(AgvModel.PositionID) => true,
            nameof(AgvModel.Equantity) => true,
            nameof(AgvModel.ControlStatus) => true,
            nameof(AgvModel.AlarmCode) => true,
            nameof(AgvModel.StopButtonStatus) => true,
            _ => false
        };
    }

    #endregion

    #region UI刷新

    private void RefreshSelectedAgvView()
    {
        if (_selectedAgv == null)
        {
            RefreshEmptySelectedAgvView();
            return;
        }

        SelectedAgvIdTextBlock.Text = _selectedAgv.DeviceID.ToString();
        SelectedPositionTextBlock.Text = _selectedAgv.PositionID.ToString();
        SelectedRunStatusTextBlock.Text = GetRunStatusText(_selectedAgv.RunStatus);
        SelectedElectricTextBlock.Text = $"{_selectedAgv.Equantity}%";

        RefreshControlStatusButtons();
        RefreshOnlineStatusView();

        SetOperationEnabled(true);
    }

    private void RefreshEmptySelectedAgvView()
    {
        SelectedAgvIdTextBlock.Text = "未选择";
        SelectedPositionTextBlock.Text = "-";
        SelectedRunStatusTextBlock.Text = "-";
        SelectedElectricTextBlock.Text = "-";

        SetSelectedStatusView(
            "未选择",
            Color.FromRgb(107, 114, 128),
            Color.FromRgb(243, 244, 246),
            Color.FromRgb(209, 213, 219));

        ManualModeButton.Content = "手动";
        AutomaticModeButton.Content = "自动";

        SetOperationEnabled(false);
    }

    private void RefreshOnlineStatusView()
    {
        switch (_selectedAgv.OnlineStatus)
        {
            case 1:
                SetSelectedStatusView(
                    "在线",
                    Color.FromRgb(22, 163, 74),
                    Color.FromRgb(236, 253, 245),
                    Color.FromRgb(187, 247, 208));
                break;

            case 2:
                SetSelectedStatusView(
                    "离线",
                    Color.FromRgb(220, 38, 38),
                    Color.FromRgb(254, 242, 242),
                    Color.FromRgb(254, 202, 202));
                break;

            default:
                SetSelectedStatusView(
                    "未知",
                    Color.FromRgb(107, 114, 128),
                    Color.FromRgb(243, 244, 246),
                    Color.FromRgb(209, 213, 219));
                break;
        }
    }

    private void SetSelectedStatusView(string text, Color foreground, Color background, Color border)
    {
        SelectedStatusTextBlock.Text = text;
        SelectedStatusTextBlock.Foreground = new SolidColorBrush(foreground);
        SelectedStatusBorder.Background = new SolidColorBrush(background);
        SelectedStatusBorder.BorderBrush = new SolidColorBrush(border);
    }

    private void RefreshControlStatusButtons()
    {
        ManualModeButton.Content = "手动";
        AutomaticModeButton.Content = "自动";

        if (_selectedAgv == null)
        {
            SetManualAutomaticButtonStyle(false, false);
            return;
        }

        var isManual = _selectedAgv.ControlStatus == AgvHelper.AgvControlStatus.Manual;
        var isAutomatic = _selectedAgv.ControlStatus == AgvHelper.AgvControlStatus.Automatic;

        SetManualAutomaticButtonStyle(isManual, isAutomatic);
    }

    private void SetManualAutomaticButtonStyle(bool isManualSelected, bool isAutomaticSelected)
    {
        ManualModeButton.Style = (Style)FindResource(isManualSelected ? "ButtonPrimary" : "ButtonStandard");
        AutomaticModeButton.Style = (Style)FindResource(isAutomaticSelected ? "ButtonPrimary" : "ButtonStandard");
    }

    private void SetOperationEnabled(bool isEnabled)
    {
        OnlineButton.IsEnabled = isEnabled;
        OfflineButton.IsEnabled = isEnabled;
        StartButton.IsEnabled = isEnabled;
        StopButton.IsEnabled = isEnabled;
        PreciseStopButton.IsEnabled = isEnabled;
        ResetButton.IsEnabled = isEnabled;

        ManualModeButton.IsEnabled = isEnabled;
        AutomaticModeButton.IsEnabled = isEnabled;

        EmergencyStopButton.IsEnabled = isEnabled;
        ReleaseEmergencyStopButton.IsEnabled = isEnabled;
    }

    #endregion

    #region 按钮事件

    private void OnlineButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckSelectedAgv())
        {
            return;
        }

        _selectedAgv.OnlineStatus = 1;

        NotifyAgvChanged();
    }

    private void OfflineButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckSelectedAgv())
        {
            return;
        }

        AgvStoreService.Instance.SetAgvOffLine(_selectedAgv);

        NotifyAgvChanged();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckSelectedAgv())
        {
            return;
        }

        if (_selectedAgv.ControlStatus == AgvHelper.AgvControlStatus.Manual)
        {
            MessageBox.Show("当前AGV处于手动模式，请先切换到自动模式。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _selectedAgv.RunStatus = 1;
        _selectedAgv.VdaPaused = false;

        NotifyAgvChanged();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckSelectedAgv())
        {
            return;
        }

        _selectedAgv.RunStatus = 0;
        _selectedAgv.VdaPaused = true;

        NotifyAgvChanged();
    }

    private void PreciseStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckSelectedAgv())
        {
            return;
        }

        _selectedAgv.RunStatus = 2;
        _selectedAgv.VdaPaused = true;

        NotifyAgvChanged();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckSelectedAgv())
        {
            return;
        }

        _selectedAgv.AlarmCode = 0;
        _selectedAgv.StopButtonStatus = 0;
        _selectedAgv.RunStatus = 0;

        NotifyAgvChanged();
    }

    private void ManualModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckSelectedAgv())
        {
            return;
        }

        _selectedAgv.ControlStatus = AgvHelper.AgvControlStatus.Manual;
        _selectedAgv.VdaPaused = true;
        _selectedAgv.RunStatus = 0;

        NotifyAgvChanged();
    }

    private void AutomaticModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckSelectedAgv())
        {
            return;
        }

        _selectedAgv.ControlStatus = AgvHelper.AgvControlStatus.Automatic;
        _selectedAgv.VdaPaused = false;

        NotifyAgvChanged();
    }

    private void EmergencyStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckSelectedAgv())
        {
            return;
        }

        _selectedAgv.StopButtonStatus = 1;
        _selectedAgv.AlarmCode = 1;
        _selectedAgv.RunStatus = 0;
        _selectedAgv.VdaPaused = true;

        NotifyAgvChanged();
    }

    private void ReleaseEmergencyStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckSelectedAgv())
        {
            return;
        }

        _selectedAgv.StopButtonStatus = 0;
        _selectedAgv.AlarmCode = 0;

        NotifyAgvChanged();
    }

    #endregion

    #region 工具方法

    private bool CheckSelectedAgv()
    {
        if (_selectedAgv != null)
        {
            return true;
        }

        MessageBox.Show("请先在左侧选择一个AGV。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);

        return false;
    }

    private void NotifyAgvChanged()
    {
        AgvStoreService.Instance.NotifySelectedAgvUpdated();
    }

    private string GetRunStatusText(int runStatus)
    {
        return runStatus switch
        {
            0 => "停止",
            1 => "运行中",
            2 => "精准停车",
            _ => "未知"
        };
    }

    #endregion
}