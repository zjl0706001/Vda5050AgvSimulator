using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Vda5050AgvSimulator.Models.Plc;

public class PlcProtocolBoardRowModel : INotifyPropertyChanged
{
    public int ProtocolItemId
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public bool IsEnabled
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = true;

    public string SignalName
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "信号";

    public string DataType
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "Bool";

    public string OffsetAddress
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "DB{DB}.DBX0.0";

    public string ActualAddress
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    public string CurrentValue
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    public string WriteValue
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    public string Remark
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    public string LastMessage
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    public static PlcProtocolBoardRowModel FromProtocolItem(PlcDbBlockProtocolItemModel item)
    {
        return new PlcProtocolBoardRowModel
        {
            ProtocolItemId = item.ProtocolItemId,
            IsEnabled = item.IsEnabled,
            SignalName = item.SignalName,
            DataType = item.DataType,
            OffsetAddress = item.OffsetAddress,
            Remark = item.Remark,
            CurrentValue = string.Empty,
            WriteValue = string.Empty,
            ActualAddress = string.Empty,
            LastMessage = string.Empty
        };
    }

    public PlcDbBlockProtocolItemModel ToProtocolItem()
    {
        return new PlcDbBlockProtocolItemModel
        {
            ProtocolItemId = ProtocolItemId,
            IsEnabled = IsEnabled,
            SignalName = SignalName,
            DataType = DataType,
            OffsetAddress = OffsetAddress,
            Remark = Remark
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}