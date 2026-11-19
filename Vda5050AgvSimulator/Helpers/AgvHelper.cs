using Vda5050AgvSimulator.Models.Agv;

namespace Vda5050AgvSimulator.Helpers;

public static class AgvHelper
{
    /// <summary>
    /// AGV控制状态。
    /// </summary>
    public static class AgvControlStatus
    {
        /// <summary>
        /// 手动。
        /// </summary>
        public const int Manual = 0;

        /// <summary>
        /// 自动。
        /// </summary>
        public const int Automatic = 1;

        public static string GetVdaOperatingMode(AgvModel agv)
        {
            return agv.ControlStatus switch
            {
                Manual => "MANUAL",
                _ => "AUTOMATIC"
            };
        }
    }
}