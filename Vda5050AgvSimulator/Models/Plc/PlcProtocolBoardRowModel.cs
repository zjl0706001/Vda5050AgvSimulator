using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Vda5050AgvSimulator.Models.Plc;

public class PlcProtocolBoardRowModel : INotifyPropertyChanged
{
    private string? _customWriteValue;

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
            if (field == value)
            {
                return;
            }

            field = value;
            ResetAddressValues();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBoolean));
        }
    } = "Bool";

    public bool IsBoolean => string.Equals(DataType?.Trim(), "Bool", StringComparison.OrdinalIgnoreCase);

    public string OffsetAddress
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            ResetAddressValues();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayAddress));
        }
    } = "DB{DB}.DBX0.0";

    public string ActualAddress
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayAddress));
        }
    } = string.Empty;

    public string DisplayAddress => string.IsNullOrEmpty(ActualAddress) ? OffsetAddress : ActualAddress;

    public bool HasCurrentValue
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged();
        }
    }

    public string CurrentValue
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged();
            if (!HasCustomWriteValue)
            {
                OnPropertyChanged(nameof(WriteValue));
            }
        }
    } = string.Empty;

    public string WriteValue
    {
        get
        {
            if (_customWriteValue != null)
            {
                return _customWriteValue;
            }

            return IsBoolean && bool.TryParse(CurrentValue, out var current)
                ? (!current).ToString()
                : CurrentValue;
        }
        set
        {
            var writeValue = value ?? string.Empty;
            if (string.Equals(writeValue, WriteValue, StringComparison.Ordinal))
            {
                return;
            }

            _customWriteValue = writeValue;
            OnPropertyChanged();
        }
    }

    public bool HasCustomWriteValue => _customWriteValue != null;

    public void ResetWriteValue()
    {
        if (!HasCustomWriteValue)
        {
            return;
        }

        _customWriteValue = null;
        OnPropertyChanged(nameof(WriteValue));
    }

    private void ResetAddressValues()
    {
        HasCurrentValue = false;
        ActualAddress = string.Empty;
        CurrentValue = string.Empty;
        ResetWriteValue();
    }

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
            if (field == value)
            {
                return;
            }

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
            Remark = item.Remark
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