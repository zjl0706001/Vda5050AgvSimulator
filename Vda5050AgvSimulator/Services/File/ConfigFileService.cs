using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vda5050AgvSimulator.Models.Agv;
using Vda5050AgvSimulator.Models.Map;
using Vda5050AgvSimulator.Models.Mqtt;
using Vda5050AgvSimulator.Models.Plc;

namespace Vda5050AgvSimulator.Services.File;

public static class ConfigFileService
{
    private static readonly string ConfigDirectory =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");

    private static readonly string MqttConfigFile =
        Path.Combine(ConfigDirectory, "mqtt.json");

    private static readonly string AgvConfigFile =
        Path.Combine(ConfigDirectory, "agvs.json");

    private static readonly string MapConfigFile =
        Path.Combine(ConfigDirectory, "map.json");

    private static readonly string PlcConfigFile =
        Path.Combine(ConfigDirectory, "plcs.json");

    private static readonly string PlcAutoWriteRuleConfigFile =
        Path.Combine(ConfigDirectory, "plc_auto_write_rules.json");

    public static MqttConfigModel LoadMqttConfig()
    {
        try
        {
            EnsureConfigDirectory();

            if (!System.IO.File.Exists(MqttConfigFile))
            {
                var defaultConfig = new MqttConfigModel();
                SaveMqttConfig(defaultConfig);
                return defaultConfig;
            }

            var json = System.IO.File.ReadAllText(MqttConfigFile);

            if (string.IsNullOrWhiteSpace(json))
            {
                var defaultConfig = new MqttConfigModel();
                SaveMqttConfig(defaultConfig);
                return defaultConfig;
            }

            var config = JsonConvert.DeserializeObject<MqttConfigModel>(json);

            if (config != null) return config;
            config = new MqttConfigModel();
            SaveMqttConfig(config);

            return config;
        }
        catch
        {
            var defaultConfig = new MqttConfigModel();

            try
            {
                SaveMqttConfig(defaultConfig);
            }
            catch
            {
                // ignored
            }

            return defaultConfig;
        }
    }

    public static void SaveMqttConfig(MqttConfigModel config)
    {
        EnsureConfigDirectory();

        var json = JsonConvert.SerializeObject(config, Formatting.Indented);

        System.IO.File.WriteAllText(MqttConfigFile, json);
    }

    public static List<AgvModel> LoadAgvs()
    {
        try
        {
            EnsureConfigDirectory();

            if (!System.IO.File.Exists(AgvConfigFile))
            {
                List<AgvModel> emptyAgvs = [];
                SaveAgvs(emptyAgvs);
                return emptyAgvs;
            }

            var json = System.IO.File.ReadAllText(AgvConfigFile);

            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            var agvs = JsonConvert.DeserializeObject<List<AgvModel>>(json);

            return agvs ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void SaveAgvs(IEnumerable<AgvModel> agvs)
    {
        EnsureConfigDirectory();

        var json = JsonConvert.SerializeObject(agvs, Formatting.Indented);

        System.IO.File.WriteAllText(AgvConfigFile, json);
    }

    public static List<PlcConfigModel> LoadPlcs()
    {
        try
        {
            EnsureConfigDirectory();

            if (!System.IO.File.Exists(PlcConfigFile))
            {
                var defaultPlcs = CreateDefaultPlcs();

                SavePlcs(defaultPlcs);

                return defaultPlcs;
            }

            var json = System.IO.File.ReadAllText(PlcConfigFile);

            if (string.IsNullOrWhiteSpace(json))
            {
                var defaultPlcs = CreateDefaultPlcs();

                SavePlcs(defaultPlcs);

                return defaultPlcs;
            }

            var plcs = JsonConvert.DeserializeObject<List<PlcConfigModel>>(json);

            return plcs ?? CreateDefaultPlcs();
        }
        catch
        {
            return CreateDefaultPlcs();
        }
    }

    public static void SavePlcs(IEnumerable<PlcConfigModel> plcs)
    {
        EnsureConfigDirectory();

        var json = JsonConvert.SerializeObject(plcs, Formatting.Indented);

        System.IO.File.WriteAllText(PlcConfigFile, json);
    }

    public static string GetPlcConfigFilePath()
    {
        EnsureConfigDirectory();

        return PlcConfigFile;
    }

    /// <summary>
    /// 加载PLC自动写入规则。
    /// 独立保存到 Config/plc_auto_write_rules.json。
    /// </summary>
    public static List<PlcAutoWriteRuleModel> LoadPlcAutoWriteRules()
    {
        try
        {
            EnsureConfigDirectory();

            if (!System.IO.File.Exists(PlcAutoWriteRuleConfigFile))
            {
                var defaultRules = CreateDefaultPlcAutoWriteRules();

                SavePlcAutoWriteRules(defaultRules);

                return defaultRules;
            }

            var json = System.IO.File.ReadAllText(PlcAutoWriteRuleConfigFile);

            if (string.IsNullOrWhiteSpace(json))
            {
                var defaultRules = CreateDefaultPlcAutoWriteRules();

                SavePlcAutoWriteRules(defaultRules);

                return defaultRules;
            }

            var rules = JsonConvert.DeserializeObject<List<PlcAutoWriteRuleModel>>(json);

            if (rules == null || rules.Count == 0)
            {
                var defaultRules = CreateDefaultPlcAutoWriteRules();

                SavePlcAutoWriteRules(defaultRules);

                return defaultRules;
            }

            return rules;
        }
        catch
        {
            return CreateDefaultPlcAutoWriteRules();
        }
    }

    /// <summary>
    /// 保存PLC自动写入规则。
    /// </summary>
    public static void SavePlcAutoWriteRules(IEnumerable<PlcAutoWriteRuleModel> rules)
    {
        EnsureConfigDirectory();

        var json = JsonConvert.SerializeObject(rules ?? [], Formatting.Indented);

        System.IO.File.WriteAllText(PlcAutoWriteRuleConfigFile, json);
    }

    /// <summary>
    /// 获取PLC自动写入规则配置文件路径。
    /// </summary>
    public static string GetPlcAutoWriteRuleConfigFilePath()
    {
        EnsureConfigDirectory();

        return PlcAutoWriteRuleConfigFile;
    }

    private static List<PlcAutoWriteRuleModel> CreateDefaultPlcAutoWriteRules()
    {
        return
        [
            PlcAutoWriteRuleModel.CreateDefault()
        ];
    }

    private static List<PlcConfigModel> CreateDefaultPlcs()
    {
        return
        [
            new PlcConfigModel
            {
                PlcId = 1,
                PlcName = "PLC-1",
                IsEnabled = true,
                PlcType = "S1200",
                IpAddress = "127.0.0.1",
                Port = 102,
                Rack = 0,
                Slot = 1,
                ConnectTimeOut = 3000,
                ReceiveTimeOut = 3000,
                PollIntervalMs = 300,
                AutoWriteEnabled = true,
                AutoWriteRules =
                [
                    PlcAutoWriteRuleModel.CreateDefault()
                ]
            }
        ];
    }

    public static PlcConfigModel LoadPlcConfig()
    {
        try
        {
            EnsureConfigDirectory();

            if (!System.IO.File.Exists(PlcConfigFile))
            {
                var defaultConfig = new PlcConfigModel();
                SavePlcConfig(defaultConfig);
                return defaultConfig;
            }

            var json = System.IO.File.ReadAllText(PlcConfigFile);

            if (string.IsNullOrWhiteSpace(json))
            {
                var defaultConfig = new PlcConfigModel();
                SavePlcConfig(defaultConfig);
                return defaultConfig;
            }

            var config = JsonConvert.DeserializeObject<PlcConfigModel>(json);

            if (config != null) return config;

            config = new PlcConfigModel();
            SavePlcConfig(config);

            return config;
        }
        catch
        {
            var defaultConfig = new PlcConfigModel();

            try
            {
                SavePlcConfig(defaultConfig);
            }
            catch
            {
                // ignored
            }

            return defaultConfig;
        }
    }

    public static void SavePlcConfig(PlcConfigModel config)
    {
        EnsureConfigDirectory();

        var json = JsonConvert.SerializeObject(config, Formatting.Indented);

        System.IO.File.WriteAllText(PlcConfigFile, json);
    }

    public static string GetAgvConfigFilePath()
    {
        EnsureConfigDirectory();

        return AgvConfigFile;
    }

    /// <summary>
    /// 只读加载地图配置。
    /// 不会自动创建map.json，也不会覆盖原地图文件。
    /// </summary>
    public static MapModel LoadMap()
    {
        try
        {
            EnsureConfigDirectory();

            if (!System.IO.File.Exists(MapConfigFile))
            {
                return new MapModel();
            }

            var json = System.IO.File.ReadAllText(MapConfigFile);

            if (string.IsNullOrWhiteSpace(json))
            {
                return new MapModel();
            }

            var map = JsonConvert.DeserializeObject<MapModel>(json);

            return map ?? new MapModel();
        }
        catch
        {
            return new MapModel();
        }
    }

    /// <summary>
    /// 尝试只读加载地图配置。
    /// </summary>
    public static bool TryLoadMap(out MapModel map, out string message)
    {
        return TryLoadMap(out map, out message, out _);
    }

    internal static bool TryLoadMap(out MapModel map, out string message, out string json)
    {
        map = new MapModel();
        message = string.Empty;
        json = string.Empty;

        try
        {
            EnsureConfigDirectory();

            if (!System.IO.File.Exists(MapConfigFile))
            {
                message = $"地图文件不存在：{MapConfigFile}";
                return false;
            }

            var fileJson = System.IO.File.ReadAllText(MapConfigFile);

            if (string.IsNullOrWhiteSpace(fileJson))
            {
                message = $"地图文件内容为空：{MapConfigFile}";
                return false;
            }

            var loadedMap = JsonConvert.DeserializeObject<MapModel>(fileJson);

            if (loadedMap == null)
            {
                message = $"地图文件解析失败：{MapConfigFile}";
                return false;
            }

            map = loadedMap;
            json = fileJson;
            return true;
        }
        catch (Exception ex)
        {
            message = $"读取地图文件失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 校验地图文件并替换 Config/map.json，保留原始 JSON 中的扩展字段。
    /// </summary>
    internal static bool TryImportMap(string sourcePath, out MapModel map, out string message, out string json)
    {
        map = new MapModel();
        message = string.Empty;
        json = string.Empty;

        try
        {
            var fullSourcePath = Path.GetFullPath(sourcePath);
            var sourceJson = System.IO.File.ReadAllText(fullSourcePath);
            var importedMap = ParseImportedMap(sourceJson);

            if (!string.Equals(fullSourcePath, MapConfigFile, StringComparison.OrdinalIgnoreCase))
            {
                SaveImportedMap(sourceJson);
            }

            map = importedMap;
            json = sourceJson;
            return true;
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidDataException ||
                                   ex is IOException || ex is UnauthorizedAccessException ||
                                   ex is ArgumentException || ex is NotSupportedException)
        {
            message = $"导入地图失败。{ex.Message}";
            return false;
        }
    }

    private static MapModel ParseImportedMap(string json)
    {
        var document = JObject.Parse(json);
        var sourcePoints = document.GetValue("points", StringComparison.OrdinalIgnoreCase) as JArray;
        if (sourcePoints == null || sourcePoints.Count == 0)
        {
            throw new InvalidDataException("地图文件需要包含非空的 points 地标列表。");
        }

        var map = document.ToObject<MapModel>();
        if (map?.points == null || map.points.Count != sourcePoints.Count)
        {
            throw new InvalidDataException("无法读取地图地标列表。");
        }

        var pointIds = new HashSet<int>();
        for (var index = 0; index < sourcePoints.Count; index++)
        {
            var sourcePoint = sourcePoints[index] as JObject;
            var point = map.points[index];
            var idText = sourcePoint?.GetValue("id", StringComparison.OrdinalIgnoreCase)?.ToString();
            if (sourcePoint == null || point == null ||
                !int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || point.id != id ||
                !HasMapCoordinate(sourcePoint, "x") || !HasMapCoordinate(sourcePoint, "y") ||
                double.IsNaN(point.x) || double.IsInfinity(point.x) ||
                double.IsNaN(point.y) || double.IsInfinity(point.y))
            {
                throw new InvalidDataException($"第 {index + 1} 个地标需要整数 id 和有效的 x、y 坐标。");
            }

            if (!pointIds.Add(id))
            {
                throw new InvalidDataException($"地标 ID {id} 重复。");
            }
        }

        return map;
    }

    private static bool HasMapCoordinate(JObject point, string name)
    {
        var token = point.GetValue(name, StringComparison.OrdinalIgnoreCase);
        return token?.Type is JTokenType.Integer or JTokenType.Float or JTokenType.String;
    }

    private static void SaveImportedMap(string json)
    {
        EnsureConfigDirectory();

        // 同一目录内替换文件，避免监听器读到尚未写完的地图。
        var temporaryPath = Path.Combine(ConfigDirectory, $".map-{Guid.NewGuid():N}.tmp");
        try
        {
            System.IO.File.WriteAllText(temporaryPath, json);
            if (System.IO.File.Exists(MapConfigFile))
            {
                System.IO.File.Replace(temporaryPath, MapConfigFile, MapConfigFile + ".bak");
            }
            else
            {
                System.IO.File.Move(temporaryPath, MapConfigFile);
            }
        }
        finally
        {
            try
            {
                System.IO.File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 清理失败时保留最初的导入错误。
            }
        }
    }

    /// <summary>
    /// 获取地图配置文件路径。
    /// </summary>
    public static string GetMapConfigFilePath()
    {
        EnsureConfigDirectory();

        return MapConfigFile;
    }

    /// <summary>
    /// 判断地图配置文件是否存在。
    /// </summary>
    public static bool MapConfigExists()
    {
        EnsureConfigDirectory();

        return System.IO.File.Exists(MapConfigFile);
    }

    private static void EnsureConfigDirectory()
    {
        if (!Directory.Exists(ConfigDirectory))
        {
            Directory.CreateDirectory(ConfigDirectory);
        }
    }
}