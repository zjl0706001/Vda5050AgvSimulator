#nullable disable

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Vda5050AgvSimulator.Models.Plc;
using Vda5050AgvSimulator.Services.Plc;

namespace Vda5050AgvSimulator.Views;

/// <summary>
/// DB块位值查看窗口。
/// 按字节展示当前虚拟PLC内部DB内存，每个字节拆分成bit0-bit7。
/// </summary>
public partial class DbBlockBitsWindow : INotifyPropertyChanged
{
    public int PlcId { get; }

    public int DbNumber { get; }

    private const int RefreshIntervalMs = 300;

    private readonly string _plcName;

    private readonly int _length;

    private readonly DispatcherTimer _refreshTimer;

    private bool _isRefreshing;

    private bool _isWritingBit;

    public string TitleText
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public string LastRefreshText
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<DbBlockBitRowModel> Rows { get; } = [];

    public event PropertyChangedEventHandler PropertyChanged;

    public DbBlockBitsWindow(int plcId, string plcName, int dbNumber, int length, byte[] bytes)
    {
        InitializeComponent();

        PlcId = plcId;
        _plcName = string.IsNullOrWhiteSpace(plcName) ? "PLC-" + plcId : plcName;
        DbNumber = dbNumber;
        _length = length <= 0 ? 256 : length;

        DataContext = this;

        TitleText = _plcName + " / DB" + DbNumber + " 位值查看，长度 " + _length + " 字节";

        LastRefreshText = "自动刷新间隔：" + RefreshIntervalMs + "ms";

        LoadRows(bytes);

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(RefreshIntervalMs)
        };

        _refreshTimer.Tick += RefreshTimer_Tick;

        Loaded += DbBlockBitsWindow_Loaded;

        Closed += DbBlockBitsWindow_Closed;
    }

    private void DbBlockBitsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Start();

        RefreshRows(false);
    }

    private void DbBlockBitsWindow_Closed(object sender, EventArgs e)
    {
        _refreshTimer.Stop();

        _refreshTimer.Tick -= RefreshTimer_Tick;

        Loaded -= DbBlockBitsWindow_Loaded;

        Closed -= DbBlockBitsWindow_Closed;
    }

    private void RefreshTimer_Tick(object sender, EventArgs e)
    {
        RefreshRows(false);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRows(true);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RefreshRows(bool showErrorMessage)
    {
        if (_isRefreshing || _isWritingBit)
        {
            return;
        }

        _isRefreshing = true;

        try
        {
            if (!PlcVirtualServerService.Instance.TryReadDbBytes(
                    PlcId,
                    DbNumber,
                    _length,
                    out var bytes,
                    out var message))
            {
                LastRefreshText = "刷新失败：" + message;

                if (showErrorMessage)
                {
                    MessageBox.Show(
                        "刷新DB块失败：" + message,
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                return;
            }

            LoadRows(bytes);

            LastRefreshText = "最后刷新：" + DateTime.Now.ToString("HH:mm:ss.fff");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void LoadRows(byte[] bytes)
    {
        bytes ??= [];

        for (var i = 0; i < bytes.Length; i++)
        {
            var value = bytes[i];

            if (i < Rows.Count)
            {
                Rows[i].DbNumber = DbNumber;
                Rows[i].ByteIndex = i;
                Rows[i].Value = value;
                continue;
            }

            Rows.Add(new DbBlockBitRowModel
            {
                DbNumber = DbNumber,
                ByteIndex = i,
                Value = value
            });
        }

        while (Rows.Count > bytes.Length)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }


    private void BitTextBlock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2)
        {
            return;
        }

        e.Handled = true;

        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (element.DataContext is not DbBlockBitRowModel row)
        {
            return;
        }

        if (!int.TryParse(element.Tag?.ToString(), out var bitIndex))
        {
            return;
        }

        ToggleBit(row, bitIndex);
    }

    private void ToggleBit(DbBlockBitRowModel row, int bitIndex)
    {
        if (row == null)
        {
            return;
        }

        if (bitIndex < 0 || bitIndex > 7)
        {
            MessageBox.Show(
                "bit位必须在0-7之间。",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        if (_isWritingBit)
        {
            return;
        }

        _isWritingBit = true;

        try
        {
            if (!PlcVirtualServerService.Instance.TryToggleDbBit(
                    PlcId,
                    DbNumber,
                    row.ByteIndex,
                    bitIndex,
                    out var newValue,
                    out var message))
            {
                LastRefreshText = "写入失败：" + message;

                MessageBox.Show(
                    "写入bit位失败：" + message,
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            row.Value = newValue
                ? (byte)(row.Value | (1 << bitIndex))
                : (byte)(row.Value & ~(1 << bitIndex));

            LastRefreshText = $"已写入：DB{DbNumber}.DBX{row.ByteIndex}.{bitIndex} = {newValue}，{DateTime.Now:HH:mm:ss.fff}";
        }
        finally
        {
            _isWritingBit = false;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void TopmostButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;

        TopmostButton.Content = Topmost ? "取消置顶" : "置顶";
    }
}