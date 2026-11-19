using System;

namespace Vda5050AgvSimulator.Helpers;

public static class VdaTimeHelper
{
    /// <summary>
    /// 获取 VDA5050 报文使用的 UTC 时间戳。
    /// 
    /// 示例：
    /// 2026-05-03T12:30:00Z
    /// </summary>
    public static string GetUtcTimestamp()
    {
        return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }
}