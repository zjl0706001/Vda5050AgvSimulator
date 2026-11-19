#nullable disable

using System;
using System.ServiceProcess;
using System.Threading.Tasks;

namespace Vda5050AgvSimulator.Services.Windows;

/// <summary>
/// Mosquitto Windows 服务控制。
/// 只负责 Windows 服务的启动、停止及状态读取，
/// 不负责 MQTT 客户端连接、订阅和发布。
/// </summary>
public sealed class MosquittoWindowsService
{
    /// <summary>
    /// Mosquitto 安装为 Windows 服务后的服务名称。
    /// 一般默认为 mosquitto。
    /// </summary>
    private const string ServiceName = "mosquitto";

    public static MosquittoWindowsService Instance { get; } = new();

    private MosquittoWindowsService()
    {
    }

    /// <summary>
    /// 判断 Mosquitto Windows 服务是否已经安装。
    /// </summary>
    public bool IsInstalled()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);

            controller.Refresh();

            _ = controller.Status;

            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 判断 Mosquitto Windows 服务是否正在运行。
    /// </summary>
    public bool IsRunning()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);

            controller.Refresh();

            return controller.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 获取当前服务状态文字。
    /// </summary>
    public string GetStatusText()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);

            controller.Refresh();

            return controller.Status switch
            {
                ServiceControllerStatus.Running => "运行中",
                ServiceControllerStatus.Stopped => "已停止",
                ServiceControllerStatus.StartPending => "正在启动",
                ServiceControllerStatus.StopPending => "正在停止",
                ServiceControllerStatus.Paused => "已暂停",
                ServiceControllerStatus.PausePending => "正在暂停",
                ServiceControllerStatus.ContinuePending => "正在恢复",
                _ => controller.Status.ToString()
            };
        }
        catch (InvalidOperationException)
        {
            return "未安装";
        }
        catch
        {
            return "状态未知";
        }
    }

    /// <summary>
    /// 启动 Mosquitto Windows 服务。
    /// </summary>
    public Task StartAsync(TimeSpan timeout)
    {
        return Task.Run(() =>
        {
            using var controller = CreateController();

            controller.Refresh();

            if (controller.Status == ServiceControllerStatus.Running)
            {
                return;
            }

            if (controller.Status == ServiceControllerStatus.StopPending)
            {
                controller.WaitForStatus(
                    ServiceControllerStatus.Stopped,
                    timeout);
            }

            controller.Refresh();

            if (controller.Status == ServiceControllerStatus.Paused)
            {
                controller.Continue();
            }
            else if (controller.Status != ServiceControllerStatus.StartPending)
            {
                controller.Start();
            }

            controller.WaitForStatus(
                ServiceControllerStatus.Running,
                timeout);
        });
    }

    /// <summary>
    /// 停止 Mosquitto Windows 服务。
    /// </summary>
    public Task StopAsync(TimeSpan timeout)
    {
        return Task.Run(() =>
        {
            using var controller = CreateController();

            controller.Refresh();

            if (controller.Status == ServiceControllerStatus.Stopped)
            {
                return;
            }

            if (controller.Status == ServiceControllerStatus.StopPending)
            {
                controller.WaitForStatus(
                    ServiceControllerStatus.Stopped,
                    timeout);

                return;
            }

            if (controller.Status == ServiceControllerStatus.StartPending)
            {
                controller.WaitForStatus(
                    ServiceControllerStatus.Running,
                    timeout);

                controller.Refresh();
            }

            if (!controller.CanStop)
            {
                throw new InvalidOperationException(
                    "Mosquitto Windows 服务不允许停止。");
            }

            controller.Stop();

            controller.WaitForStatus(
                ServiceControllerStatus.Stopped,
                timeout);
        });
    }

    /// <summary>
    /// 创建服务控制器并检查服务是否存在。
    /// </summary>
    private static ServiceController CreateController()
    {
        var controller = new ServiceController(ServiceName);

        try
        {
            controller.Refresh();

            _ = controller.Status;

            return controller;
        }
        catch
        {
            controller.Dispose();

            throw new InvalidOperationException(
                $"未找到 Windows 服务“{ServiceName}”。" +
                "\r\n请确认 Mosquitto 已经安装为 Windows 服务。");
        }
    }
}