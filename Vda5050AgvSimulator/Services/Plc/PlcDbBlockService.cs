#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using HslCommunication.Profinet.Siemens;
using Vda5050AgvSimulator.Models.Plc;

namespace Vda5050AgvSimulator.Services.Plc;

/// <summary>
/// PLC DB块服务。
/// 负责虚拟PLC DB块配置、推导、初始化。
/// </summary>
public class PlcDbBlockService
{
    public static PlcDbBlockService Instance { get; } = new();

    private const int MaxS7DbLength = 1 << 21;

    private static readonly Regex DbAddressRegex =
        new(@"^%?DB(?<db>\d+)\.(?:DBX)?(?<byte>\d+)(?:\.(?<bit>[0-7])(?::BOOL)?)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DbAddressTemplateRegex =
        new(@"^%?DB(?<db>\{DB\}|\*)\.(?:DBX)?(?<byte>\d+)(?:\.(?<bit>[0-7])(?::BOOL)?)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DbOffsetAddressRegex =
        new(@"^%?(?:DBX)?(?<byte>\d+)(?:\.(?<bit>[0-7])(?::BOOL)?)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private PlcDbBlockService()
    {
    }

    /// <summary>
    /// 规范化PLC的DB块配置。
    /// 不再默认添加DB1500和DB2090。
    /// 重点：保存配置时必须保留手动配置的DB块，即使该DB块 IsEnabled=false。
    /// 运行时真正创建DB内存时，再只取启用的DB块。
    /// </summary>
    public void NormalizeDbBlocks(PlcConfigModel plc)
    {
        if (plc == null)
        {
            return;
        }

        plc.DbBlocks ??= [];

        var configBlocks = GetConfigDbBlocks(plc);

        plc.DbBlocks.Clear();

        foreach (var block in configBlocks)
        {
            plc.DbBlocks.Add(block);
        }
    }

    /// <summary>
    /// 初始化 SiemensS7Server 的 DB 内存区。
    /// 重点：HSL 的 SiemensS7Server 需要先 AddDbBlock，再读写 DB 数据。
    /// </summary>
    public void InitializeServerDbBlocks(SiemensS7Server server, PlcConfigModel plc)
    {
        if (server == null || plc == null)
        {
            return;
        }

        var blocks = GetEffectiveDbBlocks(plc)
            .Where(x => x != null)
            .Where(x => x.IsEnabled)
            .Where(x => x.DbNumber > 0)
            .ToList();

        if (blocks.Count == 0)
        {
            throw new InvalidOperationException($@"{plc.PlcName} 没有可用的DB块配置。请先手动新增DB块，或在DB块中绑定全局自动写入规则。");
        }

        foreach (var block in blocks)
        {
            var length = NormalizeLength(block.Length);
            var dbNumber = block.DbNumber;

            if (dbNumber > ushort.MaxValue)
            {
                throw new InvalidOperationException($"{plc.PlcName} 的 DB{dbNumber} 超过 S7 DB 编号范围。");
            }

            try
            {
                AddDbBlockToServer(server, dbNumber, length);

                var write = server.Write($"DB{dbNumber}.0", new byte[length]);
                if (!write.IsSuccess)
                {
                    throw new InvalidOperationException(write.Message);
                }

                if (server is Rcs4SiemensS7Server s7Server)
                {
                    s7Server.RegisterDbLength(dbNumber, length);
                }
            }
            catch (Exception ex)
            {
                var cause = ex.GetBaseException();
                throw new InvalidOperationException(
                    $"{plc.PlcName} 的 DB{dbNumber} 初始化失败，长度 {length} 字节。{cause.Message}", ex);
            }
        }

        InitDefaultValues(server, plc);
    }

    /// <summary>
    /// 兼容不同版本 HslCommunication 的 AddDbBlock 方法。
    /// 有些版本是 AddDbBlock(int db)
    /// 有些版本是 AddDbBlock(int db, int length)
    /// </summary>
    private static void AddDbBlockToServer(SiemensS7Server server, int dbNumber, int length)
    {
        if (server == null)
        {
            return;
        }

        var serverType = server.GetType();

        var addWithLength = serverType.GetMethod(
            "AddDbBlock",
            [
                typeof(int),
                typeof(int)
            ]);

        if (addWithLength != null)
        {
            addWithLength.Invoke(server, [
                dbNumber,
                length
            ]);

            return;
        }

        var addWithUShortLength = serverType.GetMethod(
            "AddDbBlock",
            [
                typeof(int),
                typeof(ushort)
            ]);

        if (addWithUShortLength != null)
        {
            addWithUShortLength.Invoke(server, [
                dbNumber,
                (ushort)Math.Min(length, ushort.MaxValue)
            ]);

            return;
        }

        var addOnlyDbNumber = serverType.GetMethod(
            "AddDbBlock",
            [
                typeof(int)
            ]);

        if (addOnlyDbNumber == null)
        {
            throw new MissingMethodException(
                "当前 HslCommunication 版本的 SiemensS7Server 未找到 AddDbBlock 方法，请升级 HslCommunication。");
        }

        addOnlyDbNumber.Invoke(server, [
            dbNumber
        ]);
    }

    /// <summary>
    /// 获取用于保存配置的DB块列表。
    /// 与 GetEffectiveDbBlocks 不同：这里会保留手动配置但未启用的DB块，避免保存时丢失配置。
    /// </summary>
    public List<PlcDbBlockModel> GetConfigDbBlocks(PlcConfigModel plc)
    {
        var dict = new Dictionary<int, PlcDbBlockModel>();

        if (plc == null)
        {
            return dict.Values
                .OrderBy(x => x.DbNumber)
                .ToList();
        }

        if (plc.DbBlocks != null)
        {
            foreach (var block in plc.DbBlocks)
            {
                if (block == null)
                {
                    continue;
                }

                if (block.DbNumber <= 0)
                {
                    continue;
                }

                AddOrMergeConfigBlock(dict, block);
            }
        }

        AddBlocksFromRules(dict, plc);

        return dict.Values
            .OrderBy(x => x.DbNumber)
            .ToList();
    }

    /// <summary>
    /// 获取运行时最终有效DB块列表。
    /// 只包含启用的手动DB块，以及根据启用DB块中启用的自动写入规则推导出来的DB块。
    /// </summary>
    public List<PlcDbBlockModel> GetEffectiveDbBlocks(PlcConfigModel plc)
    {
        var dict = new Dictionary<int, PlcDbBlockModel>();

        if (plc == null)
        {
            return dict.Values
                .OrderBy(x => x.DbNumber)
                .ToList();
        }

        if (plc.DbBlocks != null)
        {
            foreach (var block in plc.DbBlocks)
            {
                if (block == null)
                {
                    continue;
                }

                if (!block.IsEnabled)
                {
                    continue;
                }

                if (block.DbNumber <= 0)
                {
                    continue;
                }

                AddOrExpandBlock(
                    dict,
                    block.DbNumber,
                    block.Length,
                    string.IsNullOrWhiteSpace(block.BlockName) ? $"DB{block.DbNumber}" : block.BlockName,
                    block.Remark);

                if (dict.TryGetValue(block.DbNumber, out var effectiveBlock))
                {
                    CopyDbBlockAutoWriteConfig(effectiveBlock, block);
                }
            }
        }

        AddBlocksFromRules(dict, plc);

        return dict.Values
            .OrderBy(x => x.DbNumber)
            .ToList();
    }

    /// <summary>
    /// 根据DB块绑定的全局自动写入规则地址自动补充DB块。
    /// DB块只保存 RuleId，规则实体从 PlcAutoWriteRuleStoreService 单例中读取。
    /// </summary>
    private void AddBlocksFromRules(Dictionary<int, PlcDbBlockModel> dict, PlcConfigModel plc)
    {
        if (dict == null || plc == null || plc.DbBlocks == null)
        {
            return;
        }

        PlcAutoWriteRuleStoreService.Instance.EnsureLoadedFromConfig();

        foreach (var dbBlock in plc.DbBlocks)
        {
            if (dbBlock == null)
            {
                continue;
            }

            if (!dbBlock.HasEnabledAutoWriteRules())
            {
                continue;
            }

            var rules = PlcAutoWriteRuleStoreService.Instance.GetEnabledRulesByRefs(dbBlock.AutoWriteRuleList);

            foreach (var rule in rules)
            {
                if (rule == null)
                {
                    continue;
                }

                AddBlocksFromRuleTriggers(dict, dbBlock, rule);

                AddBlocksFromRuleWriteItems(dict, dbBlock, rule);
            }
        }
    }

    /// <summary>
    /// 根据规则触发条件地址自动补充DB块。
    /// </summary>
    private void AddBlocksFromRuleTriggers(Dictionary<int, PlcDbBlockModel> dict, PlcDbBlockModel dbBlock,
        PlcAutoWriteRuleModel rule)
    {
        if (dict == null || dbBlock == null || rule == null || rule.Triggers == null)
        {
            return;
        }

        foreach (var trigger in rule.Triggers)
        {
            if (trigger == null)
            {
                continue;
            }

            if (!trigger.IsEnabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(trigger.ReadAddress))
            {
                continue;
            }

            AddBlockFromAddress(
                dict,
                dbBlock.DbNumber,
                trigger.ReadAddress,
                $"根据DB{dbBlock.DbNumber}绑定规则触发条件自动扩展。规则={rule.RuleName} 条件={trigger.TriggerName}");
        }
    }

    /// <summary>
    /// 根据规则写入动作地址自动补充DB块。
    /// </summary>
    private void AddBlocksFromRuleWriteItems(Dictionary<int, PlcDbBlockModel> dict, PlcDbBlockModel dbBlock,
        PlcAutoWriteRuleModel rule)
    {
        if (dict == null || dbBlock == null || rule == null || rule.WriteItems == null)
        {
            return;
        }

        foreach (var writeItem in rule.WriteItems)
        {
            if (writeItem == null)
            {
                continue;
            }

            if (!writeItem.IsEnabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(writeItem.WriteAddress))
            {
                continue;
            }

            AddBlockFromAddress(
                dict,
                dbBlock.DbNumber,
                writeItem.WriteAddress,
                $"根据DB{dbBlock.DbNumber}绑定规则写入动作自动扩展。规则={rule.RuleName} 动作={writeItem.WriteName}");
        }
    }

    /// <summary>
    /// 根据地址自动补充DB块。
    /// 例如 5.0 会按当前绑定DB块推导该DB块至少需要覆盖第5字节。
    /// </summary>
    private void AddBlockFromAddress(Dictionary<int, PlcDbBlockModel> dict, int dbNumber, string address, string remark)
    {
        if (dict == null || dbNumber <= 0)
        {
            return;
        }

        if (!TryParseDbAddressOrTemplate(address, dbNumber, out _, out var byteOffset, out _))
        {
            return;
        }

        var requiredLength = Math.Min(byteOffset + 32, MaxS7DbLength);

        AddOrExpandBlock(dict, dbNumber, requiredLength, $"DB{dbNumber}", remark);
    }

    /// <summary>
    /// 保留手动DB块上的自动写入开关、全局规则引用和复位配置。
    /// NormalizeDbBlocks 会重建有效DB块列表，因此这里必须把引用列表和复位配置带过去。
    /// </summary>
    private void CopyDbBlockAutoWriteConfig(PlcDbBlockModel target, PlcDbBlockModel source)
    {
        if (target == null || source == null)
        {
            return;
        }

        target.AutoWriteEnabled = source.AutoWriteEnabled;

        target.AutoWriteRuleList ??= [];
        target.AutoWriteRuleList.Clear();

        if (source.AutoWriteRuleList != null)
        {
            foreach (var ruleRef in source.AutoWriteRuleList)
            {
                if (ruleRef == null)
                {
                    continue;
                }

                target.AutoWriteRuleList.Add(ruleRef.Clone());
            }
        }

        target.ResetItems ??= [];
        target.ResetItems.Clear();

        if (source.ResetItems == null)
        {
            return;
        }

        foreach (var resetItem in source.ResetItems)
        {
            if (resetItem == null)
            {
                continue;
            }

            target.ResetItems.Add(resetItem.Clone());
        }
    }

    /// <summary>
    /// 添加或合并用于保存配置的手动DB块。
    /// 这里不因为 IsEnabled=false 丢弃DB块。
    /// </summary>
    private void AddOrMergeConfigBlock(Dictionary<int, PlcDbBlockModel> dict, PlcDbBlockModel source)
    {
        if (dict == null || source == null)
        {
            return;
        }

        if (source.DbNumber <= 0)
        {
            return;
        }

        var clone = source.Clone();

        clone.Length = NormalizeLength(clone.Length);

        if (string.IsNullOrWhiteSpace(clone.BlockName))
        {
            clone.BlockName = $"DB{clone.DbNumber}";
        }

        if (clone.Remark == null)
        {
            clone.Remark = string.Empty;
        }

        if (!dict.TryGetValue(clone.DbNumber, out var existingBlock))
        {
            dict[clone.DbNumber] = clone;
            return;
        }

        if (existingBlock.Length < clone.Length)
        {
            existingBlock.Length = clone.Length;
        }

        existingBlock.IsEnabled = existingBlock.IsEnabled || clone.IsEnabled;

        if (string.IsNullOrWhiteSpace(existingBlock.BlockName))
        {
            existingBlock.BlockName = clone.BlockName;
        }

        if (string.IsNullOrWhiteSpace(existingBlock.Remark))
        {
            existingBlock.Remark = clone.Remark;
        }

        if (existingBlock.AutoWriteRuleList == null || existingBlock.AutoWriteRuleList.Count == 0)
        {
            CopyDbBlockAutoWriteConfig(existingBlock, clone);
        }
    }

    /// <summary>
    /// 添加或扩展DB块长度。
    /// </summary>
    private void AddOrExpandBlock(
        Dictionary<int, PlcDbBlockModel> dict,
        int dbNumber,
        int length,
        string blockName,
        string remark)
    {
        if (dict == null)
        {
            return;
        }

        if (dbNumber <= 0)
        {
            return;
        }

        var normalizedLength = NormalizeLength(length);

        if (dict.TryGetValue(dbNumber, out var existingBlock))
        {
            if (existingBlock.Length < normalizedLength)
            {
                existingBlock.Length = normalizedLength;
            }

            if (string.IsNullOrWhiteSpace(existingBlock.BlockName))
            {
                existingBlock.BlockName = blockName;
            }

            if (string.IsNullOrWhiteSpace(existingBlock.Remark))
            {
                existingBlock.Remark = remark ?? string.Empty;
            }

            return;
        }

        dict[dbNumber] = new PlcDbBlockModel
        {
            DbNumber = dbNumber,
            Length = normalizedLength,
            IsEnabled = true,
            BlockName = string.IsNullOrWhiteSpace(blockName) ? $"DB{dbNumber}" : blockName,
            Remark = remark ?? string.Empty
        };
    }

    /// <summary>
    /// 初始化默认值。
    /// 注意：不主动创建DB1500。
    /// 只有用户手动配置了DB1500并且长度覆盖DBW6时，才初始化DB1500.DBW6。
    /// 多触发多写入版本：
    /// 只初始化每个写入动作的 ResetValue。
    /// </summary>
    private void InitDefaultValues(SiemensS7Server server, PlcConfigModel plc)
    {
        if (server == null || plc == null)
        {
            return;
        }

        InitDb1500DefaultValues(server, plc);

        InitAutoWriteRuleDefaultValues(server, plc);
    }

    /// <summary>
    /// 初始化DB1500默认值。
    /// </summary>
    private void InitDb1500DefaultValues(SiemensS7Server server, PlcConfigModel plc)
    {
        if (server == null || plc == null)
        {
            return;
        }

        try
        {
            if (HasDbBlock(plc, 1500, 8))
            {
                var writeAgvModuleType = server.Write("DB1500.6", (short)0);
            }
        }
        catch (Exception)
        {
            // ignored
        }
    }

    /// <summary>
    /// 初始化自动写入规则的默认写入值。
    /// 多写入动作版本：
    /// 当写入动作配置 ResetWhenNotTrigger=true 时，启动时先写 ResetValue。
    /// </summary>
    private void InitAutoWriteRuleDefaultValues(SiemensS7Server server, PlcConfigModel plc)
    {
        if (server == null || plc == null || !plc.AutoWriteEnabled || plc.DbBlocks == null)
        {
            return;
        }

        PlcAutoWriteRuleStoreService.Instance.EnsureLoadedFromConfig();

        foreach (var dbBlock in plc.DbBlocks)
        {
            if (dbBlock == null)
            {
                continue;
            }

            if (!dbBlock.HasEnabledAutoWriteRules())
            {
                continue;
            }

            var rules = PlcAutoWriteRuleStoreService.Instance.GetEnabledRulesByRefs(dbBlock.AutoWriteRuleList);

            foreach (var rule in rules)
            {
                if (rule == null)
                {
                    continue;
                }

                if (rule.WriteItems == null)
                {
                    continue;
                }

                foreach (var writeItem in rule.WriteItems)
                {
                    InitAutoWriteItemDefaultValue(server, dbBlock, rule, writeItem);
                }
            }
        }
    }

    /// <summary>
    /// 初始化单个写入动作默认值。
    /// </summary>
    private void InitAutoWriteItemDefaultValue(
        SiemensS7Server server,
        PlcDbBlockModel dbBlock,
        PlcAutoWriteRuleModel rule,
        PlcAutoWriteItemModel writeItem)
    {
        if (server == null || dbBlock == null || rule == null || writeItem == null)
        {
            return;
        }

        if (!writeItem.IsEnabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(writeItem.WriteAddress))
        {
            return;
        }

        if (!writeItem.ResetWhenNotTrigger)
        {
            return;
        }

        if (!IsBoolDbAddressOrTemplate(writeItem.WriteAddress))
        {
            return;
        }

        var resolvedAddress = ResolveDbAddressForDbBlock(writeItem.WriteAddress, dbBlock.DbNumber);

        try
        {
            var write = server.Write(resolvedAddress, writeItem.ResetValue);

            if (!write.IsSuccess)
            {
                return;
            }
        }
        catch (Exception)
        {
            // ignored
        }
    }

    /// <summary>
    /// 判断PLC是否有指定DB块，并且长度覆盖某个字节。
    /// </summary>
    private bool HasDbBlock(PlcConfigModel plc, int dbNumber, int minLength)
    {
        if (plc == null)
        {
            return false;
        }

        var blocks = GetEffectiveDbBlocks(plc);

        return blocks.Any(x =>
            x.IsEnabled &&
            x.DbNumber == dbNumber &&
            x.Length >= minLength);
    }

    /// <summary>
    /// 判断是否为Bool类型DB地址。
    /// Bool地址必须包含bit位，例如：
    /// DB1500.0.0
    /// DB1500.2.0
    /// DB2090.200.3
    /// </summary>
    public bool IsBoolDbAddress(string address)
    {
        return TryParseDbAddress(address, out _, out _, out var bitOffset) && bitOffset.HasValue;
    }

    /// <summary>
    /// 判断是否为Bool类型DB地址或DB模板地址。
    /// 全局规则建议直接使用 字节.位，例如 5.0。
    /// 执行时会自动组合成当前绑定DB块号，例如 DB2090.5.0。
    /// 兼容 DB{DB}.5.0、DB*.5.0 和旧的 DB1500.5.0。
    /// </summary>
    public bool IsBoolDbAddressOrTemplate(string address)
    {
        if (TryParseDbAddress(address, out _, out _, out var bitOffset))
        {
            return bitOffset.HasValue;
        }

        if (TryParseDbAddressTemplate(address, out _, out bitOffset))
        {
            return bitOffset.HasValue;
        }

        return TryParseDbOffsetAddress(address, out _, out bitOffset) && bitOffset.HasValue;
    }

    /// <summary>
    /// 将全局规则地址解析为指定DB块的实际地址。
    /// 支持：
    /// 1. 5.0 这种偏移地址。推荐全局规则使用这种格式。
    /// 2. DB{DB}.5.0 / DB*.5.0 这种模板地址。
    /// 3. DB1500.5.0 这种旧地址。旧地址执行时只取字节和位，DB号替换为当前绑定DB块号。
    /// </summary>
    public string ResolveDbAddressForDbBlock(string address, int dbNumber)
    {
        if (dbNumber <= 0)
        {
            return address?.Trim() ?? string.Empty;
        }

        if (!TryParseDbAddressOrTemplate(address, dbNumber, out _, out var byteOffset, out var bitOffset))
        {
            return address?.Trim() ?? string.Empty;
        }

        return bitOffset.HasValue
            ? $"DB{dbNumber}.{byteOffset}.{bitOffset.Value}"
            : $"DB{dbNumber}.{byteOffset}";
    }

    /// <summary>
    /// 解析DB地址或DB模板地址。
    /// </summary>
    public bool TryParseDbAddressOrTemplate(
        string address,
        int defaultDbNumber,
        out int dbNumber,
        out int byteOffset,
        out int? bitOffset)
    {
        if (TryParseDbAddress(address, out dbNumber, out byteOffset, out bitOffset))
        {
            return true;
        }

        if (TryParseDbAddressTemplate(address, out byteOffset, out bitOffset))
        {
            dbNumber = defaultDbNumber;

            return dbNumber is > 0 and <= ushort.MaxValue;
        }

        if (TryParseDbOffsetAddress(address, out byteOffset, out bitOffset))
        {
            dbNumber = defaultDbNumber;

            return dbNumber is > 0 and <= ushort.MaxValue;
        }

        dbNumber = 0;
        byteOffset = 0;
        bitOffset = null;
        return false;
    }

    /// <summary>
    /// 解析DB模板地址。
    /// 支持 DB{DB}.0.0 和 DB*.0.0。
    /// </summary>
    private bool TryParseDbAddressTemplate(string address, out int byteOffset, out int? bitOffset)
    {
        byteOffset = 0;
        bitOffset = null;

        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var text = address.Trim();

        var match = DbAddressTemplateRegex.Match(text);

        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["byte"].Value, out byteOffset))
        {
            return false;
        }

        if (match.Groups["bit"].Success)
        {
            if (int.TryParse(match.Groups["bit"].Value, out var bit))
            {
                bitOffset = bit;
            }
        }

        return byteOffset is >= 0 and < MaxS7DbLength;
    }

    /// <summary>
    /// 解析省略DB块号的偏移地址。
    /// 支持：
    /// 5
    /// 5.0
    /// 200.3
    /// </summary>
    private bool TryParseDbOffsetAddress(string address, out int byteOffset, out int? bitOffset)
    {
        byteOffset = 0;
        bitOffset = null;

        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var text = address.Trim();

        var match = DbOffsetAddressRegex.Match(text);

        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["byte"].Value, out byteOffset))
        {
            return false;
        }

        if (match.Groups["bit"].Success)
        {
            if (int.TryParse(match.Groups["bit"].Value, out var bit))
            {
                bitOffset = bit;
            }
        }

        return byteOffset is >= 0 and < MaxS7DbLength;
    }

    /// <summary>
    /// 解析DB地址。
    /// 支持：
    /// DB1500.0
    /// DB1500.6
    /// DB1500.0.0
    /// DB1500.2.0
    /// </summary>
    public bool TryParseDbAddress(string address, out int dbNumber, out int byteOffset, out int? bitOffset)
    {
        dbNumber = 0;
        byteOffset = 0;
        bitOffset = null;

        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var text = address.Trim();

        var match = DbAddressRegex.Match(text);

        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["db"].Value, out dbNumber))
        {
            return false;
        }

        if (!int.TryParse(match.Groups["byte"].Value, out byteOffset))
        {
            return false;
        }

        if (match.Groups["bit"].Success)
        {
            if (int.TryParse(match.Groups["bit"].Value, out var bit))
            {
                bitOffset = bit;
            }
        }

        return (dbNumber is > 0 and <= ushort.MaxValue) && (byteOffset is >= 0 and < MaxS7DbLength);
    }

    /// <summary>
    /// DB块长度规范化。
    /// 最小256字节，按256字节对齐。
    /// </summary>
    private int NormalizeLength(int length)
    {
        if (length > MaxS7DbLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "DB 长度超出 S7ANY 地址范围。");
        }

        const int alignment = 256;
        return (Math.Max(length, alignment) + alignment - 1) / alignment * alignment;
    }

    /// <summary>
    /// 创建默认DB块集合。
    /// 当前不再默认创建DB1500 / DB2090。
    /// </summary>
    public ObservableCollection<PlcDbBlockModel> CreateDefaultDbBlocks()
    {
        return [];
    }
}
