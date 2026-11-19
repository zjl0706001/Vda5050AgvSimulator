using HandyControl.Controls;
using HandyControl.Data;

namespace Vda5050AgvSimulator.Helpers;

/// <summary>
/// Growl消息提示辅助类。
/// </summary>
public static class GrowlHelper
{
    /// <summary>
    /// 显示成功提示。
    /// </summary>
    public static void Success(string message, string token, int waitTime = 2)
    {
        Growl.Clear(token);

        Growl.Success(new GrowlInfo
        {
            Message = message,
            Token = token,
            WaitTime = waitTime,
            ShowCloseButton = true
        });
    }

    /// <summary>
    /// 显示警告提示。
    /// </summary>
    public static void Warning(string message, string token, int waitTime = 3)
    {
        Growl.Clear(token);

        Growl.Warning(new GrowlInfo
        {
            Message = message,
            Token = token,
            WaitTime = waitTime,
            ShowCloseButton = true
        });
    }

    /// <summary>
    /// 显示错误提示。
    /// 注意：这里内部仍然使用 Warning 样式，避免 Error 类型长时间不自动关闭。
    /// </summary>
    public static void Error(string message, string token, int waitTime = 5)
    {
        Growl.Clear(token);

        Growl.Warning(new GrowlInfo
        {
            Message = message,
            Token = token,
            WaitTime = waitTime,
            ShowCloseButton = true
        });
    }

    /// <summary>
    /// 显示普通信息提示。
    /// </summary>
    public static void Info(string message, string token, int waitTime = 3)
    {
        Growl.Clear(token);

        Growl.Info(new GrowlInfo
        {
            Message = message,
            Token = token,
            WaitTime = waitTime,
            ShowCloseButton = true
        });
    }

    /// <summary>
    /// 显示不自动关闭的警告提示。
    /// </summary>
    public static void WarningStay(string message, string token)
    {
        Growl.Clear(token);

        Growl.Warning(new GrowlInfo
        {
            Message = message,
            Token = token,
            StaysOpen = true,
            ShowCloseButton = true
        });
    }

    /// <summary>
    /// 清空指定Token下的Growl提示。
    /// </summary>
    public static void Clear(string token)
    {
        Growl.Clear(token);
    }
}