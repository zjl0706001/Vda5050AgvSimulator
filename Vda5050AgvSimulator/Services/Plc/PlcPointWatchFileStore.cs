#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using IoFile = System.IO.File;

namespace Vda5050AgvSimulator.Services.Plc;

internal static class PlcPointWatchFileStore
{
    private static readonly string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vda5050AgvSimulator");
    private static readonly string SettingsPath = Path.Combine(AppDataPath, "Config", "PlcPointWatch.xml");
    private static readonly UTF8Encoding LogEncoding = new(false);

    internal static string LogDirectory { get; } = Path.Combine(AppDataPath, "Logs", "PlcPointChanges");

    internal static List<PlcPointWatchSetting> LoadSettings()
    {
        if (!IoFile.Exists(SettingsPath))
        {
            return new List<PlcPointWatchSetting>();
        }

        using var reader = XmlReader.Create(SettingsPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var document = new XmlDocument { XmlResolver = null };
        document.Load(reader);
        var root = document.DocumentElement;
        if (root == null || root.Name != "PlcPointWatch" || root.NamespaceURI.Length != 0)
        {
            throw new InvalidDataException("监听点位配置格式不正确。");
        }

        var settings = new List<PlcPointWatchSetting>();
        foreach (XmlNode node in root.ChildNodes)
        {
            if (node is not XmlElement point || point.Name != "Point" || point.NamespaceURI.Length != 0)
            {
                continue;
            }

            var enabled = point.GetAttributeNode("enabled")?.Value ?? "true";
            settings.Add(new PlcPointWatchSetting(
                XmlConvert.ToInt32(point.GetAttributeNode("plcId")?.Value ?? "0"),
                XmlConvert.ToInt32(point.GetAttributeNode("dbNumber")?.Value ?? "0"),
                point.GetAttribute("name"),
                point.GetAttribute("address"),
                point.GetAttributeNode("dataType")?.Value ?? "Bool",
                XmlConvert.ToBoolean(enabled.ToLowerInvariant())));
        }

        return settings;
    }

    internal static void SaveSettings(IEnumerable<PlcPointWatchSetting> settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var tempPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = XmlWriter.Create(tempPath, new XmlWriterSettings
                   {
                       Indent = true,
                       NewLineHandling = NewLineHandling.Entitize
                   }))
            {
                writer.WriteStartElement("PlcPointWatch");
                foreach (var point in settings)
                {
                    writer.WriteStartElement("Point");
                    writer.WriteAttributeString("plcId", XmlConvert.ToString(point.PlcId));
                    writer.WriteAttributeString("dbNumber", XmlConvert.ToString(point.DbNumber));
                    writer.WriteAttributeString("name", point.Name);
                    writer.WriteAttributeString("address", point.Address);
                    writer.WriteAttributeString("dataType", point.DataType);
                    writer.WriteAttributeString("enabled", XmlConvert.ToString(point.IsEnabled));
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            if (IoFile.Exists(SettingsPath))
            {
                IoFile.Replace(tempPath, SettingsPath, null);
            }
            else
            {
                IoFile.Move(tempPath, SettingsPath);
            }
        }
        finally
        {
            if (IoFile.Exists(tempPath))
            {
                IoFile.Delete(tempPath);
            }
        }
    }

    internal static void AppendChanges(DateTimeOffset timestamp, IReadOnlyList<PlcPointChangeRecord> records)
    {
        Directory.CreateDirectory(LogDirectory);
        var date = timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var path = Path.Combine(LogDirectory, $"PlcPointChanges_{date}.log");
        var text = new StringBuilder();
        foreach (var record in records)
        {
            var time = record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
            text.AppendLine($"{time}\t{Quote(record.Target)}\t{Quote(record.Name)}\t{Quote(record.Address)}\t" +
                            $"{Quote(record.DataType)}\t{Quote(record.OldValue)} -> {Quote(record.NewValue)}");
        }

        IoFile.AppendAllText(path, text.ToString(), LogEncoding);
    }

    private static string Quote(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return "\"" + escaped + "\"";
    }
}

internal sealed class PlcPointWatchSetting
{
    internal int PlcId { get; }
    internal int DbNumber { get; }
    internal string Name { get; }
    internal string Address { get; }
    internal string DataType { get; }
    internal bool IsEnabled { get; }

    internal PlcPointWatchSetting(int plcId, int dbNumber, string name, string address, string dataType, bool isEnabled)
    {
        if (plcId <= 0 || dbNumber is <= 0 or > ushort.MaxValue ||
            string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(dataType))
        {
            throw new InvalidDataException("监听点位需要有效的 PLC、DB、地址和数据类型。");
        }

        PlcId = plcId;
        DbNumber = dbNumber;
        Address = address.Trim();
        Name = string.IsNullOrWhiteSpace(name) ? Address : name.Trim();
        DataType = dataType.Trim();
        IsEnabled = isEnabled;
    }
}

public sealed class PlcPointChangeRecord
{
    public DateTimeOffset Timestamp { get; }
    public string TimeText => Timestamp.ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    public string Target { get; }
    public string Name { get; }
    public string Address { get; }
    public string DataType { get; }
    public string OldValue { get; }
    public string NewValue { get; }
    public string OldValueText => OldValue.Length == 0 ? "（空）" : OldValue;
    public string NewValueText => NewValue.Length == 0 ? "（空）" : NewValue;

    internal PlcPointChangeRecord(DateTimeOffset timestamp, string target, PlcPointWatchSetting setting,
        string address, string oldValue, string newValue)
    {
        Timestamp = timestamp;
        Target = target;
        Name = setting.Name;
        Address = address;
        DataType = setting.DataType;
        OldValue = oldValue;
        NewValue = newValue;
    }
}
