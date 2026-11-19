#nullable disable

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HandyControl.Controls;
using HandyControl.Data;
using Vda5050AgvSimulator.Services;
using Vda5050AgvSimulator.Services.Agv;
using Vda5050AgvSimulator.ViewModels;
using Vda5050AgvSimulator.Views;
using MessageBox = HandyControl.Controls.MessageBox;
using TabControl = HandyControl.Controls.TabControl;
using TabItem = HandyControl.Controls.TabItem;

namespace Vda5050AgvSimulator;

public partial class MainWindow
{
    private AgvListView _agvListView;

    private AgvMapView _agvMapView;

    private AgvOperationView _agvOperationView;

    private OrderView _orderView;

    private MqttConfigView _mqttConfigView;

    private MessageMonitorView _messageMonitorView;

    private LogView _logView;

    private UserControl _dashboardView;

    private UserControl _plcEntryView;

    public MainWindow()
    {
        InitializeComponent();

        AgvStoreService.Instance.AgvStateUpdated += AgvStoreService_AgvStateUpdated;

        DataContext = new MainViewModel();

        ShowDashboard();
    }

    private void MainSideMenu_SelectionChanged(object sender, FunctionEventArgs<object> e)
    {
        var item = e.Info as SideMenuItem;

        if (item == null)
        {
            return;
        }

        var key = item.Tag?.ToString();

        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        NavigateTo(key);
    }

    private void NavigateTo(string key)
    {
        switch (key)
        {
            case "Dashboard":
                ShowDashboard();
                break;

            case "AgvList":
                ShowAgvList();
                break;

            case "AgvMap":
                ShowAgvMap();
                break;

            case "AgvOperation":
                ShowAgvOperation();
                break;

            case "Order":
                ShowOrder();
                break;

            case "MqttConfig":
                ShowMqttConfig();
                break;

            case "MessageMonitor":
                ShowMessageMonitor();
                break;

            case "Log":
                ShowLog();
                break;

            case "PlcConfig":
                ShowPlcConfigEntry();
                break;
        }
    }

    private void ShowDashboard()
    {
        PageTitleTextBlock.Text = "总览看板";

        if (_dashboardView == null)
        {
            _dashboardView = CreateDashboardView();
        }

        MainContentControl.Content = _dashboardView;
    }

    private UserControl CreateDashboardView()
    {
        var root = new Grid();

        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(100) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(260) });

        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        var mqttCard = CreateCard(new MqttConfigView(), new Thickness(0, 0, 0, 12));
        Grid.SetRow(mqttCard, 0);
        Grid.SetColumn(mqttCard, 0);
        Grid.SetColumnSpan(mqttCard, 3);
        root.Children.Add(mqttCard);

        var agvListCard = CreateCard(new AgvListView(), new Thickness(0, 0, 12, 12));
        Grid.SetRow(agvListCard, 1);
        Grid.SetColumn(agvListCard, 0);
        root.Children.Add(agvListCard);

        var mapCard = CreateCard(new AgvMapView(), new Thickness(0, 0, 12, 12));
        Grid.SetRow(mapCard, 1);
        Grid.SetColumn(mapCard, 1);
        root.Children.Add(mapCard);

        var rightTab = new TabControl
        {
            Padding = new Thickness(10)
        };

        rightTab.Items.Add(new TabItem
        {
            Header = "AGV操作",
            FontWeight = FontWeights.Bold,
            FontSize = 16,
            Content = new AgvOperationView()
        });

        rightTab.Items.Add(new TabItem
        {
            Header = "订单信息",
            FontWeight = FontWeights.Bold,
            FontSize = 16,
            Content = new OrderView()
        });

        var rightCard = CreateCard(rightTab, new Thickness(0, 0, 0, 12));
        Grid.SetRow(rightCard, 1);
        Grid.SetColumn(rightCard, 2);
        root.Children.Add(rightCard);

        var bottomTab = new TabControl();

        bottomTab.Items.Add(new TabItem
        {
            Header = "运行日志",
            Content = new LogView()
        });

        bottomTab.Items.Add(new TabItem
        {
            Header = "MQTT消息监控",
            Content = new MessageMonitorView()
        });

        var bottomCard = CreateCard(bottomTab, new Thickness(0));
        Grid.SetRow(bottomCard, 2);
        Grid.SetColumn(bottomCard, 0);
        Grid.SetColumnSpan(bottomCard, 3);
        root.Children.Add(bottomCard);

        return new UserControl
        {
            Content = root
        };
    }

    private static HandyControl.Controls.Card CreateCard(UIElement content, Thickness margin)
    {
        return new HandyControl.Controls.Card
        {
            Margin = margin,
            Padding = new Thickness(10),
            Background = Brushes.White,
            Content = content
        };
    }

    private void ShowAgvList()
    {
        PageTitleTextBlock.Text = "AGV列表";

        if (_agvListView == null)
        {
            _agvListView = new AgvListView();
        }

        MainContentControl.Content = _agvListView;
    }

    private void ShowAgvMap()
    {
        PageTitleTextBlock.Text = "地图监控";

        if (_agvMapView == null)
        {
            _agvMapView = new AgvMapView();
        }

        MainContentControl.Content = _agvMapView;
    }

    private void ShowAgvOperation()
    {
        PageTitleTextBlock.Text = "AGV操作";

        if (_agvOperationView == null)
        {
            _agvOperationView = new AgvOperationView();
        }

        MainContentControl.Content = _agvOperationView;
    }

    private void ShowOrder()
    {
        PageTitleTextBlock.Text = "订单信息";

        if (_orderView == null)
        {
            _orderView = new OrderView();
        }

        MainContentControl.Content = _orderView;
    }

    private void ShowMqttConfig()
    {
        PageTitleTextBlock.Text = "MQTT配置";

        if (_mqttConfigView == null)
        {
            _mqttConfigView = new MqttConfigView();
        }

        MainContentControl.Content = _mqttConfigView;
    }

    private void ShowMessageMonitor()
    {
        PageTitleTextBlock.Text = "MQTT消息监控";

        if (_messageMonitorView == null)
        {
            _messageMonitorView = new MessageMonitorView();
        }

        MainContentControl.Content = _messageMonitorView;
    }

    private void ShowLog()
    {
        PageTitleTextBlock.Text = "运行日志";

        if (_logView == null)
        {
            _logView = new LogView();
        }

        MainContentControl.Content = _logView;
    }

    private void ShowPlcConfigEntry()
    {
        PageTitleTextBlock.Text = "PLC配置";

        if (_plcEntryView == null)
        {
            _plcEntryView = CreatePlcEntryView();
        }

        MainContentControl.Content = _plcEntryView;
    }

    private UserControl CreatePlcEntryView()
    {
        var root = new Grid();

        var card = new HandyControl.Controls.Card
        {
            Width = 420,
            Height = 220,
            Padding = new Thickness(22),
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var panel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };

        panel.Children.Add(new TextBlock
        {
            Text = "PLC配置",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39)),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        panel.Children.Add(new TextBlock
        {
            Text = "当前PLC配置仍使用独立窗口，后续可继续改成内嵌页面。",
            Margin = new Thickness(0, 12, 0, 20),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        });

        var button = new Button
        {
            Content = "打开PLC配置",
            Width = 160,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand
        };

        button.Click += (_, _) => OpenPlcConfigWindow();

        panel.Children.Add(button);

        card.Content = panel;

        root.Children.Add(card);

        return new UserControl
        {
            Content = root
        };
    }

    private void OpenPlcConfigWindow()
    {
        try
        {
            var existingWindow = Application.Current.Windows
                .OfType<PlcConfigWindow>()
                .FirstOrDefault();

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

            var window = new PlcConfigWindow
            {
                Owner = this
            };

            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "打开PLC配置窗口失败：\r\n" + ex.Message,
                "异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void AgvStoreService_AgvStateUpdated(object sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _agvListView?.InvalidateVisual();
            _agvMapView?.InvalidateVisual();
            _dashboardView?.InvalidateVisual();
        });
    }
}