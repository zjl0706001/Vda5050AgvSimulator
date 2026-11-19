using System;
using System.IO;
using System.Threading;

namespace Vda5050AgvSimulator.Helpers;

public static class FreezeTrace
{
    private static readonly object LockObj = new();

    private static readonly string LogPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "freeze_trace.log");

    public static void Write(string msg)
    {
        try
        {
            lock (LockObj)
            {
                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:HH:mm:ss.fff} [T:{Thread.CurrentThread.ManagedThreadId}] {msg}\r\n");
            }
        }
        catch
        {
            // 诊断日志不能影响主流程
        }
    }
}