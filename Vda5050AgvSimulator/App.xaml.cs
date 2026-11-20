using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Vda5050AgvSimulator.Services.Agv;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows.Threading;
using Vda5050AgvSimulator.Services.Plc;

namespace Vda5050AgvSimulator;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private const string MutexName = "Vda5050AgvSimulator_SingleInstance_Mutex";
    private const string PipeName = "Vda5050AgvSimulator_SingleInstance_Pipe";

    private static Mutex? _appMutex;
    private static bool _createdNew;

    private static CancellationTokenSource? _pipeCancellationTokenSource;
    private static Task? _pipeTask;

    public App()
    {
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _appMutex = new Mutex(
            true,
            MutexName,
            out _createdNew);

        if (!_createdNew)
        {
            NotifyExistingInstanceShowWindow();

            Shutdown();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += App_UnhandledException;

        DispatcherUnhandledException += App_DispatcherUnhandledException;

        ConfigStoreService.Instance.EnsureLoaded();

        MapStoreService.Initialize();

        ConfigStoreService.Instance.StartMapConfigWatcher();

        ConfigStoreService.Instance.MapConfigReloadFailed += (_, message) => { };

        StartSingleInstancePipeServer();

        base.OnStartup(e);
    }

    /// <summary>
    /// 非UI线程未处理异常。
    /// </summary>
    private static void App_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (IsWpfInvalidWindowHandleException(e.ExceptionObject as Exception))
        {
            return;
        }

        MessageBox.Show(e.ExceptionObject?.ToString() ?? string.Empty);
    }

    /// <summary>
    /// UI线程未处理异常。
    /// </summary>
    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (IsWpfInvalidWindowHandleException(e.Exception))
        {
            e.Handled = true;
            return;
        }

        MessageBox.Show(e.Exception.ToString());

        e.Handled = true;
    }

    /// <summary>
    /// 是否为WPF窗口句柄失效异常。
    /// 该异常常见于 WindowChrome / HwndMouseInputProvider / GetWindowRect。
    /// </summary>
    private static bool IsWpfInvalidWindowHandleException(Exception? ex)
    {
        while (ex != null)
        {
            if (ex is TargetInvocationException && ex.InnerException != null)
            {
                ex = ex.InnerException;
                continue;
            }

            if (ex is Win32Exception win32Exception)
            {
                if (win32Exception.NativeErrorCode == 1400 ||
                    win32Exception.NativeErrorCode == 0x578 ||
                    win32Exception.ErrorCode == unchecked((int)0x80070578) ||
                    win32Exception.HResult == unchecked((int)0x80070578))
                {
                    var stack = ex.StackTrace ?? string.Empty;

                    if (stack.Contains("WindowChromeWorker") ||
                        stack.Contains("HwndMouseInputProvider") ||
                        stack.Contains("GetWindowRect") ||
                        stack.Contains("GetEffectiveClientArea") ||
                        stack.Contains("GetEffectiveClientRect") ||
                        stack.Contains("PossiblyDeactivate"))
                    {
                        return true;
                    }

                    return true;
                }
            }

            var exceptionStack = ex.StackTrace ?? string.Empty;

            if (exceptionStack.Contains("WindowChromeWorker") &&
                exceptionStack.Contains("GetWindowRect"))
            {
                return true;
            }

            if (exceptionStack.Contains("HwndMouseInputProvider") &&
                exceptionStack.Contains("GetEffectiveClientRect"))
            {
                return true;
            }

            ex = ex.InnerException;
        }

        return false;
    }

    /// <summary>
    /// 应用退出时统一关闭所有仍由 WPF 管理的窗口，包括没有 Owner 的独立窗口。
    /// 使用快照倒序关闭，避免关闭过程中修改 Application.Windows 集合。
    /// </summary>
    private static void CloseAllApplicationWindows()
    {
        if (Current == null)
        {
            return;
        }

        void CloseWindows()
        {
            var windows = Current.Windows.Cast<Window>().ToList();
            var mainWindow = Current.MainWindow;

            foreach (var window in windows.Where(x => !ReferenceEquals(x, mainWindow)))
            {
                try
                {
                    window.Close();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"关闭子窗口失败：{window.GetType().Name}，{ex}");
                }
            }

            if (mainWindow != null)
            {
                try
                {
                    mainWindow.Close();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"关闭主窗口失败：{ex}");
                }
            }
        }

        if (Current.Dispatcher.CheckAccess())
        {
            CloseWindows();
        }
        else
        {
            Current.Dispatcher.Invoke(CloseWindows);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            StopSingleInstancePipeServer();

            // 退出前关闭全部 WPF 窗口（包括未设置 Owner 的独立窗口）。
            CloseAllApplicationWindows();

            if (_createdNew)
            {
                var plcService = PlcVirtualServerService.Instance;

                try
                {
                    foreach (var plc in PlcStoreService.Instance.GetSnapshotList())
                    {
                        if (plc != null)
                        {
                            plcService.StopPlc(plc.PlcId);
                        }
                    }
                }
                finally
                {
                    plcService.StopAllPlcs();
                }
            }
        }
        finally
        {
            if (_createdNew)
            {
                _appMutex?.ReleaseMutex();
            }

            _appMutex?.Dispose();
            _appMutex = null;

            base.OnExit(e);
        }
    }

    #region 单实例通信

    /// <summary>
    /// 启动单实例管道服务
    /// </summary>
    private static void StartSingleInstancePipeServer()
    {
        _pipeCancellationTokenSource = new CancellationTokenSource();

        _pipeTask = Task.Run(() => { RunSingleInstancePipeServer(_pipeCancellationTokenSource.Token); });
    }

    /// <summary>
    /// 停止单实例管道服务
    /// </summary>
    private static void StopSingleInstancePipeServer()
    {
        try
        {
            _pipeCancellationTokenSource?.Cancel();
        }
        catch
        {
        }

        _pipeCancellationTokenSource?.Dispose();
        _pipeCancellationTokenSource = null;
        _pipeTask = null;
    }

    /// <summary>
    /// 单实例管道服务
    /// </summary>
    private static void RunSingleInstancePipeServer(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipeServer = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                pipeServer.WaitForConnection();

                using var reader = new StreamReader(pipeServer);

                var message = reader.ReadLine();

                if (message == "SHOW")
                {
                    RestoreMainWindowFromTray();
                }
            }
            catch
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 通知已运行程序显示窗口
    /// </summary>
    private static void NotifyExistingInstanceShowWindow()
    {
        try
        {
            using var pipeClient = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out);

            pipeClient.Connect(800);

            using var writer = new StreamWriter(pipeClient)
            {
                AutoFlush = true
            };

            writer.WriteLine("SHOW");
        }
        catch
        {
            // 原程序可能还没完成初始化，通知失败时第二个实例直接退出
        }
    }

    #endregion

    #region 恢复主窗口

    /// <summary>
    /// 从托盘恢复主窗口
    /// </summary> 
    public static void RestoreMainWindowFromTray()
    {
        try
        {
            Current.Dispatcher.Invoke(() =>
            {
                var window = GetMainWindow();

                if (window == null)
                {
                    return;
                }

                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                if (window.Visibility != Visibility.Visible)
                {
                    window.Visibility = Visibility.Visible;
                }

                if (!window.ShowInTaskbar)
                {
                    window.ShowInTaskbar = true;
                }

                if (!window.IsVisible)
                {
                    window.Show();
                }

                var helper = new WindowInteropHelper(window);
                var hwnd = helper.Handle;

                if (hwnd == IntPtr.Zero)
                {
                    hwnd = helper.EnsureHandle();
                }

                if (hwnd != IntPtr.Zero)
                {
                    if (IsIconic(hwnd))
                    {
                        ShowWindow(hwnd, SW_RESTORE);
                    }
                    else
                    {
                        ShowWindow(hwnd, SW_SHOW);
                    }

                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }

                window.Activate();
                window.Focus();
            });
        }
        catch
        {
        }
    }

    /// <summary>
    /// 获取主窗口
    /// </summary>
    private static Window? GetMainWindow()
    {
        if (Application.Current.MainWindow != null)
        {
            return Application.Current.MainWindow;
        }

        foreach (Window window in Application.Current.Windows)
        {
            return window;
        }

        return null;
    }

    #endregion

    #region Win32 API

    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    #endregion
}