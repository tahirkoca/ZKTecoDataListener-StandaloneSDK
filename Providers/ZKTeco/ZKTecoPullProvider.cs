using Core.Base;
using Core.Models;
using Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using zkemkeeper;

namespace Providers.ZKTeco
{
    public class ZKTecoPullProvider : BaseDeviceProvider
    {
        private CZKEM _zkem;
        private Timer _pollingTimer;
        private readonly int _pollingIntervalSeconds;
        private DateTime _lastTransactionTime = DateTime.MinValue;
        private readonly SemaphoreSlim _pollingSemaphore = new SemaphoreSlim(1, 1);
        private const int MachineNumber = 1;
        private int _connectionFailureCount = 0;
        private const int MaxConnectionFailures = 3;
        private DateTime _lastSuccessfulPoll = DateTime.MinValue;
        private readonly TimeSpan _pollingTimeout = TimeSpan.FromMinutes(5);

        private readonly Dictionary<string, DateTime> _recentTransactions = new Dictionary<string, DateTime>();
        private readonly object _transactionLock = new object();
        private const int DuplicateWindowSeconds = 5;

        public ZKTecoPullProvider(DeviceInfo deviceInfo, int pollingIntervalSeconds = 30, ILogger logger = null) : base(deviceInfo, logger)
        {
            _zkem = new CZKEM();
            _pollingIntervalSeconds = pollingIntervalSeconds;
        }

        public override async Task<bool> ConnectAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    _zkem.SetCommPassword(0);
                    bool connected = _zkem.Connect_Net(_deviceInfo.IPAddress, _deviceInfo.Port);

                    if (connected)
                    {
                        _zkem.EnableDevice(MachineNumber, true);
                        _isConnected = true;
                        _connectionFailureCount = 0;
                        _lastSuccessfulPoll = DateTime.Now;

                        InitializeLastTransactionTime();
                        _logger?.LogInfo($"[Pull SDK] {_deviceInfo.DeviceName}: Bağlandı, LastTransactionTime={_lastTransactionTime:yyyy-MM-dd HH:mm:ss}");

                        OnConnectionStatusChanged(true, $"Bağlandı: {_deviceInfo.DeviceName}");

                        StartPolling();
                        _logger?.LogInfo($"[Pull SDK] {_deviceInfo.DeviceName}: Polling timer başlatıldı, Interval={_pollingIntervalSeconds} saniye");

                        return true;
                    }
                    else
                    {
                        int errorCode = 0;
                        _zkem.GetLastError(ref errorCode);
                        _connectionFailureCount++;
                        OnConnectionStatusChanged(false, $"Bağlantı hatası: {errorCode}", errorCode);
                        return false;
                    }
                }
                catch (AccessViolationException ex)
                {
                    _connectionFailureCount++;
                    OnConnectionStatusChanged(false, $"Bağlantı AccessViolation: {ex.Message}", -1);
                    return false;
                }
                catch (SEHException ex)
                {
                    _connectionFailureCount++;
                    OnConnectionStatusChanged(false, $"Bağlantı SEHException: {ex.Message}", -1);
                    return false;
                }
                catch (Exception ex)
                {
                    _connectionFailureCount++;
                    OnConnectionStatusChanged(false, $"Bağlantı exception: {ex.Message}", -1);
                    return false;
                }
            });
        }

        public override async Task DisconnectAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    StopPolling();

                    if (_zkem != null)
                    {
                        _zkem.Disconnect();
                    }

                    _isConnected = false;
                    OnConnectionStatusChanged(false, "Bağlantı kesildi");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Disconnect error: {ex.Message}");
                }
            });
        }

        public override async Task<bool> CheckConnectionAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (_zkem == null) return false;

                    bool isConnected = _zkem.ReadAllGLogData(MachineNumber);

                    if (!isConnected)
                    {
                        int errorCode = 0;
                        _zkem.GetLastError(ref errorCode);

                        if (errorCode == -1 || errorCode == -8 || errorCode == -2 || errorCode == -10053)
                        {
                            isConnected = false;
                        }
                        else
                        {
                            _logger?.LogWarning($"[Pull Provider] ReadAllGLogData başarısız ama hata kodu geçici olabilir: {errorCode}");
                            isConnected = _isConnected;
                        }
                    }

                    bool connectionChanged = (_isConnected != isConnected);

                    TimeSpan timeSinceLastPoll = DateTime.Now - _lastSuccessfulPoll;
                    bool pollingTimeout = _isConnected && timeSinceLastPoll > _pollingTimeout;

                    if (pollingTimeout)
                    {
                        _logger?.LogWarning($"[Pull Provider] {_deviceInfo.DeviceName}: ⚠️ {timeSinceLastPoll.TotalMinutes:F1} dakikadır başarılı polling yok");
                        isConnected = false;
                        connectionChanged = true;
                    }

                    if (connectionChanged)
                    {
                        _isConnected = isConnected;

                        if (isConnected)
                        {
                            _connectionFailureCount = 0;
                            _lastSuccessfulPoll = DateTime.Now;
                            _logger?.LogInfo($"[Pull Provider] {_deviceInfo.DeviceName}: ✅ Yeniden bağlandı");
                            StartPolling();
                            OnConnectionStatusChanged(true, "Yeniden bağlandı");
                        }
                        else
                        {
                            _connectionFailureCount++;
                            _logger?.LogWarning($"[Pull Provider] {_deviceInfo.DeviceName}: ❌ Bağlantı koptu (Hata: {_connectionFailureCount})");
                            StopPolling();
                            OnConnectionStatusChanged(false, "Bağlantı koptu");
                        }
                    }

                    return isConnected;
                }
                catch (AccessViolationException ex)
                {
                    _connectionFailureCount++;

                    if (_isConnected)
                    {
                        _isConnected = false;
                        _logger?.LogError($"[Pull Provider] CheckConnection AccessViolation: {ex.Message}\\n{ex.StackTrace}");
                        StopPolling();
                        OnConnectionStatusChanged(false, "Bağlantı kontrol AccessViolation");
                    }

                    return false;
                }
                catch (SEHException ex)
                {
                    _connectionFailureCount++;

                    if (_isConnected)
                    {
                        _isConnected = false;
                        _logger?.LogError($"[Pull Provider] CheckConnection SEHException: {ex.Message}\\n{ex.StackTrace}");
                        StopPolling();
                        OnConnectionStatusChanged(false, "Bağlantı kontrol SEHException");
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    _connectionFailureCount++;

                    if (_isConnected)
                    {
                        _isConnected = false;
                        _logger?.LogError($"[Pull Provider] CheckConnection exception: {ex.Message}");
                        StopPolling();
                        OnConnectionStatusChanged(false, "Bağlantı kontrol hatası");
                    }

                    return false;
                }
            });
        }

        private async Task PollForTransactionsAsync()
        {
            if (!await _pollingSemaphore.WaitAsync(0))
            {
                _logger?.LogDebug($"[Pull Provider] {_deviceInfo.DeviceName}: Polling zaten çalışıyor, atlandı");
                return;
            }

            try
            {
                if (!_isConnected || _zkem == null)
                {
                    _logger?.LogDebug($"[Pull Provider] {_deviceInfo.DeviceName}: Bağlı değil, polling atlandı");
                    return;
                }

                await Task.Run(() =>
                {
                    try
                    {
                        bool readSuccess = _zkem.ReadAllGLogData(MachineNumber);

                        if (!readSuccess)
                        {
                            int errorCode = 0;
                            _zkem.GetLastError(ref errorCode);

                            _logger?.LogWarning($"[Pull Provider] ReadAllGLogData başarısız (Error={errorCode})");

                            if (errorCode == -1 || errorCode == -8 || errorCode == -2 || errorCode == -10053)
                            {
                                _connectionFailureCount++;

                                if (_connectionFailureCount >= MaxConnectionFailures)
                                {
                                    _logger?.LogError($"[Pull Provider] ❌ Sürekli başarısız polling");
                                    _isConnected = false;
                                    StopPolling();
                                    OnConnectionStatusChanged(false, $"Polling başarısız (Error={errorCode})");
                                }
                            }

                            return;
                        }

                        _connectionFailureCount = 0;
                        _lastSuccessfulPoll = DateTime.Now;

                        int dwEnrollNumber = 0;
                        int dwVerifyMode = 0;
                        int dwInOutMode = 0;
                        int dwYear = 0;
                        int dwMonth = 0;
                        int dwDay = 0;
                        int dwHour = 0;
                        int dwMinute = 0;
                        int dwSecond = 0;
                        int dwWorkCode = 0;

                        int totalLogCount = 0;
                        int processedLogCount = 0;

                        string enrollNumberStr = "";

                        while (_zkem.SSR_GetGeneralLogData(MachineNumber, out enrollNumberStr, out dwVerifyMode, out dwInOutMode, out dwYear, out dwMonth, out dwDay, out dwHour, out dwMinute, out dwSecond, ref dwWorkCode))
                        {
                            totalLogCount++;

                            try
                            {
                                DateTime transactionTime = new DateTime(dwYear, dwMonth, dwDay, dwHour, dwMinute, dwSecond);

                                string transactionKey = $"{enrollNumberStr}_{transactionTime:yyyyMMddHHmmss}_{dwInOutMode}";

                                bool isDuplicate = false;
                                lock (_transactionLock)
                                {
                                    var keysToRemove = _recentTransactions
                                        .Where(kvp => (DateTime.Now - kvp.Value).TotalSeconds > DuplicateWindowSeconds)
                                        .Select(kvp => kvp.Key)
                                        .ToList();

                                    foreach (var key in keysToRemove)
                                        _recentTransactions.Remove(key);

                                    if (_recentTransactions.ContainsKey(transactionKey))
                                    {
                                        isDuplicate = true;
                                    }
                                    else
                                    {
                                        _recentTransactions[transactionKey] = DateTime.Now;
                                    }
                                }

                                if (isDuplicate)
                                {
                                    _logger?.LogDebug($"[Pull Provider] Duplicate atlandı: {enrollNumberStr} @ {transactionTime:HH:mm:ss}");
                                    continue;
                                }

                                if (transactionTime > _lastTransactionTime)
                                {
                                    processedLogCount++;

                                    var eventArgs = new DeviceTransactionEventArgs
                                    {
                                        EnrollNumber = enrollNumberStr,
                                        IsInvalid = false,
                                        AttendanceState = dwInOutMode,
                                        VerifyMethod = dwVerifyMode,
                                        TransactionTime = transactionTime,
                                        WorkCode = dwWorkCode,
                                        DeviceInfo = _deviceInfo,
                                        TransactionType = DetermineTransactionType(dwInOutMode)
                                    };

                                    OnTransactionReceived(eventArgs);

                                    _logger?.LogInfo($"[Pull Provider] Transaction: {enrollNumberStr} @ {transactionTime:yyyy-MM-dd HH:mm:ss}");

                                    _lastTransactionTime = transactionTime;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogError($"[Pull Provider] Transaction işleme hatası: {ex.Message}");
                            }
                        }

                        if (totalLogCount > 0)
                        {
                            _logger?.LogInfo($"[Pull Provider] ✅ Polling: Toplam={totalLogCount}, İşlenen={processedLogCount}");
                        }
                        else
                        {
                            _logger?.LogDebug($"[Pull Provider] Polling: Yeni log yok");
                        }
                    }
                    catch (AccessViolationException ex)
                    {
                        _logger?.LogError($"[Pull Provider] ❌ Polling AccessViolation: {ex.Message}\\n{ex.StackTrace}");
                        _connectionFailureCount++;

                        if (_connectionFailureCount >= MaxConnectionFailures)
                        {
                            _isConnected = false;
                            StopPolling();
                            OnConnectionStatusChanged(false, "Polling AccessViolation");
                        }
                    }
                    catch (SEHException ex)
                    {
                        _logger?.LogError($"[Pull Provider] ❌ Polling SEHException: {ex.Message}\\n{ex.StackTrace}");
                        _connectionFailureCount++;

                        if (_connectionFailureCount >= MaxConnectionFailures)
                        {
                            _isConnected = false;
                            StopPolling();
                            OnConnectionStatusChanged(false, "Polling SEHException");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"[Pull Provider] ❌ Polling hatası: {ex.Message}");
                        _connectionFailureCount++;

                        if (_connectionFailureCount >= MaxConnectionFailures)
                        {
                            _isConnected = false;
                            StopPolling();
                            OnConnectionStatusChanged(false, "Polling exception");
                        }
                    }
                });
            }
            finally
            {
                _pollingSemaphore.Release();
            }
        }

        private void InitializeLastTransactionTime()
        {
            if (_lastTransactionTime != DateTime.MinValue)
                return;

            try
            {
                int dwYear = 0, dwMonth = 0, dwDay = 0, dwHour = 0, dwMinute = 0, dwSecond = 0;
                if (_zkem.GetDeviceTime(MachineNumber, ref dwYear, ref dwMonth, ref dwDay, ref dwHour, ref dwMinute, ref dwSecond))
                {
                    DateTime deviceTime = new DateTime(dwYear, dwMonth, dwDay, dwHour, dwMinute, dwSecond);
                    _lastTransactionTime = deviceTime.AddMinutes(-5);
                    _logger?.LogInfo($"[Pull SDK] {_deviceInfo.DeviceName}: LastTransactionTime cihaz zamanına göre ayarlandı: {_lastTransactionTime:yyyy-MM-dd HH:mm:ss}");
                }
                else
                {
                    _lastTransactionTime = DateTime.Now.AddMinutes(-5);
                    _logger?.LogWarning($"[Pull SDK] {_deviceInfo.DeviceName}: Cihaz zamanı alınamadı, Server zamanı kullanıldı: {_lastTransactionTime:yyyy-MM-dd HH:mm:ss}");
                }
            }
            catch (Exception ex)
            {
                _lastTransactionTime = DateTime.Now.AddMinutes(-5);
                _logger?.LogError($"[Pull SDK] InitializeLastTransactionTime hatası: {ex.Message}");
            }
        }

        private void StartPolling()
        {
            if (_pollingTimer != null)
            {
                try
                {
                    _pollingTimer.Dispose();
                }
                catch { }
            }

            _pollingTimer = new Timer(async _ => await PollForTransactionsAsync(),null,TimeSpan.Zero,TimeSpan.FromSeconds(_pollingIntervalSeconds));

            _logger?.LogDebug($"[Pull SDK] {_deviceInfo.DeviceName}: Polling timer (yeniden) başlatıldı");
        }

        private void StopPolling()
        {
            if (_pollingTimer != null)
            {
                try
                {
                    _pollingTimer.Dispose();
                    _pollingTimer = null;
                    _logger?.LogDebug($"[Pull SDK] {_deviceInfo.DeviceName}: Polling timer durduruldu");
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"[Pull SDK] {_deviceInfo.DeviceName}: Polling timer durdurma hatası: {ex.Message}");
                }
            }
        }

        private string DetermineTransactionType(int dwInOutMode)
        {
            string deviceName = _deviceInfo.DeviceName?.ToLower() ?? "";

            if (deviceName.Contains("çıkış"))
                return "Çıkış";
            else if (deviceName.Contains("yemekhane"))
                return "Yemekhane";
            else
            {
                switch (dwInOutMode)
                {
                    case 0: return "Giriş";
                    case 1: return "Çıkış";
                    case 2: return "Ara Çıkış";
                    case 3: return "Ara Giriş";
                    case 4: return "Mesai Başlangıcı";
                    case 5: return "Mesai Bitişi";
                    case 6: return "Yemekhane";
                    default: return $"Bilinmeyen ({dwInOutMode})";
                }
            }
        }

        public override async Task<bool> DeleteUserAsync(string enrollNumber)
        {
            if (!_isConnected || _zkem == null)
            {
                System.Diagnostics.Debug.WriteLine($"DeleteUserAsync: Cihaz bağlı değil - {_deviceInfo.DeviceName}");
                return false;
            }

            return await Task.Run(() =>
            {
                try
                {
                    int enrollNum = 0;
                    if (!int.TryParse(enrollNumber, out enrollNum))
                    {
                        System.Diagnostics.Debug.WriteLine($"DeleteUserAsync: EnrollNumber geçersiz: {enrollNumber}");
                        return false;
                    }

                    bool success = _zkem.DeleteEnrollData(MachineNumber, enrollNum, 1, 12);

                    _zkem.RefreshData(MachineNumber);

                    if (!success)
                    {
                        int errorCode = 0;
                        _zkem.GetLastError(ref errorCode);
                        System.Diagnostics.Debug.WriteLine($"DeleteEnrollData failed: EnrollNumber={enrollNumber}, ErrorCode={errorCode}");
                    }

                    return success;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"DeleteUserAsync exception: {ex.Message}");
                    return false;
                }
            });
        }

        public override void InitializeLastLogTime(DateTime? time)
        {
            if (time.HasValue)
            {
                _lastTransactionTime = time.Value.AddSeconds(-1);
                _logger?.LogInfo($"[Pull SDK] {_deviceInfo.DeviceName}: LastTransactionTime dışarıdan initialize edildi: {_lastTransactionTime:yyyy-MM-dd HH:mm:ss}");
            }
            else
            {
                _lastTransactionTime = DateTime.MinValue;
                _logger?.LogInfo($"[Pull SDK] {_deviceInfo.DeviceName}: LastTransactionTime NULL (DB'de kayıt yok), cihaz saatine göre ayarlanacak.");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    StopPolling();
                    DisconnectAsync().Wait(5000);
                    _pollingSemaphore?.Dispose();
                    _zkem = null;
                }
                base.Dispose(disposing);
            }
        }

        public override async Task<bool> AddUserAsync(string enrollNumber, string name = "", string password = "", int privilege = 0, bool enabled = true, string cardNumber = "")
        {
            if (!_isConnected || _zkem == null)
            {
                _logger?.LogWarning($"AddUserAsync: Cihaz bağlı değil - {_deviceInfo.DeviceName}");
                return false;
            }

            return await Task.Run(() =>
            {
                try
                {
                    if (!int.TryParse(enrollNumber, out int enrollNum))
                    {
                        _logger?.LogError($"AddUserAsync: EnrollNumber geçersiz: {enrollNumber}");
                        return false;
                    }

                    try
                    {
                        string existingName = "";
                        string existingPassword = "";
                        int existingPrivilege = 0;
                        bool existingEnabled = true;

                        bool userExists = _zkem.GetUserInfo(MachineNumber, enrollNum,
                            ref existingName, ref existingPassword, ref existingPrivilege, ref existingEnabled);

                        if (userExists)
                        {
                            _logger?.LogInfo($"[Pull SDK] {_deviceInfo.DeviceName}: Kullanıcı zaten mevcut: {enrollNumber}, güncelleniyor...");

                            if (!string.IsNullOrEmpty(cardNumber))
                            {
                                _zkem.SetStrCardNumber(cardNumber);
                            }

                            bool updateSuccess = _zkem.SetUserInfo(MachineNumber, enrollNum,
                                existingName, existingPassword, existingPrivilege, enabled);

                            if (updateSuccess)
                            {
                                _logger?.LogInfo($"✅ Kullanıcı aktif edildi: {enrollNumber}");
                            }
                            else
                            {
                                int errorCode = 0;
                                _zkem.GetLastError(ref errorCode);
                                _logger?.LogError($"SetUserInfo failed: EnrollNumber={enrollNumber}, ErrorCode={errorCode}");
                            }

                            return updateSuccess;
                        }

                        _logger?.LogInfo($"[Pull SDK] {_deviceInfo.DeviceName}: Yeni kullanıcı ekleniyor: {enrollNumber}");
                        
                        if (!string.IsNullOrEmpty(cardNumber))
                        {
                            _zkem.SetStrCardNumber(cardNumber);
                        }

                        bool success = _zkem.SetUserInfo(MachineNumber, enrollNum,
                            string.IsNullOrEmpty(name) ? enrollNumber : name,
                            password,
                            privilege,
                            enabled);

                        if (!success)
                        {
                            int errorCode = 0;
                            _zkem.GetLastError(ref errorCode);
                            _logger?.LogError($"SetUserInfo (new user) failed: EnrollNumber={enrollNumber}, ErrorCode={errorCode}");
                            return false;
                        }

                        _logger?.LogInfo($"✅ [Pull SDK] {_deviceInfo.DeviceName}: Kullanıcı başarıyla eklendi: {enrollNumber}");
                        return true;
                    }
                    finally
                    {
                        _zkem.RefreshData(MachineNumber);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"AddUserAsync exception: {ex.Message}\n{ex.StackTrace}");
                    return false;
                }
            });
        }
        public override async Task<bool> OpenDoorAsync()
        {
            return await Task.Run(() =>
            {
                _logger?.LogWarning($"[Pull SDK] {_deviceInfo.DeviceName}: OpenDoorAsync bu SDK tipi için henüz tam desteklenmemektedir.");
                return false;
            });
        }
    }
}
