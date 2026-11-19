#nullable disable

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Vda5050AgvSimulator.Models.Plc;
using Vda5050AgvSimulator.Services.Plc;

namespace Vda5050AgvSimulator.Views;

/// <summary>
/// DB块复位配置窗口。
/// 每个PLC下的每个DB块都可以单独维护自己的复位项列表。
///
/// 说明：
/// 1. 当前窗口用纯代码构建界面，不依赖复杂XAML资源，避免点击“复位配置”时因为XAML资源/样式解析异常导致窗口闪退。
/// 2. 数据先编辑副本，点击确定后再写回目标DB块。
/// 3. 复位地址支持 5.0、DB{DB}.5.0、DB*.5.0、DB1500.5.0，最终执行时按当前DB块号解析。
/// </summary>
public partial class PlcDbBlockResetConfigWindow : Window, INotifyPropertyChanged
{
    private readonly PlcDbBlockModel _targetDbBlock;

    public int DbNumber => _targetDbBlock?.DbNumber ?? 0;

    public ObservableCollection<PlcDbBlockResetItemModel> ResetItems { get; } = [];

    public PlcDbBlockResetItemModel SelectedResetItem
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public PlcDbBlockResetConfigWindow(PlcDbBlockModel dbBlock)
    {
        if (dbBlock == null)
        {
            throw new ArgumentNullException(nameof(dbBlock));
        }

        _targetDbBlock = dbBlock;

        Title = "DB块复位配置";
        Width = 780;
        Height = 520;
        MinWidth = 780;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(245, 247, 250));

        DataContext = this;

        Content = BuildContent();

        LoadFromTarget();
    }

    private FrameworkElement BuildContent()
    {
        var root = new Grid
        {
            Margin = new Thickness(14)
        };

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headerBorder = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(229, 231, 235)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(headerBorder, 0);

        var headerPanel = new StackPanel();

        headerPanel.Children.Add(new TextBlock
        {
            Text = "DB块复位配置",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39))
        });

        var tipText = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            TextWrapping = TextWrapping.Wrap,
            Text = $"当前配置对象：DB{DbNumber}。复位地址建议只写偏移地址，例如 2.0；执行时会自动解析为当前DB块，例如 DB{DbNumber}.2.0。也兼容 DB{{DB}}.2.0、DB*.2.0、DB1500.2.0。"
        };

        headerPanel.Children.Add(tipText);
        headerBorder.Child = headerPanel;
        root.Children.Add(headerBorder);

        var toolbarGrid = new Grid
        {
            Margin = new Thickness(0, 0, 0, 8)
        };
        toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(toolbarGrid, 1);

        toolbarGrid.Children.Add(new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
            Text = "勾选启用的复位项会在点击“复位DB”时逐项写入；没有启用复位项时仍按原逻辑清零整个DB块。"
        });

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        Grid.SetColumn(buttonPanel, 1);

        buttonPanel.Children.Add(CreateButton("新增复位项", AddResetItemButton_Click));
        buttonPanel.Children.Add(CreateButton("删除选中", RemoveResetItemButton_Click));
        buttonPanel.Children.Add(CreateButton("清空配置", ClearResetItemsButton_Click));

        toolbarGrid.Children.Add(buttonPanel);
        root.Children.Add(toolbarGrid);

        var gridBorder = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(229, 231, 235)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8)
        };
        Grid.SetRow(gridBorder, 2);

        var dataGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            RowHeight = 34,
            ColumnHeaderHeight = 28
        };

        dataGrid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(ResetItems)));
        dataGrid.SetBinding(DataGrid.SelectedItemProperty, new Binding(nameof(SelectedResetItem))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });

        dataGrid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "启用",
            Width = 60,
            Binding = new Binding(nameof(PlcDbBlockResetItemModel.IsEnabled))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            }
        });

        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "ID",
            Width = 60,
            Binding = new Binding(nameof(PlcDbBlockResetItemModel.ResetItemId))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            }
        });

        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "名称",
            Width = 150,
            Binding = new Binding(nameof(PlcDbBlockResetItemModel.ResetName))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            }
        });

        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "复位地址",
            Width = 130,
            Binding = new Binding(nameof(PlcDbBlockResetItemModel.ResetAddress))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            }
        });

        dataGrid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "复位值",
            Width = 80,
            Binding = new Binding(nameof(PlcDbBlockResetItemModel.ResetValue))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            }
        });

        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "备注",
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(PlcDbBlockResetItemModel.Remark))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            }
        });

        gridBorder.Child = dataGrid;
        root.Children.Add(gridBorder);

        var footerGrid = new Grid
        {
            Margin = new Thickness(0, 12, 0, 0)
        };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(footerGrid, 3);

        footerGrid.Children.Add(new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            Text = "示例：允许进入 2.0=false；请求离开 1.0=false；请求上升 378.0=false。"
        });

        var okPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        Grid.SetColumn(okPanel, 1);

        var okButton = CreateButton("确定", OkButton_Click);
        okButton.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
        okButton.Foreground = Brushes.White;
        okButton.BorderBrush = new SolidColorBrush(Color.FromRgb(37, 99, 235));

        okPanel.Children.Add(okButton);
        okPanel.Children.Add(CreateButton("取消", CancelButton_Click));

        footerGrid.Children.Add(okPanel);
        root.Children.Add(footerGrid);

        return root;
    }

    private static Button CreateButton(string text, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 86,
            Height = 30,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };

        button.Click += click;

        return button;
    }

    private void LoadFromTarget()
    {
        ResetItems.Clear();

        _targetDbBlock.ResetItems ??= [];

        foreach (var resetItem in _targetDbBlock.ResetItems)
        {
            if (resetItem == null)
            {
                continue;
            }

            ResetItems.Add(resetItem.Clone());
        }

        NormalizeResetItems();

        SelectedResetItem = ResetItems.FirstOrDefault();
    }

    private void AddResetItemButton_Click(object sender, RoutedEventArgs e)
    {
        var nextId = GetNextResetItemId();

        var resetItem = new PlcDbBlockResetItemModel
        {
            ResetItemId = nextId,
            ResetName = $"复位项-{nextId}",
            IsEnabled = true,
            ResetAddress = "0.0",
            ResetValue = false,
            Remark = string.Empty
        };

        ResetItems.Add(resetItem);

        SelectedResetItem = resetItem;
    }

    private void RemoveResetItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedResetItem == null)
        {
            MessageBox.Show("请先选择要删除的复位项。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            $"确定删除复位项吗？\r\n{SelectedResetItem.ResetName}",
            "删除复位项",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var index = ResetItems.IndexOf(SelectedResetItem);

        ResetItems.Remove(SelectedResetItem);

        SelectedResetItem = ResetItems.Count == 0
            ? null
            : ResetItems[Math.Min(index, ResetItems.Count - 1)];
    }

    private void ClearResetItemsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ResetItems.Count == 0)
        {
            return;
        }

        var result = MessageBox.Show(
            "确定清空当前DB块的所有复位配置吗？\r\n清空后点击“复位DB”会按整块清零逻辑执行。",
            "清空复位配置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        ResetItems.Clear();

        SelectedResetItem = null;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateResetItems())
        {
            return;
        }

        NormalizeResetItems();

        _targetDbBlock.ResetItems ??= [];
        _targetDbBlock.ResetItems.Clear();

        foreach (var resetItem in ResetItems.OrderBy(x => x.ResetItemId))
        {
            if (resetItem == null)
            {
                continue;
            }

            _targetDbBlock.ResetItems.Add(resetItem.Clone());
        }

        DialogResult = true;

        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;

        Close();
    }

    private bool ValidateResetItems()
    {
        var duplicateId = ResetItems
            .Where(x => x != null)
            .Where(x => x.ResetItemId > 0)
            .GroupBy(x => x.ResetItemId)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicateId != null)
        {
            MessageBox.Show(
                $"复位项ID重复：{duplicateId.Key}",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        foreach (var resetItem in ResetItems.Where(x => x != null))
        {
            if (resetItem.ResetItemId <= 0)
            {
                resetItem.ResetItemId = GetNextResetItemId();
            }

            if (string.IsNullOrWhiteSpace(resetItem.ResetName))
            {
                resetItem.ResetName = $"复位项-{resetItem.ResetItemId}";
            }

            resetItem.ResetAddress = resetItem.ResetAddress?.Trim() ?? string.Empty;
            resetItem.Remark ??= string.Empty;

            if (!resetItem.IsEnabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(resetItem.ResetAddress))
            {
                MessageBox.Show(
                    $"启用的复位项地址不能为空：{resetItem.ResetName}",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (!PlcDbBlockService.Instance.IsBoolDbAddressOrTemplate(resetItem.ResetAddress))
            {
                MessageBox.Show(
                    $"复位项地址必须是Bool地址，例如 5.0，也兼容 DB{{DB}}.5.0、DB*.5.0、DB1500.5.0。\r\n复位项={resetItem.ResetName}\r\n当前地址={resetItem.ResetAddress}",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
        }

        return true;
    }

    private void NormalizeResetItems()
    {
        var usedIds = ResetItems
            .Where(x => x != null)
            .Where(x => x.ResetItemId > 0)
            .GroupBy(x => x.ResetItemId)
            .Where(x => x.Count() == 1)
            .Select(x => x.Key)
            .ToHashSet();

        var nextId = 1;

        foreach (var resetItem in ResetItems.Where(x => x != null).ToList())
        {
            if (resetItem.ResetItemId <= 0 || ResetItems.Count(x => x != null && x.ResetItemId == resetItem.ResetItemId) > 1)
            {
                while (usedIds.Contains(nextId))
                {
                    nextId++;
                }

                resetItem.ResetItemId = nextId;
                usedIds.Add(nextId);
                nextId++;
            }

            if (string.IsNullOrWhiteSpace(resetItem.ResetName))
            {
                resetItem.ResetName = $"复位项-{resetItem.ResetItemId}";
            }

            resetItem.ResetAddress = resetItem.ResetAddress?.Trim() ?? string.Empty;

            resetItem.Remark ??= string.Empty;
        }
    }

    private int GetNextResetItemId()
    {
        var maxId = ResetItems
            .Where(x => x != null)
            .Select(x => x.ResetItemId)
            .DefaultIfEmpty(0)
            .Max();

        return maxId + 1;
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
