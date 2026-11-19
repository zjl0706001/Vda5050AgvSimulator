using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Vda5050AgvSimulator.Services.Agv;

/// <summary>
/// 运行卡死诊断服务。
/// 兼容 .NET Framework 4.8 / .NET Core / .NET。
/// </summary>
public static class FreezeDiagnosticService
{
    private const int MaxQueueCount = 2000;

    private const int LogBatchDelayMs = 200;

    private static readonly ConcurrentQueue<string> LogQueue = new();

    private static readonly SemaphoreSlim LogSignal = new(0);

    private static readonly ConcurrentDictionary<string, DateTime> Heartbeats = new();

    private static readonly ConcurrentDictionary<string, long> LastRateLimitedLogTicks = new();

    private static readonly object StartLock = new();

    private static int _writerStarted;

    private static long _droppedLogCount;

    private static int _queuedLogCount;

    /// <summary>
    /// 写一条普通诊断日志。
    /// </summary>
    public static void Write(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        StartWriterOnce();

        var queuedCount = Interlocked.Increment(ref _queuedLogCount);

        if (queuedCount > MaxQueueCount)
        {
            Interlocked.Decrement(ref _queuedLogCount);
            Interlocked.Increment(ref _droppedLogCount);
            return;
        }

        var line =
            $"[{DateTime.Now:HH:mm:ss.fff}] [T{Thread.CurrentThread.ManagedThreadId}] {message}";

        LogQueue.Enqueue(line);

        LogSignal.Release();
    }

    /// <summary>
    /// 按Key限频写诊断日志。
    /// 用于耗时、超时等可能连续发生的告警，避免卡顿后日志反过来放大CPU、IO和字符串分配。
    /// </summary>
    public static void WriteRateLimited(
        string key,
        string message,
        int minIntervalMilliseconds = 5000)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        key = string.IsNullOrWhiteSpace(key) ? message : key;

        var nowTicks = DateTime.UtcNow.Ticks;
        var intervalTicks = TimeSpan
            .FromMilliseconds(Math.Max(1, minIntervalMilliseconds))
            .Ticks;

        while (true)
        {
            if (!LastRateLimitedLogTicks.TryGetValue(key, out var lastTicks))
            {
                if (LastRateLimitedLogTicks.TryAdd(key, nowTicks))
                {
                    Write(message);
                    return;
                }

                continue;
            }

            if (nowTicks - lastTicks < intervalTicks)
            {
                return;
            }

            if (LastRateLimitedLogTicks.TryUpdate(key, nowTicks, lastTicks))
            {
                Write(message);
                return;
            }
        }
    }

    /// <summary>
    /// 标记一个模块仍然存活。
    /// </summary>
    public static void Heartbeat(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Heartbeats[name] = DateTime.UtcNow;
    }

    /// <summary>
    /// 获取指定模块距离上次心跳的毫秒数。
    /// </summary>
    public static long GetHeartbeatAgeMilliseconds(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return -1;
        }

        if (!Heartbeats.TryGetValue(name, out var lastTime))
        {
            return -1;
        }

        var age = DateTime.UtcNow - lastTime;

        return Math.Max(0, (long)age.TotalMilliseconds);
    }

    /// <summary>
    /// 输出心跳过慢的模块。
    /// 可在 WatchDog 或主循环中低频调用。
    /// </summary>
    public static void DumpSlowHeartbeats(int warnMilliseconds = 5000)
    {
        var now = DateTime.UtcNow;

        foreach (var item in Heartbeats.ToArray())
        {
            var age = Math.Max(0, (long)(now - item.Value).TotalMilliseconds);

            if (age >= warnMilliseconds)
            {
                WriteRateLimited(
                    "HEARTBEAT_" + item.Key,
                    $"心跳超时：{item.Key} 已 {age}ms 未更新",
                    warnMilliseconds);
            }
        }
    }

    /// <summary>
    /// 创建耗时统计范围。
    /// using (FreezeDiagnosticService.Scope("xxx", 300)) { ... }
    /// 超过 warnMilliseconds 才写日志，避免日志过多。
    /// </summary>
    public static IDisposable Scope(string name, int warnMilliseconds = 300)
    {
        return new ScopeRuntime(name, warnMilliseconds);
    }

    private static void StartWriterOnce()
    {
        if (Volatile.Read(ref _writerStarted) == 1)
        {
            return;
        }

        lock (StartLock)
        {
            if (_writerStarted == 1)
            {
                return;
            }

            _writerStarted = 1;

            Task.Run(WriterLoopAsync);
        }
    }

    private static async Task WriterLoopAsync()
    {
        while (true)
        {
            try
            {
                await LogSignal.WaitAsync().ConfigureAwait(false);

                // 合并短时间内连续产生的诊断日志，减少AppendAllText调用次数。
                await Task.Delay(LogBatchDelayMs).ConfigureAwait(false);

                while (LogSignal.Wait(0))
                {
                }

                var builder = new StringBuilder();

                while (LogQueue.TryDequeue(out var line))
                {
                    Interlocked.Decrement(ref _queuedLogCount);
                    builder.AppendLine(line);

                    if (builder.Length >= 32 * 1024)
                    {
                        break;
                    }
                }

                var dropped = Interlocked.Exchange(ref _droppedLogCount, 0);

                if (dropped > 0)
                {
                    builder.AppendLine(
                        $"[{DateTime.Now:HH:mm:ss.fff}] [T{Thread.CurrentThread.ManagedThreadId}] 诊断日志队列过长，已丢弃 {dropped} 条");
                }

                if (builder.Length <= 0)
                {
                    continue;
                }

                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FreezeLogs");

                Directory.CreateDirectory(dir);

                var file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd") + "_freeze.log");

                // 注意：这里必须写 System.IO.File，避免被项目中的 Services.File 命名空间干扰。
                System.IO.File.AppendAllText(file, builder.ToString(), Encoding.UTF8);

                if (!LogQueue.IsEmpty)
                {
                    LogSignal.Release();
                }
            }
            catch
            {
                if (!LogQueue.IsEmpty)
                {
                    try
                    {
                        LogSignal.Release();
                    }
                    catch
                    {
                    }
                }

                await Task.Delay(1000).ConfigureAwait(false);
            }
        }
    }

    private sealed class ScopeRuntime : IDisposable
    {
        private readonly string _name;

        private readonly int _warnMilliseconds;

        private readonly Stopwatch _stopwatch;

        public ScopeRuntime(string name, int warnMilliseconds)
        {
            _name = string.IsNullOrWhiteSpace(name) ? "未命名耗时范围" : name;

            _warnMilliseconds = Math.Max(1, warnMilliseconds);

            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();

            if (_stopwatch.ElapsedMilliseconds >= _warnMilliseconds)
            {
                WriteRateLimited(
                    "SCOPE_" + _name,
                    $"{_name} 耗时过长：{_stopwatch.ElapsedMilliseconds}ms",
                    5000);
            }
        }
    }
}
