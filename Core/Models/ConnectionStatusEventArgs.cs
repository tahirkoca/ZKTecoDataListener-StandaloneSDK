using System;

namespace Core.Models
{
    public class ConnectionStatusEventArgs : EventArgs
    {
        public bool IsConnected { get; set; }
        public string Message { get; set; }
        public int ErrorCode { get; set; }
        public DeviceInfo DeviceInfo { get; set; }
    }
}

