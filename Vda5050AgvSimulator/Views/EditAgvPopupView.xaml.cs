using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vda5050AgvSimulator.Models;
using Vda5050AgvSimulator.Models.Agv;
using Vda5050AgvSimulator.Services.Agv;

namespace Vda5050AgvSimulator.Views;

public partial class EditAgvPopupView : INotifyPropertyChanged
{
    private AgvModel? _sourceAgv;

    private bool _isApplyingMapPoint;

    private AgvModel _editingAgv = new();

    private string _deviceTypeText = "HPVDA5050";

    private string _actionsText = string.Empty;

    private string _actionStatesText = string.Empty;

    /// <summary>
    /// 当前弹窗正在编辑的AGV副本。必须为public，WPF绑定才能访问。
    /// </summary>
    public AgvModel EditingAgv
    {
        get => _editingAgv;
        private set => SetField(ref _editingAgv, value);
    }

    public string DeviceTypeText
    {
        get => _deviceTypeText;
        private set => SetField(ref _deviceTypeText, value);
    }

    public string ActionsText
    {
        get => _actionsText;
        private set => SetField(ref _actionsText, value);
    }

    public string ActionStatesText
    {
        get => _actionStatesText;
        private set => SetField(ref _actionStatesText, value);
    }

    /// <summary>
    /// 动作编辑集合。必须为public，DataGrid绑定才能访问。
    /// </summary>
    public ObservableCollection<ActionEditorItem> ActionEditors { get; } = [];

    /// <summary>
    /// 动作状态编辑集合。必须为public，DataGrid绑定才能访问。
    /// </summary>
    public ObservableCollection<ActionStateEditorItem> ActionStateEditors { get; } = [];

    public List<string> BlockingTypeOptions { get; set; } =
    [
        "NONE",
        "SOFT",
        "HARD"
    ];

    public List<string> ActionStatusOptions { get; set; } =
    [
        "WAITING",
        "INITIALIZING",
        "RUNNING",
        "FINISHED",
        "FAILED"
    ];

    public List<OptionItem>? RunStatusOptions { get; set; }

    public List<OptionItem>? RunDirectionOptions { get; set; }

    public List<OptionItem>? DodgeAreaOptions { get; set; }

    public List<OptionItem>? OnlineStatusOptions { get; set; }

    public List<OptionItem>? TrafficStatusOptions { get; set; }

    public List<OptionItem>? ChargeStatusOptions { get; set; }

    public List<StringOptionItem>? LoadStatusOptions { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? CloseRequested;

    public event EventHandler<AgvEditedEventArgs>? SaveRequested;

    public EditAgvPopupView()
    {
        InitializeComponent();

        InitOptions();

        DataContext = this;
    }

    /// <summary>
    /// 加载需要编辑的AGV。使用副本编辑，取消时不会污染原对象。
    /// </summary>
    public void SetAgv(AgvModel agv)
    {
        // ArgumentNullException.ThrowIfNull(agv);

        ValidationText.Text = string.Empty;
        _sourceAgv = agv;

        var editingAgv = CloneAgv(agv);

        // 地标存在时，以地图配置刷新X/Y；不会覆盖用户原来的角度。
        ApplyMapPointToAgv(editingAgv, out _);

        EditingAgv = editingAgv;
        DeviceTypeText = GetDeviceTypeText(agv);

        ActionsText = editingAgv.actions == null
            ? "null"
            : JsonConvert.SerializeObject(editingAgv.actions, Formatting.Indented);

        ActionStatesText = editingAgv.actionStates == null
            ? "[]"
            : JsonConvert.SerializeObject(editingAgv.actionStates, Formatting.Indented);

        LoadActionEditors(editingAgv);

        // EditingAgv替换后由PropertyChanged统一刷新，不再重置DataContext。
        RefreshCoordinateTextBox();
    }

    private void InitOptions()
    {
        RunStatusOptions =
        [
            new OptionItem { Value = 0, Name = "停止" },
            new OptionItem { Value = 1, Name = "运行中" },
            new OptionItem { Value = 2, Name = "精准停车" }
        ];

        RunDirectionOptions =
        [
            new OptionItem { Value = 0, Name = "不变" },
            new OptionItem { Value = 1, Name = "向前" },
            new OptionItem { Value = 2, Name = "向后" }
        ];

        DodgeAreaOptions =
        [
            new OptionItem { Value = 0, Name = "不变" },
            new OptionItem { Value = 1, Name = "避障区域1" },
            new OptionItem { Value = 2, Name = "避障区域2" },
            new OptionItem { Value = 3, Name = "避障区域3" },
            new OptionItem { Value = 4, Name = "避障区域4" },
            new OptionItem { Value = 5, Name = "避障区域5" },
            new OptionItem { Value = 6, Name = "避障区域6" },
            new OptionItem { Value = 7, Name = "避障区域7" },
            new OptionItem { Value = 8, Name = "避障区域8" },
            new OptionItem { Value = 9, Name = "避障区域9" },
            new OptionItem { Value = 10, Name = "避障区域10" },
            new OptionItem { Value = 11, Name = "避障区域11" },
            new OptionItem { Value = 12, Name = "避障区域12" },
            new OptionItem { Value = 13, Name = "避障区域13" },
            new OptionItem { Value = 14, Name = "避障区域14" },
            new OptionItem { Value = 15, Name = "避障区域15" },
            new OptionItem { Value = 16, Name = "避障区域16" },
            new OptionItem { Value = 16, Name = "没有避障" }
        ];

        OnlineStatusOptions =
        [
            new OptionItem { Value = 0, Name = "不变" },
            new OptionItem { Value = 1, Name = "在线" },
            new OptionItem { Value = 2, Name = "离线" }
        ];

        TrafficStatusOptions =
        [
            new OptionItem { Value = 0, Name = "未知" },
            new OptionItem { Value = 1, Name = "放行" },
            new OptionItem { Value = 2, Name = "管制" }
        ];

        ChargeStatusOptions =
        [
            new OptionItem { Value = 0, Name = "未知" },
            new OptionItem { Value = 1, Name = "充电启动" },
            new OptionItem { Value = 2, Name = "充电停止" }
        ];

        LoadStatusOptions =
        [
            new StringOptionItem { Value = "UNLOAD", Name = "空载" },
            new StringOptionItem { Value = "LOADED", Name = "满载" }
        ];
    }

    public class StringOptionItem
    {
        public string Value { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
    }

    private string GetDeviceTypeText(AgvModel? agv)
    {
        if (agv == null)
        {
            return "HPVDA5050";
        }

        return agv.DeviceType == 3 ? "HPVDA5050" : agv.DeviceType.ToString();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // 提交仍以LostFocus更新的输入项，例如运行速度。
        Keyboard.ClearFocus();

        if (_sourceAgv == null)
        {
            ShowValidationError("没有找到需要修改的AGV。");
            return;
        }

        if (string.IsNullOrWhiteSpace(EditingAgv.LocalIP))
        {
            ShowValidationError("本地IP不能为空。");
            return;
        }

        if (string.IsNullOrWhiteSpace(EditingAgv.RemoteIP))
        {
            ShowValidationError("远程IP不能为空。");
            return;
        }

        if (EditingAgv.LocalPort < 0 || EditingAgv.LocalPort > 65535)
        {
            ShowValidationError("本地端口必须在 0 - 65535 之间。");
            return;
        }

        if (EditingAgv.RemotePort < 0 || EditingAgv.RemotePort > 65535)
        {
            ShowValidationError("远程端口必须在 0 - 65535 之间。");
            return;
        }

        if (EditingAgv.Equantity is < 0 or > 100)
        {
            ShowValidationError("电量必须在 0 - 100 之间。");
            return;
        }

        if (EditingAgv.Speed < 0)
        {
            ShowValidationError("运行速度不能小于 0。");
            return;
        }

        if (double.IsNaN(EditingAgv.Angle)
            || double.IsInfinity(EditingAgv.Angle)
            || EditingAgv.Angle < 0
            || EditingAgv.Angle > 360)
        {
            ShowValidationError("角度必须在 0 - 360 之间。");
            return;
        }

        if (!ApplyMapPointToAgv(EditingAgv, out var mapMessage))
        {
            ShowValidationError(mapMessage);
            return;
        }

        ApplyActionEditorsToAgv();

        SaveRequested?.Invoke(this, new AgvEditedEventArgs(_sourceAgv, CloneAgv(EditingAgv)));
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void ShowValidationError(string message)
    {
        ValidationText.Text = message;
    }

    private static AgvModel CloneAgv(AgvModel? source)
    {
        if (source == null)
        {
            return new AgvModel();
        }

        return new AgvModel
        {
            AGVCount = source.AGVCount,
            DeviceID = source.DeviceID,
            DeviceType = source.DeviceType,
            LocalIP = source.LocalIP,
            LocalPort = source.LocalPort,
            RemoteIP = source.RemoteIP,
            RemotePort = source.RemotePort,
            PositionID = source.PositionID,
            X = source.X,
            Y = source.Y,
            Angle = source.Angle,
            Equantity = source.Equantity,
            RunStatus = source.RunStatus,
            RunDirection = source.RunDirection,
            Speed = source.Speed,
            DodgeArea = source.DodgeArea,
            OnlineStatus = source.OnlineStatus,
            TrafficStatus = source.TrafficStatus,
            ChargeStatus = source.ChargeStatus,
            TranslationStatus = source.TranslationStatus,
            RotateStatus = source.RotateStatus,
            BranchStatus = source.BranchStatus,
            Pin1Status = source.Pin1Status,
            Pin2Status = source.Pin2Status,
            Plat1Status = source.Plat1Status,
            Plat2Status = source.Plat2Status,
            Jig1Status = source.Jig1Status,
            Jig2Status = source.Jig2Status,
            Roller1Status = source.Roller1Status,
            Roller2Status = source.Roller2Status,
            Roller3Status = source.Roller3Status,
            Roller4Status = source.Roller4Status,
            LoadStatus = source.LoadStatus,
            AlarmCode = source.AlarmCode,
            Roller1LoadStatus = source.Roller1LoadStatus,
            Roller2LoadStatus = source.Roller2LoadStatus,
            Roller3LoadStatus = source.Roller3LoadStatus,
            Roller4LoadStatus = source.Roller4LoadStatus,
            MusicStatus = source.MusicStatus,
            StopButtonStatus = source.StopButtonStatus,
            ControlStatus = source.ControlStatus,
            ReleaseRFID = source.ReleaseRFID,
            AGVOutputSignal1 = source.AGVOutputSignal1,
            AGVOutputSignal2 = source.AGVOutputSignal2,
            FirstPoweron = source.FirstPoweron,
            ButtonRelease = source.ButtonRelease,
            Electric = source.Electric,
            LiftHeight = source.LiftHeight,
            ButtonShutdown = source.ButtonShutdown,
            ButtonSleep = source.ButtonSleep,
            TaskChange = source.TaskChange,
            TaskNo = source.TaskNo,
            RCSOutputSignal1 = source.RCSOutputSignal1,
            RCSOutputSignal2 = source.RCSOutputSignal2,
            NextDistance = source.NextDistance,
            ControlRFID = source.ControlRFID,
            SimulationButtonStart = source.SimulationButtonStart,
            ActionID = source.ActionID,
            ReleaseRFID1 = source.ReleaseRFID1,
            ReleaseRFID2 = source.ReleaseRFID2,
            ReleaseRFID3 = source.ReleaseRFID3,
            AnalogSignal1 = source.AnalogSignal1,
            AnalogSignal2 = source.AnalogSignal2,
            tr_cmd = source.tr_cmd,
            tr_id_index = source.tr_id_index,
            tr_id_hour = source.tr_id_hour,
            tr_id_day = source.tr_id_day,
            tr_id_month = source.tr_id_month,
            tr_id_year = source.tr_id_year,
            tr_sender = source.tr_sender,
            tr_type = source.tr_type,
            tr_step_size = source.tr_step_size,
            tr_start_delay = source.tr_start_delay,
            actions = source.actions == null ? null : [.. source.actions],
            actionStates = source.actionStates == null
                ? []
                : [.. source.actionStates],
            CurrentOrderId = source.CurrentOrderId,
            CurrentOrderUpdateId = source.CurrentOrderUpdateId,
            LastNodeSequenceId = source.LastNodeSequenceId,
            VdaPaused = source.VdaPaused
        };
    }

    /// <summary>
    /// 地标点变化时，自动从地图配置中读取X/Y坐标。
    /// </summary>
    private void PositionIdTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isApplyingMapPoint)
        {
            return;
        }

        try
        {
            if (sender is not TextBox textBox)
            {
                return;
            }

            _isApplyingMapPoint = true;

            if (!MapStoreService.TryApplyPositionByPointId(EditingAgv, textBox.Text, out var message))
            {
                return;
            }

            RefreshCoordinateTextBox();
        }
        catch (Exception)
        {
            // ignored
        }
        finally
        {
            _isApplyingMapPoint = false;
        }
    }

    /// <summary>
    /// 根据AGV地标点，从地图配置中自动填充X/Y。
    /// </summary>
    private static bool ApplyMapPointToAgv(AgvModel agv, out string message)
    {
        return MapStoreService.TryApplyPositionByPositionId(agv, out message);
    }

    /// <summary>
    /// 刷新坐标输入框显示。
    /// </summary>
    private void RefreshCoordinateTextBox()
    {
        CoordinateXTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();

        CoordinateYTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
    }

    /// <summary>
    /// 加载actions和actionStates到列表编辑器。
    /// </summary>
    private void LoadActionEditors(AgvModel agv)
    {
        ActionEditors.Clear();

        ActionStateEditors.Clear();

        if (agv.actions != null)
        {
            foreach (var action in agv.actions)
            {
                var actionObject = JObject.FromObject(action);

                var parameter = actionObject["actionParameters"]?.FirstOrDefault() as JObject;

                ActionEditors.Add(new ActionEditorItem
                {
                    ActionId = actionObject["actionId"]?.ToString() ?? string.Empty,
                    ActionType = actionObject["actionType"]?.ToString() ?? string.Empty,
                    ActionDescription = actionObject["actionDescription"]?.ToString() ?? string.Empty,
                    BlockingType = actionObject["blockingType"]?.ToString() ?? "HARD",
                    ParameterKey = parameter?["key"]?.ToString() ?? string.Empty,
                    ParameterValue = parameter?["value"]?.ToString() ?? string.Empty
                });
            }
        }

        foreach (var actionState in agv.actionStates ?? [])
        {
            ActionStateEditors.Add(new ActionStateEditorItem
            {
                ActionId = actionState.actionId,
                ActionType = actionState.actionType,
                ActionDescription = actionState.actionDescription,
                ActionStatus = actionState.actionStatus,
                ResultDescription = actionState.resultDescription
            });
        }
    }

    /// <summary>
    /// 新增动作。
    /// </summary>
    private void AddActionButton_Click(object sender, RoutedEventArgs e)
    {
        var actionId = Guid.NewGuid().ToString();

        ActionEditors.Add(new ActionEditorItem
        {
            ActionId = actionId,
            ActionType = "tine_up",
            ActionDescription = string.Empty,
            BlockingType = "HARD",
            ParameterKey = "height",
            ParameterValue = "0.01"
        });
    }

    /// <summary>
    /// 删除选中的动作。
    /// </summary>
    private void DeleteActionButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedActions = ActionDataGrid.SelectedItems.OfType<ActionEditorItem>().ToArray();
        foreach (var action in selectedActions)
        {
            ActionEditors.Remove(action);
        }
    }

    /// <summary>
    /// 新增动作状态。
    /// </summary>
    private void AddActionStateButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedActions = ActionDataGrid.SelectedItems.OfType<ActionEditorItem>().ToArray();
        if (selectedActions.Length > 0)
        {
            foreach (var action in selectedActions)
            {
                ActionStateEditors.Add(new ActionStateEditorItem
                {
                    ActionId = action.ActionId,
                    ActionType = action.ActionType,
                    ActionDescription = action.ActionDescription,
                    ActionStatus = "FINISHED",
                    ResultDescription = string.Empty
                });
            }

            return;
        }

        ActionStateEditors.Add(new ActionStateEditorItem
        {
            ActionId = Guid.NewGuid().ToString(),
            ActionType = "tine_up",
            ActionDescription = string.Empty,
            ActionStatus = "FINISHED",
            ResultDescription = string.Empty
        });
    }

    /// <summary>
    /// 删除选中的动作状态。
    /// </summary>
    private void DeleteActionStateButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedStates = ActionStateDataGrid.SelectedItems.OfType<ActionStateEditorItem>().ToArray();
        foreach (var state in selectedStates)
        {
            ActionStateEditors.Remove(state);
        }
    }

    /// <summary>
    /// 根据actions生成actionStates。
    /// </summary>
    private void GenerateActionStatesButton_Click(object sender, RoutedEventArgs e)
    {
        ActionStateEditors.Clear();

        foreach (var action in ActionEditors)
        {
            if (string.IsNullOrWhiteSpace(action.ActionId))
            {
                action.ActionId = Guid.NewGuid().ToString();
            }

            ActionStateEditors.Add(new ActionStateEditorItem
            {
                ActionId = action.ActionId,
                ActionType = action.ActionType,
                ActionDescription = action.ActionDescription,
                ActionStatus = "FINISHED",
                ResultDescription = string.Empty
            });
        }
    }

    /// <summary>
    /// 保存前将列表编辑器内容写回AGV模型。
    /// </summary>
    private void ApplyActionEditorsToAgv()
    {
        var actions = new List<AgvActionModel>();

        foreach (var item in ActionEditors)
        {
            if (string.IsNullOrWhiteSpace(item.ActionId))
            {
                item.ActionId = Guid.NewGuid().ToString();
            }

            var actionObject = new JObject
            {
                ["actionId"] = item.ActionId,
                ["actionType"] = item.ActionType ?? string.Empty,
                ["actionDescription"] = item.ActionDescription ?? string.Empty,
                ["blockingType"] = string.IsNullOrWhiteSpace(item.BlockingType) ? "HARD" : item.BlockingType,
                ["actionParameters"] = CreateActionParameters(item)
            };

            var action = actionObject.ToObject<AgvActionModel>();

            if (action != null)
            {
                actions.Add(action);
            }
        }

        var actionStates = new List<AgvActionStateModel>();

        foreach (var item in ActionStateEditors)
        {
            if (string.IsNullOrWhiteSpace(item.ActionId))
            {
                item.ActionId = Guid.NewGuid().ToString();
            }

            actionStates.Add(new AgvActionStateModel
            {
                actionId = item.ActionId ?? string.Empty,
                actionType = item.ActionType ?? string.Empty,
                actionDescription = item.ActionDescription ?? string.Empty,
                actionStatus = string.IsNullOrWhiteSpace(item.ActionStatus) ? "FINISHED" : item.ActionStatus,
                resultDescription = string.IsNullOrWhiteSpace(item.ResultDescription) ? null : item.ResultDescription
            });
        }

        EditingAgv.actions = actions.Count == 0 ? null : actions;

        EditingAgv.actionStates = actionStates;

        ActionsText = EditingAgv.actions == null
            ? "null"
            : JsonConvert.SerializeObject(EditingAgv.actions, Formatting.Indented);

        ActionStatesText = JsonConvert.SerializeObject(EditingAgv.actionStates, Formatting.Indented);
    }

    /// <summary>
    /// 创建动作参数。
    /// </summary>
    private static JArray CreateActionParameters(ActionEditorItem item)
    {
        var result = new JArray();

        if (string.IsNullOrWhiteSpace(item.ParameterKey))
        {
            return result;
        }

        result.Add(new JObject
        {
            ["key"] = item.ParameterKey,
            ["value"] = item.ParameterValue ?? string.Empty
        });

        return result;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public class AgvEditedEventArgs(AgvModel? sourceAgv, AgvModel editedAgv) : EventArgs
{
    public AgvModel? SourceAgv { get; private set; } = sourceAgv;

    public AgvModel EditedAgv { get; private set; } = editedAgv;
}

public class ActionEditorItem
{
    public string ActionId { get; set; } = string.Empty;

    public string ActionType { get; set; } = string.Empty;

    public string ActionDescription { get; set; } = string.Empty;

    public string BlockingType { get; set; } = "HARD";

    public string ParameterKey { get; set; } = string.Empty;

    public string ParameterValue { get; set; } = string.Empty;
}

public class ActionStateEditorItem
{
    public string ActionId { get; set; } = string.Empty;

    public string ActionType { get; set; } = string.Empty;

    public string ActionDescription { get; set; } = string.Empty;

    public string ActionStatus { get; set; } = "FINISHED";

    public string? ResultDescription { get; set; } = string.Empty;
}