using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using Vda5050AgvSimulator.Helpers;
using Vda5050AgvSimulator.Models.Mqtt;
using Vda5050AgvSimulator.Services.Agv;

namespace Vda5050AgvSimulator.Views;

public partial class MqttParameterPopupView
{
    private const string GrowlToken = "MqttConfigGrowl";

    private MqttConfigModel _config = new();

    public event EventHandler? CloseRequested;

    public event EventHandler<MqttConfigModel>? SaveRequested;

    public event DragDeltaEventHandler? PopupDragDelta;

    /// <summary>
    /// 当前MQTT配置，设置时会克隆一份配置并刷新绑定数据。
    /// </summary>
    public MqttConfigModel Config
    {
        set
        {
            _config = CloneConfig(value);
            DataContext = _config;
        }
    }

    /// <summary>
    /// 初始化MQTT参数配置弹窗。
    /// </summary>
    public MqttParameterPopupView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 点击保存按钮，校验配置通过后通知外部保存配置。
    /// </summary>
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateConfig(_config, out var validateMessage))
        {
            ShowPopupWarning(validateMessage);
            return;
        }

        SaveRequested?.Invoke(this, CloneConfig(_config));
        ShowPopupSuccess("配置已保存。");
    }

    /// <summary>
    /// 点击取消按钮，通知外部关闭弹窗。
    /// </summary>
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 点击关闭按钮，通知外部关闭弹窗。
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 点击恢复默认按钮，将配置恢复为默认MQTT配置。
    /// </summary>
    private void RestoreDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        Config = new MqttConfigModel();

        ShowPopupSuccess("已恢复默认配置。");
    }

    /// <summary>
    /// 拖动弹窗头部时，将拖动偏移量通知给外部Popup。
    /// </summary>
    private void HeaderDragThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        PopupDragDelta?.Invoke(this, e);
    }

    /// <summary>
    /// 点击测试连接按钮。
    /// </summary>
    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await TestConnectionAsync();
        }
        catch (Exception ex)
        {
            ShowPopupError($"MQTT连接测试异常：{ex.Message}");

            TestConnectionButton.IsEnabled = true;
            TestConnectionButton.Content = "测试连接";
        }
    }

    /// <summary>
    /// 执行MQTT测试连接逻辑。
    /// </summary>
    private async Task TestConnectionAsync()
    {
        if (!ValidateConfig(_config, out var validateMessage))
        {
            ShowPopupWarning(validateMessage);
            return;
        }

        TestConnectionButton.IsEnabled = false;
        TestConnectionButton.Content = "测试中...";

        var testConfig = CloneConfig(_config);

        testConfig.ClientId = $"{_config.ClientId}_Test_{DateTime.Now:HHmmssfff}";

        testConfig.AutoReconnect = false;

        var testMqttService = new MqttService();

        try
        {
            await testMqttService.ConnectAsync(testConfig);

            await Task.Delay(300);

            if (testMqttService.IsConnected)
            {
                ShowPopupSuccess(
                    $"MQTT连接测试成功！\r\n服务端：{testConfig.ServerIp}:{testConfig.ServerPort}\r\n用户名：{testConfig.Username}\r\nClientId：{testConfig.ClientId}");
            }
            else
            {
                ShowPopupWarning(
                    $"MQTT连接测试失败。\r\n服务端：{testConfig.ServerIp}:{testConfig.ServerPort}\r\n用户名：{testConfig.Username}\r\n\r\n请检查：\r\n1. Mosquitto是否启动\r\n2. IP和端口是否正确\r\n3. 用户名密码是否正确\r\n4. mosquitto.conf 是否使用了正确的 password_file");
            }
        }
        catch (Exception ex)
        {
            ShowPopupError(
                $"MQTT连接测试异常：\r\n{ex.Message}\r\n\r\n服务端：{testConfig.ServerIp}:{testConfig.ServerPort}");
        }
        finally
        {
            try
            {
                if (testMqttService.IsConnected)
                {
                    await testMqttService.DisconnectAsync();
                }
            }
            catch
            {
                // 测试连接断开失败不影响主流程
            }

            TestConnectionButton.IsEnabled = true;
            TestConnectionButton.Content = "测试连接";
        }
    }

    /// <summary>
    /// 显示Popup内部警告提示。
    /// </summary>
    private static void ShowPopupWarning(string message)
    {
        GrowlHelper.Warning(message, GrowlToken);
    }

    /// <summary>
    /// 显示Popup内部成功提示。
    /// </summary>
    private static void ShowPopupSuccess(string message)
    {
        GrowlHelper.Success(message, GrowlToken);
    }

    /// <summary>
    /// 显示Popup内部异常提示。
    /// </summary>
    private static void ShowPopupError(string message)
    {
        GrowlHelper.Error(message, GrowlToken);
    }

    /// <summary>
    /// 校验MQTT配置参数。
    /// </summary>
    private static bool ValidateConfig(MqttConfigModel? config, out string message)
    {
        var result = config switch
        {
            null => (false, "MQTT配置为空。"),

            { ServerIp: var serverIp } when string.IsNullOrWhiteSpace(serverIp)
                => (false, "服务端IP不能为空。"),

            { ServerPort: <= 0 or > 65535 }
                => (false, "服务端端口必须在 1 到 65535 之间。"),

            { ClientId: var clientId } when string.IsNullOrWhiteSpace(clientId)
                => (false, "客户端ID不能为空。"),

            { KeepAliveSeconds: <= 0 }
                => (false, "心跳秒数必须大于 0。"),

            { InterfaceName: var interfaceName } when string.IsNullOrWhiteSpace(interfaceName)
                => (false, "接口名称不能为空，例如 VDA。"),

            { ProtocolVersion: var protocolVersion } when string.IsNullOrWhiteSpace(protocolVersion)
                => (false, "协议版本不能为空，例如 V2.0.0。"),

            { Manufacturer: var manufacturer } when string.IsNullOrWhiteSpace(manufacturer)
                => (false, "制造商不能为空，例如 HX。"),

            { MapId: var mapId } when string.IsNullOrWhiteSpace(mapId)
                => (false, "地图ID不能为空。"),

            { ZoneId: var zoneId } when string.IsNullOrWhiteSpace(zoneId)
                => (false, "区域ID不能为空。"),

            { OrderSubscribeTopic: var orderSubscribeTopic } when string.IsNullOrWhiteSpace(orderSubscribeTopic)
                => (false, "Order订阅Topic不能为空。"),

            { InstantActionsSubscribeTopic: var instantActionsSubscribeTopic } when string.IsNullOrWhiteSpace(
                    instantActionsSubscribeTopic)
                => (false, "即时动作Topic不能为空。"),

            { StatePublishTopic: var statePublishTopic } when string.IsNullOrWhiteSpace(statePublishTopic)
                => (false, "State发布Topic不能为空。"),

            { VisualizationPublishTopic: var visualizationPublishTopic } when string.IsNullOrWhiteSpace(
                    visualizationPublishTopic)
                => (false, "Visualization发布Topic不能为空。"),

            { ConnectionPublishTopic: var connectionPublishTopic } when string.IsNullOrWhiteSpace(
                    connectionPublishTopic)
                => (false, "Connection发布Topic不能为空。"),

            { FactSheetPublishTopic: var factSheetPublishTopic } when string.IsNullOrWhiteSpace(factSheetPublishTopic)
                => (false, "FactSheet发布Topic不能为空。"),

            { StatePublishTopic: var statePublishTopic } when !statePublishTopic.Contains("{serialNumber}")
                => (false, "State发布Topic必须包含 {serialNumber} 占位符。"),

            { VisualizationPublishTopic: var visualizationPublishTopic } when !visualizationPublishTopic.Contains(
                    "{serialNumber}")
                => (false, "Visualization发布Topic必须包含 {serialNumber} 占位符。"),

            { ConnectionPublishTopic: var connectionPublishTopic } when !connectionPublishTopic.Contains(
                    "{serialNumber}")
                => (false, "Connection发布Topic必须包含 {serialNumber} 占位符。"),

            { FactSheetPublishTopic: var factSheetPublishTopic } when !factSheetPublishTopic.Contains("{serialNumber}")
                => (false, "FactSheet发布Topic必须包含 {serialNumber} 占位符。"),

            { StatePublishIntervalMs: <= 0 }
                => (false, "State发布周期必须大于 0。"),

            { VisualizationPublishIntervalMs: <= 0 }
                => (false, "Visualization发布周期必须大于 0。"),

            { ConnectionPublishIntervalMs: <= 0 }
                => (false, "Connection发布周期必须大于 0。"),

            { Qos: < 0 or > 2 }
                => (false, "QoS只能是 0、1、2。"),

            { FactSheetConfig: null }
                => (false, "FactSheet配置不能为空。"),

            { FactSheetConfig.MaxLoadMass: < 0 }
                => (false, "FactSheet最大载重不能小于 0。"),

            _ => (true, string.Empty)
        };

        message = result.Item2;

        return result.Item1;
    }

    /// <summary>
    /// 克隆MQTT配置，避免直接修改原始配置对象。
    /// </summary>
    private static MqttConfigModel CloneConfig(MqttConfigModel? source)
    {
        if (source == null)
        {
            return new MqttConfigModel();
        }

        return new MqttConfigModel
        {
            ServerIp = source.ServerIp,
            ServerPort = source.ServerPort,
            Username = source.Username,
            Password = source.Password,
            ClientId = source.ClientId,
            AutoReconnect = source.AutoReconnect,
            KeepAliveSeconds = source.KeepAliveSeconds,

            InterfaceName = source.InterfaceName,
            ProtocolVersion = source.ProtocolVersion,
            Manufacturer = source.Manufacturer,
            MapId = source.MapId,
            ZoneId = source.ZoneId,

            OrderSubscribeTopic = source.OrderSubscribeTopic,
            InstantActionsSubscribeTopic = source.InstantActionsSubscribeTopic,
            StatePublishTopic = source.StatePublishTopic,
            VisualizationPublishTopic = source.VisualizationPublishTopic,
            ConnectionPublishTopic = source.ConnectionPublishTopic,
            FactSheetPublishTopic = source.FactSheetPublishTopic,

            StatePublishIntervalMs = source.StatePublishIntervalMs,
            VisualizationPublishIntervalMs = source.VisualizationPublishIntervalMs,
            ConnectionPublishIntervalMs = source.ConnectionPublishIntervalMs,

            Qos = source.Qos,
            RetainState = source.RetainState,
            RetainVisualization = source.RetainVisualization,
            RetainConnection = source.RetainConnection,
            RetainFactSheet = source.RetainFactSheet,
            PublishFactSheetOnOnline = source.PublishFactSheetOnOnline,

            FactSheetConfig = new FactSheetConfigModel
            {
                AgvKinematic = source.FactSheetConfig.AgvKinematic,
                AgvClass = source.FactSheetConfig?.AgvClass ?? 0,
                MaxLoadMass = source.FactSheetConfig?.MaxLoadMass ?? 0
            }
        };
    }
}