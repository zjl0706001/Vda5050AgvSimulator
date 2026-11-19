#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;

namespace Vda5050AgvSimulator.Services.Plc;

/// <summary>
/// Windows本机网卡IPv4地址管理服务。
/// 用于把虚拟PLC地址作为辅助IP添加到电脑网卡。
/// 不覆盖网卡原有IP。
/// </summary>
public sealed class WindowsNetworkAddressService
{
    public static WindowsNetworkAddressService Instance { get; } = new();

    private WindowsNetworkAddressService()
    {
    }

    /// <summary>
    /// 获取可供用户手动选择的网卡。
    /// 排除回环和隧道接口；断开连接的网卡仍显示，方便预先配置辅助IP。
    /// </summary>
    public IReadOnlyList<NetworkInterface> GetConfigurableNetworkInterfaces()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x != null)
            .Where(x => x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Where(x => x.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .OrderByDescending(x => x.OperationalStatus == OperationalStatus.Up)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 将一个IPv4辅助地址添加到用户明确选择的网卡。
    /// 不修改原有IP、网关或DNS；已存在于本机任意网卡的地址会跳过。
    /// 返回true表示本次新增成功，返回false表示地址已存在并被跳过。
    /// </summary>
    public bool TryAddIpv4AddressToInterface(
        string networkInterfaceId,
        string ipAddress,
        string subnetMask,
        out string message)
    {
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(networkInterfaceId))
        {
            throw new ArgumentException("请先选择目标网卡。", nameof(networkInterfaceId));
        }

        var targetAddress = ParseIpv4(ipAddress, nameof(ipAddress));
        var maskAddress = ParseIpv4(subnetMask, nameof(subnetMask));

        if (IPAddress.IsLoopback(targetAddress) ||
            targetAddress.Equals(IPAddress.Any) ||
            targetAddress.Equals(IPAddress.Broadcast) ||
            targetAddress.GetAddressBytes()[0] >= 224)
        {
            throw new ArgumentException("不能将该地址作为网卡辅助IPv4地址：" + targetAddress, nameof(ipAddress));
        }

        if (!IsValidSubnetMask(maskAddress))
        {
            throw new ArgumentException("子网掩码无效，必须是连续的IPv4掩码，例如 255.255.255.0。", nameof(subnetMask));
        }

        if (IsNetworkOrBroadcastAddress(targetAddress, maskAddress))
        {
            throw new ArgumentException("该IP是所选子网掩码对应的网络地址或广播地址：" + targetAddress, nameof(ipAddress));
        }

        var targetInterface = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(x => string.Equals(x.Id, networkInterfaceId, StringComparison.OrdinalIgnoreCase));

        if (targetInterface == null)
        {
            throw new InvalidOperationException("目标网卡已不存在，请刷新网卡列表后重试。");
        }

        if (targetInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
            targetInterface.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
        {
            throw new InvalidOperationException("回环网卡和隧道网卡不能添加辅助IP。");
        }

        var existingInterface = FindInterfaceContainingAddress(targetAddress);
        if (existingInterface != null)
        {
            message = string.Equals(existingInterface.Id, targetInterface.Id, StringComparison.OrdinalIgnoreCase)
                ? "该IP已存在于选定网卡，已跳过。"
                : $"该IP已存在于其他网卡「{existingInterface.Name}」，已跳过。";
            return false;
        }

        EnsureAdministrator();

        var interfaceName = targetInterface.Name;
        if (interfaceName.Contains("\""))
        {
            throw new InvalidOperationException("网卡名称包含不支持的引号字符：" + interfaceName);
        }

        var arguments =
            "interface ipv4 add address " +
            "name=\"" + interfaceName + "\" " +
            "address=" + targetAddress + " " +
            "mask=" + maskAddress + " " +
            "skipassource=true";

        var result = RunNetsh(arguments);
        if (!result.IsSuccess)
        {
            var actualInterface = FindInterfaceContainingAddress(targetAddress);
            if (actualInterface == null ||
                !string.Equals(actualInterface.Id, targetInterface.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "添加网卡辅助IP失败：" + targetAddress +
                    "\r\n目标网卡：" + interfaceName +
                    "\r\n" + result);
            }

            message = "IP已配置成功（netsh返回非零，但系统检测到地址已生效）。";
            return true;
        }

        message = "添加成功。";
        return true;
    }

    private static bool IsValidSubnetMask(IPAddress subnetMask)
    {
        var bytes = subnetMask.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        var zeroBitSeen = false;
        var oneCount = 0;
        foreach (var value in bytes)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var isOne = (value & (1 << bit)) != 0;
                if (isOne)
                {
                    if (zeroBitSeen)
                    {
                        return false;
                    }

                    oneCount++;
                }
                else
                {
                    zeroBitSeen = true;
                }
            }
        }

        return oneCount is >= 1 and <= 32;
    }

    private static bool IsNetworkOrBroadcastAddress(IPAddress address, IPAddress subnetMask)
    {
        var addressValue = ToUInt32(address);
        var maskValue = ToUInt32(subnetMask);
        var hostMask = ~maskValue;

        // /31和/32网段没有传统意义上的网络/广播主机地址。
        if (hostMask <= 1)
        {
            return false;
        }

        var networkAddress = addressValue & maskValue;
        var broadcastAddress = networkAddress | hostMask;
        return addressValue == networkAddress || addressValue == broadcastAddress;
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }

    /// <summary>
    /// 确保指定IPv4地址已配置在电脑某个网卡上。
    /// 地址已经存在时不会重复添加。
    /// 返回实际使用的网卡名称；回环地址返回“回环地址”。
    /// </summary>
    public string EnsureLocalIpv4Address(
        string ipAddress,
        string subnetMask = "255.255.255.0")
    {
        var targetAddress = ParseIpv4(ipAddress, nameof(ipAddress));

        var maskAddress = ParseIpv4(subnetMask, nameof(subnetMask));

        if (IPAddress.IsLoopback(targetAddress))
        {
            return "回环地址";
        }

        if (targetAddress.Equals(IPAddress.Any) ||
            targetAddress.Equals(IPAddress.Broadcast))
        {
            throw new ArgumentException(
                "不能把该IP添加到本机网卡：" + ipAddress,
                nameof(ipAddress));
        }

        /*
         * 已经存在于任意网卡时直接返回。
         * 避免重复执行netsh产生“对象已存在”等错误。
         */
        var existingInterface = FindInterfaceContainingAddress(targetAddress);

        if (existingInterface != null)
        {
            return existingInterface.Name;
        }

        EnsureAdministrator();

        var targetInterface = FindBestNetworkInterface(targetAddress);

        if (targetInterface == null)
        {
            throw new InvalidOperationException(
                "没有找到可用的本机IPv4网卡，无法添加PLC IP：" +
                ipAddress);
        }

        var interfaceName = targetInterface.Name;

        if (interfaceName.Contains("\""))
        {
            throw new InvalidOperationException(
                "网卡名称包含不支持的引号字符：" + interfaceName);
        }

        var arguments =
            "interface ipv4 add address " +
            "name=\"" + interfaceName + "\" " +
            "address=" + targetAddress + " " +
            "mask=" + maskAddress + " " +
            "skipassource=true";

        var result = RunNetsh(arguments);

        if (!result.IsSuccess)
        {
            /*
             * netsh执行后再检查一次。
             * 某些系统返回的本地化输出可能不准确，
             * 只要IP最终已经存在，就认为操作成功。
             */
            if (FindInterfaceContainingAddress(targetAddress) == null)
            {
                throw new InvalidOperationException(
                    "添加本机PLC IP失败：" + targetAddress +
                    "\r\n网卡：" + interfaceName +
                    "\r\n" + result);
            }
        }

        return interfaceName;
    }

    /// <summary>
    /// 判断当前进程是否以管理员权限运行。
    /// </summary>
    public bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();

        var principal = new WindowsPrincipal(identity);

        return principal.IsInRole(
            WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// 判断指定IPv4地址是否已经配置在本机。
    /// </summary>
    public bool IsLocalIpv4Address(string ipAddress)
    {
        var address = ParseIpv4(ipAddress, nameof(ipAddress));

        return FindInterfaceContainingAddress(address) != null;
    }

    /// <summary>
    /// 查找已经包含目标IP的网卡。
    /// </summary>
    private static NetworkInterface FindInterfaceContainingAddress(
        IPAddress targetAddress)
    {
        foreach (var networkInterface
                 in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var properties = networkInterface.GetIPProperties();

                foreach (var unicastAddress
                         in properties.UnicastAddresses)
                {
                    if (unicastAddress.Address.AddressFamily !=
                        AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    if (unicastAddress.Address.Equals(targetAddress))
                    {
                        return networkInterface;
                    }
                }
            }
            catch
            {
                // 单个网卡读取失败不影响其他网卡判断
            }
        }

        return null;
    }

    /// <summary>
    /// 自动选择最适合添加PLC IP的网卡。
    ///
    /// 优先级：
    /// 1、已有IP与目标PLC处于同一网段的网卡；
    /// 2、正在运行的有线网卡；
    /// 3、带默认网关的活动网卡；
    /// 4、其他活动IPv4网卡。
    /// </summary>
    private static NetworkInterface FindBestNetworkInterface(
        IPAddress targetAddress)
    {
        var interfaces = NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(IsUsableNetworkInterface)
            .ToList();

        if (interfaces.Count == 0)
        {
            return null;
        }

        var sameSubnetInterface = interfaces.FirstOrDefault(networkInterface =>
            HasAddressInSameSubnet(
                networkInterface,
                targetAddress));

        if (sameSubnetInterface != null)
        {
            return sameSubnetInterface;
        }

        var wiredInterface = interfaces.FirstOrDefault(networkInterface =>
            IsWiredNetworkInterface(
                networkInterface.NetworkInterfaceType));

        if (wiredInterface != null)
        {
            return wiredInterface;
        }

        var gatewayInterface = interfaces.FirstOrDefault(
            HasIpv4Gateway);

        return gatewayInterface ?? interfaces[0];
    }

    /// <summary>
    /// 判断网卡是否可用于添加IPv4地址。
    /// </summary>
    private static bool IsUsableNetworkInterface(
        NetworkInterface networkInterface)
    {
        if (networkInterface == null)
        {
            return false;
        }

        if (networkInterface.OperationalStatus !=
            OperationalStatus.Up)
        {
            return false;
        }

        if (networkInterface.NetworkInterfaceType ==
            NetworkInterfaceType.Loopback ||
            networkInterface.NetworkInterfaceType ==
            NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        try
        {
            return networkInterface
                .GetIPProperties()
                .UnicastAddresses
                .Any(x =>
                    x.Address.AddressFamily ==
                    AddressFamily.InterNetwork);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判断网卡是否已有与目标IP同网段的IPv4地址。
    /// </summary>
    private static bool HasAddressInSameSubnet(
        NetworkInterface networkInterface,
        IPAddress targetAddress)
    {
        try
        {
            foreach (var unicastAddress in networkInterface
                         .GetIPProperties()
                         .UnicastAddresses)
            {
                if (unicastAddress.Address.AddressFamily !=
                    AddressFamily.InterNetwork)
                {
                    continue;
                }

                var mask = unicastAddress.IPv4Mask;

                if (mask == null)
                {
                    continue;
                }

                if (IsSameSubnet(
                        unicastAddress.Address,
                        targetAddress,
                        mask))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// 判断两个IPv4地址在指定掩码下是否属于同一网段。
    /// </summary>
    private static bool IsSameSubnet(
        IPAddress first,
        IPAddress second,
        IPAddress mask)
    {
        var firstBytes = first.GetAddressBytes();
        var secondBytes = second.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();

        if (firstBytes.Length != 4 ||
            secondBytes.Length != 4 ||
            maskBytes.Length != 4)
        {
            return false;
        }

        for (var index = 0; index < 4; index++)
        {
            if ((firstBytes[index] & maskBytes[index]) !=
                (secondBytes[index] & maskBytes[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 判断是否为有线网卡。
    /// </summary>
    private static bool IsWiredNetworkInterface(
        NetworkInterfaceType interfaceType)
    {
        return interfaceType ==
               NetworkInterfaceType.Ethernet ||
               interfaceType ==
               NetworkInterfaceType.GigabitEthernet ||
               interfaceType ==
               NetworkInterfaceType.FastEthernetFx ||
               interfaceType ==
               NetworkInterfaceType.FastEthernetT;
    }

    /// <summary>
    /// 判断网卡是否配置了IPv4默认网关。
    /// </summary>
    private static bool HasIpv4Gateway(
        NetworkInterface networkInterface)
    {
        try
        {
            return networkInterface
                .GetIPProperties()
                .GatewayAddresses
                .Any(x =>
                    x.Address.AddressFamily ==
                    AddressFamily.InterNetwork &&
                    !x.Address.Equals(IPAddress.Any));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解析并验证IPv4地址。
    /// </summary>
    private static IPAddress ParseIpv4(
        string value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "IPv4地址不能为空。",
                parameterName);
        }

        if (!IPAddress.TryParse(
                value.Trim(),
                out var address) ||
            address.AddressFamily !=
            AddressFamily.InterNetwork)
        {
            throw new ArgumentException(
                "IPv4地址格式不正确：" + value,
                parameterName);
        }

        return address;
    }

    /// <summary>
    /// 检查管理员权限。
    /// </summary>
    private void EnsureAdministrator()
    {
        if (!IsAdministrator())
        {
            throw new InvalidOperationException(
                "添加本机网卡IP需要管理员权限，" +
                "请以管理员身份运行AGV模拟器。");
        }
    }

    /// <summary>
    /// 执行netsh命令。
    /// </summary>
    private static NetshResult RunNetsh(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "netsh.exe",
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
                Error = "netsh进程启动失败。"
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

    private sealed class NetshResult
    {
        public int ExitCode { get; set; }

        public string Output { get; set; }

        public string Error { get; set; }

        public bool IsSuccess => ExitCode == 0;

        public override string ToString()
        {
            return
                "退出码：" + ExitCode +
                "\r\n输出：" + (Output ?? string.Empty) +
                "\r\n错误：" + (Error ?? string.Empty);
        }
    }
}