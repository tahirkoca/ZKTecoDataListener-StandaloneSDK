using System.Threading.Tasks;
using Core.Models;

namespace Services.Interfaces
{
    public interface INotificationService
    {
        Task SendDeviceDisconnectedNotificationAsync(ConnectionStatusEventArgs status);
    }
}

