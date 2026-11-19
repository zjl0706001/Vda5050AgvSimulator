#nullable disable

using Newtonsoft.Json;

namespace Vda5050AgvSimulator.Models.Vda;

public class VdaFactSheetModel
{
    [JsonProperty("typeSpecification")] public TypeSpecificationModel TypeSpecification { get; set; } = new();

    [JsonProperty("headerId")] public long HeaderId { get; set; }

    [JsonProperty("timestamp")] public string Timestamp { get; set; }

    [JsonProperty("version")] public string Version { get; set; }

    [JsonProperty("manufacturer")] public string Manufacturer { get; set; }

    [JsonProperty("serialNumber")] public string SerialNumber { get; set; }
}

public class TypeSpecificationModel
{
    [JsonProperty("agvKinematic")] public int AgvKinematic { get; set; }

    [JsonProperty("agvClass")] public int AgvClass { get; set; }

    [JsonProperty("maxLoadMass")] public int MaxLoadMass { get; set; }
}