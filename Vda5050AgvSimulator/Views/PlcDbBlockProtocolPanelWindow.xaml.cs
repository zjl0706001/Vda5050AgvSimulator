#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Vda5050AgvSimulator.Models.Plc;
using Vda5050AgvSimulator.Services.Plc;

namespace Vda5050AgvSimulator.Views;

public partial class PlcDbBlockProtocolPanelWindow : PlcToolPage, INotifyPropertyChanged
{
    private const int RangeSplit = 200;
    private const int LastByteOffset = 400;
    // 每张卡片的目标宽度，实际宽度随窗口和对比面板宽度自动调整。
    private const int DefaultNormalCardSize = 220;
    private const int DefaultCompareCardSize = 160;

    // 另用新配置文件，避免将旧版“每行列数”误读成卡片大小。
    private static readonly string CardSizeSettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vda5050AgvSimulator", "PlcPointPanelCardSizes.txt");

    private int _normalCardSize = DefaultNormalCardSize;
    private int _compareCardSize = DefaultCompareCardSize;

    private static readonly Dictionary<string, string> ChineseSignalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LifeBit"] = "系统健康",
        ["Offline"] = "离线模式",
        ["Online"] = "在线模式",
        ["Alarm"] = "故障",
        ["EmergencyStop"] = "急停",
        ["EnterRequest"] = "请求进入",
        ["Entering"] = "正在进入",
        ["ReachedPosition"] = "小车到位",
        ["Leaving"] = "正在离开",
        ["BindComplete"] = "绑定完成",
        ["UnbindComplete"] = "解绑完成",
        ["WritePackID1Done"] = "Pack码1写入完成",
        ["WriteGuobiaoPackID1Done"] = "12位码1写入完成",
        ["WritePackID2Done"] = "Pack码2写入完成",
        ["WriteGuobiaoPackID2Done"] = "12位码2写入完成",
        ["AGVID"] = "小车编号",
        ["ErrorCode"] = "报错代码",
        ["PACKLayers"] = "Pack层数",
        ["AGVModuleType"] = "小车模组类型",
        ["PackID1"] = "Pack码1",
        ["ShippingCode1"] = "传输码1",
        ["PackID2"] = "Pack码2",
        ["ShippingCode2"] = "传输码2",
        ["UPComplete"] = "上升完成",
        ["DownComplete"] = "下降完成",
        ["Block1LimitpieceStatus"] = "1号限位片状态",
        ["Block2LimitpieceStatus"] = "2号限位片状态",
        ["AGVBodyAmount"] = "小车载体数量",
        ["EmergencyStopOK"] = "急停正常",
        ["EnterInterlock"] = "进入互锁",
        ["EnterAllowed"] = "允许进入",
        ["LeaveInterlock"] = "离开互锁",
        ["LeaveRequest"] = "请求离开",
        ["Bypass"] = "屏蔽",
        ["RequestWritePackID1"] = "请求写入Pack码1",
        ["Route"] = "小车执行路径",
        ["CheckAGVModuleType"] = "检查小车模组类型",
        ["RequestUP"] = "请求上升",
        ["RequestDOWN"] = "请求下降",
        ["Clampingposition"] = "小车夹紧位置",
        ["HeatStationRequestEmptyPallet"] = "加热请求回流空车",
        ["HeatStationErr"] = "加热库故障",
        ["HeatCycleAgvAmount"] = "加热回流小车数量"
    };

    private static readonly char[] RemarkSeparators = ['：', ':', '，', ',', '。', '；', ';', '（', '(', '\r', '\n'];

    private readonly DispatcherTimer _refreshTimer;
    private readonly PanelTarget _initialTarget;
    private PanelTarget _selectedTarget;
    private PanelRange _leftRange;
    private PanelRange _rightRange;
    private Button? _firstValueButton;
    private Button? _pendingEditButton;
    private Button? _editorSourceButton;
    private PointCard? _editingCard;

    // 配置窗口用初始目标查找已打开的弹窗。
    public int PlcId => _initialTarget.PlcId;

    public int DbNumber => _initialTarget.DbNumber;

    public IReadOnlyList<PanelTarget> Targets { get; }

    public IReadOnlyList<PanelRange> RangeOptions { get; }

    // 数字单位为 WPF DIP（100% 缩放时可理解为 px）；不再固定列数。
    public IReadOnlyList<int> CardSizeOptions { get; } =
        new[] { 100, 120, 140, 160, 180, 200, 220, 240, 260, 280, 300, 320, 360 };

    // 100~160 的卡片用上下结构展示信号、地址和数值，避免挤压。
    public bool IsCompactCard => CardSize < 180;

    // 窄卡片同步缩小，宽卡片上的编辑条最多占 180 个 WPF 单位。
    public double EditorPopupWidth => Math.Max(100, Math.Min(180, CardSize));

    public int CardSize
    {
        get => IsComparisonSelected ? _compareCardSize : _normalCardSize;
        set
        {
            if (!CardSizeOptions.Contains(value) || value == CardSize)
            {
                return;
            }

            if (IsComparisonSelected)
            {
                _compareCardSize = value;
            }
            else
            {
                _normalCardSize = value;
            }

            CloseValueEditor(restoreFocus: false);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCompactCard));
            OnPropertyChanged(nameof(EditorPopupWidth));
            SaveCardSizeSettings();
        }
    }

    public PanelTarget SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            // 外部配置页可能重新激活此面板，编辑期间继续使用打开时的目标。
            if (value == null || ReferenceEquals(value, _selectedTarget) || _editingCard != null)
            {
                return;
            }

            CancelPendingEditGesture();
            _selectedTarget = value;
            OperationFeedbackText.Text = string.Empty;
            UpdateTitle();
            RefreshValues();
            OnPropertyChanged();
        }
    }

    public PanelRange LeftRange
    {
        get => _leftRange;
        set => SetComparisonRange(ref _leftRange, value);
    }

    public PanelRange RightRange
    {
        get => _rightRange;
        set => SetComparisonRange(ref _rightRange, value);
    }

    private bool IsComparisonSelected => RangeTabs?.SelectedItem is TabItem { Tag: "Compare" };

    public event PropertyChangedEventHandler? PropertyChanged;

    public PlcDbBlockProtocolPanelWindow(int plcId, int dbNumber, IEnumerable<PlcProtocolBoardRowModel> rows)
    {
        var savedPlcs = PlcStoreService.Instance.GetSnapshotList();
        var plcNames = savedPlcs.GroupBy(plc => plc.PlcId).ToDictionary(group => group.Key, group =>
        {
            var name = group.First().PlcName?.Trim();
            return name is { Length: > 0 } ? name : $"PLC-{group.Key}";
        });
        if (!plcNames.ContainsKey(plcId))
        {
            plcNames.Add(plcId, $"PLC-{plcId}");
        }

        var duplicateNames = new HashSet<string>(plcNames.Values
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key), StringComparer.OrdinalIgnoreCase);

        string GetPlcLabel(int id) => duplicateNames.Contains(plcNames[id]) ? $"{plcNames[id]}（{id}）" : plcNames[id];

        // 当前目标可含未应用的自定义项；其他目标从全局协议生成视图。
        _initialTarget = new PanelTarget(plcId, dbNumber, GetPlcLabel(plcId),
            () => rows.Where(row => row != null).Select(row => row.ToProtocolItem()));
        _selectedTarget = _initialTarget;
        _leftRange = _initialTarget.AgvToPlc;
        _rightRange = _initialTarget.PlcToAgv;
        var targets = new List<PanelTarget> { _initialTarget };
        var targetKeys = new HashSet<(int PlcId, int DbNumber)> { (plcId, dbNumber) };
        foreach (var plc in savedPlcs)
        {
            if (plc.PlcId <= 0 || plc.DbBlocks == null)
            {
                continue;
            }

            foreach (var block in plc.DbBlocks)
            {
                if (block == null || block.DbNumber is <= 0 or > ushort.MaxValue ||
                    !targetKeys.Add((plc.PlcId, block.DbNumber)))
                {
                    continue;
                }

                targets.Add(new PanelTarget(plc.PlcId, block.DbNumber, GetPlcLabel(plc.PlcId),
                    () => PlcProtocolTemplate.Basic.GetEffectiveItems(block)));
            }
        }

        Targets = targets.OrderBy(target => target.PlcId).ThenBy(target => target.DbNumber).ToList();
        RangeOptions = Targets.SelectMany(target => new[] { target.AgvToPlc, target.PlcToAgv }).ToList();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        _refreshTimer.Tick += (_, _) =>
        {
            if (IsVisible)
            {
                RefreshValues();
            }
        };

        LoadCardSizeSettings();
        InitializeComponent();
        UpdateTitle();
        DataContext = this;

        Loaded += (_, _) =>
        {
            RefreshValues();
            _refreshTimer.Start();
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            CloseValueEditor(restoreFocus: false);
        };
    }

    internal void SelectInitialTarget()
    {
        SelectedTarget = _initialTarget;
    }

    private void SetComparisonRange(ref PanelRange current, PanelRange? value,
        [CallerMemberName] string? propertyName = null)
    {
        if (value == null || ReferenceEquals(current, value) || _editingCard != null)
        {
            return;
        }

        CancelPendingEditGesture();
        current = value;
        OperationFeedbackText.Text = string.Empty;
        RefreshValues();
        OnPropertyChanged(propertyName);
    }

    private void UpdateTitle()
    {
        Title = IsComparisonSelected ? "点位操作面板 · 左右对比" : $"{SelectedTarget.DisplayName} · 点位操作面板";
    }

    private void LoadCardSizeSettings()
    {
        try
        {
            if (!File.Exists(CardSizeSettingsFile))
            {
                return;
            }

            var saved = File.ReadAllLines(CardSizeSettingsFile);
            if (saved.Length >= 1 && int.TryParse(saved[0], out var normal) &&
                CardSizeOptions.Contains(normal))
            {
                _normalCardSize = normal;
            }

            if (saved.Length >= 2 && int.TryParse(saved[1], out var compare) &&
                CardSizeOptions.Contains(compare))
            {
                _compareCardSize = compare;
            }
        }
        catch (IOException)
        {
            // 读取失败使用默认尺寸，不影响 PLC 监控。
        }
        catch (UnauthorizedAccessException)
        {
            // 读取失败使用默认尺寸，不影响 PLC 监控。
        }
    }

    private void SaveCardSizeSettings()
    {
        try
        {
            var directory = Path.GetDirectoryName(CardSizeSettingsFile);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllLines(CardSizeSettingsFile,
                new[] { _normalCardSize.ToString(), _compareCardSize.ToString() });
        }
        catch (IOException)
        {
            // 配置不可写不影响本次显示。
        }
        catch (UnauthorizedAccessException)
        {
            // 配置不可写不影响本次显示。
        }
    }

    private static string GetPointDisplayName(PlcProtocolBoardRowModel point, int byteOffset)
    {
        var name = point.SignalName?.Trim() ?? string.Empty;
        if (HasChineseCharacters(name))
        {
            return name;
        }

        // 旧模板的四个写码请求共用名称，按 DBX201 的位号区分。
        if (byteOffset == 201 && point.IsBoolean &&
            name.Equals("RequestWritePackID1", StringComparison.OrdinalIgnoreCase))
        {
            var address = point.OffsetAddress.Split(':')[0].Trim().Replace(" ", string.Empty);
            var bitSeparator = address.LastIndexOf('.');
            if (bitSeparator >= 0 && bitSeparator == address.Length - 2)
            {
                var requestName = address[bitSeparator + 1] switch
                {
                    '2' => "请求写入Pack码1",
                    '3' => "请求写入传输码1",
                    '4' => "请求写入Pack码2",
                    '5' => "请求写入传输码2",
                    _ => null
                };
                if (requestName != null)
                {
                    return requestName;
                }
            }
        }

        if (ChineseSignalNames.TryGetValue(name, out var chineseName))
        {
            return chineseName;
        }

        var remark = point.Remark?.Trim() ?? string.Empty;
        var separator = remark.IndexOfAny(RemarkSeparators);
        var remarkName = separator >= 0 ? remark.Substring(0, separator).Trim() : remark;
        if (HasChineseCharacters(remarkName))
        {
            return remarkName;
        }

        return name.Length > 0 ? name : $"点位 {point.ProtocolItemId}";
    }

    private static bool HasChineseCharacters(string text)
    {
        return text.Any(character => character is >= '\u3400' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF');
    }

    private void RefreshValues()
    {
        var (first, second) = GetActiveRanges();
        RefreshRange(first);
        if (!ReferenceEquals(first, second))
        {
            RefreshRange(second);
        }
    }

    private static void RefreshRange(PanelRange range)
    {
        var target = range.Target;
        target.IsPlcRunning = PlcVirtualServerService.Instance.IsPlcRunning(target.PlcId);

        foreach (var card in range.Cards)
        {
            PlcProtocolBoardValueService.TryRefreshRowValue(target.PlcId, target.DbNumber, card.Point);
        }
    }

    private (PanelRange First, PanelRange Second) GetActiveRanges()
    {
        return IsComparisonSelected ? (LeftRange, RightRange) : (SelectedTarget.AgvToPlc, SelectedTarget.PlcToAgv);
    }

    private bool IsActiveCard(PointCard card)
    {
        var (first, second) = GetActiveRanges();
        return first.Cards.Contains(card) || second.Cards.Contains(card);
    }

    private void PointValueButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button button || _editingCard != null || !button.IsEnabled)
        {
            return;
        }

        if (e.ClickCount == 1)
        {
            _firstValueButton = button;
            return;
        }

        // 编辑条收起后，连续点击确认键的第二下可能落到下方其他点位。
        if (e.ClickCount != 2 || !ReferenceEquals(_firstValueButton, button) ||
            button.DataContext is not PointCard card ||
            !IsActiveCard(card))
        {
            return;
        }

        _firstValueButton = null;
        e.Handled = true;
        if (card.Point.IsBoolean)
        {
            WriteRowValue(card);
            return;
        }

        // 第二次按下只捕获鼠标，等这次点击在同一按钮上释放后再进入编辑。
        if (button.CaptureMouse())
        {
            _pendingEditButton = button;
        }
    }

    private void PointValueButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button button || !ReferenceEquals(_pendingEditButton, button))
        {
            return;
        }

        e.Handled = true;
        var bounds = new Rect(0, 0, button.ActualWidth, button.ActualHeight);
        var releasedInside = button.IsMouseCaptured && bounds.Contains(e.GetPosition(button));
        CancelPendingEditGesture();
        if (!releasedInside || !button.IsEnabled || !button.IsVisible || _editingCard != null ||
            button.DataContext is not PointCard card || card.Point.IsBoolean || !IsActiveCard(card))
        {
            return;
        }

        PlcProtocolBoardValueService.TryRefreshRowValue(card.Target.PlcId, card.Target.DbNumber, card.Point);
        _editingCard = card;
        _editorSourceButton = button;
        ValueEditorFlyout.DataContext = card;
        // 输入框只在打开时取一次当前值，后续轮询可以继续更新卡片。
        ValueEditorText.Text = card.Point.CurrentValue ?? string.Empty;
        ValueEditorError.Text = string.Empty;
        ValueEditorFlyout.Visibility = Visibility.Visible;
        ValueEditorFlyout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        PositionValueEditor();
        ValueEditorLayer.LayoutUpdated += ValueEditorLayer_LayoutUpdated;
        ValueEditorText.Focus();
        ValueEditorText.SelectAll();
    }

    private void PointValueButton_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(_pendingEditButton, sender))
        {
            _pendingEditButton = null;
        }
    }

    private void CancelPendingEditGesture()
    {
        var button = _pendingEditButton;
        _firstValueButton = null;
        _pendingEditButton = null;
        if (button?.IsMouseCaptured == true)
        {
            button.ReleaseMouseCapture();
        }
    }

    private void ValueEditorLayer_LayoutUpdated(object? sender, EventArgs e)
    {
        PositionValueEditor();
    }

    private void PositionValueEditor()
    {
        var source = _editorSourceButton;
        if (source == null || !source.IsLoaded || !source.IsVisible)
        {
            return;
        }

        const double gap = 3;
        var anchor = new Rect(source.TranslatePoint(new Point(), ValueEditorLayer), source.RenderSize);
        var size = ValueEditorFlyout.DesiredSize;
        var viewport = ValueEditorLayer.RenderSize;
        var left = Math.Max(gap, Math.Min(anchor.Right - size.Width, viewport.Width - size.Width - gap));
        var top = anchor.Bottom + gap;

        // 点击底部点位时向上展开。点位滚出视口后，编辑条随它一起滚出。
        if (anchor.Bottom <= 0 || (anchor.Top < viewport.Height && top + size.Height > viewport.Height - gap))
        {
            top = anchor.Top - size.Height - gap;
        }

        if (Canvas.GetLeft(ValueEditorFlyout) != left)
        {
            Canvas.SetLeft(ValueEditorFlyout, left);
        }
        if (Canvas.GetTop(ValueEditorFlyout) != top)
        {
            Canvas.SetTop(ValueEditorFlyout, top);
        }
    }

    private void PageContent_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 编辑条在页面内容的兄弟图层中。这里仅收到用户下一次点向页面的按下事件。
        if (e.ClickCount == 1)
        {
            _firstValueButton = null;
        }
        if (_editingCard != null)
        {
            CloseValueEditor(restoreFocus: false);
        }
    }

    private void SaveValueButton_Click(object sender, RoutedEventArgs e)
    {
        SaveEditedValue();
    }

    private void SaveEditedValue()
    {
        var card = _editingCard;
        if (card == null)
        {
            return;
        }

        if (WriteRowValue(card, ValueEditorText.Text))
        {
            CloseValueEditor();
            return;
        }

        ValueEditorError.Text = card.Point.LastMessage;
        if (string.IsNullOrWhiteSpace(ValueEditorError.Text))
        {
            ValueEditorError.Text = "写入失败，请检查输入值和 PLC 状态。";
        }
        ValueEditorText.Focus();
    }

    private bool WriteRowValue(PointCard card, string? explicitValue = null)
    {
        if (!IsActiveCard(card))
        {
            card.Point.LastMessage = "目标点位已切换，请重新选择。";
            return false;
        }

        var target = card.Target;
        var written = PlcProtocolBoardValueService.TryWriteRowValue(
            target.PlcId, target.DbNumber, card.Point, explicitValue);
        OperationFeedbackText.Text = $"{target.DisplayName} · {card.DisplayName} · {card.Point.LastMessage}";
        OperationFeedbackText.Foreground = written ? Brushes.SeaGreen : Brushes.Firebrick;
        return written;
    }

    private void CancelValueButton_Click(object sender, RoutedEventArgs e)
    {
        CloseValueEditor();
    }

    private void CloseValueEditor(bool restoreFocus = true)
    {
        CancelPendingEditGesture();
        if (_editingCard == null)
        {
            return;
        }

        var source = _editorSourceButton;
        _editingCard = null;
        _editorSourceButton = null;
        ValueEditorLayer.LayoutUpdated -= ValueEditorLayer_LayoutUpdated;
        ValueEditorFlyout.Visibility = Visibility.Collapsed;
        ValueEditorFlyout.DataContext = null;
        if (restoreFocus && source != null && source.IsVisible && source.IsEnabled)
        {
            source.Focus();
        }
    }

    private void ValueEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ValueEditorText.IsKeyboardFocused)
        {
            e.Handled = true;
            if (!e.IsRepeat)
            {
                SaveEditedValue();
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseValueEditor();
        }
    }

    private void RangeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender) && IsLoaded)
        {
            CloseValueEditor(restoreFocus: false);
            OperationFeedbackText.Text = string.Empty;
            UpdateTitle();
            OnPropertyChanged(nameof(CardSize));
            OnPropertyChanged(nameof(IsCompactCard));
            OnPropertyChanged(nameof(EditorPopupWidth));
            RefreshValues();
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshValues();
    }


    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class PanelTarget : INotifyPropertyChanged
    {
        private bool _isPlcRunning;

        public int PlcId { get; }

        public int DbNumber { get; }

        public string DisplayName { get; }

        public PanelRange AgvToPlc { get; }

        public PanelRange PlcToAgv { get; }

        public bool IsPlcRunning
        {
            get => _isPlcRunning;
            internal set
            {
                if (_isPlcRunning != value)
                {
                    _isPlcRunning = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlcRunning)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal PanelTarget(int plcId, int dbNumber, string plcLabel,
            Func<IEnumerable<PlcDbBlockProtocolItemModel>> getItems)
        {
            PlcId = plcId;
            DbNumber = dbNumber;
            DisplayName = $"{plcLabel} / DB{dbNumber}";

            // 选择器只创建范围标签；首次查看该目标时再创建卡片和独立的实时值。
            var cards = new Lazy<(IReadOnlyList<PointCard> Lower, IReadOnlyList<PointCard> Upper)>(
                () => BuildCards(getItems()));
            AgvToPlc = new PanelRange(this, "AGV→PLC", () => cards.Value.Lower);
            PlcToAgv = new PanelRange(this, "PLC→AGV", () => cards.Value.Upper);
        }

        private (IReadOnlyList<PointCard> Lower, IReadOnlyList<PointCard> Upper) BuildCards(
            IEnumerable<PlcDbBlockProtocolItemModel> items)
        {
            var lower = new List<PointCard>();
            var upper = new List<PointCard>();
            foreach (var item in items)
            {
                if (item == null ||
                    !PlcVirtualServerService.TryGetProtocolByteOffset(item.OffsetAddress, out var byteOffset) ||
                    byteOffset > LastByteOffset) continue;

                var point = PlcProtocolBoardRowModel.FromProtocolItem(item);
                var range = byteOffset < RangeSplit ? lower : upper;
                range.Add(new PointCard(this, point, GetPointDisplayName(point, byteOffset)));
            }
            return (lower, upper);
        }
    }

    public sealed class PanelRange
    {
        private readonly Func<IReadOnlyList<PointCard>> _getCards;
        public PanelTarget Target { get; }
        public string DisplayName { get; }
        public IReadOnlyList<PointCard> Cards => _getCards();

        internal PanelRange(PanelTarget target, string direction, Func<IReadOnlyList<PointCard>> getCards)
        {
            Target = target;
            DisplayName = $"{target.DisplayName} · {direction}";
            _getCards = getCards;
        }
    }

    public sealed class PointCard
    {
        public PanelTarget Target { get; }

        public PlcProtocolBoardRowModel Point { get; }

        public string DisplayName { get; }

        internal PointCard(PanelTarget target, PlcProtocolBoardRowModel point, string displayName)
        {
            Target = target;
            Point = point;
            DisplayName = displayName;
        }
    }
}


/// <summary>
/// 根据卡片目标尺寸和当前点位容器的实际宽度，计算每张卡片的显示宽度。
/// 例如：容器 870、目标尺寸 200 -> 四列，每张约 217；变窄时自动换成三列/两列/一列。
/// 每次窗口尺寸变化时，MultiBinding 会重新计算，完全不需要内部滚动条。
/// </summary>
public sealed class AdaptivePointCardWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        const double defaultDesiredWidth = 220;
        double desiredWidth = values.Length > 0 && values[0] is int size && size > 0
            ? size : defaultDesiredWidth;

        if (values.Length < 2 || values[1] is not double availableWidth ||
            double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 1)
        {
            return desiredWidth;
        }

        // 减去微小余量，防止浮点/像素取整引起最后一张卡片意外折行。
        double safeWidth = Math.Max(1, availableWidth - 2);
        int columns = Math.Max(1, (int)Math.Floor(safeWidth / desiredWidth));
        return Math.Max(1, Math.Floor(safeWidth / columns));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("卡片宽度仅支持单向计算。");
    }
}
