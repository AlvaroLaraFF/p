namespace PUi.Platform
{
    public interface ISystemInfo
    {
        /// <summary>Reads the data now (memory changes). Never throws.</summary>
        SystemInfoData Read();
    }
}
