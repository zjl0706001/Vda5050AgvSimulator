#nullable enable

using Vda5050AgvSimulator.Models.Plc;

namespace Vda5050AgvSimulator.Services.Plc;

internal static class PlcProtocolBoardValueService
{
    internal static bool TryRefreshRowValue(int plcId, int dbNumber, PlcProtocolBoardRowModel row)
    {
        var service = PlcVirtualServerService.Instance;
        if (!row.IsEnabled || !service.IsPlcRunning(plcId))
        {
            row.HasCurrentValue = false;
            row.ActualAddress = string.Empty;
            row.CurrentValue = string.Empty;
            row.LastMessage = row.IsEnabled ? "PLC未运行" : "未启用";
            return false;
        }

        var readSucceeded = service.TryReadProtocolItemValue(
            plcId, dbNumber, row.ToProtocolItem(), out var actualAddress, out var valueText, out var message);

        row.ActualAddress = actualAddress;
        row.CurrentValue = readSucceeded ? valueText : string.Empty;
        row.HasCurrentValue = readSucceeded;
        row.LastMessage = readSucceeded ? "读取成功" : "读取失败：" + message;
        return readSucceeded;
    }

    internal static bool TryWriteRowValue(int plcId, int dbNumber, PlcProtocolBoardRowModel row,
        string? explicitValue = null)
    {
        var service = PlcVirtualServerService.Instance;
        if (!row.IsEnabled || !service.IsPlcRunning(plcId))
        {
            row.LastMessage = row.IsEnabled ? "PLC未运行，不能写入" : "未启用，不能写入";
            return false;
        }

        if (explicitValue == null && !row.HasCustomWriteValue && !TryRefreshRowValue(plcId, dbNumber, row))
        {
            return false;
        }

        if (!service.TryWriteProtocolItemValue(
                plcId, dbNumber, row.ToProtocolItem(), explicitValue ?? row.WriteValue,
                out var actualAddress, out var message))
        {
            row.ActualAddress = actualAddress;
            row.LastMessage = "写入失败：" + message;
            return false;
        }

        var readBackSucceeded = TryRefreshRowValue(plcId, dbNumber, row);
        row.ResetWriteValue();
        row.LastMessage = readBackSucceeded ? "写入成功" : "已写入，" + row.LastMessage;
        return true;
    }
}
