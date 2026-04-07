using System;
using System.Threading.Tasks;
using Core.Models;

namespace Core.Interfaces
{
    public interface IDeviceProvider : IDisposable
    {
        string Manufacturer { get; }
        string SdkType { get; }
        bool IsConnected { get; }
        DeviceInfo DeviceInfo { get; }
        Task<bool> ConnectAsync();
        Task DisconnectAsync();
        Task<bool> CheckConnectionAsync();
        event EventHandler<DeviceTransactionEventArgs> TransactionReceived;
        event EventHandler<ConnectionStatusEventArgs> ConnectionStatusChanged;
        Task<bool> DeleteUserAsync(string enrollNumber);
        void InitializeLastLogTime(DateTime? time);
        Task<bool> AddUserAsync(string enrollNumber, string name = "", string password = "", int privilege = 0, bool enabled = true, string cardNumber = "");
        Task<bool> OpenDoorAsync();
    }
}

