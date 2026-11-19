#nullable disable

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Vda5050AgvSimulator.Models.Plc;

/// <summary>
/// PLC自动写入触发条件。
/// 例如：读取 0.0，执行时会自动组合为当前绑定DB块号，例如 DB2090.0.0。
/// </summary>
public class PlcAutoWriteTriggerModel : INotifyPropertyChanged
{
    public int TriggerId
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public string TriggerName
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "触发条件";

    /// <summary>
    /// 是否启用该触发条件。
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
    /// 读取地址。
    /// Bool地址示例：5.0。也兼容 DB{DB}.5.0 或具体地址如 DB2090.5.0
    /// </summary>
    public string ReadAddress
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "0.0";

    /// <summary>
    /// 触发值。
    /// 例如读到 true 时触发。
    /// </summary>
    public bool TriggerValue
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = true;

    /// <summary>
    /// 兼容旧配置字段。
    /// 当前版本延时触发已经提升到 PlcAutoWriteRuleModel 规则级别，触发条件本身不再单独延时。
    /// </summary>
    public bool DelayTriggerEnabled
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 兼容旧配置字段。
    /// 当前版本执行逻辑不再读取触发条件级延时时间。
    /// </summary>
    public int DelayMilliseconds
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
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

    public PlcAutoWriteTriggerModel Clone()
    {
        return new PlcAutoWriteTriggerModel
        {
            TriggerId = TriggerId,
            TriggerName = TriggerName,
            IsEnabled = IsEnabled,
            ReadAddress = ReadAddress,
            TriggerValue = TriggerValue,
            DelayTriggerEnabled = DelayTriggerEnabled,
            DelayMilliseconds = DelayMilliseconds,
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
/// PLC自动写入动作。
/// 例如：触发后写 2.0 = true，执行时会自动组合为当前绑定DB块号，例如 DB2090.2.0。
/// </summary>
public class PlcAutoWriteItemModel : INotifyPropertyChanged
{
    public int WriteId
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public string WriteName
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "写入动作";

    /// <summary>
    /// 是否启用该写入动作。
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
    /// 写入地址。
    /// Bool地址示例：5.0。也兼容 DB{DB}.5.0 或具体地址如 DB2090.5.0
    /// </summary>
    public string WriteAddress
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "2.0";

    /// <summary>
    /// 触发后写入的值。
    /// </summary>
    public bool WriteValue
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = true;

    /// <summary>
    /// 当规则未触发时，是否自动复位该写入地址。
    /// </summary>
    public bool ResetWhenNotTrigger
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = true;

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

    public string Remark
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    public PlcAutoWriteItemModel Clone()
    {
        return new PlcAutoWriteItemModel
        {
            WriteId = WriteId,
            WriteName = WriteName,
            IsEnabled = IsEnabled,
            WriteAddress = WriteAddress,
            WriteValue = WriteValue,
            ResetWhenNotTrigger = ResetWhenNotTrigger,
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
/// PLC自动写入规则。
/// 支持多触发、多写入。
/// 规则统一按“全部启用触发条件都满足”判断，不再提供 Any/All 关系选择。
/// 例如：多个请求信号全部满足后，自动写入多个允许信号。
/// </summary>
public class PlcAutoWriteRuleModel : INotifyPropertyChanged
{
    public PlcAutoWriteRuleModel()
    {
        Triggers.CollectionChanged += Triggers_CollectionChanged;
        WriteItems.CollectionChanged += WriteItems_CollectionChanged;
    }

    public int RuleId
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    public string RuleName
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "请求进入 -> 允许进入";

    /// <summary>
    /// 是否启用该规则。
    /// 只有勾选后才会执行自动写入。
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
    /// 是否启用规则延时触发。
    /// true：全部启用触发条件满足后，连续保持 DelayMilliseconds 毫秒，才执行写入动作。
    /// false：全部启用触发条件满足后立即执行写入动作。
    /// </summary>
    public bool DelayTriggerEnabled
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DelayTriggerSummary));
        }
    }

    /// <summary>
    /// 规则延时时间，单位：毫秒。
    /// 只有 DelayTriggerEnabled=true 且 DelayMilliseconds>0 时生效。
    /// </summary>
    public int DelayMilliseconds
    {
        get;
        set
        {
            field = value < 0 ? 0 : value;

            if (field == 0)
            {
                DelayTriggerEnabled = false;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(DelayTriggerSummary));
        }
    }

    /// <summary>
    /// 触发条件列表。
    /// </summary>
    public ObservableCollection<PlcAutoWriteTriggerModel> Triggers
    {
        get;
        set
        {
            if (field != null)
            {
                field.CollectionChanged -= Triggers_CollectionChanged;
            }

            field = value ?? [];

            field.CollectionChanged += Triggers_CollectionChanged;

            OnPropertyChanged();
        }
    } = [];

    /// <summary>
    /// 写入动作列表。
    /// </summary>
    public ObservableCollection<PlcAutoWriteItemModel> WriteItems
    {
        get;
        set
        {
            if (field != null)
            {
                field.CollectionChanged -= WriteItems_CollectionChanged;
            }

            field = value ?? [];

            field.CollectionChanged += WriteItems_CollectionChanged;

            OnPropertyChanged();
        }
    } = [];

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
    /// 规则延时摘要，仅用于界面显示。
    /// </summary>
    public string DelayTriggerSummary
    {
        get
        {
            if (!DelayTriggerEnabled || DelayMilliseconds <= 0)
            {
                return "无";
            }

            return $"{DelayMilliseconds}ms";
        }
    }

    /// <summary>
    /// 创建默认规则。
    /// 等价于旧版：读请求进入，写允许进入。
    /// </summary>
    public static PlcAutoWriteRuleModel CreateDefault()
    {
        var rule = new PlcAutoWriteRuleModel
        {
            RuleId = 1,
            RuleName = "请求进入 -> 允许进入",
            IsEnabled = true,
            DelayTriggerEnabled = false,
            DelayMilliseconds = 0,
            Remark = "读取请求进入信号，自动写入允许进入信号。"
        };

        rule.Triggers.Add(new PlcAutoWriteTriggerModel
        {
            TriggerId = 1,
            TriggerName = "请求进入",
            IsEnabled = true,
            ReadAddress = "0.0",
            TriggerValue = true,
            DelayTriggerEnabled = false,
            DelayMilliseconds = 0,
            Remark = string.Empty
        });

        rule.WriteItems.Add(new PlcAutoWriteItemModel
        {
            WriteId = 1,
            WriteName = "允许进入",
            IsEnabled = true,
            WriteAddress = "2.0",
            WriteValue = true,
            ResetWhenNotTrigger = true,
            ResetValue = false,
            Remark = string.Empty
        });

        return rule;
    }

    public PlcAutoWriteRuleModel Clone()
    {
        var clone = new PlcAutoWriteRuleModel
        {
            RuleId = RuleId,
            RuleName = RuleName,
            IsEnabled = IsEnabled,
            DelayTriggerEnabled = DelayTriggerEnabled,
            DelayMilliseconds = DelayMilliseconds,
            Remark = Remark
        };

        foreach (var trigger in Triggers)
        {
            if (trigger == null)
            {
                continue;
            }

            clone.Triggers.Add(trigger.Clone());
        }

        foreach (var writeItem in WriteItems)
        {
            if (writeItem == null)
            {
                continue;
            }

            clone.WriteItems.Add(writeItem.Clone());
        }

        return clone;
    }

    /// <summary>
    /// 判断该规则是否有效。
    /// 至少需要一个启用触发条件和一个启用写入动作。
    /// </summary>
    public bool IsValid()
    {
        if (!IsEnabled)
        {
            return false;
        }

        if (Triggers == null || Triggers.Count == 0)
        {
            return false;
        }

        if (WriteItems == null || WriteItems.Count == 0)
        {
            return false;
        }

        var hasEnabledTrigger = false;

        foreach (var trigger in Triggers)
        {
            if (trigger == null || !trigger.IsEnabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(trigger.ReadAddress))
            {
                continue;
            }

            hasEnabledTrigger = true;
            break;
        }

        if (!hasEnabledTrigger)
        {
            return false;
        }

        var hasEnabledWriteItem = false;

        foreach (var writeItem in WriteItems)
        {
            if (writeItem == null || !writeItem.IsEnabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(writeItem.WriteAddress))
            {
                continue;
            }

            hasEnabledWriteItem = true;
            break;
        }

        return hasEnabledWriteItem;
    }

    private void Triggers_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Triggers));
        OnPropertyChanged(nameof(DelayTriggerSummary));
    }

    private void WriteItems_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(WriteItems));
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}