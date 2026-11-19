#nullable disable

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Vda5050AgvSimulator.Models.Plc;

public class DbBlockBitRowModel : INotifyPropertyChanged
{
    public int DbNumber
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
            OnPropertyChanged(nameof(ByteAddress));
        }
    }

    public int ByteIndex
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
            OnPropertyChanged(nameof(ByteAddress));
        }
    }

    public byte Value
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
            OnPropertyChanged(nameof(DecimalValue));
            OnPropertyChanged(nameof(HexValue));
            OnPropertyChanged(nameof(BinaryValue));
            OnPropertyChanged(nameof(Bit0));
            OnPropertyChanged(nameof(Bit1));
            OnPropertyChanged(nameof(Bit2));
            OnPropertyChanged(nameof(Bit3));
            OnPropertyChanged(nameof(Bit4));
            OnPropertyChanged(nameof(Bit5));
            OnPropertyChanged(nameof(Bit6));
            OnPropertyChanged(nameof(Bit7));
        }
    }

    public string ByteAddress => $"DB{DbNumber}.DBB{ByteIndex}";

    public int DecimalValue => Value;

    public string HexValue => $"0x{Value:X2}";

    public string BinaryValue => Convert.ToString(Value, 2).PadLeft(8, '0');

    public bool Bit0 => GetBit(0);

    public bool Bit1 => GetBit(1);

    public bool Bit2 => GetBit(2);

    public bool Bit3 => GetBit(3);

    public bool Bit4 => GetBit(4);

    public bool Bit5 => GetBit(5);

    public bool Bit6 => GetBit(6);

    public bool Bit7 => GetBit(7);

    private bool GetBit(int bitIndex)
    {
        return (Value & (1 << bitIndex)) != 0;
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}