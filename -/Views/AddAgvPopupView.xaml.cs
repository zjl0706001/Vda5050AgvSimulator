using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Vda5050AgvSimulator.Models.Agv;

namespace Vda5050AgvSimulator.Views;

public partial class AddAgvPopupView : UserControl
{
    private AddAgvRequestModel _request = new();

    public event EventHandler CloseRequested;

    public event EventHandler<AddAgvRequestModel> AddRequested;

    public event DragDeltaEventHandler PopupDragDelta;

    public AddAgvRequestModel Request
    {
        get => _request;
        set
        {
            _request = CloneRequest(value);
            DataContext = _request;
        }
    }

    public AddAgvPopupView()
    {
        InitializeComponent();

        Request = new AddAgvRequestModel();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_request.DeviceID <= 0)
        {
            MessageBox.Show("设备ID必须大于 0", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_request.AGVCount <= 0)
        {
            MessageBox.Show("设备数量必须大于 0", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_request.DeviceType))
        {
            MessageBox.Show("设备类型不能为空", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_request.LocalIP))
        {
            MessageBox.Show("本地IP不能为空", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_request.RemoteIP))
        {
            MessageBox.Show("远程IP不能为空", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_request.Equantity < 0 || _request.Equantity > 100)
        {
            MessageBox.Show("电量范围必须在 0 ~ 100 之间", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AddRequested.Invoke(this, CloneRequest(_request));
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RestoreDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        Request = new AddAgvRequestModel();
    }

    private void HeaderDragThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        PopupDragDelta?.Invoke(this, e);
    }

    private static AddAgvRequestModel CloneRequest(AddAgvRequestModel? source)
    {
        if (source == null)
        {
            return new AddAgvRequestModel();
        }

        return new AddAgvRequestModel
        {
            DeviceID = source.DeviceID,
            AGVCount = source.AGVCount,
            DeviceType = source.DeviceType,
            LocalIP = source.LocalIP,
            LocalPort = source.LocalPort,
            RemoteIP = source.RemoteIP,
            RemotePort = source.RemotePort,
            PositionID = source.PositionID,
            X = source.X,
            Y = source.Y,
            Angle = source.Angle,
            Equantity = source.Equantity
        };
    }
}