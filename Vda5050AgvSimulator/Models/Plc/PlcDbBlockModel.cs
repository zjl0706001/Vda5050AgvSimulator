#nullable disable

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Vda5050AgvSimulator.Models.Plc;

/// <summary>
/// DB块绑定的全局自动写入规则引用。
/// 注意：这里不保存 PlcAutoWriteRuleModel 实体，只保存 RuleId。
/// 真正执行时，根据 RuleId 去全局规则库 PlcAutoWriteRuleStoreService 中查找规则。
/// </summary>
public class PlcDbBlockAutoWriteRuleRefModel : INotifyPropertyChanged
{
    /// <summary>
    /// 当前DB块内的规则引用ID。
    /// 只用于DB块内部排序和删除。
    /// </summary>
    public int RuleRefId
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 是否启用该规则引用。
    /// DB块启用、DB块自动写入启用、规则引用启用、全局规则启用时，才会执行。
    /// </summary>
    public bool IsEnabled
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = true;

    /// <summary>
    /// 引用的全局自动写入规则ID。
    /// 0表示未选择规则。
    /// </summary>
    public int RuleId
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 规则名称缓存。
    /// 仅用于显示，不作为执行依据。
    /// 执行时仍然以 RuleId 去全局规则库查找。
    /// </summary>
    public string RuleName
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    /// <summary>
    /// 执行顺序。
    /// 一个DB块绑定多个规则时，按 SortIndex 从小到大执行。
    /// </summary>
    public int SortIndex
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 备注。
    /// </summary>
    public string Remark
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    public PlcDbBlockAutoWriteRuleRefModel Clone()
    {
        return new PlcDbBlockAutoWriteRuleRefModel
        {
            RuleRefId = RuleRefId,
            IsEnabled = IsEnabled,
            RuleId = RuleId,
            RuleName = RuleName,
            SortIndex = SortIndex,
            Remark = Remark
        };
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}


/// <summary>
/// DB块复位配置项。
/// 用于点击“复位当前DB”时，把指定Bool地址写成指定复位值。
/// 地址支持：5.0、DB{DB}.5.0、DB*.5.0、DB1500.5.0。
/// 执行时会按当前DB块号解析为实际地址。
/// 复位项实体由全局复位模板共用，不再按每个DB块分别维护。
/// </summary>
public class PlcDbBlockResetItemModel : INotifyPropertyChanged
{
    /// <summary>
    /// 复位项ID。
    /// 只用于全局复位模板内部排序和删除。
    /// </summary>
    public int ResetItemId
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 复位项名称。
    /// </summary>
    public string ResetName
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "复位项";

    /// <summary>
    /// 是否启用该复位项。
    /// </summary>
    public bool IsEnabled
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = true;

    /// <summary>
    /// 复位地址。
    /// 推荐只写偏移地址，例如 2.0。
    /// 执行时会自动组合为当前DB块号，例如 DB2090.2.0。
    /// </summary>
    public string ResetAddress
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "0.0";

    /// <summary>
    /// 复位值。
    /// </summary>
    public bool ResetValue
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 备注。
    /// </summary>
    public string Remark
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    public PlcDbBlockResetItemModel Clone()
    {
        return new PlcDbBlockResetItemModel
        {
            ResetItemId = ResetItemId,
            ResetName = ResetName,
            IsEnabled = IsEnabled,
            ResetAddress = ResetAddress,
            ResetValue = ResetValue,
            Remark = Remark
        };
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}


/// <summary>
/// DB块协议看板配置项。
/// 用于在协议看板里展示当前DB块的信号定义，例如：LifeBit / Bool / DB{DB}.DBX0.0 / 系统健康。
/// 注意：该配置只负责协议展示，不参与自动写入和复位执行。
/// </summary>
public class PlcDbBlockProtocolItemModel : INotifyPropertyChanged
{
    /// <summary>
    /// 协议项ID。
    /// 只用于当前DB块内部排序和删除。
    /// </summary>
    public int ProtocolItemId
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 是否启用该协议项。
    /// </summary>
    public bool IsEnabled
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = true;

    /// <summary>
    /// 信号名称。
    /// </summary>
    public string SignalName
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "信号";

    /// <summary>
    /// 数据类型。
    /// 例如 Bool、Byte、Word、Int、DInt、Real、String、ByteArray。
    /// </summary>
    public string DataType
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "Bool";

    /// <summary>
    /// 偏移量地址或显示地址。
    /// 建议写 DB{DB}.DBX0.0、DB{DB}.DBB2、DB{DB}.DBW4，也可以只写 0.0 / DBX0.0 / DBW4。
    /// </summary>
    public string OffsetAddress
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "DB{DB}.DBX0.0";

    /// <summary>
    /// 注释。
    /// </summary>
    public string Remark
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    public PlcDbBlockProtocolItemModel Clone()
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

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// PLC DB块配置模型。
/// 一条记录代表一个虚拟PLC内部的DB块。
///
/// 设计说明：
/// 1. DB块本身负责维护该DB块要执行的自动写入规则引用列表。
/// 2. 规则本体是全局配置，不保存在DB块里。
/// 3. DB块只保存 RuleId，运行时根据 RuleId 去全局规则库中获取 PlcAutoWriteRuleModel。
/// 4. 一个DB块可以绑定多条全局规则。
/// 5. 绑定的规则按 SortIndex 顺序执行。
/// 6. 每条规则内部触发条件统一按“全部满足”判断。
/// </summary>
public class PlcDbBlockModel : INotifyPropertyChanged
{
    public PlcDbBlockModel()
    {
        AutoWriteRuleList.CollectionChanged += AutoWriteRuleList_CollectionChanged;
        ResetItems.CollectionChanged += ResetItems_CollectionChanged;
        ProtocolItems.CollectionChanged += ProtocolItems_CollectionChanged;
    }

    /// <summary>
    /// DB块号。
    /// 例如：1500 对应 DB1500。
    /// </summary>
    public int DbNumber
    {
        get;
        set
        {
            field = value;

            if (string.IsNullOrWhiteSpace(BlockName))
            {
                BlockName = $"DB{field}";
            }

            OnPropertyChanged();
        }
    } = 1500;

    /// <summary>
    /// DB块长度，单位：字节。
    /// </summary>
    public int Length
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = 512;

    /// <summary>
    /// 是否启用该DB块。
    /// </summary>
    public bool IsEnabled
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = true;

    /// <summary>
    /// DB块名称。
    /// </summary>
    public string BlockName
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "DB1500";

    /// <summary>
    /// 备注。
    /// </summary>
    public string Remark
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    /// <summary>
    /// 当前DB块是否启用自动写入。
    /// </summary>
    public bool AutoWriteEnabled
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = true;

    /// <summary>
    /// 当前DB块绑定的全局自动写入规则引用列表。
    ///
    /// 注意：
    /// 这里保存的是规则引用，不是规则实体。
    /// RuleId 对应全局 PlcAutoWriteRuleModel.RuleId。
    /// 一个DB块可以绑定多条规则。
    /// </summary>
    public ObservableCollection<PlcDbBlockAutoWriteRuleRefModel> AutoWriteRuleList
    {
        get;
        set
        {
            if (field != null)
            {
                field.CollectionChanged -= AutoWriteRuleList_CollectionChanged;
            }

            field = value ?? [];

            field.CollectionChanged += AutoWriteRuleList_CollectionChanged;

            OnPropertyChanged();
        }
    } = [];

    /// <summary>
    /// 旧版按DB块保存的复位配置项。
    /// 仅用于兼容和迁移旧配置；新的运行逻辑统一读取 PlcConfigModel.GlobalResetItems。
    /// PlcStoreService 加载后会把全局复位模板同步到该列表，避免旧版本配置文件丢失数据。
    /// </summary>
    public ObservableCollection<PlcDbBlockResetItemModel> ResetItems
    {
        get;
        set
        {
            if (field != null)
            {
                field.CollectionChanged -= ResetItems_CollectionChanged;
            }

            field = value ?? [];

            field.CollectionChanged += ResetItems_CollectionChanged;

            OnPropertyChanged();
        }
    } = [];

    /// <summary>
    /// 获取下一个复位项ID。
    /// </summary>
    public int GetNextResetItemId()
    {
        if (ResetItems == null || ResetItems.Count == 0)
        {
            return 1;
        }

        var maxId = 0;

        foreach (var resetItem in ResetItems)
        {
            if (resetItem == null)
            {
                continue;
            }

            if (resetItem.ResetItemId > maxId)
            {
                maxId = resetItem.ResetItemId;
            }
        }

        return maxId + 1;
    }

    /// <summary>
    /// 添加一个复位项。
    /// </summary>
    public PlcDbBlockResetItemModel AddResetItem(string resetAddress = "0.0", bool resetValue = false)
    {
        ResetItems ??= [];

        var resetItemId = GetNextResetItemId();

        var resetItem = new PlcDbBlockResetItemModel
        {
            ResetItemId = resetItemId,
            ResetName = $"复位项-{resetItemId}",
            IsEnabled = true,
            ResetAddress = resetAddress,
            ResetValue = resetValue,
            Remark = string.Empty
        };

        ResetItems.Add(resetItem);

        return resetItem;
    }


    /// <summary>
    /// 当前DB块的协议看板配置项。
    /// 用于展示当前DB块有哪些信号、数据类型、偏移量地址和注释。
    /// </summary>
    public ObservableCollection<PlcDbBlockProtocolItemModel> ProtocolItems
    {
        get;
        set
        {
            if (field != null)
            {
                field.CollectionChanged -= ProtocolItems_CollectionChanged;
            }

            field = value ?? [];

            field.CollectionChanged += ProtocolItems_CollectionChanged;

            OnPropertyChanged();
        }
    } = [];

    /// <summary>
    /// 获取下一个协议项ID。
    /// </summary>
    public int GetNextProtocolItemId()
    {
        if (ProtocolItems == null || ProtocolItems.Count == 0)
        {
            return 1;
        }

        var maxId = 0;

        foreach (var protocolItem in ProtocolItems)
        {
            if (protocolItem == null)
            {
                continue;
            }

            if (protocolItem.ProtocolItemId > maxId)
            {
                maxId = protocolItem.ProtocolItemId;
            }
        }

        return maxId + 1;
    }

    /// <summary>
    /// 添加一个协议看板配置项。
    /// </summary>
    public PlcDbBlockProtocolItemModel AddProtocolItem(string signalName = "信号", string dataType = "Bool", string offsetAddress = "DB{DB}.DBX0.0", string remark = "")
    {
        ProtocolItems ??= [];

        var protocolItemId = GetNextProtocolItemId();

        var protocolItem = new PlcDbBlockProtocolItemModel
        {
            ProtocolItemId = protocolItemId,
            IsEnabled = true,
            SignalName = string.IsNullOrWhiteSpace(signalName) ? $"信号-{protocolItemId}" : signalName,
            DataType = string.IsNullOrWhiteSpace(dataType) ? "Bool" : dataType,
            OffsetAddress = string.IsNullOrWhiteSpace(offsetAddress) ? "DB{DB}.DBX0.0" : offsetAddress,
            Remark = remark ?? string.Empty
        };

        ProtocolItems.Add(protocolItem);

        return protocolItem;
    }

    /// <summary>
    /// 是否存在可执行的自动写入规则引用。
    /// </summary>
    public bool HasEnabledAutoWriteRules()
    {
        if (!IsEnabled)
        {
            return false;
        }

        if (!AutoWriteEnabled)
        {
            return false;
        }

        if (AutoWriteRuleList == null || AutoWriteRuleList.Count == 0)
        {
            return false;
        }

        foreach (var ruleRef in AutoWriteRuleList)
        {
            if (ruleRef == null)
            {
                continue;
            }

            if (!ruleRef.IsEnabled)
            {
                continue;
            }

            if (ruleRef.RuleId <= 0)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// 获取下一个规则引用ID。
    /// </summary>
    public int GetNextRuleRefId()
    {
        if (AutoWriteRuleList == null || AutoWriteRuleList.Count == 0)
        {
            return 1;
        }

        var maxId = 0;

        foreach (var ruleRef in AutoWriteRuleList)
        {
            if (ruleRef == null)
            {
                continue;
            }

            if (ruleRef.RuleRefId > maxId)
            {
                maxId = ruleRef.RuleRefId;
            }
        }

        return maxId + 1;
    }

    /// <summary>
    /// 获取下一个执行顺序。
    /// </summary>
    public int GetNextSortIndex()
    {
        if (AutoWriteRuleList == null || AutoWriteRuleList.Count == 0)
        {
            return 1;
        }

        var maxSortIndex = 0;

        foreach (var ruleRef in AutoWriteRuleList)
        {
            if (ruleRef == null)
            {
                continue;
            }

            if (ruleRef.SortIndex > maxSortIndex)
            {
                maxSortIndex = ruleRef.SortIndex;
            }
        }

        return maxSortIndex + 1;
    }

    /// <summary>
    /// 添加一个全局规则引用。
    /// </summary>
    public PlcDbBlockAutoWriteRuleRefModel AddRuleRef(int ruleId, string ruleName = "")
    {
        AutoWriteRuleList ??= [];

        var ruleRef = new PlcDbBlockAutoWriteRuleRefModel
        {
            RuleRefId = GetNextRuleRefId(),
            IsEnabled = true,
            RuleId = ruleId,
            RuleName = ruleName ?? string.Empty,
            SortIndex = GetNextSortIndex(),
            Remark = string.Empty
        };

        AutoWriteRuleList.Add(ruleRef);

        return ruleRef;
    }

    /// <summary>
    /// 删除指定规则引用。
    /// </summary>
    public bool RemoveRuleRef(PlcDbBlockAutoWriteRuleRefModel ruleRef)
    {
        if (AutoWriteRuleList == null || ruleRef == null)
        {
            return false;
        }

        return AutoWriteRuleList.Remove(ruleRef);
    }

    /// <summary>
    /// 删除指定引用ID的规则引用。
    /// </summary>
    public bool RemoveRuleRefById(int ruleRefId)
    {
        if (AutoWriteRuleList == null)
        {
            return false;
        }

        PlcDbBlockAutoWriteRuleRefModel target = null;

        foreach (var ruleRef in AutoWriteRuleList)
        {
            if (ruleRef == null)
            {
                continue;
            }

            if (ruleRef.RuleRefId == ruleRefId)
            {
                target = ruleRef;
                break;
            }
        }

        if (target == null)
        {
            return false;
        }

        return AutoWriteRuleList.Remove(target);
    }

    /// <summary>
    /// 判断当前DB块是否已经引用某个全局规则。
    /// </summary>
    public bool ContainsRule(int ruleId)
    {
        if (AutoWriteRuleList == null)
        {
            return false;
        }

        foreach (var ruleRef in AutoWriteRuleList)
        {
            if (ruleRef == null)
            {
                continue;
            }

            if (ruleRef.RuleId == ruleId)
            {
                return true;
            }
        }

        return false;
    }

    public PlcDbBlockModel Clone()
    {
        var clone = new PlcDbBlockModel
        {
            DbNumber = DbNumber,
            Length = Length,
            IsEnabled = IsEnabled,
            BlockName = BlockName,
            Remark = Remark,
            AutoWriteEnabled = AutoWriteEnabled
        };

        if (AutoWriteRuleList != null)
        {
            foreach (var ruleRef in AutoWriteRuleList)
            {
                if (ruleRef == null)
                {
                    continue;
                }

                clone.AutoWriteRuleList.Add(ruleRef.Clone());
            }
        }

        if (ResetItems != null)
        {
            foreach (var resetItem in ResetItems)
            {
                if (resetItem == null)
                {
                    continue;
                }

                clone.ResetItems.Add(resetItem.Clone());
            }
        }

        if (ProtocolItems != null)
        {
            foreach (var protocolItem in ProtocolItems)
            {
                if (protocolItem == null)
                {
                    continue;
                }

                clone.ProtocolItems.Add(protocolItem.Clone());
            }
        }

        return clone;
    }

    private void AutoWriteRuleList_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(AutoWriteRuleList));
    }
    private void ProtocolItems_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ProtocolItems));
    }
    private void ResetItems_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ResetItems));
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
