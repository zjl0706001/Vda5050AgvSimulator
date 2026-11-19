using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandyControl.Controls;
using Vda5050AgvSimulator.Helpers;
using Vda5050AgvSimulator.Models.Mqtt;
using Vda5050AgvSimulator.Services.Agv;
using Vda5050AgvSimulator.Services.Plc;
using Vda5050AgvSimulator.Services.Windows;
using MessageBox = System.Windows.MessageBox;

namespace Vda5050AgvSimulator.Views;

public partial class MqttConfigView
{
    private bool _isMqttConnected;

    private bool _isMqttOperating;

    private bool _isAgvPublishLoopRunning;

    private MqttConfigModel _mqttConfig;
    private PlcConfigWindow? _plcConfigWindow;
    private readonly MqttService _mqttService = new();

    private readonly Vda5050Service _vda5050Service;

    /// <summary>
    /// 是否正在操作 Mosquitto Windows 服务。
    /// </summary>
    private bool _isMosquittoOperating;

    /// <summary>
    /// 本机 Mosquitto Windows 服务控制。
    /// </summary>
    private readonly MosquittoWindowsService _mosquittoWindowsService =
        MosquittoWindowsService.Instance;

    /// <summary>
    /// 初始化MQTT配置视图，加载配置、绑定事件并刷新界面状态。
    /// 注意：这里只显示PLC配置概况，不启动、不停止PLC模拟服务。
    /// </summary>
    public MqttConfigView()
    {
        InitializeComponent();

        _mqttConfig = ConfigStoreService.Instance.MqttConfig;

        ConfigStoreService.Instance.MqttConfigChanged += ConfigStoreService_MqttConfigChanged;

        MqttParameterPopup.Config = _mqttConfig;

        PlcStoreService.Instance.EnsureLoadedFromConfig();

        PlcStoreService.Instance.PlcConfigChanged += PlcStoreService_PlcConfigChanged;

        var agvSimulationService = new AgvSimulationService();

        _vda5050Service = new Vda5050Service(_mqttService, agvSimulationService);

        _mqttService.ConnectionChanged += MqttService_ConnectionChanged;

        _mqttService.MessageReceived += MqttService_MessageReceived;

        _isMqttConnected = _mqttService.IsConnected;

        RefreshMqttConfigUi();

        RefreshPlcConfigUi();

        RefreshMqttConnectionUi();
        RefreshMosquittoServiceUi();
    }

    /// <summary>
    /// 启动或停止本机 Mosquitto Windows 服务。
    /// MQTT客户端连接与Windows服务控制相互独立。
    /// 停止服务前会先主动断开当前MQTT客户端，避免自动重连循环。
    /// </summary>
    private async void MosquittoServiceSwitchButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            if (_isMosquittoOperating || _isMqttOperating)
            {
                return;
            }

            _isMosquittoOperating = true;

            RefreshMosquittoServiceUi();

            if (!_mosquittoWindowsService.IsInstalled())
            {
                MessageBox.Show(
                    "没有找到 Mosquitto Windows 服务。\r\n" +
                    "请确认 Mosquitto 已安装为 Windows 服务，服务名称为 mosquitto。",
                    "MQTT服务",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            if (_mosquittoWindowsService.IsRunning())
            {
                if (_mqttService.IsConnected)
                {
                    _vda5050Service.StopPublishLoop();

                    await _mqttService.DisconnectAsync();

                    _isMqttConnected = false;

                    RefreshMqttConnectionUi();
                }

                await _mosquittoWindowsService.StopAsync(
                    TimeSpan.FromSeconds(15));

                Growl.WarningGlobal("本机 Mosquitto MQTT 服务已停止。");
            }
            else
            {
                await _mosquittoWindowsService.StartAsync(
                    TimeSpan.FromSeconds(15));

                Growl.SuccessGlobal("本机 Mosquitto MQTT 服务已启动。");
            }
        }
        catch (TimeoutException)
        {
            MessageBox.Show(
                "等待 Mosquitto 服务状态变化超时。",
                "MQTT服务",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "操作 Mosquitto Windows 服务失败：\r\n" + ex.Message +
                "\r\n\r\n请确认程序已使用管理员权限运行。",
                "MQTT服务",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isMosquittoOperating = false;

            RefreshMosquittoServiceUi();

            RefreshMqttConnectionUi();
        }
    }

    /// <summary>
    /// 刷新本机 Mosquitto Windows 服务按钮。
    /// </summary>
    private void RefreshMosquittoServiceUi()
    {
        if (MosquittoServiceSwitchButton == null)
        {
            return;
        }

        if (_isMosquittoOperating)
        {
            MosquittoServiceSwitchButton.Content = "处理中...";
            MosquittoServiceSwitchButton.IsEnabled = false;
            MosquittoServiceSwitchButton.Background =
                new SolidColorBrush(Color.FromRgb(107, 114, 128));
            MosquittoServiceSwitchButton.BorderBrush =
                new SolidColorBrush(Color.FromRgb(107, 114, 128));

            return;
        }

        if (!_mosquittoWindowsService.IsInstalled())
        {
            MosquittoServiceSwitchButton.Content = "未安装MQTT";
            MosquittoServiceSwitchButton.IsEnabled = false;
            MosquittoServiceSwitchButton.Background =
                new SolidColorBrush(Color.FromRgb(107, 114, 128));
            MosquittoServiceSwitchButton.BorderBrush =
                new SolidColorBrush(Color.FromRgb(107, 114, 128));
            MosquittoServiceSwitchButton.ToolTip =
                "未找到名称为 mosquitto 的 Windows 服务";

            return;
        }

        var isRunning = _mosquittoWindowsService.IsRunning();

        MosquittoServiceSwitchButton.IsEnabled = true;
        MosquittoServiceSwitchButton.ToolTip =
            "Mosquitto服务状态：" +
            _mosquittoWindowsService.GetStatusText();

        if (isRunning)
        {
            MosquittoServiceSwitchButton.Content = "停止本机MQTT";
            MosquittoServiceSwitchButton.Background =
                new SolidColorBrush(Color.FromRgb(239, 68, 68));
            MosquittoServiceSwitchButton.BorderBrush =
                new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }
        else
        {
            MosquittoServiceSwitchButton.Content = "启动本机MQTT";
            MosquittoServiceSwitchButton.Background =
                new SolidColorBrush(Color.FromRgb(22, 163, 74));
            MosquittoServiceSwitchButton.BorderBrush =
                new SolidColorBrush(Color.FromRgb(22, 163, 74));
        }

        MosquittoServiceSwitchButton.Foreground = Brushes.White;
    }

    /// <summary>
    /// 处理运行期PLC配置变更。
    /// 这里只刷新PLC启用台数、规则数量等概况。
    /// </summary>
    private void PlcStoreService_PlcConfigChanged(
        object sender,
        EventArgs e)
    {
        RunOnUiThread(RefreshPlcConfigUi);
    }

    /// <summary>
    /// 处理运行期MQTT配置变更。
    /// </summary>
    private void ConfigStoreService_MqttConfigChanged(
        object? sender,
        MqttConfigModel config)
    {
        RunOnUiThread(() =>
        {
            _mqttConfig = config;

            MqttParameterPopup.Config = _mqttConfig;

            RefreshMqttConfigUi();
            RefreshMqttConnectionUi();
        });
    }

    /// <summary>
    /// 点击Broker信息区域时，打开MQTT参数配置Popup。
    /// </summary>
    private void BrokerInfoBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_mqttService.IsConnected)
        {
            Growl.WarningGlobal("MQTT已连接，请先断开连接后再修改参数。");
            return;
        }

        _mqttConfig = ConfigStoreService.Instance.MqttConfig;

        MqttParameterPopup.Config = _mqttConfig;

        ConfigPopup.IsOpen = true;

        Dispatcher.BeginInvoke(new Action(() => { PopupHelper.CenterPopupInScreen(ConfigPopup, this); }));
    }

    /// <summary>
    /// 点击PLC配置区域时，打开PLC配置窗口。
    /// 注意：这里只负责打开窗口和刷新概况，不保存、不启动、不停止PLC服务。
    /// </summary>
    private void PlcInfoBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        OpenPlcConfigWindow();
    }


    /// <summary>
    /// 打开PLC多配置窗口。
    /// PLC配置保存、PLC模拟服务启动停止全部在PlcConfigWindow内部处理。
    /// </summary>
    private void OpenPlcConfigWindow()
    {
        if (_plcConfigWindow is { IsVisible: true })
        {
            _plcConfigWindow.Activate();

            _plcConfigWindow.Focus();

            return;
        }

        _plcConfigWindow = null;

        _plcConfigWindow = new PlcConfigWindow
        {
            Topmost = false,
            ShowInTaskbar = true
        };

        _plcConfigWindow.Closed += PlcConfigWindow_Closed;

        _plcConfigWindow.Show();
    }

    private void PlcConfigWindow_Closed(object sender, EventArgs e)
    {
        if (_plcConfigWindow != null)
        {
            _plcConfigWindow.Closed -= PlcConfigWindow_Closed;
        }

        _plcConfigWindow = null;

        RefreshPlcConfigUi();
    }

    /// <summary>
    /// 处理MQTT配置弹窗关闭请求。
    /// </summary>
    private void MqttParameterPopupView_CloseRequested(object sender, EventArgs e)
    {
        ConfigPopup.IsOpen = false;
    }

    /// <summary>
    /// 保存MQTT配置，并刷新配置显示和连接状态界面。
    /// </summary>
    private void MqttParameterPopupView_SaveRequested(object sender, MqttConfigModel config)
    {
        _mqttConfig = config;

        ConfigStoreService.Instance.SaveMqttConfig(_mqttConfig);

        RefreshMqttConfigUi();

        RefreshMqttConnectionUi();

        ConfigPopup.IsOpen = false;
    }

    /// <summary>
    /// 拖动MQTT配置弹窗，并限制弹窗不超出屏幕工作区。
    /// </summary>
    private void MqttParameterPopupView_PopupDragDelta(object sender, DragDeltaEventArgs e)
    {
        PopupHelper.MovePopupInScreen(ConfigPopup, this, e.HorizontalChange, e.VerticalChange);
    }

    /// <summary>
    /// 点击MQTT连接按钮，根据当前连接状态执行连接或断开。
    /// 注意：这里只控制MQTT和AGV发布循环，不控制PLC模拟服务。
    /// </summary>
    private async void MqttConnectSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isMqttOperating)
            {
                return;
            }

            _isMqttOperating = true;

            if (_mqttService.IsConnected)
            {
                await DisconnectMqttAsync();
            }
            else
            {
                await ConnectMqttAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"MQTT操作失败：{ex.Message}",
                "MQTT连接",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            ApplyMqttConnectionState(_mqttService.IsConnected);

            _isMqttOperating = false;

            RefreshMqttConnectionUi();
        }
    }

    /// <summary>
    /// 执行MQTT连接，连接成功后启动AGV状态发布循环。
    /// </summary>
    private async Task ConnectMqttAsync()
    {
        RefreshMqttConnectingUi();

        _mqttConfig = ConfigStoreService.Instance.MqttConfig;

        RefreshMqttConfigUi();

        await _mqttService.ConnectAsync();

        ApplyMqttConnectionState(_mqttService.IsConnected);
    }

    /// <summary>
    /// 执行MQTT断开，断开前停止AGV状态发布循环。
    /// </summary>
    private async Task DisconnectMqttAsync()
    {
        RefreshMqttDisconnectingUi();

        StopAgvPublishLoopIfNeeded();

        AgvStoreService.Instance.SetAllAgvOffLine();

        await _mqttService.DisconnectAsync();

        ApplyMqttConnectionState(_mqttService.IsConnected);
    }

    /// <summary>
    /// 处理MQTT连接状态变化，自动启动或停止AGV发布循环。
    /// </summary>
    private void MqttService_ConnectionChanged(bool connected)
    {
        RunOnUiThread(() =>
        {
            ApplyMqttConnectionState(connected);

            if (!_isMqttOperating)
            {
                RefreshMqttConnectionUi();
            }
        });
    }

    /// <summary>
    /// 处理收到的MQTT消息。
    /// 当前回调运行在MQTT业务单消费者线程，不切换到UI线程。
    /// </summary>
    private void MqttService_MessageReceived(
        string topic,
        string payload,
        string messageType,
        string serialNumber)
    {
        _vda5050Service.HandleMqttMessage(topic, payload);
    }

    private void RunOnUiThread(Action action)
    {
        if (action == null)
        {
            return;
        }

        if (Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.BeginInvoke(
            action,
            DispatcherPriority.Background);
    }

    /// <summary>
    /// 根据真实MQTT连接状态同步界面状态和AGV发布循环。
    /// 注意：这里不再控制PLC模拟服务。
    /// </summary>
    private void ApplyMqttConnectionState(bool connected)
    {
        _isMqttConnected = connected;

        if (connected)
        {
            StartAgvPublishLoopIfNeeded();
        }
        else
        {
            StopAgvPublishLoopIfNeeded();
        }
    }

    /// <summary>
    /// 如果AGV状态发布循环未启动，则启动发布循环。
    /// </summary>
    private void StartAgvPublishLoopIfNeeded()
    {
        if (_isAgvPublishLoopRunning)
        {
            return;
        }

        _vda5050Service.StartPublishLoop();

        _isAgvPublishLoopRunning = true;
    }

    /// <summary>
    /// 如果AGV状态发布循环已启动，则停止发布循环。
    /// </summary>
    private void StopAgvPublishLoopIfNeeded()
    {
        if (!_isAgvPublishLoopRunning)
        {
            return;
        }

        _vda5050Service.StopPublishLoop();

        _isAgvPublishLoopRunning = false;
    }

    /// <summary>
    /// 刷新MQTT配置显示信息。
    /// </summary>
    private void RefreshMqttConfigUi()
    {
        ServerIpTextBlock.Text = _mqttConfig.ServerIp;

        ServerPortTextBlock.Text = _mqttConfig.ServerPort.ToString();

        MqttBrokerTextBlock.Text = $"Broker: {_mqttConfig.ServerIp}:{_mqttConfig.ServerPort}";
    }

    /// <summary>
    /// 刷新PLC配置概况显示。
    /// 这里只显示：PLC总数、启用台数、自动写入规则数量。
    /// </summary>
    private void RefreshPlcConfigUi()
    {
        PlcStoreService.Instance.EnsureLoadedFromConfig();

        var plcs = PlcStoreService.Instance.GetSnapshotList();

        var totalCount = plcs.Count;

        var enabledCount = plcs.Count(x => x.IsEnabled);

        var autoWritePlcCount = plcs.Count(x => x.IsEnabled && x.AutoWriteEnabled);

        var enabledRuleCount = plcs
            .Where(x => x.IsEnabled && x.AutoWriteEnabled)
            .Sum(x => x.AutoWriteRules?.Count(r => r.IsEnabled) ?? 0);

        if (totalCount <= 0)
        {
            PlcConfigTextBlock.Text = "PLC: 未配置";

            PlcStatusEllipse.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));

            PlcInfoBorder.ToolTip = "未配置PLC";

            return;
        }

        var firstEnabledPlc = plcs.FirstOrDefault(x => x.IsEnabled) ?? plcs.FirstOrDefault();

        PlcConfigTextBlock.Text =
            $"PLC: {enabledCount}/{totalCount}台";


        if (enabledCount <= 0)
        {
            PlcStatusEllipse.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }
        else if (PlcVirtualServerService.Instance.IsRunning)
        {
            PlcStatusEllipse.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
        }
        else if (enabledRuleCount > 0)
        {
            PlcStatusEllipse.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        }
        else
        {
            PlcStatusEllipse.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        }

        if (firstEnabledPlc != null)
        {
            PlcInfoBorder.ToolTip =
                $"主PLC：{firstEnabledPlc.PlcName} {firstEnabledPlc.PlcType} {firstEnabledPlc.IpAddress}:{firstEnabledPlc.Port}";
        }
    }

    /// <summary>
    /// 刷新MQTT连接中的界面状态。
    /// </summary>
    private void RefreshMqttConnectingUi()
    {
        MqttConnectSwitchButton.Content = "连接中...";
        MqttConnectSwitchButton.IsEnabled = false;
        MqttConnectSwitchButton.Background = new SolidColorBrush(Color.FromRgb(107, 114, 128));
        MqttConnectSwitchButton.BorderBrush = new SolidColorBrush(Color.FromRgb(107, 114, 128));
        MqttConnectSwitchButton.Foreground = Brushes.White;

        MqttStatusEllipse.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        MqttStatusTextBlock.Text = "连接中";
        MqttStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        MqttBrokerTextBlock.Text = $"Broker: {_mqttConfig.ServerIp}:{_mqttConfig.ServerPort}";
    }

    /// <summary>
    /// 刷新MQTT断开中的界面状态。
    /// </summary>
    private void RefreshMqttDisconnectingUi()
    {
        MqttConnectSwitchButton.Content = "断开中...";
        MqttConnectSwitchButton.IsEnabled = false;
        MqttConnectSwitchButton.Background = new SolidColorBrush(Color.FromRgb(107, 114, 128));
        MqttConnectSwitchButton.BorderBrush = new SolidColorBrush(Color.FromRgb(107, 114, 128));
        MqttConnectSwitchButton.Foreground = Brushes.White;

        MqttStatusEllipse.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        MqttStatusTextBlock.Text = "断开中";
        MqttStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        MqttBrokerTextBlock.Text = $"Broker: {_mqttConfig.ServerIp}:{_mqttConfig.ServerPort}";
    }

    /// <summary>
    /// 根据当前MQTT连接状态刷新按钮、状态灯和状态文字。
    /// </summary>
    private void RefreshMqttConnectionUi()
    {
        MqttBrokerTextBlock.Text = $"Broker: {_mqttConfig.ServerIp}:{_mqttConfig.ServerPort}";

        if (_isMqttOperating)
        {
            MqttConnectSwitchButton.IsEnabled = false;
            return;
        }

        MqttConnectSwitchButton.IsEnabled = true;

        if (_isMqttConnected)
        {
            MqttConnectSwitchButton.Content = "断开";
            MqttConnectSwitchButton.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            MqttConnectSwitchButton.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            MqttConnectSwitchButton.Foreground = Brushes.White;

            MqttStatusEllipse.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            MqttStatusTextBlock.Text = "已连接";
            MqttStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
        }
        else
        {
            MqttConnectSwitchButton.Content = "连接";
            MqttConnectSwitchButton.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
            MqttConnectSwitchButton.BorderBrush = new SolidColorBrush(Color.FromRgb(37, 99, 235));
            MqttConnectSwitchButton.Foreground = Brushes.White;

            MqttStatusEllipse.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            MqttStatusTextBlock.Text = "未连接";
            MqttStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }

        RefreshPlcConfigUi();
    }
}