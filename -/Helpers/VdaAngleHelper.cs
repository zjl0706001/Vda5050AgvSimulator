using System;

namespace Vda5050AgvSimulator.Helpers;

public static class VdaAngleHelper
{
    /// <summary>
    /// 将模拟器内部角度制转换为 VDA5050 使用的弧度制。
    /// 输入可以是0到360度，也可以是任意正负角度。
    /// 输出范围为 (-π, π]，用于 agvPosition.theta / turntableState.theta。
    /// </summary>
    public static double DegreeToVdaTheta(double angleDegree)
    {
        return DegreeToRadian(NormalizeSignedDegree(angleDegree));
    }

    /// <summary>
    /// 将 VDA5050 的弧度角转换为模拟器内部角度制。
    /// 输出范围为 0 到 360 度。
    /// </summary>
    public static double VdaThetaToDegree(double theta)
    {
        return NormalizeDegree(RadianToDegree(theta));
    }

    /// <summary>
    /// 角度转弧度。
    /// </summary>
    public static double DegreeToRadian(double angleDegree)
    {
        return angleDegree * Math.PI / 180.0;
    }

    /// <summary>
    /// 弧度转角度。
    /// </summary>
    public static double RadianToDegree(double theta)
    {
        return theta * 180.0 / Math.PI;
    }

    /// <summary>
    /// 角度归一化到 0 到 360。
    /// </summary>
    public static double NormalizeDegree(double angleDegree)
    {
        var degree = angleDegree % 360.0;

        if (degree < 0)
        {
            degree += 360.0;
        }

        return degree;
    }

    /// <summary>
    /// 角度归一化到 -180 到 180。
    /// 适合转盘/托盘相对角度显示。
    /// </summary>
    public static double NormalizeSignedDegree(double angleDegree)
    {
        var degree = NormalizeDegree(angleDegree);

        if (degree > 180.0)
        {
            degree -= 360.0;
        }

        return degree;
    }

    /// <summary>
    /// 弧度归一化到 (-π, π]。
    /// </summary>
    public static double NormalizeTheta(double theta)
    {
        while (theta > Math.PI)
        {
            theta -= 2.0 * Math.PI;
        }

        while (theta <= -Math.PI)
        {
            theta += 2.0 * Math.PI;
        }

        return theta;
    }
}
