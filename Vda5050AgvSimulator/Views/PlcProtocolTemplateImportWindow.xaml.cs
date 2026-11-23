using System.Windows;
using Vda5050AgvSimulator.Services.Plc;

namespace Vda5050AgvSimulator.Views;

/// <summary>只读的全局点位表。方向筛选只改变列表显示。</summary>
public partial class PlcProtocolTemplateImportWindow : Window
{
    public PlcProtocolTemplateImportWindow()
    {
        InitializeComponent();
        DirectionBox.SelectedIndex = 0;
        DirectionBox.SelectionChanged += (_, _) => ShowDirection();
        ShowDirection();
    }

    private void ShowDirection()
    {
        var shared = PlcProtocolTemplate.Basic;
        var signals = DirectionBox.SelectedIndex switch
        {
            1 => shared.AgvToPlc,
            2 => shared.PlcToAgv,
            _ => shared.Signals
        };
        SignalsGrid.ItemsSource = signals;
        CountText.Text = $"显示 {signals.Count} / {shared.Count} 个信号　·　完整双向布局需 {shared.RequiredLength} 字节";
    }
}
