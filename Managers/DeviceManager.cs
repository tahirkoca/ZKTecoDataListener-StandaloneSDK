using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;
using Core.Models;
using Providers.ZKTeco;
using Services.Interfaces;

namespace Managers
{
    public class DeviceManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, IDeviceProvider> _providers;
        private readonly IDeviceRepository _deviceRepository;
        private readonly Timer _connectionCheckTimer;
        private readonly Timer _reconnectionTimer;
        private readonly object _lockObject = new object();
        private bool _disposed = false;
        private readonly ILogger _logger;

        // Event'ler
        public event EventHandler<DeviceTransactionEventArgs> TransactionReceived;
        public event EventHandler<ConnectionStatusEventArgs> ConnectionStatusChanged;

        public DeviceManager(IDeviceRepository deviceRepository, ILogger logger)
        {
            _deviceRepository = deviceRepository ?? throw new ArgumentNullException(nameof(deviceRepository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _providers = new ConcurrentDictionary<string, IDeviceProvider>();
            _connectionCheckTimer = new Timer(CheckConnectionsAsync, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
            _reconnectionTimer = new Timer(ReconnectDisconnectedDevicesAsync, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        public async Task InitializeAsync()
        {
            try
            {
                _logger.LogInfo("DeviceManager başlatılıyor...");

                var devices = await _deviceRepository.GetActiveDevicesAsync();

                var tasks = devices.Select(device => Task.Run(async () =>
                {
                    try
                    {
                        await ConnectDeviceAsync(device);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Cihaz bağlantı hatası [{device.DeviceName}]: {ex.Message}");
                    }
                }));

                await Task.WhenAll(tasks);

                _logger.LogInfo($"{devices.Count} cihaz için bağlantı denemesi tamamlandı");
            }
            catch (Exception ex)
            {
                _logger.LogError($"DeviceManager başlatma hatası: {ex.Message}");
                throw;
            }
        }

        private async Task ConnectDeviceAsync(DeviceInfo deviceInfo)
        {
            string key = GetDeviceKey(deviceInfo);

            if (_providers.ContainsKey(key) && _providers[key].IsConnected)
                return;

            try
            {
                _logger.LogInfo($"Bağlanılıyor: [{deviceInfo.CompanyName}] {deviceInfo.DeviceName} ({deviceInfo.IPAddress})");

                IDeviceProvider provider = CreateProvider(deviceInfo, _logger);

                provider.TransactionReceived += OnProviderTransactionReceived;
                provider.ConnectionStatusChanged += OnProviderConnectionStatusChanged;

                try
                {
                    DateTime? lastLogDate = await _deviceRepository.GetLastLogDateAsync(deviceInfo.DeviceId);
                    provider.InitializeLastLogTime(lastLogDate);

                    if (lastLogDate.HasValue)
                        _logger.LogInfo($"[{deviceInfo.DeviceName}] Son log tarihi ile initialize edildi: {lastLogDate.Value:yyyy-MM-dd HH:mm:ss}");
                    else
                        _logger.LogInfo($"[{deviceInfo.DeviceName}] Son log tarihi bulunamadı (yeni kurulum?), cihaz saatine göre başlatılacak.");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[{deviceInfo.DeviceName}] Son log tarihi alma hatası: {ex.Message}");
                }

                bool connected = await provider.ConnectAsync();

                if (connected)
                {
                    _providers.AddOrUpdate(key, provider, (k, v) =>
                    {
                        v?.Dispose();
                        return provider;
                    });

                    await _deviceRepository.UpdateDeviceConnectionStatusAsync(deviceInfo.DeviceId, true);
                    _logger.LogInfo($"✅ Bağlandı: [{deviceInfo.CompanyName}] {deviceInfo.DeviceName}");
                }
                else
                {
                    provider.Dispose();
                    await _deviceRepository.UpdateDeviceConnectionStatusAsync(deviceInfo.DeviceId, false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Bağlantı hatası [{deviceInfo.DeviceName}]: {ex.Message}");
                await _deviceRepository.UpdateDeviceConnectionStatusAsync(deviceInfo.DeviceId, false);
            }
        }

        private IDeviceProvider CreateProvider(DeviceInfo deviceInfo, ILogger logger = null)
        {
            string manufacturer = deviceInfo.Manufacturer?.ToUpper() ?? "";

            switch (manufacturer)
            {
                case "ZKTECO":
                    return ZKTecoProviderFactory.CreateProvider(deviceInfo, logger);

                // Gelecekte başka markalar için:
                // case "HIKVISION":
                //     return HikvisionProviderFactory.CreateProvider(deviceInfo);
                // case "DAHUA":
                //     return DahuaProviderFactory.CreateProvider(deviceInfo);

                default:
                    throw new NotSupportedException($"Desteklenmeyen marka: {deviceInfo.Manufacturer}");
            }
        }

        private async void CheckConnectionsAsync(object state)
        {
            await Task.Run(async () =>
            {
                var tasks = _providers.Values.Select(async provider =>
                {
                    try
                    {
                        await provider.CheckConnectionAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Bağlantı kontrolü hatası: {ex.Message}");
                    }
                });

                await Task.WhenAll(tasks);
            });
        }

        private async void ReconnectDisconnectedDevicesAsync(object state)
        {
            await Task.Run(async () =>
            {
                try
                {
                    var disconnectedDevices = await _deviceRepository.GetDisconnectedDevicesAsync();

                    foreach (var device in disconnectedDevices)
                    {
                        string key = GetDeviceKey(device);

                        if (!_providers.ContainsKey(key) || !_providers[key].IsConnected)
                        {
                            await ConnectDeviceAsync(device);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Yeniden bağlantı hatası: {ex.Message}");
                }
            });
        }

        private void OnProviderTransactionReceived(object sender, DeviceTransactionEventArgs e)
        {
            try
            {
                TransactionReceived?.Invoke(this, e);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Transaction event invoke hatası: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private async void OnProviderConnectionStatusChanged(object sender, ConnectionStatusEventArgs e)
        {
            try
            {
                await _deviceRepository.UpdateDeviceConnectionStatusAsync(e.DeviceInfo.DeviceId, e.IsConnected);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Bağlantı durumu güncelleme hatası: {ex.Message}");
            }
            ConnectionStatusChanged?.Invoke(this, e);
        }

        private string GetDeviceKey(DeviceInfo deviceInfo)
        {
            return $"{deviceInfo.IPAddress}:{deviceInfo.Port}";
        }

        public async Task<bool> OpenDoorAsync(int deviceId)
        {
            try
            {
                IDeviceProvider provider = _providers.Values.FirstOrDefault(p =>
                    p.DeviceInfo.DeviceId == deviceId && p.IsConnected);

                if (provider == null)
                {
                    _logger.LogWarning($"Kapı açılamadı: Cihaz bulunamadı veya bağlı değil: DeviceId={deviceId}");
                    return false;
                }

                return await provider.OpenDoorAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError($"OpenDoorAsync hatası [DeviceId={deviceId}]: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> DeleteUserFromDeviceAsync(int deviceId, string enrollNumber)
        {
            try
            {
                IDeviceProvider provider = _providers.Values.FirstOrDefault(p =>
                    p.DeviceInfo.DeviceId == deviceId && p.IsConnected);

                if (provider == null)
                {
                    _logger.LogWarning($"Cihaz bulunamadı veya bağlı değil: DeviceId={deviceId}");
                    return false;
                }

                bool success = await provider.DeleteUserAsync(enrollNumber);

                if (success)
                {
                    _logger.LogInfo($"✅ Kullanıcı cihazdan silindi: DeviceId={deviceId}, EnrollNumber={enrollNumber}");
                }
                else
                {
                    _logger.LogWarning($"⚠️ Kullanıcı cihazdan silinemedi: DeviceId={deviceId}, EnrollNumber={enrollNumber}");
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError($"DeleteUserFromDeviceAsync hatası: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _connectionCheckTimer?.Dispose();
                _reconnectionTimer?.Dispose();

                foreach (var provider in _providers.Values)
                {
                    try
                    {
                        provider.TransactionReceived -= OnProviderTransactionReceived;
                        provider.ConnectionStatusChanged -= OnProviderConnectionStatusChanged;
                        provider.Dispose();
                    }
                    catch { }
                }

                _providers.Clear();
                _disposed = true;
            }
        }

        public async Task<bool> AddUserToDeviceAsync(int deviceId, string enrollNumber, string name = "", string cardNumber = "")
        {
            try
            {
                IDeviceProvider provider = _providers.Values.FirstOrDefault(p =>
                    p.DeviceInfo.DeviceId == deviceId && p.IsConnected);

                if (provider == null)
                {
                    _logger.LogWarning($"Cihaz bulunamadı veya bağlı değil: DeviceId={deviceId}");
                    return false;
                }

                bool success = await provider.AddUserAsync(enrollNumber, name, "", 0, true, cardNumber);

                if (success)
                {
                    _logger.LogInfo($"✅ Kullanıcı cihaza eklendi: DeviceId={deviceId}, EnrollNumber={enrollNumber}, Name={name}");
                }
                else
                {
                    _logger.LogWarning($"⚠️ Kullanıcı cihaza eklenemedi: DeviceId={deviceId}, EnrollNumber={enrollNumber}");
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError($"AddUserToDeviceAsync hatası: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }
    }
}

