using System;
using System.Collections.Generic;
using System.Linq;

namespace Vda5050AgvSimulator.Helpers;

/// <summary>
/// AGV旋转方向计算辅助类。
/// </summary>
public static class AgvTurnHelper
{
    /// <summary>
    /// 旋转方向。
    /// </summary>
    public enum TurnDirection
    {
        NoTurn = 0,

        /// <summary>
        /// 左旋，角度增加。
        /// </summary>
        LeftTurn = 1,

        /// <summary>
        /// 右旋，角度减少。
        /// </summary>
        RightTurn = 2
    }

    /// <summary>
    /// 旋转决策结果。
    /// </summary>
    public class TurnDecision
    {
        public TurnDirection Direction { get; set; }

        public double RotationAngle { get; set; }


        public double CurrentAngle { get; set; }

        public double TargetAngle { get; set; }

        public string ConstraintApplied { get; set; } = string.Empty;
    }

    /// <summary>
    /// 旋转约束。
    /// 注意：这里使用精确角度对匹配，不使用范围匹配。
    /// </summary>
    private class TurnConstraint
    {
        public double FromAngle { get; set; }

        public double ToAngle { get; set; }

        public TurnDirection Direction { get; set; }

        public string Description { get; set; } = string.Empty;
    }

    private static readonly List<TurnConstraint> Constraints = [];

    static AgvTurnHelper()
    {
        ResetDefaultConstraints();
    }

    /// <summary>
    /// 重置默认旋转约束。
    /// </summary>
    public static void ResetDefaultConstraints()
    {
        Constraints.Clear();

        AddConstraint(270, 90, TurnDirection.RightTurn, "270°到90°只能右旋");
        AddConstraint(90, 270, TurnDirection.LeftTurn, "90°到270°只能左旋");
        AddConstraint(0, 270, TurnDirection.NoTurn, "0°到270°不能旋转");
        AddConstraint(0, 90, TurnDirection.NoTurn, "0°到90°不能旋转");
        AddConstraint(90, 0, TurnDirection.NoTurn, "90°到0°不能旋转");
        AddConstraint(270, 0, TurnDirection.NoTurn, "270°到0°不能旋转");
    }

    /// <summary>
    /// 添加旋转约束。
    /// </summary>
    public static void AddConstraint(double fromAngle, double toAngle, TurnDirection direction, string description = "")
    {
        Constraints.Add(new TurnConstraint
        {
            FromAngle = NormalizeAngleDegree(fromAngle),
            ToAngle = NormalizeAngleDegree(toAngle),
            Direction = direction,
            Description = description
        });
    }

    /// <summary>
    /// 计算旋转决策。
    /// </summary>
    public static TurnDecision CalculateTurn(double currentAngle, double targetAngle)
    {
        currentAngle = NormalizeAngleDegree(currentAngle);
        targetAngle = NormalizeAngleDegree(targetAngle);

        var decision = new TurnDecision
        {
            CurrentAngle = currentAngle,
            TargetAngle = targetAngle
        };

        if (IsSameAngle(currentAngle, targetAngle))
        {
            decision.Direction = TurnDirection.NoTurn;
            decision.RotationAngle = 0;
            return decision;
        }

        var matchedConstraint = Constraints.FirstOrDefault(x =>
            IsSameAngle(currentAngle, x.FromAngle) &&
            IsSameAngle(targetAngle, x.ToAngle));

        var leftTurnDiff = GetLeftTurnDifference(currentAngle, targetAngle);

        var rightTurnDiff = GetRightTurnDifference(currentAngle, targetAngle);

        if (matchedConstraint != null)
        {
            decision.Direction = matchedConstraint.Direction;
            decision.ConstraintApplied = matchedConstraint.Description;

            decision.RotationAngle = matchedConstraint.Direction switch
            {
                TurnDirection.LeftTurn => leftTurnDiff,
                TurnDirection.RightTurn => rightTurnDiff,
                _ => 0
            };

            return decision;
        }

        if (leftTurnDiff <= rightTurnDiff)
        {
            decision.Direction = TurnDirection.LeftTurn;
            decision.RotationAngle = leftTurnDiff;
        }
        else
        {
            decision.Direction = TurnDirection.RightTurn;
            decision.RotationAngle = rightTurnDiff;
        }

        return decision;
    }

    /// <summary>
    /// 左旋角度差，角度增加方向。
    /// </summary>
    public static double GetLeftTurnDifference(double fromAngle, double toAngle)
    {
        var diff = (NormalizeAngleDegree(toAngle) - NormalizeAngleDegree(fromAngle)) % 360.0;

        if (diff < 0)
        {
            diff += 360.0;
        }

        return diff;
    }

    /// <summary>
    /// 右旋角度差，角度减少方向。
    /// </summary>
    public static double GetRightTurnDifference(double fromAngle, double toAngle)
    {
        var diff = (NormalizeAngleDegree(fromAngle) - NormalizeAngleDegree(toAngle)) % 360.0;

        if (diff < 0)
        {
            diff += 360.0;
        }

        return diff;
    }

    /// <summary>
    /// 角度归一化到 0 到 360。
    /// </summary>
    public static double NormalizeAngleDegree(double angleDegree)
    {
        var result = angleDegree % 360.0;

        if (result < 0)
        {
            result += 360.0;
        }

        return result;
    }

    /// <summary>
    /// 判断两个角度是否相等。
    /// </summary>
    public static bool IsSameAngle(double angle1, double angle2, double tolerance = 0.5)
    {
        angle1 = NormalizeAngleDegree(angle1);
        angle2 = NormalizeAngleDegree(angle2);

        var diff = Math.Abs(angle1 - angle2);

        diff = Math.Min(diff, 360.0 - diff);

        return diff <= tolerance;
    }
}