using System;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace Vda5050AgvSimulator.Helpers;

/// <summary>
/// Popup拖动显示辅助类。
/// </summary>
public static class PopupHelper
{
    /// <summary>
    /// 将Popup居中显示到屏幕工作区。
    /// </summary>
    public static void CenterPopupInScreen(Popup? popup, FrameworkElement? owner)
    {
        if (popup == null)
        {
            return;
        }

        if (owner == null)
        {
            return;
        }

        if (popup.Child is not FrameworkElement popupChild)
        {
            return;
        }

        var placementTarget = popup.PlacementTarget as FrameworkElement ?? owner;

        popupChild.UpdateLayout();
        placementTarget.UpdateLayout();

        var popupWidth = popupChild.ActualWidth;
        var popupHeight = popupChild.ActualHeight;

        if (popupWidth <= 0 || popupHeight <= 0)
        {
            return;
        }

        var basePoint = GetPopupBasePoint(popup, placementTarget, popupWidth, popupHeight);

        var screenBasePoint = placementTarget.PointToScreen(basePoint);

        var source = PresentationSource.FromVisual(placementTarget);

        if (source?.CompositionTarget != null)
        {
            screenBasePoint = source.CompositionTarget.TransformFromDevice.Transform(screenBasePoint);
        }

        var workArea = SystemParameters.WorkArea;

        var targetHorizontalOffset = workArea.Left + (workArea.Width - popupWidth) / 2 - screenBasePoint.X;
        var targetVerticalOffset = workArea.Top + (workArea.Height - popupHeight) / 2 - screenBasePoint.Y;

        SetPopupOffsetInScreen(popup, owner, targetHorizontalOffset, targetVerticalOffset);
    }

    /// <summary>
    /// 根据拖动偏移量移动Popup，并限制Popup不超出屏幕工作区。
    /// </summary>
    public static void MovePopupInScreen(Popup? popup, FrameworkElement owner, double horizontalChange,
        double verticalChange)
    {
        if (popup == null)
        {
            return;
        }

        var targetHorizontalOffset = popup.HorizontalOffset + horizontalChange;
        var targetVerticalOffset = popup.VerticalOffset + verticalChange;

        SetPopupOffsetInScreen(popup, owner, targetHorizontalOffset, targetVerticalOffset);
    }

    /// <summary>
    /// 设置Popup偏移量，并限制Popup不超出屏幕工作区。
    /// </summary>
    private static void SetPopupOffsetInScreen(Popup? popup, FrameworkElement? owner, double targetHorizontalOffset,
        double targetVerticalOffset)
    {
        if (popup == null)
        {
            return;
        }

        if (owner == null || popup.Child is not FrameworkElement popupChild)
        {
            popup.HorizontalOffset = targetHorizontalOffset;
            popup.VerticalOffset = targetVerticalOffset;
            return;
        }

        var placementTarget = popup.PlacementTarget as FrameworkElement ?? owner;

        popupChild.UpdateLayout();
        placementTarget.UpdateLayout();

        var popupWidth = popupChild.ActualWidth;
        var popupHeight = popupChild.ActualHeight;

        if (popupWidth <= 0 || popupHeight <= 0)
        {
            popup.HorizontalOffset = targetHorizontalOffset;
            popup.VerticalOffset = targetVerticalOffset;
            return;
        }

        var basePoint = GetPopupBasePoint(popup, placementTarget, popupWidth, popupHeight);

        var screenBasePoint = placementTarget.PointToScreen(basePoint);

        var source = PresentationSource.FromVisual(placementTarget);

        if (source?.CompositionTarget != null)
        {
            screenBasePoint = source.CompositionTarget.TransformFromDevice.Transform(screenBasePoint);
        }

        var workArea = SystemParameters.WorkArea;

        const double padding = 4;

        var minHorizontalOffset = workArea.Left + padding - screenBasePoint.X;
        var maxHorizontalOffset = workArea.Right - padding - popupWidth - screenBasePoint.X;

        var minVerticalOffset = workArea.Top + padding - screenBasePoint.Y;
        var maxVerticalOffset = workArea.Bottom - padding - popupHeight - screenBasePoint.Y;

        if (minHorizontalOffset > maxHorizontalOffset)
        {
            maxHorizontalOffset = minHorizontalOffset;
        }

        if (minVerticalOffset > maxVerticalOffset)
        {
            maxVerticalOffset = minVerticalOffset;
        }

        var finalHorizontalOffset = Clamp(targetHorizontalOffset, minHorizontalOffset, maxHorizontalOffset);
        var finalVerticalOffset = Clamp(targetVerticalOffset, minVerticalOffset, maxVerticalOffset);

        if (Math.Abs(popup.HorizontalOffset - finalHorizontalOffset) > 0.1)
        {
            popup.HorizontalOffset = finalHorizontalOffset;
        }

        if (Math.Abs(popup.VerticalOffset - finalVerticalOffset) > 0.1)
        {
            popup.VerticalOffset = finalVerticalOffset;
        }
    }

    /// <summary>
    /// 根据Popup的Placement计算未偏移前的基准点。
    /// </summary>
    private static Point GetPopupBasePoint(Popup popup, FrameworkElement placementTarget, double popupWidth,
        double popupHeight)
    {
        return popup.Placement switch
        {
            PlacementMode.Bottom => new Point(0, placementTarget.ActualHeight),
            PlacementMode.Top => new Point(0, -popupHeight),
            PlacementMode.Right => new Point(placementTarget.ActualWidth, 0),
            PlacementMode.Left => new Point(-popupWidth, 0),
            PlacementMode.Center => new Point((placementTarget.ActualWidth - popupWidth) / 2,
                (placementTarget.ActualHeight - popupHeight) / 2),
            _ => new Point(0, 0)
        };
    }

    /// <summary>
    /// 将数值限制在指定范围内。
    /// </summary>
    private static double Clamp(double value, double min, double max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }
}