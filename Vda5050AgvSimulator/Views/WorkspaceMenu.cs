using System;
using System.Drawing;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using MenuItem = AntdUI.MenuItem;

namespace Vda5050AgvSimulator.Views;

public sealed class WorkspaceMenu : WindowsFormsHost
{
    private readonly AntdUI.Menu _menu = new AntdUI.Menu
    {
        Dock = DockStyle.Fill,
        Mode = AntdUI.TMenuMode.Inline,
        ColorScheme = AntdUI.TAMode.Light,
        BackColor = Color.FromArgb(20, 47, 61),
        ForeColor = Color.FromArgb(204, 222, 225),
        BackHover = Color.FromArgb(37, 67, 80),
        BackActive = Color.FromArgb(8, 113, 115),
        ForeActive = Color.White,
        Padding = new Padding(0),
        Gap = 12,
        IconGap = 12,
        itemMargin = 5,
        Radius = 9,
        FocusMode = AntdUI.TFocusMode.Border,
        TabStop = true,
        MouseRightCtrl = false,
        AccessibleRole = AccessibleRole.MenuBar,
        AccessibleDescription = "使用上下方向键切换菜单项，按回车打开。"
    };

    public static readonly DependencyProperty CompactProperty =
        DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(WorkspaceMenu),
            new PropertyMetadata(false, (host, args) =>
                ((WorkspaceMenu)host)._menu.SetCollapsed((bool)args.NewValue)));

    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public string SelectedPageTitle => _menu.SelectItem?.Text ?? string.Empty;

    public event Action<string>? PageSelected;

    public WorkspaceMenu()
    {
        AutomationProperties.SetName(this, "工作区导航");
        _menu.Items.Add(new MenuItem("车辆管理", "CarOutlined") { ID = "fleet" });
        _menu.Items.Add(new MenuItem("车辆操作", "ControlOutlined") { ID = "operations" });
        _menu.Items.Add(new MenuItem("订单信息", "ProfileOutlined") { ID = "orders" });
        _menu.Items.Add(new MenuItem("MQTT 连接", "ApiOutlined") { ID = "mqtt" });
        _menu.Items.Add(new MenuItem("PLC 配置", "DeploymentUnitOutlined") { ID = "plc" });
        Child = _menu;
        _menu.SelectChanged += (_, _) =>
        {
            var item = _menu.SelectItem;
            if (item?.ID == null) return;
            _menu.AccessibleName = "工作区导航，" + item.Text;
            PageSelected?.Invoke(item.ID);
        };
        _menu.SelectIndex(0, false);
    }

    public void SelectPage(string key)
    {
        foreach (MenuItem item in _menu.Items)
        {
            if (item.ID == key)
            {
                _menu.Select(item, false);
                return;
            }
        }
    }
}
