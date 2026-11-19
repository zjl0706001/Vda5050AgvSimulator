using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Vda5050AgvSimulator.Models.Map;

/// <summary>
/// 地图模型。
/// 对应地图JSON根节点。
/// </summary>
public class MapModel
{
    /// <summary>
    /// 地图ID。
    /// </summary>
    [JsonProperty("mapId")]
    public string mapId { get; set; } = string.Empty;

    /// <summary>
    /// 地图版本。
    /// </summary>
    [JsonProperty("mapVersion")]
    public string mapVersion { get; set; } = string.Empty;

    /// <summary>
    /// 地图点位集合。
    /// </summary>
    [JsonProperty("points")]
    public List<MapPointModel> points { get; set; } = [];

    /// <summary>
    /// 地图时间戳。
    /// </summary>
    [JsonProperty("timeStamp")]
    public string timeStamp { get; set; } = string.Empty;

    /// <summary>
    /// 根据点位ID查找地图点。
    /// </summary>
    public MapPointModel? FindPoint(int pointId)
    {
        return points.FirstOrDefault(x => x.id == pointId);
    }

    /// <summary>
    /// 根据点位ID查找地图点。
    /// </summary>
    public MapPointModel? FindPoint(string pointId)
    {
        if (!int.TryParse(pointId, out var id))
        {
            return null;
        }

        return FindPoint(id);
    }

    /// <summary>
    /// 判断地图中是否存在指定点位。
    /// </summary>
    public bool HasPoint(int pointId)
    {
        return FindPoint(pointId) != null;
    }

    /// <summary>
    /// 判断地图中是否存在指定点位。
    /// </summary>
    public bool HasPoint(string pointId)
    {
        return FindPoint(pointId) != null;
    }

    /// <summary>
    /// 尝试根据点位ID获取地图点。
    /// </summary>
    public bool TryGetPoint(int pointId, out MapPointModel? point)
    {
        point = FindPoint(pointId);

        return point != null;
    }

    /// <summary>
    /// 尝试根据点位ID获取地图点。
    /// </summary>
    public bool TryGetPoint(string pointId, out MapPointModel? point)
    {
        point = FindPoint(pointId);

        return point != null;
    }
}

/// <summary>
/// 地图点位模型。
/// </summary>
public class MapPointModel
{
    /// <summary>
    /// 点位ID。
    /// </summary>
    [JsonProperty("id")]
    public int id { get; set; }

    /// <summary>
    /// X坐标。
    /// </summary>
    [JsonProperty("x")]
    public double x { get; set; }

    /// <summary>
    /// Y坐标。
    /// </summary>
    [JsonProperty("y")]
    public double y { get; set; }

    /// <summary>
    /// 扩展字段。
    /// 当前JSON里是空对象 {}，后续如果地图点有额外参数可以放这里。
    /// </summary>
    [JsonProperty("extra")]
    public Dictionary<string, JToken> extra { get; set; } = [];
}