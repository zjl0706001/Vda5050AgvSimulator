using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Vda5050AgvSimulator.Services.Agv;
using Vda5050AgvSimulator.ViewModels;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Vda5050AgvSimulator;

public partial class MainWindow
{
    private Forms.NotifyIcon? _notifyIcon;

    private bool _isRealExit;

    private const double StackedWorkspaceWidth = 930;
    private const double SplitterSize = 12;

    private bool _isWorkspaceStacked;
    private GridLength _wideDetailsWidth = new GridLength(372);
    private GridLength _stackedFleetHeight = new GridLength(1, GridUnitType.Star);
    private GridLength _stackedDetailsHeight = new GridLength(1.12, GridUnitType.Star);

    public MainWindow()
    {
        InitializeComponent();

        ConfigStoreService.Instance.EnsureLoaded();

        MapStoreService.Initialize();

        ConfigStoreService.Instance.StartMapConfigWatcher();

        DataContext = new MainViewModel();

        InitTrayIcon();

        Closing += MainWindow_Closing;

        Closed += MainWindow_Closed;
    }

    private void WorkspaceGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || DetailsPanel == null)
        {
            return;
        }

        var stacked = e.NewSize.Width < StackedWorkspaceWidth;
        if (stacked == _isWorkspaceStacked)
        {
            return;
        }

        _isWorkspaceStacked = stacked;
        if (stacked)
        {
            _wideDetailsWidth = DetailsColumn.Width;
            FleetColumn.MinWidth = 0;
            SplitterColumn.Width = new GridLength(0);
            DetailsColumn.MinWidth = 0;
            DetailsColumn.Width = new GridLength(0);

            FleetRow.MinHeight = 104;
            FleetRow.Height = _stackedFleetHeight;
            SplitterRow.Height = new GridLength(SplitterSize);
            DetailsRow.MinHeight = 144;
            DetailsRow.Height = _stackedDetailsHeight;

            Grid.SetColumn(DetailsPanel, 0);
            Grid.SetRow(DetailsPanel, 2);
            ColumnSplitter.Visibility = Visibility.Collapsed;
            RowSplitter.Visibility = Visibility.Visible;
            return;
        }

        _stackedFleetHeight = FleetRow.Height;
        _stackedDetailsHeight = DetailsRow.Height;
        FleetRow.MinHeight = 0;
        FleetRow.Height = new GridLength(1, GridUnitType.Star);
        SplitterRow.Height = new GridLength(0);
        DetailsRow.MinHeight = 0;
        DetailsRow.Height = new GridLength(0);

        FleetColumn.MinWidth = 370;
        SplitterColumn.Width = new GridLength(SplitterSize);
        DetailsColumn.MinWidth = 310;
        DetailsColumn.Width = _wideDetailsWidth;

        Grid.SetColumn(DetailsPanel, 2);
        Grid.SetRow(DetailsPanel, 0);
        ColumnSplitter.Visibility = Visibility.Visible;
        RowSplitter.Visibility = Visibility.Collapsed;
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

        Close();
    }

    /// <summary>
    /// 窗口关闭后释放托盘图标资源。
    /// </summary>
    private void MainWindow_Closed(object sender, EventArgs e)
    {
        if (_notifyIcon == null) return;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _notifyIcon = null;
    }
}