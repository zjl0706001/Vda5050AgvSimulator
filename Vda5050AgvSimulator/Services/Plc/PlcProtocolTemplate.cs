#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Vda5050AgvSimulator.Models.Plc;

namespace Vda5050AgvSimulator.Services.Plc;

/// <summary>全模拟器共用的工站协议。信号只定义块内偏移，读写时由当前 PLC 和 DB 确定目标。</summary>
public sealed class PlcProtocolTemplate
{
    private static readonly Regex TypedAddressPrefix = new(
        @"(^|\.)DB[XBWD](?=[0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly Signal[] _signals;
    private readonly Dictionary<(string, int, int?), Signal> _byAddress;

    public static PlcProtocolTemplate Basic { get; }
    public IReadOnlyList<Signal> AgvToPlc { get; }
    public IReadOnlyList<Signal> PlcToAgv { get; }
    public int RequiredLength { get; }
    public int Count => _signals.Length;
    public IReadOnlyList<Signal> Signals { get; }

    static PlcProtocolTemplate()
    {
        var agvToPlc = new[]
        {
            Signal.Bool("LifeBit", 0, 0, "系统健康"),
            Signal.Bool("Offline", 0, 1, "离线模式"),
            Signal.Bool("Online", 0, 2, "在线模式"),
            Signal.Bool("Alarm", 0, 3, "故障"),
            Signal.Bool("EmergencyStop", 0, 4, "急停"),
            Signal.Bool("EnterRequest", 0, 5, "请求进入"),
            Signal.Bool("Entering", 0, 6, "正在进入"),
            Signal.Bool("ReachedPosition", 0, 7, "小车到位"),
            Signal.Bool("Leaving", 1, 0, "正在离开"),
            Signal.Bool("BindComplete", 1, 1, "绑定完成"),
            Signal.Bool("UnbindComplete", 1, 2, "解绑完成"),
            Signal.Bool("WritePackID1Done", 1, 3, "写入PackID1完成"),
            Signal.Bool("WriteGuobiaoPackID1Done", 1, 4, "写入PackID1_12位码完成"),
            Signal.Bool("WritePackID2Done", 1, 5, "写入PackID2完成"),
            Signal.Bool("WriteGuobiaoPackID2Done", 1, 6, "写入PackID2_12位码完成"),
            Signal.Byte("AGVID", 2, "AGV小车编号"),
            Signal.Byte("ErrorCode", 3, "报错代码"),
            Signal.Int16("PACKLayers", 4, "pack层数：1：第一层；2：第二层；3：第三层"),
            Signal.Int16("AGVModuleType", 6, "AGV到达公共区域，到位后，请求进站时，发送CheckAGVModuleType信号和AGVModuleType（当前模组类型）进行校验"),
            Signal.S7String("PackID1", 8, 40, "Pack码1"),
            Signal.S7String("ShippingCode1", 50, 40, "传输码1"),
            Signal.S7String("PackID2", 92, 40, "Pack码2"),
            Signal.S7String("ShippingCode2", 134, 40, "传输码2"),
            Signal.Bool("UPComplete", 176, 0, "上升完成"),
            Signal.Bool("DownComplete", 176, 1, "下降完成"),
            Signal.Int16("Block1LimitpieceStatus", 178, "Block1限位片状态"),
            Signal.Int16("Block2LimitpieceStatus", 180, "Block2限位片状态"),
            Signal.Byte("AGVBodyAmount", 177, "AGV载体数量，例如涂胶站需要等待入箱模组小车数量，备用，自行修改数据类型")
        };

        var plcToAgv = new[]
        {
            Signal.Bool("LifeBit", 200, 0, "系统健康"),
            Signal.Bool("Offline", 200, 1, "离线模式（非自动状态）"),
            Signal.Bool("Online", 200, 2, "在线模式（自动状态）"),
            Signal.Bool("Alarm", 200, 3, "故障"),
            Signal.Bool("EmergencyStopOK", 200, 4, "急停"),
            Signal.Bool("EnterInterlock", 200, 5, "进入互锁"),
            Signal.Bool("EnterAllowed", 200, 6, "允许进入"),
            Signal.Bool("LeaveInterlock", 200, 7, "离开互锁"),
            Signal.Bool("LeaveRequest", 201, 0, "请求离开"),
            Signal.Bool("Bypass", 201, 1, "屏蔽"),
            Signal.Bool("RequestWritePackID1", 201, 2, "请求写入Pack码1"),
            Signal.Bool("RequestWritePackID1", 201, 3, "请求写入传输码1"),
            Signal.Bool("RequestWritePackID1", 201, 4, "请求写入Pack码2"),
            Signal.Bool("RequestWritePackID1", 201, 5, "请求写入传输码2"),
            Signal.Int16("Route", 202, "AGV小车执行的路径"),
            Signal.Int16("PACKLayers", 206, "箱体上线工站扫码箱体后，上传MES，记录层数：1：第一层；2：第二层；3：第三层"),
            Signal.Int16("CheckAGVModuleType", 208, "入箱设备判断箱体类型，输出模组小车类型：1：模组小车类型1；2：模组小车类型2"),
            Signal.S7String("PackID1", 210, 40, "Pack码1：24位码"),
            Signal.S7String("ShippingCode1", 252, 40, "传输码1：12位码（总成码）或12位码（PN）"),
            Signal.S7String("PackID2", 294, 40, "Pack码1：24位码"),
            Signal.S7String("ShippingCode2", 336, 40, "传输码2：12位码（总成码）或12位码（PN）"),
            Signal.Bool("RequestUP", 378, 0, "请求上升"),
            Signal.Bool("RequestDOWN", 378, 1, "请求下降"),
            Signal.Bool("Clampingposition", 378, 2, "AGV夹紧位置"),
            Signal.Bool("HeatStationRequestEmptyPallet", 378, 3, "加热请求回流空车"),
            Signal.Bool("HeatStationErr", 378, 4, "加热库故障"),
            Signal.Int16("Block1LimitpieceStatus", 380, "Block1限位片状态"),
            Signal.Int16("Block2LimitpieceStatus", 382, "Block2限位片状态"),
            Signal.Int16("HeatCycleAgvAmount", 384, "加热回流小车数量")
        };

        Basic = new PlcProtocolTemplate(agvToPlc, plcToAgv);
    }

    private PlcProtocolTemplate(Signal[] agvToPlc, Signal[] plcToAgv)
    {
        AgvToPlc = Array.AsReadOnly(agvToPlc);
        PlcToAgv = Array.AsReadOnly(plcToAgv);
        _signals = agvToPlc.Concat(plcToAgv).ToArray();
        Signals = Array.AsReadOnly(_signals);
        _byAddress = _signals.ToDictionary(
            signal => (signal.Name.ToUpperInvariant(), signal.ByteOffset, signal.Bit));
        foreach (var signal in _signals)
        {
            RequiredLength = Math.Max(RequiredLength, signal.EndExclusive);
        }
    }

    public sealed class Signal
    {
        public string Name { get; }
        public string DataType { get; }
        public int ByteOffset { get; }
        public int? Bit { get; }
        public int EndExclusive { get; }
        public string RelativeAddress { get; }
        public string Remark { get; }

        private Signal(string name, string dataType, string area, int byteOffset, int width, string remark, int? bit = null)
        {
            if (byteOffset < 0) throw new ArgumentOutOfRangeException(nameof(byteOffset));
            if (bit.HasValue && (bit.Value < 0 || bit.Value > 7)) throw new ArgumentOutOfRangeException(nameof(bit));

            Name = name;
            DataType = dataType;
            ByteOffset = byteOffset;
            Bit = bit;
            Remark = remark;
            EndExclusive = checked(byteOffset + width);

            var offset = byteOffset.ToString(CultureInfo.InvariantCulture);
            if (bit.HasValue)
            {
                offset += "." + bit.Value.ToString(CultureInfo.InvariantCulture);
            }
            RelativeAddress = area + offset;
        }

        public static Signal Bool(string name, int offset, int bit, string remark) =>
            new Signal(name, "Bool", "DBX", offset, 1, remark, bit);

        public static Signal Byte(string name, int offset, string remark) =>
            new Signal(name, "Byte", "DBB", offset, 1, remark);

        public static Signal Int16(string name, int offset, string remark) =>
            new Signal(name, "Int", "DBW", offset, 2, remark);

        public static Signal S7String(string name, int offset, int maxChars, string remark)
        {
            if (maxChars < 1 || maxChars > 254) throw new ArgumentOutOfRangeException(nameof(maxChars));
            // S7 字符串在内容前占用两个长度字节。
            var dataType = "String[" + maxChars.ToString(CultureInfo.InvariantCulture) + "]";
            return new Signal(name, dataType, "DBX", offset, maxChars + 2, remark);
        }
    }

    /// <summary>每次只为正在使用的目标产生工作行。工作行的值、启停状态及写入输入属于该目标。</summary>
    public List<PlcDbBlockProtocolItemModel> GetEffectiveItems(PlcDbBlockModel block,
        IEnumerable<PlcDbBlockProtocolItemModel> localItems = null)
    {
        var stored = GetLocalItems(localItems ?? block.ProtocolItems).ToList();
        var keys = new HashSet<(string, int, int?)>();
        var ids = new HashSet<int>(stored.Where(item => item.ProtocolItemId > 0).Select(item => item.ProtocolItemId));
        var nextId = Math.Max(_signals.Length, ids.DefaultIfEmpty(0).Max());
        var result = new List<PlcDbBlockProtocolItemModel>(_signals.Length + stored.Count);
        foreach (var item in stored)
        {
            var copy = item.Clone();
            if (TryGetAddress(item.OffsetAddress, block.DbNumber, out var actualDb, out var offset, out var bit) &&
                actualDb == block.DbNumber)
            {
                var key = ((item.SignalName ?? string.Empty).Trim().ToUpperInvariant(), offset, bit);
                keys.Add(key);
                if (copy.IsEnabled && _byAddress.TryGetValue(key, out var signal) &&
                    string.Equals(copy.DataType, signal.DataType, StringComparison.OrdinalIgnoreCase) &&
                    signal.EndExclusive > block.Length)
                {
                    copy.IsEnabled = false;
                }
            }
            result.Add(copy);
        }

        for (var i = 0; i < _signals.Length; i++)
        {
            var signal = _signals[i];
            if (keys.Contains((signal.Name.ToUpperInvariant(), signal.ByteOffset, signal.Bit))) continue;
            var id = i + 1;
            if (!ids.Add(id))
            {
                do
                {
                    nextId = nextId == int.MaxValue ? _signals.Length + 1 : nextId + 1;
                } while (!ids.Add(nextId));
                id = nextId;
            }
            result.Add(new PlcDbBlockProtocolItemModel
            {
                ProtocolItemId = id,
                IsEnabled = signal.EndExclusive <= block.Length,
                SignalName = signal.Name,
                DataType = signal.DataType,
                OffsetAddress = signal.RelativeAddress,
                Remark = signal.Remark
            });
        }
        return result.OrderBy(item => item.ProtocolItemId).ToList();
    }

    // 仅折叠确定等价的旧内置副本。自定义 ID、固定 DB 地址、备注和停用状态都保留。
    private bool IsDefaultCopy(PlcDbBlockProtocolItemModel item)
    {
        if (!item.IsEnabled || item.ProtocolItemId <= 0 || item.ProtocolItemId > _signals.Length) return false;
        var signal = _signals[item.ProtocolItemId - 1];
        return item.SignalName == signal.Name && item.DataType == signal.DataType && item.Remark == signal.Remark &&
               (item.OffsetAddress == signal.RelativeAddress || item.OffsetAddress == "DB{DB}." + signal.RelativeAddress);
    }

    public IEnumerable<PlcDbBlockProtocolItemModel> GetLocalItems(IEnumerable<PlcDbBlockProtocolItemModel> items)
    {
        return (items ?? Enumerable.Empty<PlcDbBlockProtocolItemModel>())
            .Where(item => item != null && !IsDefaultCopy(item));
    }

    public void CompactSavedCopy(PlcDbBlockModel block)
    {
        var items = block?.ProtocolItems;
        if (items == null) return;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] != null && IsDefaultCopy(items[i])) items.RemoveAt(i);
        }
    }

    private static bool TryGetAddress(string address, int dbNumber, out int actualDb, out int offset, out int? bit)
    {
        var text = (address ?? string.Empty).Trim().TrimStart('%').Replace(" ", string.Empty);
        var suffix = text.IndexOf(':');
        if (suffix >= 0) text = text.Substring(0, suffix);
        text = TypedAddressPrefix.Replace(text, "$1");
        return PlcDbBlockService.Instance.TryParseDbAddressOrTemplate(text, dbNumber, out actualDb, out offset, out bit);
    }

    internal static bool SameAddress(string left, string right, int dbNumber)
    {
        return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase) ||
               (TryGetAddress(left, dbNumber, out var leftDb, out var leftOffset, out var leftBit) &&
                TryGetAddress(right, dbNumber, out var rightDb, out var rightOffset, out var rightBit) &&
                leftDb == rightDb && leftOffset == rightOffset && leftBit == rightBit);
    }

    public void AppendDefinitions(Action<string, string, string, string> addItem)
    {
        foreach (var signal in _signals)
            addItem(signal.Name, signal.DataType, signal.RelativeAddress, signal.Remark);
    }
}
