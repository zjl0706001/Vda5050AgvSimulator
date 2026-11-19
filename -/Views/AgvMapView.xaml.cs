using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Vda5050AgvSimulator.Views;

public partial class AgvMapView : UserControl
{
    public AgvMapView()
    {
        InitializeComponent();
    }

    private void MapCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 后面做地图缩放
    }

    private void MapCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 后面做拖拽
    }

    private void MapCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 后面做拖拽结束
    }

    private void MapCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        // 后面做地图平移
    }

    private void MapContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double radius = 11;

        MapContentGrid.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, MapContentGrid.ActualWidth, MapContentGrid.ActualHeight),
            RadiusX = radius,
            RadiusY = radius
        };
    }
}