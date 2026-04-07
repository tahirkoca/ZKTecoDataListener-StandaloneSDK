using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Core.Models;

namespace Services.Interfaces
{
    public interface IDeviceRepository
    {
        Task<List<DeviceInfo>> GetActiveDevicesAsync();
        Task<List<DeviceInfo>> GetDisconnectedDevicesAsync();
        Task UpdateDeviceConnectionStatusAsync(int deviceId, bool isConnected);
        Task<DateTime?> GetLastLogDateAsync(int deviceId);
        
        // Cihaz tetikleme kuyruğu yönetimi
        Task<List<dynamic>> GetPendingTriggersAsync();
        Task MarkTriggerAsReadAsync(int id);
    }
}

