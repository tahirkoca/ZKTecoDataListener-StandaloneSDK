namespace Core.Models
{
    public class DeviceInfo
    {
        public int DeviceId { get; set; }
        public string DeviceName { get; set; }
        public string IPAddress { get; set; }
        public int Port { get; set; }
        public int CompanyId { get; set; }
        public string CompanyName { get; set; }
        public string DeviceModel { get; set; }
        public string Manufacturer { get; set; }
        public string SdkType { get; set; }
        public string ConnectionString { get; set; }
    }
}

