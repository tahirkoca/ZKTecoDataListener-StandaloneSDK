using System;
using System.Threading.Tasks;

namespace Core.Interfaces
{
    public interface IDeviceConnection : IDisposable
    {
        bool IsConnected { get; }
        DateTime LastConnectionTime { get; }
        int ConnectionAttempts { get; }
        bool MailSent { get; set; }
        Task<bool> ConnectAsync();
        Task DisconnectAsync();
        Task<bool> CheckConnectionAsync();
    }
}

