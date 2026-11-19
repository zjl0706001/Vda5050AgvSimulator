#nullable disable

using System;
using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using System.Text;

namespace Vda5050AgvSimulator.Services.Plc;

/// <summary>
/// Windows portproxy 管理服务。
/// 用于把外部PLC地址转发到本机内部端口，例如：
/// 172.16.4.2:102 -> 127.0.0.1:103
/// </summary>
public class WindowsPortProxyService
{
    public static WindowsPortProxyService Instance { get; } = new();

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
        ValidateAddressAndPort(listenAddress, listenPort, nameof(listenAddress));

        ValidateAddressAndPort(connectAddress, connectPort, nameof(connectAddress));

        EnsureAdministrator();

        DeleteProxy(listenAddress, listenPort, true);

        var args =
            "interface portproxy add v4tov4 " +
            "listenaddress=" + listenAddress + " " +
            "listenport=" + listenPort + " " +
            "connectaddress=" + connectAddress + " " +
            "connectport=" + connectPort;

        var result = RunNetsh(args);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                "创建端口代理失败：" +
                listenAddress + ":" + listenPort +
                " -> " +
                connectAddress + ":" + connectPort +
                "\r\n" + result);
        }
    }

    public void DeleteProxy(string listenAddress, int listenPort)
    {
        DeleteProxy(listenAddress, listenPort, false);
    }

    public void DeleteProxy(string listenAddress, int listenPort, bool ignoreError)
    {
        ValidateAddressAndPort(listenAddress, listenPort, nameof(listenAddress));

        EnsureAdministrator();

        var args =
            "interface portproxy delete v4tov4 " +
            "listenaddress=" + listenAddress + " " +
            "listenport=" + listenPort;

        var result = RunNetsh(args);

        if (!ignoreError && !result.IsSuccess)
        {
            throw new InvalidOperationException(
                "删除端口代理失败：" +
                listenAddress + ":" + listenPort +
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

    private static void ValidateAddressAndPort(string address, int port, string paramName)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("IP地址不能为空。", paramName);
        }

        if (!IPAddress.TryParse(address.Trim(), out _))
        {
            throw new ArgumentException("IP地址格式不正确：" + address, paramName);
        }

        if (port <= 0 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "端口范围必须是 1-65535。");
        }
    }

    private static NetshResult RunNetsh(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "netsh",
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
            return new NetshResult
            {
                ExitCode = -1,
                Output = string.Empty,
                Error = "netsh 进程启动失败。"
            };
        }

        var output = process.StandardOutput.ReadToEnd();

        var error = process.StandardError.ReadToEnd();

        process.WaitForExit();

        return new NetshResult
        {
            ExitCode = process.ExitCode,
            Output = output,
            Error = error
        };
    }

    private class NetshResult
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