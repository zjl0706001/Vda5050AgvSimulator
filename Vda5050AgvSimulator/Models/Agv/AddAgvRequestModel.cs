namespace Vda5050AgvSimulator.Models.Agv;

public class AddAgvRequestModel
{
    public int DeviceID { get; set; } = 1;
    public int AGVCount { get; set; } = 1;

    public string DeviceType { get; set; } = "HPVDA5050";

    public string LocalIP { get; set; } = "127.0.0.1";

    public int LocalPort { get; set; } = 0;

    public string RemoteIP { get; set; } = "127.0.0.1";

    public int RemotePort { get; set; } = 0;

    public int PositionID { get; set; } = 0;

    public double X { get; set; } = 0;

    public double Y { get; set; } = 0;

    public double Angle { get; set; } = 0;

    public double Equantity { get; set; } = 100;
}