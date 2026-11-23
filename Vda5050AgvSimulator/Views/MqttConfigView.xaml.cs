using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Vda5050AgvSimulator.Helpers;
using Vda5050AgvSimulator.Models.Mqtt;
using Vda5050AgvSimulator.Services.Agv;
using Vda5050AgvSimulator.Services.Windows;

namespace Vda5050AgvSimulator.Views;

public partial class MqttConfigView
{
    public static readonly DependencyProperty ConnectionStatusProperty =
        DependencyProperty.Register(nameof(ConnectionStatus), typeof(string), typeof(MqttConfigView),
            new PropertyMetadata("未连接"));

    public string ConnectionStatus
    {
        get => (string)GetValue(ConnectionStatusProperty);
        private set => SetValue(ConnectionStatusProperty, value);
    }

    private bool _isMqttConnected;

    private bool _isMqttOperating;

    private bool _isAgvPublishLoopRunning;

    private MqttConfigModel _mqttConfig;
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
    /// </summary>
    public MqttConfigView()
    {
        InitializeComponent();

        _mqttConfig = ConfigStoreService.Instance.MqttConfig;

        ConfigStoreService.Instance.MqttConfigChanged += ConfigStoreService_MqttConfigChanged;

        ParameterEditor.Config = _mqttConfig;

        var agvSimulationService = new AgvSimulationService();

        _vda5050Service = new Vda5050Service(_mqttService, agvSimulationService);

        _mqttService.ConnectionChanged += MqttService_ConnectionChanged;

        _mqttService.MessageReceived += MqttService_MessageReceived;

        _isMqttConnected = _mqttService.IsConnected;

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
            MqttConnectSwitchButton.IsEnabled = false;

            RefreshMosquittoServiceUi();

            if (!_mosquittoWindowsService.IsInstalled())
            {
                GrowlHelper.Warning("没有找到 Mosquitto Windows 服务。\r\n" +
                    "请确认 Mosquitto 已安装为 Windows 服务，服务名称为 mosquitto。", MqttParameterPopupView.FeedbackToken);

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

                GrowlHelper.Warning("本机 Mosquitto MQTT 服务已停止。", MqttParameterPopupView.FeedbackToken);
            }
            else
            {
                await _mosquittoWindowsService.StartAsync(
                    TimeSpan.FromSeconds(15));

                GrowlHelper.Success("本机 Mosquitto MQTT 服务已启动。", MqttParameterPopupView.FeedbackToken);
            }
        }
        catch (TimeoutException)
        {
            GrowlHelper.Warning("等待 Mosquitto 服务状态变化超时。", MqttParameterPopupView.FeedbackToken);
        }
        catch (Exception ex)
        {
            GrowlHelper.Error("操作 Mosquitto Windows 服务失败：\r\n" + ex.Message +
                "\r\n\r\n请确认程序已使用管理员权限运行。", MqttParameterPopupView.FeedbackToken);
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
    /// 处理运行期MQTT配置变更。
    /// </summary>
    private void ConfigStoreService_MqttConfigChanged(
        object? sender,
        MqttConfigModel config)
    {
        RunOnUiThread(() =>
        {
            _mqttConfig = config;

            ParameterEditor.Config = _mqttConfig;

            RefreshMqttConnectionUi();
        });
    }

    private void RevertParameters(object sender, EventArgs e)
    {
        ParameterEditor.Config = ConfigStoreService.Instance.MqttConfig;
    }

    private void SaveParameters(object sender, MqttConfigModel config)
    {
        ConfigStoreService.Instance.SaveMqttConfig(config);
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
            ParameterEditor.SetEditingEnabled(false);

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
            GrowlHelper.Error($"MQTT操作失败：{ex.Message}", MqttParameterPopupView.FeedbackToken);
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
        _mqttConfig = ConfigStoreService.Instance.MqttConfig;

        RefreshMqttConnectingUi();

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
        ConnectionStatus = "连接中";
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
        ConnectionStatus = "断开中";
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

        ParameterEditor.SetEditingEnabled(!_isMqttConnected);
        MqttConnectSwitchButton.IsEnabled = !_isMosquittoOperating;

        if (_isMqttConnected)
        {
            MqttConnectSwitchButton.Content = "断开";
            MqttConnectSwitchButton.ToolTip = "断开当前 MQTT 连接";
            MqttConnectSwitchButton.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            MqttConnectSwitchButton.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            MqttConnectSwitchButton.Foreground = Brushes.White;

            MqttStatusEllipse.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            ConnectionStatus = "已连接";
            MqttStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
        }
        else
        {
            MqttConnectSwitchButton.Content = "连接";
            MqttConnectSwitchButton.ToolTip = "连接到已保存的 MQTT 服务端";
            MqttConnectSwitchButton.Background = new SolidColorBrush(Color.FromRgb(22, 119, 255));
            MqttConnectSwitchButton.BorderBrush = new SolidColorBrush(Color.FromRgb(22, 119, 255));
            MqttConnectSwitchButton.Foreground = Brushes.White;

            MqttStatusEllipse.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            ConnectionStatus = "未连接";
            MqttStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }
    }
}