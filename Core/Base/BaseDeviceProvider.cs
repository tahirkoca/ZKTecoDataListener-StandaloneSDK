using System;
using System.Threading.Tasks;
using Core.Interfaces;
using Core.Models;
using Services.Interfaces;

namespace Core.Base
{
    public abstract class BaseDeviceProvider : IDeviceProvider
    {
        protected DeviceInfo _deviceInfo;
        protected bool _isConnected;
        protected bool _disposed = false;
        protected ILogger _logger;

        public string Manufacturer => _deviceInfo?.Manufacturer ?? "Unknown";
        public string SdkType => _deviceInfo?.SdkType ?? "Unknown";
        public bool IsConnected => _isConnected;
        public DeviceInfo DeviceInfo => _deviceInfo;

        public event EventHandler<DeviceTransactionEventArgs> TransactionReceived;
        public event EventHandler<ConnectionStatusEventArgs> ConnectionStatusChanged;

        protected BaseDeviceProvider(DeviceInfo deviceInfo, ILogger logger = null)
        {
            _deviceInfo = deviceInfo ?? throw new ArgumentNullException(nameof(deviceInfo));
            _logger = logger;
        }

        public abstract Task<bool> ConnectAsync();
        public abstract Task DisconnectAsync();
        public abstract Task<bool> CheckConnectionAsync();
        public abstract Task<bool> DeleteUserAsync(string enrollNumber);
        public abstract Task<bool> AddUserAsync(string enrollNumber, string name = "", string password = "", int privilege = 0, bool enabled = true, string cardNumber = "");
        public abstract Task<bool> OpenDoorAsync();
        public virtual void InitializeLastLogTime(DateTime? time)
        {
            // Base sınıf bir şey yapmaz, provider'lar override etmeli
            if (time.HasValue)
                _logger?.LogDebug($"InitializeLastLogTime çağrıldı: {time.Value:yyyy-MM-dd HH:mm:ss} (Base implementasyon)");
            else
                _logger?.LogDebug($"InitializeLastLogTime çağrıldı: NULL (Base implementasyon)");
        }
        protected virtual void OnTransactionReceived(DeviceTransactionEventArgs e)
        {
            // Event'i direkt invoke et - handler'lar zaten kendi içinde Task.Run kullanıyor
            // GetInvocationList ve foreach döngüsü gereksiz gecikme yaratıyor
            try
            {
                TransactionReceived?.Invoke(this, e);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Event invoke hatası: {ex.Message}\n{ex.StackTrace}");
            }
        }
        protected virtual void OnConnectionStatusChanged(bool isConnected, string message, int errorCode = 0)
        {
            ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs
            {
                IsConnected = isConnected,
                Message = message,
                ErrorCode = errorCode,
                DeviceInfo = _deviceInfo
            });
        }
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Bağlantıyı kapat
                    DisconnectAsync().Wait(5000);
                }
                _disposed = true;
            }
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}

