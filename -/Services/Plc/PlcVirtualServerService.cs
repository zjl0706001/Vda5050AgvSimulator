#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HslCommunication.Profinet.Siemens;
using Vda5050AgvSimulator.Models.Plc;

namespace Vda5050AgvSimulator.Services.Plc;

/// <summary>
/// PLC虚拟服务端服务。
/// 当前版本使用：
/// 1. HslCommunication SiemensS7Server 在本机内部端口启动，例如 127.0.0.1:103。
/// 2. Windows portproxy 把外部 PLC IP:代理端口 转发到内部端口，例如 172.16.4.2:102 -> 127.0.0.1:103。
/// </summary>
public class PlcVirtualServerService
{
    public static PlcVirtualServerService Instance { get; } = new();

    private readonly object _lockObj = new();

    private readonly ConcurrentDictionary<int, SiemensS7Server> _serverMap = new();

    private readonly ConcurrentDictionary<int, PlcProxyInfo> _proxyMap = new();

    /// <summary>
    /// 用于避免自动写入循环里重复打印相同跳过日志。
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _lastTraceMap = new();

    /// <summary>
    /// 规则延时触发状态缓存。
    /// Key = PLC + DB块 + 规则。
    /// 当规则全部启用触发条件连续满足到指定毫秒后，才执行写入动作。
    /// </summary>
    private readonly ConcurrentDictionary<string, PlcDelayedTriggerState> _delayedTriggerStateMap = new();

    private CancellationTokenSource _cancellationTokenSource;

    private const string ProxyConnectAddress = "127.0.0.1";

    public bool IsRunning => _serverMap.Count > 0;

    public int RunningCount => _serverMap.Count;

    private PlcVirtualServerService()
    {
    }

    /// <summary>
    /// 兼容旧逻辑：启动所有已启用PLC。
    /// </summary>
    public void Start()
    {
        Log("调用 Start：准备启动所有已启用PLC。");

        StartEnabledPlcsFromStore();
    }

    /// <summary>
    /// 兼容旧逻辑：停止全部PLC。
    /// </summary>
    public void Stop()
    {
        Log("调用 Stop：准备停止全部PLC。");

        StopAllPlcs();
    }

    /// <summary>
    /// 兼容旧逻辑：重新加载所有已启用PLC。
    /// </summary>
    public void Reload()
    {
        Log("调用 Reload：准备重新加载所有已启用PLC。");

        ReloadEnabledPlcsFromStore();
    }

    /// <summary>
    /// 判断指定PLC是否正在运行。
    /// </summary>
    public bool IsPlcRunning(int plcId)
    {
        return _serverMap.ContainsKey(plcId);
    }

    /// <summary>
    /// 读取当前虚拟PLC内部DB块字节。
    /// 用于DB块位值查看窗口热更新。
    /// </summary>
    public bool TryReadDbBytes(
        int plcId,
        int dbNumber,
        int length,
        out byte[] bytes,
        out string message)
    {
        bytes = [];

        message = string.Empty;

        if (plcId <= 0)
        {
            message = "PLC ID不正确。";
            return false;
        }

        if (dbNumber <= 0)
        {
            message = "DB块号不正确。";
            return false;
        }

        if (length <= 0)
        {
            message = "读取长度必须大于0。";
            return false;
        }

        if (length > ushort.MaxValue)
        {
            length = ushort.MaxValue;
        }

        if (!_serverMap.TryGetValue(plcId, out var server))
        {
            message = "PLC模拟服务未运行，PlcId=" + plcId;
            return false;
        }

        try
        {
            var address = "DB" + dbNumber + ".0";

            var read = server.Read(address, (ushort)length);

            if (!read.IsSuccess)
            {
                message = read.Message;
                return false;
            }

            bytes = read.Content ?? [];

            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 双击DB块位值查看窗口中的bit位时，取反当前运行中的虚拟PLC Bool地址。
    /// 地址格式：DB{dbNumber}.{byteIndex}.{bitIndex}。
    /// </summary>
    public bool TryToggleDbBit(
        int plcId,
        int dbNumber,
        int byteIndex,
        int bitIndex,
        out bool newValue,
        out string message)
    {
        newValue = false;
        message = string.Empty;

        if (plcId <= 0)
        {
            message = "PLC ID不正确。";
            return false;
        }

        if (dbNumber <= 0)
        {
            message = "DB块号不正确。";
            return false;
        }

        if (byteIndex < 0)
        {
            message = "字节地址不能小于0。";
            return false;
        }

        if (bitIndex < 0 || bitIndex > 7)
        {
            message = "bit位必须在0-7之间。";
            return false;
        }

        if (!_serverMap.TryGetValue(plcId, out var server))
        {
            message = "PLC模拟服务未运行，PlcId=" + plcId;
            return false;
        }

        var address = $"DB{dbNumber}.{byteIndex}.{bitIndex}";

        try
        {
            var before = server.ReadBool(address);

            if (!before.IsSuccess)
            {
                message = $"读取当前bit位失败，地址={address}，Message={before.Message}";
                return false;
            }

            newValue = !before.Content;

            var write = server.Write(address, newValue);

            if (!write.IsSuccess)
            {
                message = $"写入bit位失败，地址={address}，Value={newValue}，Message={write.Message}";
                return false;
            }

            var after = server.ReadBool(address);

            if (!after.IsSuccess)
            {
                message = $"写入成功但回读失败，地址={address}，Value={newValue}，Message={after.Message}";
                Log($"[PLC手动取反] 地址={address} Before={before.Content} WriteValue={newValue} After=ReadFail:{after.Message}");
                return true;
            }

            newValue = after.Content;

            message = $"写入成功，地址={address}，Value={newValue}";

            Log($"[PLC手动取反] 地址={address} Before={before.Content} After={after.Content}");

            return true;
        }
        catch (Exception ex)
        {
            message = $"写入bit位异常，地址={address}，Message={ex.Message}";
            LogException($"[PLC手动取反异常] 地址={address}", ex);
            return false;
        }
    }

    /// <summary>
    /// 按DB块复位配置复位当前运行中的虚拟PLC内存。
    /// 1. 如果DB块配置了启用的复位项：逐项把Bool地址写成配置的复位值。
    /// 2. 如果没有启用的复位项：默认把整个DB块字节清零。
    /// </summary>
    public bool TryResetDbBlockByConfig(
        int plcId,
        PlcDbBlockModel dbBlock,
        out string message)
    {
        message = string.Empty;

        if (plcId <= 0)
        {
            message = "PLC ID不正确。";
            return false;
        }

        if (dbBlock == null)
        {
            message = "DB块不能为空。";
            return false;
        }

        if (dbBlock.DbNumber <= 0)
        {
            message = "DB块号不正确。";
            return false;
        }

        if (!_serverMap.TryGetValue(plcId, out var server))
        {
            message = "PLC模拟服务未运行，PlcId=" + plcId;
            return false;
        }

        var resetItems = dbBlock.ResetItems?
            .Where(x => x != null)
            .Where(x => x.IsEnabled)
            .Where(x => !string.IsNullOrWhiteSpace(x.ResetAddress))
            .OrderBy(x => x.ResetItemId)
            .ToList() ?? [];

        if (resetItems.Count == 0)
        {
            return TryClearDbBlock(server, dbBlock, out message);
        }

        var successCount = 0;
        var failMessages = new List<string>();

        foreach (var resetItem in resetItems)
        {
            if (!TryResetDbBlockItem(server, dbBlock, resetItem, out var itemMessage))
            {
                failMessages.Add(itemMessage);
                continue;
            }

            successCount++;
        }

        if (failMessages.Count > 0)
        {
            message = $"复位完成 {successCount}/{resetItems.Count} 项，失败：" + string.Join("；", failMessages);
            return false;
        }

        message = $"复位完成，共写入 {successCount} 个复位项。";
        return true;
    }

    /// <summary>
    /// 清零整个DB块。
    /// </summary>
    private bool TryClearDbBlock(SiemensS7Server server, PlcDbBlockModel dbBlock, out string message)
    {
        message = string.Empty;

        if (server == null || dbBlock == null)
        {
            message = "Server或DB块为空。";
            return false;
        }

        var length = dbBlock.Length <= 0 ? 256 : dbBlock.Length;

        if (length > ushort.MaxValue)
        {
            length = ushort.MaxValue;
        }

        try
        {
            var address = $"DB{dbBlock.DbNumber}.0";

            var write = server.Write(address, new byte[length]);

            if (!write.IsSuccess)
            {
                message = $"清零DB{dbBlock.DbNumber}失败：{write.Message}";
                return false;
            }

            message = $"DB{dbBlock.DbNumber} 已清零，长度={length}。";
            Log($"[PLC复位成功] DB{dbBlock.DbNumber} 整块清零 Length={length}");
            return true;
        }
        catch (Exception ex)
        {
            message = $"清零DB{dbBlock.DbNumber}异常：{ex.Message}";
            LogException($"[PLC复位异常] DB{dbBlock.DbNumber} 整块清零", ex);
            return false;
        }
    }

    /// <summary>
    /// 执行单个Bool复位项。
    /// </summary>
    private bool TryResetDbBlockItem(
        SiemensS7Server server,
        PlcDbBlockModel dbBlock,
        PlcDbBlockResetItemModel resetItem,
        out string message)
    {
        message = string.Empty;

        if (server == null || dbBlock == null || resetItem == null)
        {
            message = "复位项为空。";
            return false;
        }

        if (!PlcDbBlockService.Instance.IsBoolDbAddressOrTemplate(resetItem.ResetAddress))
        {
            message = $"{resetItem.ResetName} 地址不是Bool地址：{resetItem.ResetAddress}";
            return false;
        }

        var resolvedAddress = PlcDbBlockService.Instance.ResolveDbAddressForDbBlock(
            resetItem.ResetAddress,
            dbBlock.DbNumber);

        try
        {
            bool? beforeValue = null;

            var before = server.ReadBool(resolvedAddress);

            if (before.IsSuccess)
            {
                beforeValue = before.Content;
            }

            var write = server.Write(resolvedAddress, resetItem.ResetValue);

            if (!write.IsSuccess)
            {
                message = $"{resetItem.ResetName} 写入失败，地址={resolvedAddress}，Message={write.Message}";
                return false;
            }

            var after = server.ReadBool(resolvedAddress);

            Log(
                $"[PLC复位成功] DB{dbBlock.DbNumber} 复位项={resetItem.ResetName} 地址模板={resetItem.ResetAddress} 实际地址={resolvedAddress} Before={FormatNullableBool(beforeValue)} ResetValue={resetItem.ResetValue} After={(after.IsSuccess ? after.Content.ToString() : $"ReadFail:{after.Message}")}");

            return true;
        }
        catch (Exception ex)
        {
            message = $"{resetItem.ResetName} 写入异常，地址={resolvedAddress}，Message={ex.Message}";
            LogException(
                $"[PLC复位异常] DB{dbBlock.DbNumber} 复位项={resetItem.ResetName} 地址模板={resetItem.ResetAddress} 实际地址={resolvedAddress}",
                ex);
            return false;
        }
    }


    /// <summary>
    /// 读取协议看板配置项对应的当前值。
    /// 支持地址格式：DB{DB}.DBX0.0、DB*.DBX0.0、DB1500.DBX0.0、DB1500.0.0、DBX0.0、DBW4、0.0。
    /// </summary>
    public bool TryReadProtocolItemValue(
        int plcId,
        int dbNumber,
        PlcDbBlockProtocolItemModel protocolItem,
        out string actualAddress,
        out string valueText,
        out string message)
    {
        actualAddress = string.Empty;
        valueText = string.Empty;
        message = string.Empty;

        if (plcId <= 0)
        {
            message = "PLC ID不正确。";
            return false;
        }

        if (dbNumber <= 0)
        {
            message = "DB块号不正确。";
            return false;
        }

        if (protocolItem == null)
        {
            message = "协议项为空。";
            return false;
        }

        if (!_serverMap.TryGetValue(plcId, out var server))
        {
            message = "PLC模拟服务未运行，PlcId=" + plcId;
            return false;
        }

        var dataType = NormalizeProtocolDataType(protocolItem.DataType);

        if (!TryResolveProtocolAddress(
                protocolItem.OffsetAddress,
                dbNumber,
                dataType,
                out var address,
                out message))
        {
            actualAddress = address?.DisplayAddress ?? string.Empty;
            return false;
        }

        actualAddress = address.DisplayAddress;

        try
        {
            switch (dataType)
            {
                case "BOOL":
                {
                    if (address.BitOffset == null)
                    {
                        message = "Bool地址必须包含bit位，例如 DB{DB}.DBX0.0 或 0.0。";
                        return false;
                    }

                    var read = server.ReadBool(address.BoolAddress);

                    if (!read.IsSuccess)
                    {
                        message = read.Message;
                        return false;
                    }

                    valueText = read.Content ? "True" : "False";
                    return true;
                }

                case "BYTE":
                {
                    if (!TryReadRawBytes(server, address.ByteAddress, 1, out var bytes, out message))
                    {
                        return false;
                    }

                    valueText = bytes[0].ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                case "WORD":
                {
                    if (!TryReadRawBytes(server, address.ByteAddress, 2, out var bytes, out message))
                    {
                        return false;
                    }

                    valueText = ReadUInt16BigEndian(bytes, 0).ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                case "INT":
                {
                    if (!TryReadRawBytes(server, address.ByteAddress, 2, out var bytes, out message))
                    {
                        return false;
                    }

                    valueText = ReadInt16BigEndian(bytes, 0).ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                case "DINT":
                {
                    if (!TryReadRawBytes(server, address.ByteAddress, 4, out var bytes, out message))
                    {
                        return false;
                    }

                    valueText = ReadInt32BigEndian(bytes, 0).ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                case "REAL":
                {
                    if (!TryReadRawBytes(server, address.ByteAddress, 4, out var bytes, out message))
                    {
                        return false;
                    }

                    valueText = ReadSingleBigEndian(bytes, 0).ToString("0.######", CultureInfo.InvariantCulture);
                    return true;
                }

                case "STRING":
                {
                    var stringLength = GetProtocolStringLength(protocolItem.DataType);

                    if (!TryReadRawBytes(server, address.ByteAddress, stringLength + 2, out var bytes, out message))
                    {
                        return false;
                    }

                    var maxLength = bytes.Length > 0 && bytes[0] > 0 ? Math.Min(bytes[0], stringLength) : stringLength;
                    var currentLength = bytes.Length > 1 ? Math.Min(bytes[1], maxLength) : 0;
                    currentLength = Math.Min(currentLength, Math.Max(0, bytes.Length - 2));

                    valueText = currentLength <= 0
                        ? string.Empty
                        : Encoding.ASCII.GetString(bytes, 2, currentLength);
                    return true;
                }

                case "BYTEARRAY":
                {
                    var byteLength = GetProtocolByteArrayLength(protocolItem.DataType);

                    if (!TryReadRawBytes(server, address.ByteAddress, byteLength, out var bytes, out message))
                    {
                        return false;
                    }

                    valueText = BitConverter.ToString(bytes).Replace("-", " ");
                    return true;
                }

                default:
                    message = "暂不支持的数据类型：" + protocolItem.DataType;
                    return false;
            }
        }
        catch (Exception ex)
        {
            message = ex.Message;
            LogException($"[协议看板读取异常] PlcId={plcId} DB{dbNumber} Name={protocolItem.SignalName} Address={actualAddress}", ex);
            return false;
        }
    }

    /// <summary>
    /// 写入协议看板配置项对应的当前值。
    /// </summary>
    public bool TryWriteProtocolItemValue(
        int plcId,
        int dbNumber,
        PlcDbBlockProtocolItemModel protocolItem,
        string valueText,
        out string actualAddress,
        out string message)
    {
        actualAddress = string.Empty;
        message = string.Empty;

        if (plcId <= 0)
        {
            message = "PLC ID不正确。";
            return false;
        }

        if (dbNumber <= 0)
        {
            message = "DB块号不正确。";
            return false;
        }

        if (protocolItem == null)
        {
            message = "协议项为空。";
            return false;
        }

        if (!_serverMap.TryGetValue(plcId, out var server))
        {
            message = "PLC模拟服务未运行，PlcId=" + plcId;
            return false;
        }

        var dataType = NormalizeProtocolDataType(protocolItem.DataType);

        if (!TryResolveProtocolAddress(
                protocolItem.OffsetAddress,
                dbNumber,
                dataType,
                out var address,
                out message))
        {
            actualAddress = address?.DisplayAddress ?? string.Empty;
            return false;
        }

        actualAddress = address.DisplayAddress;

        try
        {
            switch (dataType)
            {
                case "BOOL":
                {
                    if (address.BitOffset == null)
                    {
                        message = "Bool地址必须包含bit位，例如 DB{DB}.DBX0.0 或 0.0。";
                        return false;
                    }

                    if (!TryParseProtocolBool(valueText, out var boolValue))
                    {
                        message = "Bool写入值不正确，请输入 true/false、1/0、是/否。";
                        return false;
                    }

                    var write = server.Write(address.BoolAddress, boolValue);

                    if (!write.IsSuccess)
                    {
                        message = write.Message;
                        return false;
                    }

                    Log($"[协议看板写入成功] PlcId={plcId} DB{dbNumber} Name={protocolItem.SignalName} Type=Bool Address={actualAddress} Value={boolValue}");
                    message = "写入成功。";
                    return true;
                }

                case "BYTE":
                {
                    if (!TryParseInteger(valueText, 0, byte.MaxValue, out var number, out message))
                    {
                        return false;
                    }

                    return TryWriteRawBytes(server, address.ByteAddress, [(byte)number], out message);
                }

                case "WORD":
                {
                    if (!TryParseInteger(valueText, 0, ushort.MaxValue, out var number, out message))
                    {
                        return false;
                    }

                    return TryWriteRawBytes(server, address.ByteAddress, WriteUInt16BigEndian((ushort)number), out message);
                }

                case "INT":
                {
                    if (!TryParseInteger(valueText, short.MinValue, short.MaxValue, out var number, out message))
                    {
                        return false;
                    }

                    return TryWriteRawBytes(server, address.ByteAddress, WriteInt16BigEndian((short)number), out message);
                }

                case "DINT":
                {
                    if (!int.TryParse(valueText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    {
                        message = "DInt写入值必须是32位整数。";
                        return false;
                    }

                    return TryWriteRawBytes(server, address.ByteAddress, WriteInt32BigEndian(number), out message);
                }

                case "REAL":
                {
                    if (!float.TryParse(valueText?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        message = "Real写入值必须是数字，例如 12.34。";
                        return false;
                    }

                    return TryWriteRawBytes(server, address.ByteAddress, WriteSingleBigEndian(number), out message);
                }

                case "STRING":
                {
                    var stringLength = GetProtocolStringLength(protocolItem.DataType);
                    var text = valueText ?? string.Empty;
                    var textBytes = Encoding.ASCII.GetBytes(text);

                    if (textBytes.Length > stringLength)
                    {
                        message = $"String长度超过限制：最大{stringLength}字节，当前{textBytes.Length}字节。";
                        return false;
                    }

                    var bytes = new byte[stringLength + 2];
                    bytes[0] = (byte)stringLength;
                    bytes[1] = (byte)textBytes.Length;
                    Array.Copy(textBytes, 0, bytes, 2, textBytes.Length);

                    return TryWriteRawBytes(server, address.ByteAddress, bytes, out message);
                }

                case "BYTEARRAY":
                {
                    if (!TryParseByteArray(valueText, out var bytes, out message))
                    {
                        return false;
                    }

                    return TryWriteRawBytes(server, address.ByteAddress, bytes, out message);
                }

                default:
                    message = "暂不支持的数据类型：" + protocolItem.DataType;
                    return false;
            }
        }
        catch (Exception ex)
        {
            message = ex.Message;
            LogException($"[协议看板写入异常] PlcId={plcId} DB{dbNumber} Name={protocolItem.SignalName} Address={actualAddress} Value={valueText}", ex);
            return false;
        }
    }

    private static string NormalizeProtocolDataType(string dataType)
    {
        var text = dataType?.Trim() ?? string.Empty;

        if (text.StartsWith("ByteArray", StringComparison.OrdinalIgnoreCase))
        {
            return "BYTEARRAY";
        }

        if (text.StartsWith("String", StringComparison.OrdinalIgnoreCase))
        {
            return "STRING";
        }

        return text.ToUpperInvariant();
    }

    private static string ReplaceIgnoreCase(string text, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(oldValue))
        {
            return text ?? string.Empty;
        }

        return Regex.Replace(
            text,
            Regex.Escape(oldValue),
            newValue ?? string.Empty,
            RegexOptions.IgnoreCase);
    }

    private static int ClampInt(int value, int minValue, int maxValue)
    {
        if (value < minValue)
        {
            return minValue;
        }

        if (value > maxValue)
        {
            return maxValue;
        }

        return value;
    }

    private static bool TryResolveProtocolAddress(
        string rawAddress,
        int defaultDbNumber,
        string dataType,
        out PlcProtocolResolvedAddress address,
        out string message)
    {
        address = new PlcProtocolResolvedAddress();
        message = string.Empty;

        if (defaultDbNumber <= 0)
        {
            message = "默认DB块号不正确。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            message = "地址不能为空。";
            return false;
        }

        var text = rawAddress.Trim().Replace(" ", string.Empty);
        text = ReplaceIgnoreCase(text, "DB{DB}", "DB" + defaultDbNumber);
        text = ReplaceIgnoreCase(text, "DB*", "DB" + defaultDbNumber);

        int dbNumber = defaultDbNumber;
        int byteOffset;
        int? bitOffset = null;

        var match = Regex.Match(
            text,
            @"^DB(?<db>\d+)\.(?:(?<area>DBX|DBB|DBW|DBD))?(?<byte>\d+)(?:\.(?<bit>[0-7]))?$",
            RegexOptions.IgnoreCase);

        if (match.Success)
        {
            dbNumber = int.Parse(match.Groups["db"].Value, CultureInfo.InvariantCulture);
            byteOffset = int.Parse(match.Groups["byte"].Value, CultureInfo.InvariantCulture);

            if (match.Groups["bit"].Success)
            {
                bitOffset = int.Parse(match.Groups["bit"].Value, CultureInfo.InvariantCulture);
            }
        }
        else
        {
            match = Regex.Match(
                text,
                @"^(?:(?<area>DBX|DBB|DBW|DBD))?(?<byte>\d+)(?:\.(?<bit>[0-7]))?$",
                RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                message = "地址格式不正确，示例：DB{DB}.DBX0.0、DB1500.DBW4、DBX0.0、0.0。";
                return false;
            }

            byteOffset = int.Parse(match.Groups["byte"].Value, CultureInfo.InvariantCulture);

            if (match.Groups["bit"].Success)
            {
                bitOffset = int.Parse(match.Groups["bit"].Value, CultureInfo.InvariantCulture);
            }
        }

        if (dbNumber <= 0 || byteOffset < 0)
        {
            message = "DB块号或字节偏移不正确。";
            return false;
        }

        if (dataType == "BOOL" && bitOffset == null)
        {
            message = "Bool地址必须包含bit位，例如 DB{DB}.DBX0.0 或 0.0。";
            return false;
        }

        if (dataType != "BOOL" && bitOffset != null)
        {
            message = "非Bool类型地址不应包含bit位，请使用 DBB/DBW/DBD 或字节偏移地址。";
            return false;
        }

        address.DbNumber = dbNumber;
        address.ByteOffset = byteOffset;
        address.BitOffset = bitOffset;
        address.ByteAddress = $"DB{dbNumber}.{byteOffset}";
        address.BoolAddress = bitOffset.HasValue ? $"DB{dbNumber}.{byteOffset}.{bitOffset.Value}" : string.Empty;
        address.DisplayAddress = bitOffset.HasValue ? address.BoolAddress : address.ByteAddress;

        return true;
    }

    private static bool TryReadRawBytes(
        SiemensS7Server server,
        string byteAddress,
        int length,
        out byte[] bytes,
        out string message)
    {
        bytes = [];
        message = string.Empty;

        if (server == null)
        {
            message = "Server为空。";
            return false;
        }

        if (length <= 0)
        {
            message = "读取长度必须大于0。";
            return false;
        }

        if (length > ushort.MaxValue)
        {
            length = ushort.MaxValue;
        }

        var read = server.Read(byteAddress, (ushort)length);

        if (!read.IsSuccess)
        {
            message = read.Message;
            return false;
        }

        bytes = read.Content ?? [];

        if (bytes.Length < length)
        {
            message = $"读取长度不足，期望={length}，实际={bytes.Length}。";
            return false;
        }

        return true;
    }

    private bool TryWriteRawBytes(
        SiemensS7Server server,
        string byteAddress,
        byte[] bytes,
        out string message)
    {
        message = string.Empty;

        if (server == null)
        {
            message = "Server为空。";
            return false;
        }

        bytes ??= [];

        var write = server.Write(byteAddress, bytes);

        if (!write.IsSuccess)
        {
            message = write.Message;
            return false;
        }

        Log($"[协议看板写入成功] Address={byteAddress} Bytes={BitConverter.ToString(bytes).Replace("-", " ")}");
        message = "写入成功。";
        return true;
    }

    private static bool TryParseProtocolBool(string valueText, out bool value)
    {
        value = false;
        var text = valueText?.Trim() ?? string.Empty;

        if (bool.TryParse(text, out value))
        {
            return true;
        }

        switch (text.ToLowerInvariant())
        {
            case "1":
            case "on":
            case "yes":
            case "y":
            case "是":
            case "真":
            case "开":
                value = true;
                return true;

            case "0":
            case "off":
            case "no":
            case "n":
            case "否":
            case "假":
            case "关":
                value = false;
                return true;

            default:
                return false;
        }
    }

    private static bool TryParseInteger(string valueText, long minValue, long maxValue, out long value, out string message)
    {
        value = 0;
        message = string.Empty;

        var text = valueText?.Trim() ?? string.Empty;

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            {
                message = "整数格式不正确。";
                return false;
            }
        }
        else if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            message = "整数格式不正确。";
            return false;
        }

        if (value < minValue || value > maxValue)
        {
            message = $"数值超出范围：{minValue} - {maxValue}。";
            return false;
        }

        return true;
    }

    private static bool TryParseByteArray(string valueText, out byte[] bytes, out string message)
    {
        bytes = [];
        message = string.Empty;

        var text = valueText?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            message = "ByteArray写入值不能为空。示例：01 02 FF 或 1,2,255。";
            return false;
        }

        var parts = text
            .Replace("0x", string.Empty).Replace("0X", string.Empty)
            .Split([' ', ',', ';', '-'], StringSplitOptions.RemoveEmptyEntries);

        var result = new List<byte>();

        foreach (var part in parts)
        {
            var token = part.Trim();

            if (token.Length <= 2 && byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexByte))
            {
                result.Add(hexByte);
                continue;
            }

            if (byte.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimalByte))
            {
                result.Add(decimalByte);
                continue;
            }

            message = "ByteArray格式不正确，示例：01 02 FF 或 1,2,255。";
            return false;
        }

        bytes = result.ToArray();
        return bytes.Length > 0;
    }

    private static int GetProtocolStringLength(string dataType)
    {
        var length = GetLengthFromDataType(dataType, 40);

        return ClampInt(length, 1, 254);
    }

    private static int GetProtocolByteArrayLength(string dataType)
    {
        var length = GetLengthFromDataType(dataType, 16);

        return ClampInt(length, 1, 512);
    }

    private static int GetLengthFromDataType(string dataType, int defaultLength)
    {
        var text = dataType?.Trim() ?? string.Empty;

        var match = Regex.Match(text, @"\[(?<len>\d+)\]|:(?<len2>\d+)$");

        if (match.Success)
        {
            var lengthText = match.Groups["len"].Success
                ? match.Groups["len"].Value
                : match.Groups["len2"].Value;

            if (int.TryParse(lengthText, out var length) && length > 0)
            {
                return length;
            }
        }

        return defaultLength;
    }

    private static ushort ReadUInt16BigEndian(byte[] bytes, int index)
    {
        return (ushort)((bytes[index] << 8) | bytes[index + 1]);
    }

    private static short ReadInt16BigEndian(byte[] bytes, int index)
    {
        return unchecked((short)ReadUInt16BigEndian(bytes, index));
    }

    private static int ReadInt32BigEndian(byte[] bytes, int index)
    {
        return (bytes[index] << 24) |
               (bytes[index + 1] << 16) |
               (bytes[index + 2] << 8) |
               bytes[index + 3];
    }

    private static float ReadSingleBigEndian(byte[] bytes, int index)
    {
        var buffer = new byte[4];
        Array.Copy(bytes, index, buffer, 0, 4);

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(buffer);
        }

        return BitConverter.ToSingle(buffer, 0);
    }

    private static byte[] WriteUInt16BigEndian(ushort value)
    {
        return [
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF)
        ];
    }

    private static byte[] WriteInt16BigEndian(short value)
    {
        return WriteUInt16BigEndian(unchecked((ushort)value));
    }

    private static byte[] WriteInt32BigEndian(int value)
    {
        return [
            (byte)((value >> 24) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF)
        ];
    }

    private static byte[] WriteSingleBigEndian(float value)
    {
        var bytes = BitConverter.GetBytes(value);

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    private class PlcProtocolResolvedAddress
    {
        public int DbNumber { get; set; }

        public int ByteOffset { get; set; }

        public int? BitOffset { get; set; }

        public string ByteAddress { get; set; }

        public string BoolAddress { get; set; }

        public string DisplayAddress { get; set; }
    }

    /// <summary>
    /// 获取代理端口。
    /// ProxyPort 是外部客户端连接端口，例如 102。
    /// Port 是内部 SiemensS7Server 监听端口，例如 103 / 1102。
    /// </summary>
    private static int GetProxyPort(PlcConfigModel plc)
    {
        if (plc == null)
        {
            return 102;
        }

        return plc.ProxyPort > 0 ? plc.ProxyPort : 102;
    }

    /// <summary>
    /// 从配置中启动所有启用的PLC。
    /// </summary>
    public int StartEnabledPlcsFromStore()
    {
        Log("开始从配置启动已启用PLC。");

        PlcStoreService.Instance.EnsureLoadedFromConfig();

        var plcs = PlcStoreService.Instance.GetSnapshotList();

        var enabledPlcs = plcs
            .Where(x => x is { IsEnabled: true })
            .ToList();

        Log($"配置中启用PLC数量：{enabledPlcs.Count}");

        var successCount = enabledPlcs.Count(StartOrReloadPlc);

        RefreshAutoWriteLoopState();

        Log($"从配置启动已启用PLC完成，成功数量：{successCount}，当前运行数量：{RunningCount}");

        return successCount;
    }

    /// <summary>
    /// 从配置中重新加载所有启用的PLC。
    /// </summary>
    public int ReloadEnabledPlcsFromStore()
    {
        Log("开始从配置重新加载已启用PLC。");

        PlcStoreService.Instance.EnsureLoadedFromConfig();

        var plcs = PlcStoreService.Instance.GetSnapshotList();

        var currentPlcIds = plcs
            .Where(x => x != null)
            .Select(x => x.PlcId)
            .ToHashSet();

        foreach (var runningPlcId in _serverMap.Keys.ToList())
        {
            if (!currentPlcIds.Contains(runningPlcId))
            {
                Log($"运行中的PLC已不存在于配置，准备停止：PlcId={runningPlcId}");

                StopPlc(runningPlcId);
            }
        }

        var successCount = 0;

        foreach (var plc in plcs.Where(x => x != null))
        {
            if (plc.IsEnabled)
            {
                if (StartOrReloadPlc(plc))
                {
                    successCount++;
                }
            }
            else
            {
                StopPlc(plc.PlcId);
            }
        }

        RefreshAutoWriteLoopState();

        Log($"重新加载已启用PLC完成，成功数量：{successCount}，当前运行数量：{RunningCount}");

        return successCount;
    }

    /// <summary>
    /// 启动或重启单台PLC。
    /// 用于PLC启用勾选、保存后重载。
    /// </summary>
    public bool StartOrReloadPlc(PlcConfigModel plc)
    {
        if (plc == null)
        {
            Log("StartOrReloadPlc 被调用，但 plc 为 null。");
            return false;
        }

        Log(
            $"StartOrReloadPlc：PlcId={plc.PlcId} Name={plc.PlcName} Enabled={plc.IsEnabled} ExternalIP={plc.IpAddress} ProxyPort={GetProxyPort(plc)} InternalPort={plc.Port} Running={IsPlcRunning(plc.PlcId)}");

        if (!plc.IsEnabled)
        {
            Log($"StartOrReloadPlc：{plc.PlcName} 未启用，执行停止。");

            StopPlc(plc.PlcId);

            return false;
        }

        if (IsPlcRunning(plc.PlcId))
        {
            Log($"StartOrReloadPlc：{plc.PlcName} 已在运行，准备先停止再重新启动。注意：这会重新初始化DB内存。");
        }

        StopPlc(plc.PlcId);

        var result = StartServer(plc);

        RefreshAutoWriteLoopState();

        Log($"StartOrReloadPlc 完成：{plc.PlcName} Result={result} 当前运行数量={RunningCount}");

        return result;
    }

    /// <summary>
    /// 停止单台PLC。
    /// </summary>
    public void StopPlc(int plcId)
    {
        DeleteProxyByPlcId(plcId);

        if (!_serverMap.TryRemove(plcId, out var server))
        {
            TraceOnce($"StopPlc_NotRunning_{plcId}", $"StopPlc：PlcId={plcId} 当前未运行，跳过停止。");

            RefreshAutoWriteLoopState();

            return;
        }

        ClearTriggerDelayStatesByPlcId(plcId);

        try
        {
            server.ServerClose();

            Log($"PLC虚拟服务已停止：PlcId={plcId}");
        }
        catch (Exception ex)
        {
            LogException($"停止PLC异常：PlcId={plcId}", ex);
        }

        RefreshAutoWriteLoopState();
    }

    /// <summary>
    /// 清理指定PLC的延时触发状态。
    /// PLC停止或重载时必须清理，避免上一次运行的计时影响下一次运行。
    /// </summary>
    private void ClearTriggerDelayStatesByPlcId(int plcId)
    {
        if (plcId <= 0)
        {
            return;
        }

        var prefix = plcId + "_";

        foreach (var key in _delayedTriggerStateMap.Keys.ToList())
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            _delayedTriggerStateMap.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// 停止全部PLC。
    /// </summary>
    public void StopAllPlcs()
    {
        Log($"准备停止全部PLC，当前运行数量：{RunningCount}");

        foreach (var plcId in _serverMap.Keys.ToList())
        {
            StopPlc(plcId);
        }

        foreach (var plcId in _proxyMap.Keys.ToList())
        {
            DeleteProxyByPlcId(plcId);
        }

        RefreshAutoWriteLoopState();

        Log($"停止全部PLC完成，当前运行数量：{RunningCount}");
    }

    /// <summary>
    /// 启动单台PLC虚拟服务。
    /// 当前 HSL SiemensS7Server 不支持指定IP绑定，
    /// 所以内部使用不同端口启动，再由 Windows portproxy 映射到指定 PLC IP:代理端口。
    /// </summary>
    private bool StartServer(PlcConfigModel plc)
    {
        if (plc == null)
        {
            return false;
        }

        SiemensS7Server server = null;

        var proxyPort = GetProxyPort(plc);

        try
        {
            if (!WindowsPortProxyService.Instance.IsAdministrator())
            {
                Log($"PLC虚拟服务启动失败：{plc.PlcName} 创建端口代理需要管理员权限，请以管理员身份运行程序。");
                return false;
            }

            if (string.IsNullOrWhiteSpace(plc.IpAddress))
            {
                Log($"PLC虚拟服务启动失败：{plc.PlcName} 未配置PLC IP。");
                return false;
            }

            if (!IPAddress.TryParse(plc.IpAddress.Trim(), out var listenIp))
            {
                Log($"PLC虚拟服务启动失败：{plc.PlcName} IP格式不正确：{plc.IpAddress}");
                return false;
            }

            if (proxyPort <= 0 || proxyPort > 65535)
            {
                Log($"PLC虚拟服务启动失败：{plc.PlcName} 代理端口不正确：{proxyPort}");
                return false;
            }

            if (plc.Port <= 0 || plc.Port > 65535)
            {
                Log($"PLC虚拟服务启动失败：{plc.PlcName} 内部端口不正确：{plc.Port}");
                return false;
            }

            if (plc.Port == proxyPort)
            {
                Log($"PLC虚拟服务启动失败：{plc.PlcName} 内部端口不能和代理端口相同。ProxyPort={proxyPort} InternalPort={plc.Port}");
                return false;
            }

            Log($"准备启动内部PLC服务：PLC={plc.PlcName} Internal={ProxyConnectAddress}:{plc.Port}");

            server = new SiemensS7Server();

            server.ServerStart(plc.Port);

            Log($"内部PLC服务启动成功：PLC={plc.PlcName} Internal={ProxyConnectAddress}:{plc.Port}");

            InitServerMemory(server, plc);

            SelfTestServerMemory(server, plc);

            Log($"准备创建端口代理：{listenIp}:{proxyPort} -> {ProxyConnectAddress}:{plc.Port}");

            WindowsPortProxyService.Instance.EnsureFirewallRule(proxyPort);

            WindowsPortProxyService.Instance.AddOrUpdateProxy(
                listenIp.ToString(),
                proxyPort,
                ProxyConnectAddress,
                plc.Port);

            _proxyMap[plc.PlcId] = new PlcProxyInfo
            {
                ListenAddress = listenIp.ToString(),
                ListenPort = proxyPort,
                ConnectAddress = ProxyConnectAddress,
                ConnectPort = plc.Port
            };

            _serverMap[plc.PlcId] = server;

            Log($"PLC虚拟服务启动成功：{plc.PlcName} 外部={listenIp}:{proxyPort} 内部={ProxyConnectAddress}:{plc.Port}");

            return true;
        }
        catch (Exception ex)
        {
            LogException($"PLC虚拟服务启动失败：{plc.PlcName} IP={plc.IpAddress} ProxyPort={proxyPort} InternalPort={plc.Port}",
                ex);

            try
            {
                server?.ServerClose();
            }
            catch (Exception closeEx)
            {
                LogException($"启动失败后关闭Server异常：{plc.PlcName}", closeEx);
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(plc.IpAddress))
                {
                    WindowsPortProxyService.Instance.DeleteProxy(plc.IpAddress.Trim(), proxyPort, true);
                }
            }
            catch (Exception proxyEx)
            {
                LogException($"启动失败后删除端口代理异常：{plc.PlcName}", proxyEx);
            }

            return false;
        }
    }

    /// <summary>
    /// 根据PLC ID删除端口代理。
    /// 优先删除当前会话记录的代理；
    /// 如果当前会话没有记录，则从配置文件中查找PLC IP，清理历史遗留代理。
    /// </summary>
    private void DeleteProxyByPlcId(int plcId)
    {
        if (_proxyMap.TryRemove(plcId, out var proxy))
        {
            DeleteProxy(proxy.ListenAddress, proxy.ListenPort, proxy.ConnectAddress, proxy.ConnectPort, plcId);
            return;
        }

        try
        {
            PlcStoreService.Instance.EnsureLoadedFromConfig();

            var plc = PlcStoreService.Instance
                .GetSnapshotList()
                .FirstOrDefault(x => x != null && x.PlcId == plcId);

            if (plc == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(plc.IpAddress))
            {
                return;
            }

            DeleteProxy(
                plc.IpAddress.Trim(),
                GetProxyPort(plc),
                ProxyConnectAddress,
                plc.Port,
                plcId);
        }
        catch (Exception ex)
        {
            LogException($"根据配置删除端口代理异常：PlcId={plcId}", ex);
        }
    }

    private void DeleteProxy(
        string listenAddress,
        int listenPort,
        string connectAddress,
        int connectPort,
        int plcId)
    {
        try
        {
            Log($"准备删除端口代理：PlcId={plcId} {listenAddress}:{listenPort} -> {connectAddress}:{connectPort}");

            WindowsPortProxyService.Instance.DeleteProxy(listenAddress, listenPort, true);

            Log($"端口代理已删除：PlcId={plcId} {listenAddress}:{listenPort}");
        }
        catch (Exception ex)
        {
            LogException($"删除端口代理异常：PlcId={plcId}", ex);
        }
    }

    /// <summary>
    /// PLC虚拟服务内存自检。
    /// 用于确认DB块是否真的初始化成功。
    /// </summary>
    private void SelfTestServerMemory(SiemensS7Server server, PlcConfigModel plc)
    {
        if (server == null || plc == null)
        {
            return;
        }

        try
        {
            var testBlock = PlcDbBlockService.Instance
                .GetEffectiveDbBlocks(plc)
                .Where(x => x != null)
                .Where(x => x.IsEnabled)
                .Where(x => x.DbNumber > 0)
                .OrderBy(x => x.DbNumber)
                .FirstOrDefault();

            if (testBlock == null)
            {
                Log($"PLC虚拟服务DB自检跳过：{plc.PlcName} 没有启用DB块。");
                return;
            }

            var dbNumber = testBlock.DbNumber;

            var testBoolAddress = $"DB{dbNumber}.0.0";

            Log($"PLC虚拟服务DB自检开始：{plc.PlcName} Address={testBoolAddress}");

            var writeBool = server.Write(testBoolAddress, false);

            var readBool = server.ReadBool(testBoolAddress);

            if (!writeBool.IsSuccess || !readBool.IsSuccess)
            {
                Log(
                    $"PLC虚拟服务DB自检失败：{plc.PlcName} Address={testBoolAddress} WriteBool={writeBool.Message} ReadBool={readBool.Message}");

                return;
            }

            Log($"PLC虚拟服务DB自检成功：{plc.PlcName} Address={testBoolAddress} Value={readBool.Content}");
        }
        catch (Exception ex)
        {
            LogException($"PLC虚拟服务DB自检异常：{plc.PlcName}", ex);
        }
    }

    /// <summary>
    /// 初始化虚拟PLC内部内存。
    /// </summary>
    private void InitServerMemory(SiemensS7Server server, PlcConfigModel plc)
    {
        Log($"InitServerMemory 开始：{plc?.PlcName}");

        PlcDbBlockService.Instance.InitializeServerDbBlocks(server, plc);

        Log($"InitServerMemory 结束：{plc?.PlcName}");
    }

    /// <summary>
    /// 根据当前运行PLC数量，启动或停止自动写入循环。
    /// </summary>
    private void RefreshAutoWriteLoopState()
    {
        if (_serverMap.Count > 0)
        {
            EnsureAutoWriteLoopStarted();
            return;
        }

        StopAutoWriteLoop();
    }

    /// <summary>
    /// 确保自动写入循环已启动。
    /// </summary>
    private void EnsureAutoWriteLoopStarted()
    {
        lock (_lockObj)
        {
            if (_cancellationTokenSource is { IsCancellationRequested: false })
            {
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();

            var token = _cancellationTokenSource.Token;

            Log("自动写入循环准备启动。");

            Task.Run(() => RunAutoWriteLoopAsync(token), token);
        }
    }

    /// <summary>
    /// 停止自动写入循环。
    /// </summary>
    private void StopAutoWriteLoop()
    {
        lock (_lockObj)
        {
            if (_cancellationTokenSource == null)
            {
                return;
            }

            Log("自动写入循环准备停止。");

            _cancellationTokenSource.Cancel();

            _cancellationTokenSource = null;
        }
    }

    /// <summary>
    /// 自动写入循环。
    /// 注意：这里不是通过网络读PLC，而是读写 SiemensS7Server 自己的内存。
    /// </summary>
    private async Task RunAutoWriteLoopAsync(CancellationToken cancellationToken)
    {
        Log("自动写入循环已启动。");

        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = 300;

            var hasCustomDelay = false;

            try
            {
                if (_serverMap.Count == 0)
                {
                    Log("自动写入循环退出：当前没有运行中的PLC。");
                    return;
                }

                PlcStoreService.Instance.EnsureLoadedFromConfig();

                var plcs = PlcStoreService.Instance.GetSnapshotList();

                foreach (var plc in plcs)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Log("自动写入循环收到取消信号。");
                        return;
                    }

                    if (plc == null)
                    {
                        continue;
                    }

                    if (!plc.IsEnabled)
                    {
                        TraceOnce(
                            $"AutoWriteSkip_DisabledPlc_{plc.PlcId}",
                            $"自动写入跳过：{plc.PlcName} IsEnabled=false");

                        continue;
                    }

                    if (!plc.AutoWriteEnabled)
                    {
                        TraceOnce(
                            $"AutoWriteSkip_AutoWriteDisabled_{plc.PlcId}",
                            $"自动写入跳过：{plc.PlcName} AutoWriteEnabled=false");

                        continue;
                    }

                    if (!_serverMap.ContainsKey(plc.PlcId))
                    {
                        TraceOnce(
                            $"AutoWriteSkip_NotRunning_{plc.PlcId}",
                            $"自动写入跳过：{plc.PlcName} 服务未运行");

                        continue;
                    }

                    if (plc.PollIntervalMs > 0)
                    {
                        delay = hasCustomDelay
                            ? Math.Min(delay, plc.PollIntervalMs)
                            : plc.PollIntervalMs;

                        hasCustomDelay = true;
                    }

                    ProcessAutoWriteRules(plc);
                }
            }
            catch (Exception ex)
            {
                LogException("PLC自动写入循环异常", ex);
            }

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch
            {
                // ignored
            }
        }

        Log("自动写入循环已结束。");
    }

    /// <summary>
    /// 执行某台PLC的自动写入规则。
    /// </summary>
    private void ProcessAutoWriteRules(PlcConfigModel plc)
    {
        if (plc == null)
        {
            return;
        }

        if (!_serverMap.TryGetValue(plc.PlcId, out var server))
        {
            TraceOnce(
                $"ProcessAutoWriteRules_NotRunning_{plc.PlcId}",
                $"执行自动写入规则跳过：{plc.PlcName} 服务未运行。");

            return;
        }

        if (plc.DbBlocks == null || plc.DbBlocks.Count == 0)
        {
            TraceOnce(
                $"ProcessAutoWriteRules_NoDbBlocks_{plc.PlcId}",
                $"执行自动写入规则跳过：{plc.PlcName} 没有配置DB块。");

            return;
        }

        PlcAutoWriteRuleStoreService.Instance.EnsureLoadedFromConfig();

        var enabledDbBlocks = plc.DbBlocks
            .Where(x => x != null)
            .Where(x => x.HasEnabledAutoWriteRules())
            .OrderBy(x => x.DbNumber)
            .ToList();

        if (enabledDbBlocks.Count == 0)
        {
            TraceOnce(
                $"ProcessAutoWriteRules_NoEnabledDbBlockRules_{plc.PlcId}",
                $"执行自动写入规则跳过：{plc.PlcName} 没有启用的DB块规则引用。");

            return;
        }

        foreach (var dbBlock in enabledDbBlocks)
        {
            var rules = PlcAutoWriteRuleStoreService.Instance.GetEnabledRulesByRefs(dbBlock.AutoWriteRuleList);

            if (rules.Count == 0)
            {
                TraceOnce(
                    $"ProcessAutoWriteRules_NoGlobalRule_{plc.PlcId}_{dbBlock.DbNumber}",
                    $"执行自动写入规则跳过：PLC={plc.PlcName} DB{dbBlock.DbNumber} 没有可执行的全局规则。");

                continue;
            }

            foreach (var rule in rules)
            {
                ProcessAutoWriteRule(server, plc, dbBlock, rule);
            }
        }
    }

    /// <summary>
    /// 执行单条自动写入规则。
    /// 支持多触发、多写入。规则统一按“全部启用触发条件满足”判断。
    /// 如果规则启用延时：全部条件满足后开始计时，连续满足到规则延时时间后才执行写入动作。
    /// </summary>
    private void ProcessAutoWriteRule(SiemensS7Server server, PlcConfigModel plc, PlcDbBlockModel dbBlock, PlcAutoWriteRuleModel rule)
    {
        if (server == null || plc == null || dbBlock == null || rule == null)
        {
            return;
        }

        if (rule.Triggers == null || rule.Triggers.Count == 0)
        {
            TraceOnce(
                $"RuleSkip_NoTriggers_{plc.PlcId}_{dbBlock.DbNumber}_{rule.RuleId}",
                $"自动写入规则跳过：{plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 没有配置触发条件。");

            RemoveRuleDelayState(plc, dbBlock, rule);

            return;
        }

        if (rule.WriteItems == null || rule.WriteItems.Count == 0)
        {
            TraceOnce(
                $"RuleSkip_NoWriteItems_{plc.PlcId}_{dbBlock.DbNumber}_{rule.RuleId}",
                $"自动写入规则跳过：{plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 没有配置写入动作。");

            RemoveRuleDelayState(plc, dbBlock, rule);

            return;
        }

        var enabledTriggers = rule.Triggers
            .Where(x => x != null)
            .Where(x => x.IsEnabled)
            .Where(x => !string.IsNullOrWhiteSpace(x.ReadAddress))
            .ToList();

        if (enabledTriggers.Count == 0)
        {
            TraceOnce(
                $"RuleSkip_NoEnabledTriggers_{plc.PlcId}_{dbBlock.DbNumber}_{rule.RuleId}",
                $"自动写入规则跳过：{plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 没有启用的有效触发条件。");

            RemoveRuleDelayState(plc, dbBlock, rule);

            return;
        }

        var enabledWriteItems = rule.WriteItems
            .Where(x => x != null)
            .Where(x => x.IsEnabled)
            .Where(x => !string.IsNullOrWhiteSpace(x.WriteAddress))
            .ToList();

        if (enabledWriteItems.Count == 0)
        {
            TraceOnce(
                $"RuleSkip_NoEnabledWriteItems_{plc.PlcId}_{dbBlock.DbNumber}_{rule.RuleId}",
                $"自动写入规则跳过：{plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 没有启用的有效写入动作。");

            RemoveRuleDelayState(plc, dbBlock, rule);

            return;
        }

        try
        {
            var triggerResults = enabledTriggers
                .Select(trigger => ReadTrigger(server, plc, dbBlock, rule, trigger))
                .ToList();

            if (triggerResults.Any(x => !x.ReadSuccess))
            {
                RemoveRuleDelayState(plc, dbBlock, rule);

                TraceOnce(
                    $"RuleSkip_ReadFail_{plc.PlcId}_{dbBlock.DbNumber}_{rule.RuleId}",
                    $"自动写入规则跳过：PLC={plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 存在触发条件读取失败，本轮不执行写入/复位。");

                return;
            }

            var conditionMatched = triggerResults.All(x => x.CurrentValue == x.TriggerValue);

            var isTriggered = EvaluateRuleDelayTrigger(plc, dbBlock, rule, conditionMatched);

            TraceOnce(
                $"RuleTriggerResult_{plc.PlcId}_{dbBlock.DbNumber}_{rule.RuleId}",
                $"自动写入规则判断：PLC={plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 关系=全部满足 条件满足={conditionMatched} 规则延时={FormatRuleDelay(rule)} 结果={isTriggered}");

            foreach (var writeItem in enabledWriteItems)
            {
                if (isTriggered)
                {
                    WriteBoolWithTrace(
                        server,
                        plc,
                        dbBlock,
                        rule,
                        writeItem,
                        writeItem.WriteAddress,
                        writeItem.WriteValue,
                        "自动写入触发");

                    continue;
                }

                if (!writeItem.ResetWhenNotTrigger)
                {
                    TraceOnce(
                        $"RuleNoReset_{plc.PlcId}_{dbBlock.DbNumber}_{rule.RuleId}_{writeItem.WriteId}",
                        $"自动写入不复位：PLC={plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 写入动作={writeItem.WriteName} 未满足规则触发，且 ResetWhenNotTrigger=false。");

                    continue;
                }

                WriteBoolWithTrace(
                    server,
                    plc,
                    dbBlock,
                    rule,
                    writeItem,
                    writeItem.WriteAddress,
                    writeItem.ResetValue,
                    "自动写入复位");
            }
        }
        catch (Exception ex)
        {
            RemoveRuleDelayState(plc, dbBlock, rule);

            LogException($"自动写入规则异常：PLC={plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName}", ex);
        }
    }

    /// <summary>
    /// 读取单个触发条件。
    /// 注意：当前版本触发条件只负责读取当前值，不再做条件级延时。
    /// 规则级延时统一由 EvaluateRuleDelayTrigger 处理。
    /// </summary>
    private PlcAutoWriteTriggerReadResult ReadTrigger(
        SiemensS7Server server,
        PlcConfigModel plc,
        PlcDbBlockModel dbBlock,
        PlcAutoWriteRuleModel rule,
        PlcAutoWriteTriggerModel trigger)
    {
        var result = new PlcAutoWriteTriggerReadResult
        {
            Trigger = trigger,
            ReadSuccess = false,
            CurrentValue = false,
            TriggerValue = trigger?.TriggerValue ?? false,
            RawValue = false
        };

        if (server == null || plc == null || dbBlock == null || rule == null || trigger == null)
        {
            return result;
        }

        if (string.IsNullOrWhiteSpace(trigger.ReadAddress))
        {
            TraceOnce(
                $"TriggerSkip_EmptyRead_{plc.PlcId}_{dbBlock.DbNumber}_{rule.RuleId}_{trigger.TriggerId}",
                $"自动写入触发条件跳过：PLC={plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 条件={trigger.TriggerName} 读取地址为空。");

            return result;
        }

        var readAddress = PlcDbBlockService.Instance.ResolveDbAddressForDbBlock(trigger.ReadAddress, dbBlock.DbNumber);

        var read = server.ReadBool(readAddress);

        if (!read.IsSuccess)
        {
            TraceOnce(
                $"TriggerReadFail_{plc.PlcId}_{dbBlock.DbNumber}_{rule.RuleId}_{trigger.TriggerId}_{readAddress}",
                $"自动写入触发条件读取失败：PLC={plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 条件={trigger.TriggerName} 地址模板={trigger.ReadAddress} 实际地址={readAddress} Message={read.Message}");

            return result;
        }

        result.ReadSuccess = true;
        result.RawValue = read.Content;
        result.CurrentValue = read.Content;

        TraceOnce(
            $"TriggerReadValue_{plc.PlcId}_{dbBlock.DbNumber}_{rule.RuleId}_{trigger.TriggerId}_{readAddress}",
            $"自动写入触发条件读取：PLC={plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 条件={trigger.TriggerName} 地址模板={trigger.ReadAddress} 实际地址={readAddress} Value={read.Content} Trigger={trigger.TriggerValue}");

        return result;
    }

    /// <summary>
    /// 判断触发条件是否启用延时。
    /// </summary>
    private static bool IsDelayTriggerEnabled(PlcAutoWriteTriggerModel trigger)
    {
        return trigger != null &&
               trigger.DelayTriggerEnabled &&
               trigger.DelayMilliseconds > 0;
    }

    /// <summary>
    /// 应用延时触发判断。
    /// </summary>
    private void ApplyDelayTrigger(
        SiemensS7Server server,
        PlcConfigModel plc,
        PlcDbBlockModel dbBlock,
        PlcAutoWriteRuleModel rule,
        PlcAutoWriteTriggerModel trigger,
        string readAddress,
        bool rawValue,
        PlcAutoWriteTriggerReadResult result)
    {
        if (server == null || plc == null || dbBlock == null || rule == null || trigger == null || result == null)
        {
            return;
        }

        var delayKey = CreateTriggerDelayKey(plc, dbBlock, rule, trigger, readAddress);

        if (rawValue != trigger.TriggerValue)
        {
            _delayedTriggerStateMap.TryRemove(delayKey, out _);

            result.CurrentValue = rawValue;
            result.DelaySatisfied = false;
            result.ElapsedMilliseconds = 0;

            TraceOnce(
                $"TriggerDelayReset_{delayKey}",
                $"自动写入延时触发复位：PLC={plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 条件={trigger.TriggerName} 实际地址={readAddress} 当前值={rawValue} 目标值={trigger.TriggerValue}。");

            return;
        }

        var now = DateTime.UtcNow;

        var state = _delayedTriggerStateMap.GetOrAdd(
            delayKey,
            _ => new PlcDelayedTriggerState
            {
                StartTimeUtc = now,
                LastMatchTimeUtc = now,
                TriggerValue = trigger.TriggerValue
            });

        if (state.TriggerValue != trigger.TriggerValue)
        {
            state.StartTimeUtc = now;
            state.TriggerValue = trigger.TriggerValue;
        }

        state.LastMatchTimeUtc = now;

        var elapsedMilliseconds = (now - state.StartTimeUtc).TotalMilliseconds;

        result.ElapsedMilliseconds = elapsedMilliseconds;

        if (elapsedMilliseconds >= trigger.DelayMilliseconds)
        {
            result.CurrentValue = trigger.TriggerValue;
            result.DelaySatisfied = true;

            TraceOnce(
                $"TriggerDelaySatisfied_{delayKey}",
                $"自动写入延时触发满足：PLC={plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 条件={trigger.TriggerName} 实际地址={readAddress} 当前值={rawValue} 延时={trigger.DelayMilliseconds}ms。");

            return;
        }

        // 当前值已经等于 TriggerValue，但连续满足时间还没到。
        // 为了让外层 All(x.CurrentValue == x.TriggerValue) 暂时不成立，这里返回相反值。
        result.CurrentValue = !trigger.TriggerValue;
        result.DelaySatisfied = false;

        TraceOnce(
            $"TriggerDelayWaiting_{delayKey}",
            $"自动写入延时触发等待：PLC={plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 条件={trigger.TriggerName} 实际地址={readAddress} 当前值={rawValue} 目标值={trigger.TriggerValue} 延时={trigger.DelayMilliseconds}ms。");
    }

    /// <summary>
    /// 删除指定触发条件的延时状态。
    /// </summary>
    private void RemoveTriggerDelayState(
        PlcConfigModel plc,
        PlcDbBlockModel dbBlock,
        PlcAutoWriteRuleModel rule,
        PlcAutoWriteTriggerModel trigger,
        string readAddress)
    {
        if (plc == null || dbBlock == null || rule == null || trigger == null)
        {
            return;
        }

        var delayKey = CreateTriggerDelayKey(plc, dbBlock, rule, trigger, readAddress);

        _delayedTriggerStateMap.TryRemove(delayKey, out _);
    }

    /// <summary>
    /// 创建延时触发状态Key。
    /// </summary>
    private static string CreateTriggerDelayKey(
        PlcConfigModel plc,
        PlcDbBlockModel dbBlock,
        PlcAutoWriteRuleModel rule,
        PlcAutoWriteTriggerModel trigger,
        string readAddress)
    {
        return $"{plc?.PlcId}_{dbBlock?.DbNumber}_{rule?.RuleId}_{trigger?.TriggerId}_{readAddress}_{trigger?.TriggerValue}_{trigger?.DelayMilliseconds}";
    }

    /// <summary>
    /// 规则级延时触发判断。
    /// 条件全部满足后开始计时，条件中断则清除计时。
    /// </summary>
    private bool EvaluateRuleDelayTrigger(
        PlcConfigModel plc,
        PlcDbBlockModel dbBlock,
        PlcAutoWriteRuleModel rule,
        bool conditionMatched)
    {
        if (plc == null || dbBlock == null || rule == null)
        {
            return false;
        }

        if (!conditionMatched)
        {
            RemoveRuleDelayState(plc, dbBlock, rule);
            return false;
        }

        if (!IsRuleDelayTriggerEnabled(rule))
        {
            RemoveRuleDelayState(plc, dbBlock, rule);
            return true;
        }

        var delayKey = CreateRuleDelayKey(plc, dbBlock, rule);

        var now = DateTime.UtcNow;

        var state = _delayedTriggerStateMap.GetOrAdd(
            delayKey,
            _ => new PlcDelayedTriggerState
            {
                StartTimeUtc = now,
                LastMatchTimeUtc = now,
                TriggerValue = true
            });

        state.LastMatchTimeUtc = now;

        var elapsedMilliseconds = (now - state.StartTimeUtc).TotalMilliseconds;

        if (elapsedMilliseconds >= rule.DelayMilliseconds)
        {
            TraceOnce(
                $"RuleDelaySatisfied_{delayKey}",
                $"自动写入规则延时满足：PLC={plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 延时={rule.DelayMilliseconds}ms 已满足，开始执行写入动作。");

            return true;
        }

        TraceOnce(
            $"RuleDelayWaiting_{delayKey}",
            $"自动写入规则延时等待：PLC={plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 条件已满足，等待规则延时={rule.DelayMilliseconds}ms 后执行写入动作。");

        return false;
    }

    /// <summary>
    /// 判断规则是否启用延时。
    /// </summary>
    private static bool IsRuleDelayTriggerEnabled(PlcAutoWriteRuleModel rule)
    {
        return rule != null &&
               rule.DelayTriggerEnabled &&
               rule.DelayMilliseconds > 0;
    }

    /// <summary>
    /// 删除指定规则的延时状态。
    /// </summary>
    private void RemoveRuleDelayState(
        PlcConfigModel plc,
        PlcDbBlockModel dbBlock,
        PlcAutoWriteRuleModel rule)
    {
        if (plc == null || dbBlock == null || rule == null)
        {
            return;
        }

        var delayKey = CreateRuleDelayKey(plc, dbBlock, rule);

        _delayedTriggerStateMap.TryRemove(delayKey, out _);
    }

    /// <summary>
    /// 创建规则延时状态Key。
    /// </summary>
    private static string CreateRuleDelayKey(
        PlcConfigModel plc,
        PlcDbBlockModel dbBlock,
        PlcAutoWriteRuleModel rule)
    {
        return $"{plc?.PlcId}_{dbBlock?.DbNumber}_{rule?.RuleId}_RULE_{rule?.DelayMilliseconds}";
    }

    private static string FormatRuleDelay(PlcAutoWriteRuleModel rule)
    {
        if (!IsRuleDelayTriggerEnabled(rule))
        {
            return "无";
        }

        return rule.DelayMilliseconds + "ms";
    }

    /// <summary>
    /// 带来源追踪的Bool写入。
    /// 用于确认到底是谁把某个地址写成True/False。
    /// </summary>
    private void WriteBoolWithTrace(
        SiemensS7Server server,
        PlcConfigModel plc,
        PlcDbBlockModel dbBlock,
        PlcAutoWriteRuleModel rule,
        PlcAutoWriteItemModel writeItem,
        string address,
        bool value,
        string source)
    {
        if (server == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            TraceOnce(
                $"WriteSkip_EmptyAddress_{plc?.PlcId}_{dbBlock?.DbNumber}_{rule?.RuleId}_{writeItem?.WriteId}",
                $"[PLC写入跳过] Source={source} PLC={plc?.PlcName} DB{dbBlock?.DbNumber} 规则={rule?.RuleName} 写入动作={writeItem?.WriteName} 写入地址为空。");

            return;
        }

        var resolvedAddress = PlcDbBlockService.Instance.ResolveDbAddressForDbBlock(address, dbBlock?.DbNumber ?? 0);

        try
        {
            bool? beforeValue = null;

            var before = server.ReadBool(resolvedAddress);

            if (before.IsSuccess)
            {
                beforeValue = before.Content;
            }

            if (before.IsSuccess && before.Content == value)
            {
                TraceOnce(
                    $"WriteSkip_{plc.PlcId}_{dbBlock.DbNumber}_{rule.RuleId}_{writeItem.WriteId}_{source}_{resolvedAddress}",
                    $"[PLC写入跳过] Source={source} PLC={plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 写入动作={writeItem.WriteName} 地址模板={address} 实际地址={resolvedAddress} 当前已是 {value}");

                return;
            }

            var write = server.Write(resolvedAddress, value);

            if (!write.IsSuccess)
            {
                Log(
                    $"[PLC写入失败] Source={source} PLC={plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 写入动作={writeItem.WriteName} 地址模板={address} 实际地址={resolvedAddress} Value={value} Before={FormatNullableBool(beforeValue)} Message={write.Message}");

                return;
            }

            var after = server.ReadBool(resolvedAddress);

            Log(
                $"[PLC写入成功] Source={source} PLC={plc.PlcName} DB{dbBlock.DbNumber} 规则={rule.RuleName} 写入动作={writeItem.WriteName} 地址模板={address} 实际地址={resolvedAddress} Before={FormatNullableBool(beforeValue)} WriteValue={value} After={(after.IsSuccess ? after.Content.ToString() : $"ReadFail:{after.Message}")}");
        }
        catch (Exception ex)
        {
            LogException(
                $"[PLC写入异常] Source={source} PLC={plc?.PlcName} DB{dbBlock?.DbNumber} 规则={rule?.RuleName} 写入动作={writeItem?.WriteName} 地址模板={address} 实际地址={resolvedAddress} Value={value}",
                ex);
        }
    }

    private static string FormatNullableBool(bool? value)
    {
        return value.HasValue ? value.Value.ToString() : "Unknown";
    }

    /// <summary>
    /// 相同key相同内容只打印一次，避免自动写入循环刷屏。
    /// 内容变化时会再次打印。
    /// </summary>
    private void TraceOnce(string key, string message)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            Log(message);
            return;
        }

        if (_lastTraceMap.TryGetValue(key, out var lastMessage) && lastMessage == message)
        {
            return;
        }

        _lastTraceMap[key] = message;

        Log(message);
    }

    private static void Log(string message)
    {
        Console.WriteLine(@$"[{DateTime.Now:HH:mm:ss.fff}][PLC模拟服务][T{Environment.CurrentManagedThreadId}] {message}");
    }

    private static void LogException(string title, Exception ex)
    {
        try
        {
            Console.WriteLine(
                @$"[{DateTime.Now:HH:mm:ss.fff}][PLC模拟服务异常][T{Environment.CurrentManagedThreadId}] {title}");

            if (ex == null)
            {
                Console.WriteLine(@$"[{DateTime.Now:HH:mm:ss.fff}][PLC模拟服务异常] Exception=null");
                return;
            }

            Console.WriteLine(@$"[{DateTime.Now:HH:mm:ss.fff}][PLC模拟服务异常] Type={ex.GetType().FullName}");
            Console.WriteLine(@$"[{DateTime.Now:HH:mm:ss.fff}][PLC模拟服务异常] Message={ex.Message}");
            Console.WriteLine(@$"[{DateTime.Now:HH:mm:ss.fff}][PLC模拟服务异常] StackTrace={ex.StackTrace}");

            if (ex.InnerException == null)
            {
                return;
            }

            Console.WriteLine(
                @$"[{DateTime.Now:HH:mm:ss.fff}][PLC模拟服务异常] InnerType={ex.InnerException.GetType().FullName}");
            Console.WriteLine(@$"[{DateTime.Now:HH:mm:ss.fff}][PLC模拟服务异常] InnerMessage={ex.InnerException.Message}");
            Console.WriteLine(
                @$"[{DateTime.Now:HH:mm:ss.fff}][PLC模拟服务异常] InnerStackTrace={ex.InnerException.StackTrace}");
        }
        catch
        {
            // ignored
        }
    }

    private class PlcAutoWriteTriggerReadResult
    {
        public PlcAutoWriteTriggerModel Trigger { get; set; }

        public bool ReadSuccess { get; set; }

        /// <summary>
        /// 外层规则判断使用的值。
        /// 普通触发时等于 RawValue；延时未满足时会临时返回与 TriggerValue 相反的值。
        /// </summary>
        public bool CurrentValue { get; set; }

        public bool RawValue { get; set; }

        public bool TriggerValue { get; set; }

        public bool DelayTriggerEnabled { get; set; }

        public int DelayMilliseconds { get; set; }

        public double ElapsedMilliseconds { get; set; }

        public bool DelaySatisfied { get; set; }
    }

    private class PlcDelayedTriggerState
    {
        public DateTime StartTimeUtc { get; set; }

        public DateTime LastMatchTimeUtc { get; set; }

        public bool TriggerValue { get; set; }
    }

    private class PlcProxyInfo
    {
        public string ListenAddress { get; set; }

        public int ListenPort { get; set; }

        public string ConnectAddress { get; set; }

        public int ConnectPort { get; set; }
    }
}