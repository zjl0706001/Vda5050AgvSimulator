#nullable disable

using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Vda5050AgvSimulator.Helpers;
using Vda5050AgvSimulator.Models.Agv;
using Vda5050AgvSimulator.Services.Agv;

namespace Vda5050AgvSimulator.Views;

public partial class AgvOperationView
{
    private readonly DispatcherTimer _refreshRetryTimer;
    private AgvModel _selectedAgv;
    private volatile bool _listening;
    private int _refreshPending;

    public AgvOperationView()
    {
        InitializeComponent();
        _refreshRetryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _refreshRetryTimer.Tick += (_, _) =>
        {
            _refreshRetryTimer.Stop();
            RequestSelectedAgvRefresh();
        };

        SetOperationEnabled(false);
        Loaded += AgvOperationView_Loaded;
        Unloaded += AgvOperationView_Unloaded;
        IsVisibleChanged += AgvOperationView_IsVisibleChanged;
    }

    private void CommandPanels_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var panel = (WrapPanel)sender;
        panel.ItemWidth = e.NewSize.Width >= 700 ? e.NewSize.Width / 2 : e.NewSize.Width;
    }

    private void AgvOperationView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_listening)
        {
            return;
        }

        var store = AgvStoreService.Instance;
        store.SelectedAgvChanged += AgvStoreService_SelectedAgvChanged;
        store.SelectedAgvUpdated += AgvStoreService_AgvStateUpdated;
        store.AgvStateUpdated += AgvStoreService_AgvStateUpdated;
        _listening = true;
        RequestSelectedAgvRefresh();
    }

    private void AgvOperationView_Unloaded(object sender, RoutedEventArgs e)
    {
        _listening = false;
        _refreshRetryTimer.Stop();
        var store = AgvStoreService.Instance;
        store.SelectedAgvChanged -= AgvStoreService_SelectedAgvChanged;
        store.SelectedAgvUpdated -= AgvStoreService_AgvStateUpdated;
        store.AgvStateUpdated -= AgvStoreService_AgvStateUpdated;
        SetSelectedAgv(null);
    }

    private void AgvOperationView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            RequestSelectedAgvRefresh();
        }
        else
        {
            _refreshRetryTimer.Stop();
        }
    }

    private void AgvStoreService_SelectedAgvChanged(object sender, AgvModel agv)
    {
        RequestSelectedAgvRefresh();
    }

    private void AgvStoreService_AgvStateUpdated(object sender, EventArgs e)
    {
        RequestSelectedAgvRefresh();
    }

    private void SetSelectedAgv(AgvModel agv)
    {
        if (ReferenceEquals(_selectedAgv, agv))
        {
            return;
        }

        if (_selectedAgv is INotifyPropertyChanged previousAgv)
        {
            previousAgv.PropertyChanged -= SelectedAgv_PropertyChanged;
        }

        _selectedAgv = agv;
        if (_selectedAgv is INotifyPropertyChanged selectedAgv)
        {
            selectedAgv.PropertyChanged += SelectedAgv_PropertyChanged;
        }

        ResetSummary(agv);
    }

    private void SelectedAgv_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case null:
            case "":
            case nameof(AgvModel.DeviceID):
            case nameof(AgvModel.OnlineStatus):
            case nameof(AgvModel.RunStatus):
            case nameof(AgvModel.PositionID):
            case nameof(AgvModel.Equantity):
            case nameof(AgvModel.LoadStatus):
            case nameof(AgvModel.ControlStatus):
            case nameof(AgvModel.AlarmCode):
            case nameof(AgvModel.StopButtonStatus):
                RequestSelectedAgvRefresh();
                break;
        }
    }

    private void RequestSelectedAgvRefresh()
    {
        if (!_listening || Dispatcher.HasShutdownStarted ||
            Interlocked.CompareExchange(ref _refreshPending, 1, 0) != 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _refreshPending, 0);
            if (_listening && IsVisible)
            {
                SetSelectedAgv(AgvStoreService.Instance.SelectedAgv);
                RefreshSelectedAgvView();
            }
        }));
    }

    private void ResetSummary(AgvModel agv)
    {
        SelectedAgvIdTextBlock.Text = agv == null ? "选择一台 AGV" : $"AGV {agv.DeviceID}";
        SelectedStatusBorder.Visibility = Visibility.Collapsed;
        SelectedPositionTextBlock.Text = "—";
        SelectedElectricTextBlock.Text = "—";
        SelectedBatteryBar.Value = 0;
        SelectedRunStatusTextBlock.Text = "—";
        SelectedLoadStatusTextBlock.Text = "—";
        SelectedSafetyTextBlock.Text = "—";
        SelectedSafetyTextBlock.Foreground = (Brush)FindResource("OpMuted");
        UpdateSelectionStyle(OnlineButton, false);
        UpdateSelectionStyle(OfflineButton, false);
        UpdateSelectionStyle(ManualModeButton, false);
        UpdateSelectionStyle(AutomaticModeButton, false);
        SetOperationEnabled(agv != null);
    }

    private void RefreshSelectedAgvView()
    {
        var agv = _selectedAgv;
        if (agv == null)
        {
            _refreshRetryTimer.Stop();
            return;
        }

        if (!Monitor.TryEnter(agv.SimulationStateLock))
        {
            _refreshRetryTimer.Start();
            return;
        }

        int deviceId;
        int positionId;
        int runStatus;
        int onlineStatus;
        int controlStatus;
        int stopButtonStatus;
        int alarmCode;
        double battery;
        string loadStatus;
        try
        {
            deviceId = agv.DeviceID;
            positionId = agv.PositionID;
            runStatus = agv.RunStatus;
            onlineStatus = agv.OnlineStatus;
            controlStatus = agv.ControlStatus;
            stopButtonStatus = agv.StopButtonStatus;
            alarmCode = agv.AlarmCode;
            battery = agv.Equantity;
            loadStatus = agv.LoadStatus;
        }
        finally
        {
            Monitor.Exit(agv.SimulationStateLock);
        }

        _refreshRetryTimer.Stop();
        SelectedAgvIdTextBlock.Text = $"AGV {deviceId}";
        SelectedPositionTextBlock.Text = positionId.ToString();
        SelectedRunStatusTextBlock.Text = GetRunStatusText(runStatus);
        SelectedLoadStatusTextBlock.Text = loadStatus?.Trim().ToUpperInvariant() switch
        {
            "LOADED" => "带载",
            "UNLOAD" => "空载",
            _ => "未知"
        };

        var hasBattery = !double.IsNaN(battery) && !double.IsInfinity(battery);
        SelectedElectricTextBlock.Text = hasBattery ? $"{battery:0.#}%" : "—";
        SelectedBatteryBar.Value = hasBattery ? Math.Min(100, Math.Max(0, battery)) : 0;

        RefreshOnlineStatusView(onlineStatus);
        UpdateSelectionStyle(OnlineButton, onlineStatus == 1);
        UpdateSelectionStyle(OfflineButton, onlineStatus == 2);
        UpdateSelectionStyle(ManualModeButton, controlStatus == AgvHelper.AgvControlStatus.Manual);
        UpdateSelectionStyle(AutomaticModeButton, controlStatus == AgvHelper.AgvControlStatus.Automatic);

        SelectedSafetyTextBlock.Text = stopButtonStatus != 0 ? "急停已触发"
            : alarmCode != 0 ? $"故障码 {alarmCode}" : "正常";
        SelectedSafetyTextBlock.Foreground = (Brush)FindResource(
            stopButtonStatus != 0 || alarmCode != 0 ? "OpDanger" : "OpMuted");
    }

    private void RefreshOnlineStatusView(int onlineStatus)
    {
        switch (onlineStatus)
        {
            case 1:
                SetSelectedStatusView("在线", "OpOnlineText", "OpOnlineFill");
                return;
            case 2:
                SetSelectedStatusView("离线", "OpOfflineText", "OpOfflineFill");
                return;
            default:
                SetSelectedStatusView("未知", "OpMuted", "OpNeutralFill");
                return;
        }
    }

    private void SetSelectedStatusView(string text, string foregroundResource, string backgroundResource)
    {
        if (SelectedStatusTextBlock.Text != text)
        {
            SelectedStatusTextBlock.Text = text;
            SelectedStatusTextBlock.Foreground = (Brush)FindResource(foregroundResource);
            SelectedStatusBorder.Background = (Brush)FindResource(backgroundResource);
        }

        SelectedStatusBorder.Visibility = Visibility.Visible;
    }

    private void UpdateSelectionStyle(Button button, bool selected)
    {
        button.Style = (Style)FindResource(selected ? "ButtonSelected" : "ButtonStandard");
    }

    private void SetOperationEnabled(bool enabled)
    {
        OnlineButton.IsEnabled = enabled;
        OfflineButton.IsEnabled = enabled;
        ManualModeButton.IsEnabled = enabled;
        AutomaticModeButton.IsEnabled = enabled;
        StartButton.IsEnabled = enabled;
        StopButton.IsEnabled = enabled;
        PreciseStopButton.IsEnabled = enabled;
        ResetButton.IsEnabled = enabled;
        EmergencyStopButton.IsEnabled = enabled;
        ReleaseEmergencyStopButton.IsEnabled = enabled;
    }

    private void OnlineButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCommand(agv => agv.OnlineStatus = 1);
    }

    private void OfflineButton_Click(object sender, RoutedEventArgs e)
    {
        if (CheckSelectedAgv())
        {
            AgvStoreService.Instance.SetAgvOffLine(_selectedAgv);
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckSelectedAgv())
        {
            return;
        }

        var agv = _selectedAgv;
        bool manual;
        lock (agv.SimulationStateLock)
        {
            manual = agv.ControlStatus == AgvHelper.AgvControlStatus.Manual;
            if (!manual)
            {
                agv.RunStatus = 1;
                agv.VdaPaused = false;
            }
        }

        if (manual)
        {
            MessageBox.Show("当前AGV处于手动模式，请先切换到自动模式。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        NotifyAgvChanged();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCommand(agv =>
        {
            agv.RunStatus = 0;
            agv.VdaPaused = true;
        });
    }

    private void PreciseStopButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCommand(agv =>
        {
            agv.RunStatus = 2;
            agv.VdaPaused = true;
        });
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCommand(agv =>
        {
            agv.AlarmCode = 0;
            agv.StopButtonStatus = 0;
            agv.RunStatus = 0;
        });
    }

    private void ManualModeButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCommand(agv =>
        {
            agv.ControlStatus = AgvHelper.AgvControlStatus.Manual;
            agv.VdaPaused = true;
            agv.RunStatus = 0;
        });
    }

    private void AutomaticModeButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCommand(agv =>
        {
            agv.ControlStatus = AgvHelper.AgvControlStatus.Automatic;
            agv.VdaPaused = false;
        });
    }

    private void EmergencyStopButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCommand(agv =>
        {
            agv.StopButtonStatus = 1;
            agv.AlarmCode = 1;
            agv.RunStatus = 0;
            agv.VdaPaused = true;
        });
    }

    private void ReleaseEmergencyStopButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCommand(agv =>
        {
            agv.StopButtonStatus = 0;
            agv.AlarmCode = 0;
        });
    }

    private void ApplyCommand(Action<AgvModel> command)
    {
        if (!CheckSelectedAgv())
        {
            return;
        }

        var agv = _selectedAgv;
        lock (agv.SimulationStateLock)
        {
            command(agv);
        }

        NotifyAgvChanged();
    }

    private bool CheckSelectedAgv()
    {
        SetSelectedAgv(AgvStoreService.Instance.SelectedAgv);
        if (_selectedAgv != null)
        {
            return true;
        }

        MessageBox.Show("请先在车辆列表中选择一台 AGV。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private static void NotifyAgvChanged()
    {
        var store = AgvStoreService.Instance;
        store.NotifySelectedAgvUpdated();
        store.NotifyAgvStateUpdated();
    }

    private static string GetRunStatusText(int runStatus)
    {
        return runStatus switch
        {
            0 => "停止",
            1 => "运行中",
            2 => "精准停车",
            _ => "未知"
        };
    }
}
