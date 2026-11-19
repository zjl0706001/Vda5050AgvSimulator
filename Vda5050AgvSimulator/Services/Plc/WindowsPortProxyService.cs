#nullable disable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Vda5050AgvSimulator.Services.Plc;

/// <summary>
/// Windows portproxy 管理服务。
/// 用于把外部PLC地址转发到本机内部端口，例如：
/// 172.16.4.2:102 -> 127.0.0.1:103
/// </summary>
public class WindowsPortProxyService
{
    public static WindowsPortProxyService Instance { get; } = new();

    private const int NetworkReadyTimeoutMilliseconds = 3000;
    private const int CommandTimeoutMilliseconds = 15000;
    private readonly object _proxyLock = new();

    private WindowsPortProxyService()
    {
    }

    public bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();

        var principal = new WindowsPrincipal(identity);

        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void AddOrUpdateProxy(
        string listenAddress,
        int listenPort,
        string connectAddress,
        int connectPort)
    {
        var listenIp = ParseAddressAndPort(listenAddress, listenPort, nameof(listenAddress));
        var connectIp = ParseAddressAndPort(connectAddress, connectPort, nameof(connectAddress));

        EnsureAdministrator();

        var args =
            "interface portproxy add v4tov4 " +
            "listenaddress=" + listenIp + " " +
            "listenport=" + listenPort + " " +
            "connectaddress=" + connectIp + " " +
            "connectport=" + connectPort;

        lock (_proxyLock)
        {
            if (!WaitUntil(() => IsLocalAddressReady(listenIp)))
            {
                throw new InvalidOperationException($"本机 IPv4 地址 {listenIp} 未就绪，请检查网卡的 IP 绑定。");
            }

            EnsureIpHelperRunning();

            var addAttempted = false;
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    DeleteProxyRule(listenIp, listenPort, true);

                    // 确认旧监听已释放，避免把原有的监听误判为新规则已生效。
                    if (!WaitUntil(() => !IsTcpListening(listenIp, listenPort)))
                    {
                        throw new InvalidOperationException($"监听地址 {listenIp}:{listenPort} 仍被占用。");
                    }

                    addAttempted = true;
                    var result = RunNetsh(args);

                    if (!result.IsSuccess)
                    {
                        throw new InvalidOperationException(
                            $"创建端口代理失败：{listenIp}:{listenPort} -> {connectIp}:{connectPort}\r\n{result}");
                    }

                    if (WaitUntil(() => IsTcpListening(listenIp, listenPort)))
                    {
                        return;
                    }
                }

                throw new InvalidOperationException(
                    $"端口代理已重试一次，{listenIp}:{listenPort} 仍未进入 TCP 监听。目标 {connectIp}:{connectPort}。");
            }
            catch (Exception error)
            {
                if (addAttempted)
                {
                    try
                    {
                        DeleteProxyRule(listenIp, listenPort, false);
                    }
                    catch (Exception cleanupError)
                    {
                        error.Data["PortProxyCleanupError"] = cleanupError.Message;
                    }
                }

                throw;
            }
        }
    }

    public void DeleteProxy(string listenAddress, int listenPort)
    {
        DeleteProxy(listenAddress, listenPort, false);
    }

    public void DeleteProxy(string listenAddress, int listenPort, bool ignoreError)
    {
        var listenIp = ParseAddressAndPort(listenAddress, listenPort, nameof(listenAddress));

        EnsureAdministrator();

        lock (_proxyLock)
        {
            DeleteProxyRule(listenIp, listenPort, ignoreError);
        }
    }

    private static void DeleteProxyRule(IPAddress listenIp, int listenPort, bool ignoreError)
    {
        var args =
            "interface portproxy delete v4tov4 " +
            "listenaddress=" + listenIp + " " +
            "listenport=" + listenPort;

        var result = RunNetsh(args);

        if (!ignoreError && !result.IsSuccess)
        {
            throw new InvalidOperationException(
                "删除端口代理失败：" +
                listenIp + ":" + listenPort +
                "\r\n" + result);
        }
    }

    public string ShowAll()
    {
        var result = RunNetsh("interface portproxy show all");

        return result.ToString();
    }

    public void EnsureFirewallRule(int listenPort)
    {
        EnsureAdministrator();

        var ruleName = "VDA5050 PLC Proxy Port " + listenPort;

        RunNetsh("advfirewall firewall delete rule name=\"" + ruleName + "\"");

        var args =
            "advfirewall firewall add rule " +
            "name=\"" + ruleName + "\" " +
            "dir=in " +
            "action=allow " +
            "protocol=TCP " +
            "localport=" + listenPort;

        var result = RunNetsh(args);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                "创建防火墙规则失败：Port=" + listenPort + "\r\n" + result);
        }
    }

    private void EnsureAdministrator()
    {
        if (!IsAdministrator())
        {
            throw new InvalidOperationException("创建/删除 Windows portproxy 需要管理员权限，请以管理员身份运行程序。");
        }
    }

    private static IPAddress ParseAddressAndPort(string address, int port, string paramName)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("IP地址不能为空。", paramName);
        }

        if (!IPAddress.TryParse(address.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("端口代理需要 IPv4 地址：" + address, paramName);
        }

        if (port <= 0 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "端口范围必须是 1-65535。");
        }

        return ip;
    }

    private static void EnsureIpHelperRunning()
    {
        const string script =
            "$ErrorActionPreference = 'Stop'; " +
            "$service = Get-Service -Name iphlpsvc; " +
            "$timeout = [TimeSpan]::FromSeconds(5); " +
            "if ($service.Status -eq 'StopPending') { $service.WaitForStatus('Stopped', $timeout) }; " +
            "$service.Refresh(); " +
            "if ($service.Status -eq 'Stopped') { $service.Start() } " +
            "elseif ($service.Status -eq 'Paused') { $service.Continue() }; " +
            "$service.WaitForStatus('Running', $timeout)";

        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = RunCommand(powershell, "-NoLogo -NoProfile -NonInteractive -Command \"" + script + "\"");

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException("IP Helper 服务未进入运行状态。\r\n" + result);
        }
    }

    private static bool IsLocalAddressReady(IPAddress address)
    {
        return address.Equals(IPAddress.Any) || NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
            adapter.GetIPProperties().UnicastAddresses.Any(unicast =>
                unicast.Address.Equals(address) &&
                unicast.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred));
    }

    private static bool IsTcpListening(IPAddress address, int port)
    {
        return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint =>
            endpoint.Port == port && endpoint.Address.Equals(address));
    }

    private static bool WaitUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds >= NetworkReadyTimeoutMilliseconds)
            {
                return false;
            }

            Thread.Sleep(100);
        }

        return true;
    }

    private static CommandResult RunNetsh(string arguments)
    {
        return RunCommand(Path.Combine(Environment.SystemDirectory, "netsh.exe"), arguments);
    }

    private static CommandResult RunCommand(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.Default,
            StandardErrorEncoding = Encoding.Default
        };

        using var process = Process.Start(startInfo);

        if (process == null)
        {
            return new CommandResult
            {
                ExitCode = -1,
                Output = string.Empty,
                Error = Path.GetFileName(fileName) + " 进程启动失败。"
            };
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(CommandTimeoutMilliseconds))
        {
            try
            {
                process.Kill();
                process.WaitForExit(1000);
            }
            catch (InvalidOperationException)
            {
                // 进程可能在等待超时后自行退出。
            }

            return new CommandResult
            {
                ExitCode = -1,
                Output = string.Empty,
                Error = Path.GetFileName(fileName) + " 执行超时。"
            };
        }

        return new CommandResult
        {
            ExitCode = process.ExitCode,
            Output = output.GetAwaiter().GetResult(),
            Error = error.GetAwaiter().GetResult()
        };
    }

    private class CommandResult
    {
        public int ExitCode { get; set; }

        public string Output { get; set; }

        public string Error { get; set; }

        public bool IsSuccess => ExitCode == 0;

        public override string ToString()
        {
            return
                "ExitCode=" + ExitCode + "\r\n" +
                "Output=" + Output + "\r\n" +
                "Error=" + Error;
        }
    }
}
