using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Vda5050AgvSimulator.Services.Agv;
using Vda5050AgvSimulator.Models.Agv;
using Vda5050AgvSimulator.ViewModels;
using Vda5050AgvSimulator.Views;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Vda5050AgvSimulator;

public partial class MainWindow
{
    private Forms.NotifyIcon? _notifyIcon;

    private bool _isRealExit;

    private readonly AgvListView _fleet;
    private readonly MqttConfigView _mqtt;
    private AgvOperationView? _operations;
    private OrderView? _orders;
    private PlcConfigWindow? _plc;

    public MainWindow()
    {
        InitializeComponent();

        ConfigStoreService.Instance.EnsureLoaded();

        MapStoreService.Initialize();

        ConfigStoreService.Instance.StartMapConfigWatcher();

        DataContext = new MainViewModel();

        _mqtt = new MqttConfigView();
        ConnectionFooter.DataContext = _mqtt;
        _fleet = new AgvListView();
        var store = AgvStoreService.Instance;
        VehicleSelection.ItemsSource = store.Agvs;
        VehicleSelection.SelectedItem = store.SelectedAgv;
        store.SelectedAgvChanged += CurrentVehicle_Changed;

        NavigationMenu.PageSelected += Navigate;
        Navigate("fleet");
        InitTrayIcon();

        Closing += MainWindow_Closing;

        Closed += MainWindow_Closed;
    }

    private void Navigate(string page)
    {
        PageContent.Content = page switch
        {
            "fleet" => _fleet,
            "operations" => _operations ??= new AgvOperationView(),
            "orders" => _orders ??= new OrderView(),
            "mqtt" => _mqtt,
            "plc" => _plc ??= new PlcConfigWindow(),
            _ => throw new ArgumentOutOfRangeException(nameof(page))
        };
        PageTitle.Text = NavigationMenu.SelectedPageTitle;
        VehiclePicker.Visibility = page is "operations" or "orders" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var compact = e.NewSize.Width < 900;
        NavigationMenu.Compact = compact;
        SidebarColumn.Width = new GridLength(compact ? 64 : 200);
        ProductLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ConnectionFooter.Visibility = ProductLabel.Visibility;
    }

    private void OpenMqttPage(object sender, RoutedEventArgs e) => NavigationMenu.SelectPage("mqtt");

    private void CurrentVehicle_Changed(object sender, AgvModel vehicle)
    {
        VehicleSelection.SelectedItem = AgvStoreService.Instance.SelectedAgv;
    }

    private void VehicleSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (VehicleSelection.SelectedItem is AgvModel vehicle && vehicle != AgvStoreService.Instance.SelectedAgv)
        {
            AgvStoreService.Instance.SetSelectedAgv(vehicle);
        }
    }

    /// <summary>
    /// 初始化托盘图标。
    /// </summary>
    private void InitTrayIcon()
    {
        _notifyIcon = new Forms.NotifyIcon();

        _notifyIcon.Text = @"VDA5050 AGV Simulator";

        var exePath = Process.GetCurrentProcess().MainModule?.FileName;

        if (!string.IsNullOrWhiteSpace(exePath))
        {
            _notifyIcon.Icon = Drawing.Icon.ExtractAssociatedIcon(exePath);
        }

        _notifyIcon.Visible = true;

        _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;

        var menu = new Forms.ContextMenuStrip();

        var showMenuItem = new Forms.ToolStripMenuItem("显示主窗口");
        showMenuItem.Click += (_, _) => ShowMainWindow();

        var hideMenuItem = new Forms.ToolStripMenuItem("隐藏到托盘");
        hideMenuItem.Click += (_, _) => HideMainWindowToTray();

        var exitMenuItem = new Forms.ToolStripMenuItem("退出程序");
        exitMenuItem.Click += (_, _) => ExitApplication();

        menu.Items.Add(showMenuItem);
        menu.Items.Add(hideMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitMenuItem);

        _notifyIcon.ContextMenuStrip = menu;
    }

    /// <summary>
    /// 双击托盘图标恢复主窗口。
    /// </summary>
    private void NotifyIcon_DoubleClick(object sender, EventArgs e)
    {
        ShowMainWindow();
    }

    /// <summary>
    /// 隐藏主窗口到托盘。
    /// </summary>
    private void HideMainWindowToTray()
    {
        if (!IsVisible)
        {
            return;
        }

        Hide();

        ShowInTaskbar = false;

        if (_notifyIcon == null) return;
        _notifyIcon.Visible = true;
        _notifyIcon.BalloonTipTitle = @"VDA5050 AGV Simulator";
        _notifyIcon.BalloonTipText = @"程序已隐藏到托盘区，双击图标可恢复。";
        _notifyIcon.ShowBalloonTip(1000);
    }

    /// <summary>
    /// 显示主窗口。
    /// </summary>
    private void ShowMainWindow()
    {
        if (!ShowInTaskbar)
        {
            ShowInTaskbar = true;
        }

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        Focus();
    }

    /// <summary>
    /// 点击关闭按钮时默认隐藏到托盘，不直接退出。
    /// </summary>
    private void MainWindow_Closing(object sender, CancelEventArgs e)
    {
        if (_isRealExit)
        {
            return;
        }

        e.Cancel = true;

        Dispatcher.BeginInvoke(new Action(HideMainWindowToTray));
    }

    /// <summary>
    /// 从托盘菜单真正退出程序。
    /// </summary>
    private void ExitApplication()
    {
        _isRealExit = true;

        _notifyIcon?.Visible = false;

        // 增量修复：明确触发 WPF 应用关闭，关闭全部 Application.Windows 窗口。
        // 托盘事件不保证一定在 WPF Dispatcher 线程执行。
        var application = Application.Current;
        if (application != null)
        {
            if (application.Dispatcher.CheckAccess())
            {
                application.Shutdown();
            }
            else
            {
                application.Dispatcher.BeginInvoke(new Action(application.Shutdown));
            }

            return;
        }

        Close();
    }

    /// <summary>
    /// 窗口关闭后释放托盘图标资源。
    /// </summary>
    private void MainWindow_Closed(object sender, EventArgs e)
    {
        AgvStoreService.Instance.SelectedAgvChanged -= CurrentVehicle_Changed;
        _plc?.CloseTools();
        NavigationMenu.Dispose();
        if (_notifyIcon == null) return;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _notifyIcon = null;
    }
}