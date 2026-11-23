#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HslCommunication.Profinet.Siemens;
using Vda5050AgvSimulator.Models.Plc;
using IoFile = System.IO.File;

namespace Vda5050AgvSimulator.Services.Plc;

/// <summary>
/// PLC虚拟服务端服务。
/// 当前版本使用：
/// S7comm 监听本机回环端口，Windows portproxy 将外部 PLC 地址转发到该端口。
/// 直连方式将 S7comm 绑定到配置的本机 IPv4 地址和直连端口。
/// </summary>
public class PlcVirtualServerService
{
    public static PlcVirtualServerService Instance { get; } = new();

    private readonly object _lockObj = new();
    private readonly object _plcLifecycleLock = new();

    private static readonly object TransportLogLock = new();
    private const long TransportLogLimitBytes = 1024 * 1024;

    private readonly ConcurrentDictionary<int, Rcs4SiemensS7Server> _serverMap = new();

    private readonly ConcurrentDictionary<int, PlcProxyInfo> _proxyMap = new();

    /// <summary>
    /// 保存每台运行中PLC启动时使用的配置快照。
    /// PLC重载前按旧配置读取DB内存，重载后按新配置恢复公共DB块数据。
    /// </summary>
    private readonly ConcurrentDictionary<int, PlcConfigModel> _runningPlcConfigMap = new();

    /// <summary>
    /// 正在重载并恢复DB内存的PLC。
    /// 自动写入循环在恢复完成前跳过这些PLC，避免初始化值或旧值被提前改写。
    /// </summary>
    private readonly ConcurrentDictionary<int, byte> _reloadingPlcIds = new();

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

    private volatile ConditionalWeakTable<SiemensS7Server,
        ConcurrentDictionary<(int DbNumber, int RuleId), PlcAutoWriteRuleExecution>> _autoWriteRuleExecutions = new();

    private long _autoWriteScanSequence;

    private CancellationTokenSource _cancellationTokenSource;

    private const string ProxyConnectAddress = "127.0.0.1";

    private const int Plc2StringDbNumber = 1500;
    private const byte Plc2StringMaxLength = 40;
    private static readonly int[] Plc2StringOffsets = [8, 50, 92, 134, 210, 252, 294, 336];

    private static readonly Regex ProtocolAddressRegex = new(
        @"^%?(?:DB(?<db>\{DB\}|\*|\d+)\.)?(?<area>DBX|DBB|DBW|DBD)?(?<byte>\d+)(?:\.(?<bit>[0-7]))?(?::(?<type>[A-Z]+)(?:\[(?<length>\d+)\]|\((?<length>\d+)\))?)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ProtocolVariableLengthTypeRegex = new(
        @"^(?:STRING|BYTEARRAY)\s*(?:\[(?<length>\d+)\]|\((?<length>\d+)\)|:(?<length>\d+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public bool IsRunning => _serverMap.Values.Any(server => server.IsTransportListening);

    public int RunningCount => _serverMap.Values.Count(server => server.IsTransportListening);

    private PlcVirtualServerService()
    {
        PlcAutoWriteRuleStoreService.Instance.PlcAutoWriteRuleChanged += (_, _) =>
        {
            // 规则启用状态实时变化后清理旧延时状态，避免重新启用时沿用上一次计时。
            _delayedTriggerStateMap.Clear();
            _lastTraceMap.Clear();
            _autoWriteRuleExecutions = new();
        };
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
        return _serverMap.TryGetValue(plcId, out var server) && server.IsTransportListening;
    }

    internal int GetPlcConnectionCount(int plcId)
    {
        return _serverMap.TryGetValue(plcId, out var server) ? server.ActiveTcpConnectionCount : 0;
    }

    internal bool TryGetAutoWriteMonitorDbLength(int plcId, int dbNumber, out int length)
    {
        length = 0;
        return _serverMap.TryGetValue(plcId, out var server) &&
               server.IsTransportListening &&
               server.DbLengths.TryGetValue(dbNumber, out length);
    }

    internal bool TryGetLastAutoWriteRuleExecution(int plcId, int dbNumber, int ruleId, out DateTime executedAtUtc)
    {
        return TryGetAutoWriteRuleExecution(plcId, dbNumber, ruleId, out _, out executedAtUtc);
    }

    internal bool TryGetAutoWriteRuleExecution(int plcId, int dbNumber, int ruleId,
        out long executionCount, out DateTime executedAtUtc)
    {
        executionCount = 0;
        executedAtUtc = default;
        if (!_serverMap.TryGetValue(plcId, out var server) ||
            !_autoWriteRuleExecutions.TryGetValue(server, out var history) ||
            !history.TryGetValue((dbNumber, ruleId), out var execution))
        {
            return false;
        }

        executionCount = execution.Count;
        executedAtUtc = execution.ExecutedAtUtc;
        return true;
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
                Log(
                    $"[PLC手动取反] 地址={address} Before={before.Content} WriteValue={newValue} After=ReadFail:{after.Message}");
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
    /// 按全局PLC复位模板复位当前运行中的虚拟PLC内存。
    /// 1. 所有PLC、所有DB块共用 PlcStoreService 中的全局复位模板。
    /// 2. 模板中的偏移地址会按当前目标DB块号解析。
    /// 3. 全局模板完全为空时，保留原逻辑：清零整个DB块。
    /// 4. 模板存在但全部禁用时，不执行复位，也不会误清零整个DB块。
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

        var configuredResetItems = PlcStoreService.Instance
            .GetGlobalResetItemsSnapshot()
            .Where(x => x != null)
            .OrderBy(x => x.ResetItemId)
            .ToList();

        if (configuredResetItems.Count == 0)
        {
            return TryClearDbBlock(plcId, server, dbBlock, out message);
        }

        var resetItems = configuredResetItems
            .Where(x => x.IsEnabled)
            .ToList();

        if (resetItems.Count == 0)
        {
            message = "全局PLC复位模板存在，但没有启用的复位项，本次未执行复位。";
            return false;
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

        message = $"复位完成，共执行全局模板中的 {successCount} 个复位项。";
        return true;
    }

    /// <summary>
    /// 清零整个DB块。
    /// </summary>
    private bool TryClearDbBlock(int plcId, Rcs4SiemensS7Server server, PlcDbBlockModel dbBlock, out string message)
    {
        message = string.Empty;

        if (server == null || dbBlock == null)
        {
            message = "Server或DB块为空。";
            return false;
        }

        if (!server.DbLengths.TryGetValue(dbBlock.DbNumber, out int length))
        {
            message = $"DB{dbBlock.DbNumber} 未在当前仿真 PLC 中启用。";
            return false;
        }

        try
        {
            var address = $"DB{dbBlock.DbNumber}.0";

            var clearedBytes = new byte[length];
            var initializedStringHeaders = false;

            if (_runningPlcConfigMap.TryGetValue(plcId, out var plc) && IsPlc2StringDb(plc, dbBlock.DbNumber))
            {
                initializedStringHeaders = InitializePlc2EmptyStringHeaders(clearedBytes, length);
            }

            var write = server.Write(address, clearedBytes);

            if (!write.IsSuccess)
            {
                message = $"清零DB{dbBlock.DbNumber}失败：{write.Message}";
                return false;
            }

            message = initializedStringHeaders
                ? $"DB{dbBlock.DbNumber} 已复位并初始化STRING长度头，长度={length}。"
                : $"DB{dbBlock.DbNumber} 已清零，长度={length}。";
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
                protocolItem.DataType,
                out var address,
                out message))
        {
            actualAddress = address?.DisplayAddress ?? string.Empty;
            return false;
        }

        actualAddress = address.DisplayAddress;

        if (!TryCheckProtocolRange(server, address, dataType, out message))
        {
            return false;
        }

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

                    valueText = ReadSingleBigEndian(bytes, 0).ToString("G9", CultureInfo.InvariantCulture);
                    return true;
                }

                case "STRING":
                {
                    var stringLength = address.ValueLength;

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
                    var byteLength = address.ValueLength;

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
            LogException(
                $"[协议看板读取异常] PlcId={plcId} DB{dbNumber} Name={protocolItem.SignalName} Address={actualAddress}", ex);
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
                protocolItem.DataType,
                out var address,
                out message))
        {
            actualAddress = address?.DisplayAddress ?? string.Empty;
            return false;
        }

        actualAddress = address.DisplayAddress;

        if (!TryCheckProtocolRange(server, address, dataType, out message))
        {
            return false;
        }

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

                    Log(
                        $"[协议看板写入成功] PlcId={plcId} DB{dbNumber} Name={protocolItem.SignalName} Type=Bool Address={actualAddress} Value={boolValue}");
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

                    return TryWriteRawBytes(server, address.ByteAddress, WriteUInt16BigEndian((ushort)number),
                        out message);
                }

                case "INT":
                {
                    if (!TryParseInteger(valueText, short.MinValue, short.MaxValue, out var number, out message))
                    {
                        return false;
                    }

                    return TryWriteRawBytes(server, address.ByteAddress, WriteInt16BigEndian((short)number),
                        out message);
                }

                case "DINT":
                {
                    if (!TryParseInteger(valueText, int.MinValue, int.MaxValue, out var number, out message))
                    {
                        return false;
                    }

                    return TryWriteRawBytes(server, address.ByteAddress, WriteInt32BigEndian((int)number),
                        out message);
                }

                case "REAL":
                {
                    if (!float.TryParse(valueText?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                            out var number) || float.IsNaN(number) || float.IsInfinity(number))
                    {
                        message = "Real写入值必须是数字，例如 12.34。";
                        return false;
                    }

                    return TryWriteRawBytes(server, address.ByteAddress, WriteSingleBigEndian(number), out message);
                }

                case "STRING":
                {
                    var stringLength = address.ValueLength;
                    var text = valueText ?? string.Empty;

                    if (text.Length > stringLength)
                    {
                        message = $"String长度超过限制：最大{stringLength}字节。";
                        return false;
                    }

                    if (text.Any(character => character > 0x7F))
                    {
                        message = "String只支持ASCII字符。";
                        return false;
                    }

                    var bytes = new byte[stringLength + 2];
                    bytes[0] = (byte)stringLength;
                    bytes[1] = (byte)text.Length;
                    Encoding.ASCII.GetBytes(text, 0, text.Length, bytes, 2);

                    return TryWriteRawBytes(server, address.ByteAddress, bytes, out message);
                }

                case "BYTEARRAY":
                {
                    if (!TryParseByteArray(valueText, out var bytes, out message))
                    {
                        return false;
                    }

                    var byteLength = address.ValueLength;
                    if (bytes.Length > byteLength)
                    {
                        message = $"ByteArray 最多写入 {byteLength} 字节，当前输入 {bytes.Length} 字节。";
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
            LogException(
                $"[协议看板写入异常] PlcId={plcId} DB{dbNumber} Name={protocolItem.SignalName} Address={actualAddress} Value={valueText}",
                ex);
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

    internal static bool TryGetProtocolByteOffset(string rawAddress, out int byteOffset)
    {
        return TryGetProtocolByteOffset(MatchProtocolAddress(rawAddress), out byteOffset);
    }

    private static Match MatchProtocolAddress(string rawAddress)
    {
        return ProtocolAddressRegex.Match((rawAddress ?? string.Empty).Trim().Replace(" ", string.Empty));
    }

    private static bool TryGetProtocolByteOffset(Match match, out int byteOffset)
    {
        return int.TryParse(match.Groups["byte"].Value, NumberStyles.None, CultureInfo.InvariantCulture,
            out byteOffset) && byteOffset <= 0x1FFFFF;
    }

    private static bool TryResolveProtocolAddress(
        string rawAddress,
        int defaultDbNumber,
        string dataType,
        string configuredType,
        out PlcProtocolResolvedAddress address,
        out string message)
    {
        address = new PlcProtocolResolvedAddress();
        message = string.Empty;

        if (defaultDbNumber is <= 0 or > ushort.MaxValue)
        {
            message = "默认DB块号不正确。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            message = "地址不能为空。";
            return false;
        }

        var match = MatchProtocolAddress(rawAddress);
        if (!match.Success)
        {
            message = "地址格式不正确，示例：DB{DB}.DBX0.0、DB1500.DBW4、DBX0.0、0.0。";
            return false;
        }

        int dbNumber = defaultDbNumber;
        var dbText = match.Groups["db"].Value;
        if (dbText.Length > 0 && dbText != "*" && !dbText.Equals("{DB}", StringComparison.OrdinalIgnoreCase) &&
            (!int.TryParse(dbText, NumberStyles.None, CultureInfo.InvariantCulture, out dbNumber) ||
             dbNumber is <= 0 or > ushort.MaxValue))
        {
            message = "DB块号必须在 1–65535 范围内。";
            return false;
        }

        if (!TryGetProtocolByteOffset(match, out int byteOffset))
        {
            message = "字节偏移超出 S7 地址范围。";
            return false;
        }

        var suffix = match.Groups["type"];
        var suffixLength = match.Groups["length"];
        bool byteArraySuffix = dataType == "BYTEARRAY" && suffixLength.Success &&
                               suffix.Value.Equals("BYTE", StringComparison.OrdinalIgnoreCase);
        if (suffix.Success && !byteArraySuffix && !suffix.Value.Equals(dataType, StringComparison.OrdinalIgnoreCase))
        {
            message = $"地址类型 {suffix.Value} 与看板配置的 {dataType} 不一致。";
            return false;
        }

        var valueLength = dataType switch
        {
            "BOOL" or "BYTE" => 1,
            "WORD" or "INT" => 2,
            "DINT" or "REAL" => 4,
            "STRING" => 40,
            "BYTEARRAY" => 16,
            _ => 0
        };
        if (valueLength == 0)
        {
            message = "暂不支持的数据类型：" + configuredType;
            return false;
        }

        bool hasVariableLength = dataType is "STRING" or "BYTEARRAY";
        if (hasVariableLength)
        {
            var typeMatch = ProtocolVariableLengthTypeRegex.Match(configuredType.Trim());
            if (!typeMatch.Success)
            {
                message = "数据类型格式不正确，示例：String[40]、ByteArray[15]。";
                return false;
            }

            var typeLength = typeMatch.Groups["length"];
            var declaredLength = typeLength.Success ? typeLength : suffixLength;
            int maxLength = dataType == "STRING" ? 254 : 512;
            if (declaredLength.Success &&
                (!int.TryParse(declaredLength.Value, NumberStyles.None, CultureInfo.InvariantCulture,
                     out valueLength) ||
                 valueLength < 1 || valueLength > maxLength))
            {
                message = $"{dataType} 长度必须在 1–{maxLength} 范围内。";
                return false;
            }
        }

        if (suffixLength.Success && (!hasVariableLength ||
                                     !int.TryParse(suffixLength.Value, NumberStyles.None,
                                         CultureInfo.InvariantCulture, out int addressLength) ||
                                     addressLength != valueLength))
        {
            message = "地址中的数据长度与看板类型定义不一致。";
            return false;
        }

        var area = match.Groups["area"].Value;
        bool isDbx = area.Equals("DBX", StringComparison.OrdinalIgnoreCase);
        var bit = match.Groups["bit"];
        int? bitOffset = bit.Success ? bit.Value[0] - '0' : null;

        if (dataType == "BOOL" && (!bitOffset.HasValue || area.Length > 0 && !isDbx))
        {
            message = "Bool地址必须包含bit位，例如 DB{DB}.DBX0.0 或 0.0。";
            return false;
        }

        // 信号模型用 DBXn.0 或 n.0 表示 STRING 和字节数组的字节起点。
        if (hasVariableLength && bitOffset == 0 && (isDbx || area.Length == 0))
        {
            bitOffset = null;
        }

        if (dataType != "BOOL" && bitOffset != null)
        {
            message = "非Bool类型地址不应包含bit位，请使用 DBB/DBW/DBD 或字节偏移地址。";
            return false;
        }

        address.DbNumber = dbNumber;
        address.ByteOffset = byteOffset;
        address.BitOffset = bitOffset;
        address.ValueLength = valueLength;
        address.ByteAddress = $"DB{dbNumber}.{byteOffset}";
        address.BoolAddress = bitOffset.HasValue ? $"DB{dbNumber}.{byteOffset}.{bitOffset.Value}" : string.Empty;
        var displayArea = dataType switch
        {
            "BOOL" => "DBX",
            "WORD" or "INT" => "DBW",
            "DINT" or "REAL" => "DBD",
            _ => "DBB"
        };
        address.DisplayAddress = $"DB{dbNumber}.{displayArea}{byteOffset}";
        if (bitOffset.HasValue)
        {
            address.DisplayAddress += $".{bitOffset.Value}";
        }

        if (suffix.Success)
        {
            address.DisplayAddress += $":{suffix.Value.ToUpperInvariant()}";
            if (suffixLength.Success)
            {
                address.DisplayAddress += $"[{valueLength}]";
            }
        }

        return true;
    }

    private static bool TryCheckProtocolRange(Rcs4SiemensS7Server server, PlcProtocolResolvedAddress address,
        string normalizedType, out string message)
    {
        message = string.Empty;
        if (!server.DbLengths.TryGetValue(address.DbNumber, out int dbLength))
        {
            message = $"DB{address.DbNumber} 未在当前仿真 PLC 中启用。";
            return false;
        }

        int length = address.ValueLength + (normalizedType == "STRING" ? 2 : 0);
        if (address.ByteOffset + length > dbLength)
        {
            message = $"地址 {address.DisplayAddress} 需要 {length} 字节，DB{address.DbNumber} 实际长度为 {dbLength}。";
            return false;
        }

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

    private static bool TryParseInteger(string valueText, long minValue, long maxValue, out long value,
        out string message)
    {
        value = 0;
        message = string.Empty;

        var text = valueText?.Trim() ?? string.Empty;

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(text.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                    out var hexValue))
            {
                message = "整数格式不正确。";
                return false;
            }

            value = hexValue;
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

        bool commaSeparated = text.IndexOf(',') >= 0 || text.IndexOf(';') >= 0;
        char[] separators = commaSeparated ? [' ', ',', ';'] : [' ', '-'];
        if (text[0] == '-')
        {
            message = "ByteArray 的字节值必须在 0–255 范围内。";
            return false;
        }

        var parts = text.Split(separators, StringSplitOptions.RemoveEmptyEntries);

        var result = new List<byte>();

        foreach (var part in parts)
        {
            var token = part.Trim();

            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!byte.TryParse(token.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                        out var prefixedByte))
                {
                    message = $"ByteArray 的十六进制值无效：{token}。";
                    return false;
                }

                result.Add(prefixedByte);
            }
            else if (commaSeparated && byte.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture,
                         out var decimalByte))
            {
                result.Add(decimalByte);
            }
            else if (token.Length <= 2 && byte.TryParse(token, NumberStyles.AllowHexSpecifier,
                         CultureInfo.InvariantCulture, out var hexByte))
            {
                result.Add(hexByte);
            }
            else if (!commaSeparated && byte.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture,
                         out var number))
            {
                result.Add(number);
            }
            else
            {
                message = "ByteArray格式不正确，示例：01 02 FF 或 1,2,255。";
                return false;
            }
        }

        bytes = [.. result];
        return bytes.Length > 0;
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
        return
        [
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
        return
        [
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

        public int ValueLength { get; set; }

        public string ByteAddress { get; set; }

        public string BoolAddress { get; set; }

        public string DisplayAddress { get; set; }
    }

    /// <summary>
    /// 获取代理端口。
    /// ProxyPort 是外部客户端连接端口，例如 102。
    /// Port 是回环地址上的 S7comm 监听端口，例如 1101 / 1105。
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

        var currentPlcIds = new HashSet<int>(plcs
            .Where(x => x != null)
            .Select(x => x.PlcId));

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
        lock (_plcLifecycleLock)
        {
            return StartOrReloadPlcCore(plc);
        }
    }

    private bool StartOrReloadPlcCore(PlcConfigModel plc)
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

            StopPlcCore(plc.PlcId);

            return false;
        }

        var hasServer = _serverMap.ContainsKey(plc.PlcId);
        if (hasServer)
        {
            _reloadingPlcIds[plc.PlcId] = 0;
        }

        try
        {
            var dbMemorySnapshots = hasServer ? CaptureRunningDbMemory(plc.PlcId) : [];
            if (hasServer || _proxyMap.ContainsKey(plc.PlcId))
            {
                StopPlcCore(plc.PlcId);
            }

            var result = StartServer(plc, dbMemorySnapshots);

            RefreshAutoWriteLoopState();

            Log($"StartOrReloadPlc 完成：{plc.PlcName} Result={result} 当前运行数量={RunningCount}");

            return result;
        }
        finally
        {
            _reloadingPlcIds.TryRemove(plc.PlcId, out _);
        }
    }

    /// <summary>
    /// 在停止运行中的PLC之前读取其DB内存。
    /// 按该 PLC 运行中的实际 DB 长度读取。
    /// </summary>
    private List<PlcDbMemorySnapshot> CaptureRunningDbMemory(int plcId)
    {
        var result = new List<PlcDbMemorySnapshot>();

        if (!_serverMap.TryGetValue(plcId, out var server))
        {
            return result;
        }

        foreach (var db in server.DbLengths.OrderBy(x => x.Key))
        {
            int dbNumber = db.Key;
            int length = db.Value;

            try
            {
                var bytes = new byte[length];
                for (int offset = 0; offset < length;)
                {
                    int count = Math.Min(ushort.MaxValue, length - offset);
                    var read = server.Read($"DB{dbNumber}.{offset}", (ushort)count);
                    if (!read.IsSuccess || read.Content?.Length != count)
                    {
                        throw new InvalidOperationException($"偏移 {offset}，读取 {count} 字节失败。{read.Message}");
                    }

                    read.Content.CopyTo(bytes, offset);
                    offset += count;
                }

                result.Add(new PlcDbMemorySnapshot
                {
                    DbNumber = dbNumber,
                    Bytes = bytes
                });
            }
            catch (Exception ex)
            {
                LogException(
                    $"[PLC重载快照异常] PlcId={plcId} DB{dbNumber} Length={length}",
                    ex);
            }
        }

        return result;
    }

    /// <summary>
    /// PLC 开始接收连接前恢复重载前保存的 DB 数据。
    /// 新配置缩短DB长度时只恢复可容纳的前缀；新增DB块保持初始化值。
    /// </summary>
    private void RestoreRunningDbMemory(
        Rcs4SiemensS7Server server,
        PlcConfigModel plc,
        IEnumerable<PlcDbMemorySnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            if (!server.DbLengths.TryGetValue(snapshot.DbNumber, out int targetLength))
            {
                continue;
            }

            var restoreLength = Math.Min(snapshot.Bytes.Length, targetLength);

            if (restoreLength <= 0)
            {
                continue;
            }

            var restoreBytes = snapshot.Bytes
                .Take(restoreLength)
                .ToArray();

            if (IsPlc2StringDb(plc, snapshot.DbNumber))
            {
                InitializePlc2EmptyStringHeaders(restoreBytes, targetLength);
            }

            try
            {
                var write = server.Write($"DB{snapshot.DbNumber}.0", restoreBytes);

                if (!write.IsSuccess)
                {
                    Log(
                        $"[PLC重载恢复失败] PLC={plc.PlcName} DB{snapshot.DbNumber} Length={restoreLength} Message={write.Message}");
                    continue;
                }

                Log(
                    $"[PLC重载恢复成功] PLC={plc.PlcName} DB{snapshot.DbNumber} Length={restoreLength}");
            }
            catch (Exception ex)
            {
                LogException(
                    $"[PLC重载恢复异常] PLC={plc.PlcName} DB{snapshot.DbNumber} Length={restoreLength}",
                    ex);
            }
        }
    }

    /// <summary>
    /// 停止单台PLC。
    /// </summary>
    public void StopPlc(int plcId)
    {
        lock (_plcLifecycleLock)
        {
            StopPlcCore(plcId);
        }
    }

    private void StopPlcCore(int plcId)
    {
        _runningPlcConfigMap.TryRemove(plcId, out _);

        if (_serverMap.TryRemove(plcId, out var server))
        {
            ClearTriggerDelayStatesByPlcId(plcId);
            try
            {
                server.StopTransport();
                LogTransport($"PLC 虚拟服务已停止，PlcId={plcId}。");
            }
            catch (Exception ex)
            {
                LogTransport($"停止 PLC 异常，PlcId={plcId}。{ex.GetBaseException().Message}");
            }
        }

        DeleteProxyByPlcId(plcId);
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
        lock (_plcLifecycleLock)
        {
            Log($"准备停止全部PLC，当前运行数量：{RunningCount}");

            foreach (var plcId in _serverMap.Keys.ToList())
            {
                StopPlcCore(plcId);
            }

            foreach (var plcId in _proxyMap.Keys.ToList())
            {
                DeleteProxyByPlcId(plcId);
            }

            RefreshAutoWriteLoopState();
            Log($"停止全部PLC完成，当前运行数量：{RunningCount}");
        }
    }

    /// <summary>
    /// 启动单台PLC虚拟服务。
    /// 初始化内存并恢复快照，然后启用回环监听和 Windows 端口代理。
    /// 直连方式在完成相同的内存初始化后启动指定地址上的监听。
    /// </summary>
    private bool StartServer(PlcConfigModel plc, List<PlcDbMemorySnapshot> snapshots)
    {
        if (plc == null)
        {
            return false;
        }

        Rcs4SiemensS7Server server = null;
        bool proxyCreated = false;

        var useDirect = plc.ConnectionMode == PlcConnectionMode.Direct;
        var directAddress = plc.DirectListenAddress?.Trim();
        var proxyPort = GetProxyPort(plc);

        try
        {
            if (!useDirect && !WindowsPortProxyService.Instance.IsAdministrator())
            {
                Log($"PLC虚拟服务启动失败：{plc.PlcName} 创建端口代理需要管理员权限，请以管理员身份运行程序。");
                return false;
            }

            if (!useDirect && string.IsNullOrWhiteSpace(plc.IpAddress))
            {
                Log($"PLC虚拟服务启动失败：{plc.PlcName} 未配置PLC IP。");
                return false;
            }

            if (!IPAddress.TryParse(useDirect ? directAddress : plc.IpAddress.Trim(), out var listenIp) ||
                listenIp.AddressFamily != AddressFamily.InterNetwork ||
                (useDirect && listenIp.Equals(IPAddress.Any)))
            {
                if (useDirect)
                {
                    LogTransport($"[{plc.PlcName}] 直连地址必须是本机 IPv4 地址。当前值 {directAddress}。");
                    return false;
                }

                Log($"PLC虚拟服务启动失败：{plc.PlcName} 代理需要 IPv4 地址：{plc.IpAddress}");
                return false;
            }

            if (!useDirect && (proxyPort <= 0 || proxyPort > 65535))
            {
                Log($"PLC虚拟服务启动失败：{plc.PlcName} 代理端口不正确：{proxyPort}");
                return false;
            }

            if (plc.GetListeningPort() <= 0 || plc.GetListeningPort() > 65535)
            {
                if (useDirect)
                {
                    LogTransport($"[{plc.PlcName}] 直连端口 {plc.DirectListenPort} 超出 1 至 65535。");
                    return false;
                }

                Log($"PLC虚拟服务启动失败：{plc.PlcName} 内部端口不正确：{plc.Port}");
                return false;
            }

            if (!useDirect && plc.Port == proxyPort)
            {
                Log($"PLC虚拟服务启动失败：{plc.PlcName} 内部端口不能和代理端口相同。ProxyPort={proxyPort} InternalPort={plc.Port}");
                return false;
            }

            var listeningEndPoint = new IPEndPoint(useDirect ? listenIp : IPAddress.Loopback, plc.GetListeningPort());
            if (_runningPlcConfigMap.Values.Any(config => listeningEndPoint.Equals(config.GetListeningEndPoint())))
            {
                if (useDirect)
                {
                    LogTransport($"[{plc.PlcName}] 直连地址 {listeningEndPoint} 已分配给另一台仿真 PLC。");
                    return false;
                }

                LogTransport($"[{plc.PlcName}] 内部端口 {plc.Port} 已分配给另一台仿真 PLC。");
                return false;
            }

            var externalAddress = listenIp.ToString();
            if (_proxyMap.Values.Any(proxy => proxy.ListenAddress == externalAddress &&
                                              proxy.ListenPort == (useDirect ? plc.DirectListenPort : proxyPort)))
            {
                if (useDirect)
                {
                    LogTransport($"[{plc.PlcName}] 直连地址 {externalAddress}:{plc.DirectListenPort} 已用作另一台 PLC 的代理入口。");
                    return false;
                }

                LogTransport($"[{plc.PlcName}] 代理地址 {externalAddress}:{proxyPort} 已分配给另一台仿真 PLC。");
                return false;
            }

            if (!useDirect && _runningPlcConfigMap.Values.Any(config =>
                    config.ConnectionMode == PlcConnectionMode.Direct &&
                    config.DirectListenPort == proxyPort &&
                    IPAddress.TryParse(config.DirectListenAddress?.Trim(), out var directIp) &&
                    listenIp.Equals(directIp)))
            {
                LogTransport($"[{plc.PlcName}] 代理地址 {externalAddress}:{proxyPort} 已用作另一台 PLC 的直连入口。");
                return false;
            }

            var plcName = plc.PlcName;
            server = new Rcs4SiemensS7Server(plc.PlcType, message => LogTransport($"[{plcName}] {message}"),
                plc.ConnectTimeOut, plc.ReceiveTimeOut);
            InitServerMemory(server, plc);
            RestoreRunningDbMemory(server, plc, snapshots);
            SelfTestServerMemory(server, plc);

            server.StartTransport(listeningEndPoint.Port, useDirect ? listeningEndPoint.Address : null);

            if (useDirect)
            {
                _runningPlcConfigMap[plc.PlcId] = plc.Clone();
                _serverMap[plc.PlcId] = server;

                LogTransport($"[{plcName}] PLC 虚拟服务已启动，直连 {listenIp}:{plc.DirectListenPort}。");

                return true;
            }

            Log($"内部PLC服务启动成功：PLC={plc.PlcName} Internal={ProxyConnectAddress}:{plc.Port}");

            Log($"准备创建端口代理：{listenIp}:{proxyPort} -> {ProxyConnectAddress}:{plc.Port}");

            WindowsPortProxyService.Instance.EnsureFirewallRule(proxyPort);

            WindowsPortProxyService.Instance.AddOrUpdateProxy(
                externalAddress,
                proxyPort,
                ProxyConnectAddress,
                plc.Port);
            proxyCreated = true;

            var runningConfig = plc.Clone();

            _proxyMap[plc.PlcId] = new PlcProxyInfo
            {
                ListenAddress = externalAddress,
                ListenPort = proxyPort,
                ConnectAddress = ProxyConnectAddress,
                ConnectPort = plc.Port
            };

            _runningPlcConfigMap[plc.PlcId] = runningConfig;
            _serverMap[plc.PlcId] = server;

            LogTransport($"[{plcName}] PLC 虚拟服务已启动，外部 {listenIp}:{proxyPort}，内部 {ProxyConnectAddress}:{plc.Port}。");

            return true;
        }
        catch (Exception ex)
        {
            if (useDirect)
            {
                LogTransport($"[{plc.PlcName}] PLC 直连启动失败，地址 {directAddress}:{plc.DirectListenPort}。" +
                             ex.GetBaseException().Message);

                if (ex.GetBaseException() is SocketException socketError)
                {
                    if (socketError.SocketErrorCode == SocketError.AddressNotAvailable)
                    {
                        LogTransport($"[{plc.PlcName}] 请确认 Windows 本机网卡已绑定 {directAddress}。");
                    }
                    else if (socketError.SocketErrorCode == SocketError.AddressAlreadyInUse)
                    {
                        LogTransport($"[{plc.PlcName}] 请检查 {directAddress}:{plc.DirectListenPort} 的旧端口代理或其他监听。");
                    }
                }
            }
            else
            {
                LogTransport($"[{plc.PlcName}] PLC 虚拟服务启动失败，外部 {plc.IpAddress}:{proxyPort}，内部端口 {plc.Port}。" +
                             ex.GetBaseException().Message);
            }

            try
            {
                server?.StopTransport();
            }
            catch (Exception closeEx)
            {
                LogException($"启动失败后关闭Server异常：{plc.PlcName}", closeEx);
            }

            try
            {
                if (proxyCreated)
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
    /// 删除当前会话为该 PLC 建立的端口代理。
    /// </summary>
    private void DeleteProxyByPlcId(int plcId)
    {
        if (_proxyMap.TryRemove(plcId, out var proxy))
        {
            DeleteProxy(proxy.ListenAddress, proxy.ListenPort, proxy.ConnectAddress, proxy.ConnectPort, plcId);
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
    private static void SelfTestServerMemory(Rcs4SiemensS7Server server, PlcConfigModel plc)
    {
        foreach (var db in server.DbLengths)
        {
            int dbNumber = db.Key;
            int length = db.Value;
            var address = $"DB{dbNumber}.{length - 1}";
            var read = server.Read(address, 1);
            if (!read.IsSuccess || read.Content?.Length != 1)
            {
                throw new InvalidOperationException($"{plc.PlcName} 的 DB{dbNumber} 末字节自检失败。{read.Message}");
            }
        }

        Log($"PLC虚拟服务DB自检成功：{plc.PlcName}，DB 数量 {server.DbLengths.Count}。");
    }

    /// <summary>
    /// 初始化虚拟PLC内部内存。
    /// </summary>
    private void InitServerMemory(Rcs4SiemensS7Server server, PlcConfigModel plc)
    {
        Log($"InitServerMemory 开始：{plc?.PlcName}");

        PlcDbBlockService.Instance.InitializeServerDbBlocks(server, plc);

        InitPlc2StringMemory(server, plc);

        Log($"InitServerMemory 结束：{plc?.PlcName}");
    }

    private static bool IsPlc2StringDb(PlcConfigModel plc, int dbNumber)
    {
        return dbNumber == Plc2StringDbNumber && plc != null &&
               (string.Equals(plc.PlcName?.Trim(), "PLC2", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(plc.IpAddress?.Trim(), "192.168.1.4", StringComparison.Ordinal) &&
                 GetProxyPort(plc) == 102));
    }

    private static void InitPlc2StringMemory(Rcs4SiemensS7Server server, PlcConfigModel plc)
    {
        if (!IsPlc2StringDb(plc, Plc2StringDbNumber))
        {
            return;
        }

        if (!server.DbLengths.TryGetValue(Plc2StringDbNumber, out int dbLength))
        {
            return;
        }

        var address = $"DB{Plc2StringDbNumber}.0";
        var lastOffset = Plc2StringOffsets[Plc2StringOffsets.Length - 1];
        var length = Math.Min(dbLength, lastOffset + Plc2StringMaxLength + 2);
        var read = server.Read(address, (ushort)length);

        if (!read.IsSuccess || read.Content?.Length != length)
        {
            throw new InvalidOperationException($"读取 {address} 以初始化STRING长度头失败：{read.Message}");
        }

        if (!InitializePlc2EmptyStringHeaders(read.Content, dbLength))
        {
            return;
        }

        var write = server.Write(address, read.Content);

        if (!write.IsSuccess)
        {
            throw new InvalidOperationException($"初始化 {address} 的STRING长度头失败：{write.Message}");
        }
    }

    private static bool InitializePlc2EmptyStringHeaders(byte[] bytes, int dbLength)
    {
        var initialized = false;

        foreach (var offset in Plc2StringOffsets)
        {
            if (offset + Plc2StringMaxLength + 2 > dbLength || offset >= bytes.Length)
            {
                break;
            }

            // 恢复旧快照时，前缀末尾可能只覆盖长度头的第一个字节。
            if (bytes[offset] == 0 && (offset + 1 == bytes.Length || bytes[offset + 1] == 0))
            {
                bytes[offset] = Plc2StringMaxLength;
                initialized = true;
            }
        }

        return initialized;
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
            var scanSequence = Interlocked.Increment(ref _autoWriteScanSequence);
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

                    if (_reloadingPlcIds.ContainsKey(plc.PlcId))
                    {
                        continue;
                    }

                    if (plc.PollIntervalMs > 0)
                    {
                        delay = hasCustomDelay
                            ? Math.Min(delay, plc.PollIntervalMs)
                            : plc.PollIntervalMs;

                        hasCustomDelay = true;
                    }

                    ProcessAutoWriteRules(plc, scanSequence);
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
    private void ProcessAutoWriteRules(PlcConfigModel plc, long scanSequence)
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
                ProcessAutoWriteRule(server, plc, dbBlock, rule, scanSequence);
            }
        }
    }

    /// <summary>
    /// 执行单条自动写入规则。
    /// 支持多触发、多写入。规则统一按“全部启用触发条件满足”判断。
    /// 如果规则启用延时：全部条件满足后开始计时，连续满足到规则延时时间后才执行写入动作。
    /// </summary>
    private void ProcessAutoWriteRule(SiemensS7Server server, PlcConfigModel plc, PlcDbBlockModel dbBlock,
        PlcAutoWriteRuleModel rule, long scanSequence)
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
            var executionHistory = _autoWriteRuleExecutions;
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

            if (isTriggered)
            {
                var history = executionHistory.GetValue(server,
                    _ => new ConcurrentDictionary<(int DbNumber, int RuleId), PlcAutoWriteRuleExecution>());
                var executedAtUtc = DateTime.UtcNow;
                history.AddOrUpdate(
                    (dbBlock.DbNumber, rule.RuleId),
                    new PlcAutoWriteRuleExecution(1, executedAtUtc, scanSequence),
                    (_, previous) =>
                    {
                        if (previous.LastTriggeredScan >= scanSequence)
                        {
                            return previous;
                        }

                        // 连续扫描都触发时沿用本次执行，条件中断后重新触发才累计。
                        return previous.LastTriggeredScan == scanSequence - 1
                            ? new PlcAutoWriteRuleExecution(previous.Count, previous.ExecutedAtUtc, scanSequence)
                            : new PlcAutoWriteRuleExecution(previous.Count + 1, executedAtUtc, scanSequence);
                    });
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
        return
            $"{plc?.PlcId}_{dbBlock?.DbNumber}_{rule?.RuleId}_{trigger?.TriggerId}_{readAddress}_{trigger?.TriggerValue}_{trigger?.DelayMilliseconds}";
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
    }

    private static void LogTransport(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        lock (TransportLogLock)
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Vda5050AgvSimulator", "Logs");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "PlcTransport.log");
                if (IoFile.Exists(path) && new FileInfo(path).Length >= TransportLogLimitBytes)
                {
                    var previousPath = path + ".1";
                    IoFile.Delete(previousPath);
                    IoFile.Move(path, previousPath);
                }

                IoFile.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"{line} 写入 PLC 日志失败。{ex.Message}");
            }
        }
    }

    private static void LogException(string title, Exception ex)
    {
        try
        {
            if (ex == null)
            {
                return;
            }

            if (ex.InnerException == null)
            {
                return;
            }
        }
        catch
        {
            // ignored
        }
    }

    private readonly struct PlcAutoWriteRuleExecution
    {
        public PlcAutoWriteRuleExecution(long count, DateTime executedAtUtc, long lastTriggeredScan)
        {
            Count = count;
            ExecutedAtUtc = executedAtUtc;
            LastTriggeredScan = lastTriggeredScan;
        }

        public long Count { get; }
        public DateTime ExecutedAtUtc { get; }
        public long LastTriggeredScan { get; }
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

    private class PlcDbMemorySnapshot
    {
        public int DbNumber { get; set; }

        public byte[] Bytes { get; set; }
    }

    private class PlcProxyInfo
    {
        public string ListenAddress { get; set; }

        public int ListenPort { get; set; }

        public string ConnectAddress { get; set; }

        public int ConnectPort { get; set; }
    }
}