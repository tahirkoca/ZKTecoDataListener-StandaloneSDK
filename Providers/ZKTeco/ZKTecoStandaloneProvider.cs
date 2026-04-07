using Core.Base;
using Core.Models;
using Services.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using zkemkeeper;

namespace Providers.ZKTeco
{
    public class ZKTecoStandaloneProvider : BaseDeviceProvider
    {
        private CZKEM _zkem;
        private const int MachineNumber = 1;

        // COM nesnesi (SDK) thread-safe olmadığı için tüm erişimleri bu kilit ile koruyacağız
        private readonly object _zkemLock = new object();

        //Senkron queue ile event kaybını önlemek için
        private readonly BlockingCollection<DeviceTransactionEventArgs> _eventQueue;
        private readonly CancellationTokenSource _cts;
        private Task _eventProcessorTask;

        // Olay işleme takibi için
        private DateTime _lastTransactionTime = DateTime.MinValue;
        private DateTime _lastEventReceived = DateTime.MinValue;
        private readonly TimeSpan _eventTimeoutThreshold = TimeSpan.FromMinutes(2);
        private readonly SemaphoreSlim _fetchLogsSemaphore = new SemaphoreSlim(1, 1);

        public ZKTecoStandaloneProvider(DeviceInfo deviceInfo, ILogger logger = null) : base(deviceInfo, logger)
        {
            // SDK nesnesini oluştur
            _zkem = new CZKEM();
            _eventQueue = new BlockingCollection<DeviceTransactionEventArgs>(10000);
            _cts = new CancellationTokenSource();
        }

        public override async Task<bool> OpenDoorAsync()
        {
            return await Task.Run(() =>
            {
                lock (_zkemLock)
                {
                    if (!_isConnected || _zkem == null) return false;

                    // Gecikme süresi 1 saniye olarak ayarlandı
                    bool success = _zkem.ACUnlock(MachineNumber, 1);
                    
                    if (success)
                    {
                        _logger?.LogInfo($"[Standalone SDK] {_deviceInfo.DeviceName}: ✅ Kapı başarıyla tetiklendi (ACUnlock)");
                    }
                    else
                    {
                        int errorCode = 0;
                        _zkem.GetLastError(ref errorCode);
                        _logger?.LogError($"[Standalone SDK] {_deviceInfo.DeviceName}: ❌ Kapı tetikleme başarısız! Hata Kodu: {errorCode}");
                    }
                    
                    return success;
                }
            });
        }

        public override async Task<bool> ConnectAsync()
        {
            return await Task.Run(() =>
            {
                lock (_zkemLock)
                {
                    try
                    {
                        if (_zkem == null) _zkem = new CZKEM();

                        _zkem.SetCommPassword(0);
                        bool connected = _zkem.Connect_Net(_deviceInfo.IPAddress, _deviceInfo.Port);

                        if (connected)
                        {
                            if (_zkem.RegEvent(MachineNumber, 65535))
                            {
                                try
                                {
                                    _zkem.OnAttTransactionEx -= OnAttTransactionEx;
                                }
                                catch { }

                                _zkem.OnAttTransactionEx += OnAttTransactionEx;
                                _lastEventReceived = DateTime.Now;

                                _logger?.LogInfo($"[Standalone SDK] {_deviceInfo.DeviceName}: Event handler kaydedildi");

                                _zkem.EnableDevice(MachineNumber, true);
                                _isConnected = true;

                                StartEventProcessor();

                                // Bağlantı sonrası eksik logları çek (5 sn gecikmeyle, cihaz stabilize olsun)
                                Task.Run(async () => { await Task.Delay(5000); await FetchMissingLogsAsync(); });

                                OnConnectionStatusChanged(true, $"Bağlandı: {_deviceInfo.DeviceName}");
                                _logger?.LogInfo($"[Standalone SDK] {_deviceInfo.DeviceName}: Cihaz aktif, event processor başlatıldı");
                                return true;
                            }
                            else
                            {
                                _zkem.Disconnect();
                                OnConnectionStatusChanged(false, "Event kaydı başarısız", -1);
                                return false;
                            }
                        }
                        else
                        {
                            int errorCode = 0;
                            _zkem.GetLastError(ref errorCode);
                            OnConnectionStatusChanged(false, $"Bağlantı hatası: {errorCode}", errorCode);
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        OnConnectionStatusChanged(false, $"Bağlantı exception: {ex.Message}", -1);
                        return false;
                    }
                }
            });
        }

        private void StartEventProcessor()
        {
            if (_eventProcessorTask != null && !_eventProcessorTask.IsCompleted)
                return;

            _eventProcessorTask = Task.Run(async () =>
            {
                _logger?.LogInfo($"[Standalone SDK] {_deviceInfo.DeviceName}: Event processor başlatıldı");

                try
                {
                    foreach (var eventArgs in _eventQueue.GetConsumingEnumerable(_cts.Token))
                    {
                        try
                        {
                            OnTransactionReceived(eventArgs);

                            _lastEventReceived = DateTime.Now;
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError($"[Standalone SDK] {_deviceInfo.DeviceName}: Event işleme hatası - {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogInfo($"[Standalone SDK] {_deviceInfo.DeviceName}: Event processor durduruldu");
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"[Standalone SDK] {_deviceInfo.DeviceName}: Event processor hatası - {ex.Message}");
                }
            }, _cts.Token);
        }

        private void OnAttTransactionEx(string enrollNumber, int isInvalid, int attState, int verifyMethod, int year, int month, int day, int hour, int minute, int second, int workCode)
        {
            try
            {
                if (!TryCreateDateTime(year, month, day, hour, minute, second, out DateTime transactionTime))
                {
                    _logger?.LogWarning($"[Standalone SDK] {_deviceInfo.DeviceName}: ❌ Geçersiz tarih formatı atlandı: {enrollNumber} @ {year}-{month}-{day} {hour}:{minute}:{second}");
                    return;
                }

                // Tarih doğrulaması ekle (2130 vb. hatalı verileri engellemek için)
                if (!IsValidTransactionTime(transactionTime))
                {
                    _logger?.LogWarning($"[Standalone SDK] {_deviceInfo.DeviceName}: ❌ Hatalı/Gelecek tarihli veri atlandı: {enrollNumber} @ {transactionTime:yyyy-MM-dd HH:mm:ss}");
                    return;
                }

                string transactionType = DetermineTransactionType(attState);
                
                // QR kod tespiti ve otomatik kapı tetikleme
                if (enrollNumber.Length > 5)
                {
                    // Şayet yemekhane cihazı ise, Yemekhane tipini "QR_YEMEKHANE" olarak ata (Log ekranı için)
                    if (transactionType == "Yemekhane")
                    {
                        transactionType = "QR_YEMEKHANE";
                        _logger?.LogInfo($"[Standalone SDK] {_deviceInfo.DeviceName}: 🥗 QR Kod ile Yemekhane işlemi algılandı ({enrollNumber})");
                        // Yemekhane için otomatik kapı açma TransactionService içindeki doğrulamadan sonra yapılacak.
                    }
                    else
                    {
                        transactionType = "QR_GIRIS";
                        _logger?.LogInfo($"[Standalone SDK] {_deviceInfo.DeviceName}: 📱 QR Kod algılandı ({enrollNumber}), kapı tetikleniyor...");
                        
                        // Normal kapı/turnike için hemen aç (fire-and-forget)
                        _ = OpenDoorAsync();
                    }
                }

                var eventArgs = new DeviceTransactionEventArgs
                {
                    EnrollNumber = enrollNumber,
                    IsInvalid = isInvalid != 0,
                    AttendanceState = attState,
                    VerifyMethod = verifyMethod,
                    TransactionTime = transactionTime,
                    WorkCode = workCode,
                    DeviceInfo = _deviceInfo,
                    TransactionType = transactionType
                };

                // Real-time gelen verinin zamanını güncelle ki taramada tekrar gelmesin
                if (transactionTime > _lastTransactionTime)
                {
                    _lastTransactionTime = transactionTime;
                }

                if (!_eventQueue.TryAdd(eventArgs, 100))
                {
                    _logger?.LogError($"[Standalone SDK] {_deviceInfo.DeviceName}: ⚠️ Event queue DOLU! Event kaybedildi - {enrollNumber} @ {transactionTime:HH:mm:ss}");
                }
            }
            catch (AccessViolationException ex)
            {
                _logger?.LogError($"[Standalone SDK] {_deviceInfo.DeviceName}: ❌ OnAttTransactionEx AccessViolation - {ex.Message}\\n{ex.StackTrace}");
            }
            catch (SEHException ex)
            {
                _logger?.LogError($"[Standalone SDK] {_deviceInfo.DeviceName}: ❌ OnAttTransactionEx SEHException - {ex.Message}\\n{ex.StackTrace}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[Standalone SDK] {_deviceInfo.DeviceName}: ❌ OnAttTransactionEx exception - {ex.Message}\\n{ex.StackTrace}");
            }
        }

        public override async Task<bool> CheckConnectionAsync()
        {
            return await Task.Run(() =>
            {
                lock (_zkemLock)
                {
                    try
                    {
                        if (_zkem == null) return false;

                        int dwYear = 0, dwMonth = 0, dwDay = 0, dwHour = 0, dwMinute = 0, dwSecond = 0;
                        
                        // Bu çağrı aslında bir "Heartbeat" (Nabız) kontrolüdür
                        bool isConnected = _zkem.GetDeviceTime(MachineNumber, ref dwYear, ref dwMonth,
                            ref dwDay, ref dwHour, ref dwMinute, ref dwSecond);

                        bool connectionChanged = (_isConnected != isConnected);

                        TimeSpan timeSinceLastEvent = DateTime.Now - _lastEventReceived;
                        bool eventTimeout = timeSinceLastEvent > _eventTimeoutThreshold;

                        // Bağlıyız ama event gelmiyor, demek ki event (dinleme) kopmuş, yenileyelim
                        if (eventTimeout && isConnected)
                        {
                            _logger?.LogWarning($"[Standalone SDK] {_deviceInfo.DeviceName}: ⚠️ {timeSinceLastEvent.TotalMinutes:F1} dakikadır event gelmiyor, handler yenileniyor...");

                            try
                            {
                                _zkem.OnAttTransactionEx -= OnAttTransactionEx;
                                _zkem.OnAttTransactionEx += OnAttTransactionEx;

                                if (_zkem.RegEvent(MachineNumber, 65535))
                                {
                                    _lastEventReceived = DateTime.Now;
                                    _logger?.LogInfo($"[Standalone SDK] {_deviceInfo.DeviceName}: ✅ Event handler başarıyla yenilendi");

                                    // Yenileme sonrası eksik logları çek (5 sn gecikmeyle)
                                    Task.Run(async () => { await Task.Delay(5000); await FetchMissingLogsAsync(); });
                                }
                                else
                                {
                                    _logger?.LogError($"[Standalone SDK] {_deviceInfo.DeviceName}: ❌ RegEvent başarısız!");
                                }
                            }
                            catch (AccessViolationException ex)
                            {
                                _logger?.LogError($"[Standalone SDK] {_deviceInfo.DeviceName}: Event handler yenileme AccessViolation - {ex.Message}\\n{ex.StackTrace}");
                            }
                            catch (SEHException ex)
                            {
                                _logger?.LogError($"[Standalone SDK] {_deviceInfo.DeviceName}: Event handler yenileme SEHException - {ex.Message}\\n{ex.StackTrace}");
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogError($"[Standalone SDK] {_deviceInfo.DeviceName}: Event handler yenileme hatası - {ex.Message}\\n{ex.StackTrace}");
                            }
                        }

                        if (connectionChanged)
                        {
                            _isConnected = isConnected;
                            _lastEventReceived = DateTime.MinValue; // Sıfırla

                            OnConnectionStatusChanged(isConnected,
                                isConnected ? "Yeniden bağlandı" : "Bağlantı koptu");

                            if (isConnected)
                            {
                                try
                                {
                                    _zkem.OnAttTransactionEx -= OnAttTransactionEx;
                                    _zkem.OnAttTransactionEx += OnAttTransactionEx;

                                    if (_zkem.RegEvent(MachineNumber, 65535))
                                    {
                                        _lastEventReceived = DateTime.Now;
                                        _logger?.LogInfo($"[Standalone SDK] {_deviceInfo.DeviceName}: Event handler yeniden kaydedildi (bağlantı yeniden kuruldu)");

                                        StartEventProcessor();

                                        // Yeniden bağlantı sonrası eksik logları çek (5 sn gecikmeyle)
                                        Task.Run(async () => { await Task.Delay(5000); await FetchMissingLogsAsync(); });
                                    }
                                    else
                                    {
                                        _logger?.LogWarning($"[Standalone SDK] {_deviceInfo.DeviceName}: Event handler yeniden kaydedilemedi (RegEvent başarısız)");
                                    }
                                }
                                catch (AccessViolationException ex)
                                {
                                    _logger?.LogError($"[Standalone SDK] {_deviceInfo.DeviceName}: Event handler yeniden kaydetme AccessViolation - {ex.Message}\\n{ex.StackTrace}");
                                }
                                catch (SEHException ex)
                                {
                                    _logger?.LogError($"[Standalone SDK] {_deviceInfo.DeviceName}: Event handler yeniden kaydetme SEHException - {ex.Message}\\n{ex.StackTrace}");
                                }
                                catch (Exception ex)
                                {
                                    _logger?.LogError($"[Standalone SDK] {_deviceInfo.DeviceName}: Event handler yeniden kaydetme hatası - {ex.Message}\\n{ex.StackTrace}");
                                }
                            }
                        }

                        return isConnected;
                    }
                    catch
                    {
                        if (_isConnected)
                        {
                            _isConnected = false;
                            _lastEventReceived = DateTime.MinValue;
                            OnConnectionStatusChanged(false, "Bağlantı kontrol hatası");
                        }
                        return false;
                    }
                }
            });
        }

        private string DetermineTransactionType(int attState)
        {
            string deviceName = _deviceInfo.DeviceName?.ToLower() ?? "";

            if (deviceName.Contains("çıkış"))
                return "Çıkış";
            else if (deviceName.Contains("yemekhane"))
                return "Yemekhane";
            else
            {
                switch (attState)
                {
                    case 0: return "Giriş";
                    case 1: return "Çıkış";
                    case 2: return "Ara Çıkış";
                    case 3: return "Ara Giriş";
                    case 4: return "Mesai Başlangıcı";
                    case 5: return "Mesai Bitişi";
                    case 6: return "Yemekhane";
                    default: return $"Bilinmeyen ({attState})";
                }
            }
        }

        public override async Task DisconnectAsync()
        {
            await Task.Run(() =>
            {
                lock (_zkemLock)
                {
                    try
                    {
                        _cts?.Cancel();
                        _eventQueue?.CompleteAdding();

                        if (_zkem != null)
                        {
                            try { _zkem.OnAttTransactionEx -= OnAttTransactionEx; } catch { }
                            _zkem.Disconnect();
                        }

                        _isConnected = false;
                        OnConnectionStatusChanged(false, "Bağlantı kesildi");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"Disconnect error: {ex.Message}");
                    }
                }
            });
        }

        public override void InitializeLastLogTime(DateTime? time)
        {
            if (time.HasValue)
            {
                _lastTransactionTime = time.Value;
                _logger?.LogInfo($"[Standalone SDK] {_deviceInfo.DeviceName}: LastTransactionTime initialize edildi: {_lastTransactionTime:yyyy-MM-dd HH:mm:ss}");
            }
            else
            {
                _lastTransactionTime = DateTime.MinValue;
                _logger?.LogInfo($"[Standalone SDK] {_deviceInfo.DeviceName}: LastTransactionTime NULL, cihazdan tüm veriler çekilebilir.");
            }
        }

        private async Task FetchMissingLogsAsync()
        {
            // Thread-safety için semaphore ile aynı anda sadece bir fetch işleminin yürümesini garanti et
            if (!await _fetchLogsSemaphore.WaitAsync(0))
                return;

            try
            {
                await Task.Run(() =>
                {
                    // COM nesnesi (SDK) kilit çatısı altında çalışmalı
                    lock (_zkemLock)
                    {
                        try
                        {
                            if (!_isConnected || _zkem == null) return;

                            _logger?.LogInfo($"[Standalone SDK] {_deviceInfo.DeviceName}: Eksik loglar taranıyor... (Son işlem: {_lastTransactionTime:yyyy-MM-dd HH:mm:ss})");

                            if (!_zkem.ReadAllGLogData(MachineNumber))
                            {
                                _logger?.LogWarning($"[Standalone SDK] {_deviceInfo.DeviceName}: Hafıza okunamadı (ReadAllGLogData)");
                                return;
                            }

                            string dwEnrollNumber = "";
                            int dwVerifyMode = 0;
                            int dwInOutMode = 0;
                            int dwYear = 0, dwMonth = 0, dwDay = 0, dwHour = 0, dwMinute = 0, dwSecond = 0;
                            int dwWorkCode = 0;

                            int totalFound = 0;
                            int processedCount = 0;

                            while (_zkem.SSR_GetGeneralLogData(MachineNumber, out dwEnrollNumber, out dwVerifyMode,
                                out dwInOutMode, out dwYear, out dwMonth, out dwDay, out dwHour, out dwMinute, out dwSecond, ref dwWorkCode))
                            {
                                totalFound++;

                                if (!TryCreateDateTime(dwYear, dwMonth, dwDay, dwHour, dwMinute, dwSecond, out DateTime transactionTime))
                                    continue;

                                // Tarih doğrulaması ekle (Hatalı verileri engellemek için)
                                if (!IsValidTransactionTime(transactionTime))
                                    continue;

                                if (transactionTime > _lastTransactionTime)
                                {
                                    processedCount++;
                                    var eventArgs = new DeviceTransactionEventArgs
                                    {
                                        EnrollNumber = dwEnrollNumber,
                                        IsInvalid = false,
                                        AttendanceState = dwInOutMode,
                                        VerifyMethod = dwVerifyMode,
                                        TransactionTime = transactionTime,
                                        WorkCode = dwWorkCode,
                                        DeviceInfo = _deviceInfo,
                                        TransactionType = DetermineTransactionType(dwInOutMode)
                                    };

                                    _eventQueue.TryAdd(eventArgs, 100);
                                    _lastTransactionTime = transactionTime;
                                }
                            }

                            if (processedCount > 0)
                                _logger?.LogInfo($"[Standalone SDK] {_deviceInfo.DeviceName}: ✅ {processedCount} adet kaçırılan log kurtarıldı.");
                            else
                                _logger?.LogDebug($"[Standalone SDK] {_deviceInfo.DeviceName}: Kaçırılan yeni log bulunamadı.");
                        }
                        catch (AccessViolationException ex)
                        {
                            _logger?.LogError($"[Standalone SDK] {_deviceInfo.DeviceName}: FetchMissingLogs AccessViolation (ReadAllGLogData/SSR_GetGeneralLogData) - {ex.Message}\\n{ex.StackTrace}");
                        }
                        catch (SEHException ex)
                        {
                            _logger?.LogError($"[Standalone SDK] {_deviceInfo.DeviceName}: FetchMissingLogs SEHException - {ex.Message}\\n{ex.StackTrace}");
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError($"[Standalone SDK] {_deviceInfo.DeviceName}: FetchMissingLogs hatası - {ex.Message}\\n{ex.StackTrace}");
                        }
                    } // Lock bitişi
                });
            }
            finally
            {
                _fetchLogsSemaphore.Release();
            }
        }

        private bool IsValidTransactionTime(DateTime time)
        {
            // Daha garanti bir yol: 2000 yılı gibi cihazın resetlendiği tarihleri de engellemek için
            // sadece son 1 yıla ve şu andan sadece 10 dakika sonrasına (saat farkı payı) izin veriyoruz.
            DateTime minDate = DateTime.Now.AddYears(-1);
            DateTime maxDate = DateTime.Now.AddMinutes(10);

            return time >= minDate && time <= maxDate;
        }

        private bool TryCreateDateTime(int year, int month, int day, int hour, int minute, int second, out DateTime result)
        {
            result = DateTime.MinValue;
            try
            {
                // Değerler mantıklı aralıklarda mı kontrol et (Hızlı eleme için)
                if (year < 2000 || year > 2100 || month < 1 || month > 12 || day < 1 || day > 31)
                    return false;

                result = new DateTime(year, month, day, hour, minute, second);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public override async Task<bool> DeleteUserAsync(string enrollNumber)
        {
            return await Task.Run(() =>
            {
                lock (_zkemLock)
                {
                    try
                    {
                        if (!_isConnected || _zkem == null)
                        {
                            _logger?.LogWarning($"DeleteUserAsync: Cihaz bağlı değil - {_deviceInfo.DeviceName}");
                            return false;
                        }

                        int enrollNum = 0;
                        if (!int.TryParse(enrollNumber, out enrollNum))
                        {
                            _logger?.LogWarning($"DeleteUserAsync: EnrollNumber geçersiz: {enrollNumber}");
                            return false;
                        }

                        _zkem.EnableDevice(MachineNumber, false);
                        bool success = _zkem.DeleteEnrollData(MachineNumber, enrollNum, 1, 12);
                        _zkem.EnableDevice(MachineNumber, true);
                        _zkem.RefreshData(MachineNumber);

                        if (!success)
                        {
                            int errorCode = 0;
                            _zkem.GetLastError(ref errorCode);
                            _logger?.LogError($"DeleteEnrollData failed: EnrollNumber={enrollNumber}, ErrorCode={errorCode}");
                        }

                        return success;
                    }
                    catch (Exception ex)
                    {
                        try { _zkem.EnableDevice(MachineNumber, true); } catch { }
                        _logger?.LogError($"DeleteUserAsync exception: {ex.Message}");
                        return false;
                    }
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    try
                    {
                        // DisconnectAsync zaten kendi lock'ını kullanıyor ama Dispose'da ekstra dikkatli olalım
                        DisconnectAsync().Wait(2000);

                        lock (_zkemLock)
                        {
                            if (_zkem != null)
                            {
                                try
                                {
                                    System.Runtime.InteropServices.Marshal.ReleaseComObject(_zkem);
                                }
                                catch { }

                                _zkem = null;
                            }
                        }

                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();

                        _logger?.LogInfo($"[Standalone SDK] {_deviceInfo.DeviceName}: COM nesneleri temizlendi");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"[Standalone SDK] Dispose hatası: {ex.Message}");
                    }
                }
                base.Dispose(disposing);
            }
        }

        public override async Task<bool> AddUserAsync(string enrollNumber, string name = "", string password = "", int privilege = 0, bool enabled = true, string cardNumber = "")
        {
            return await Task.Run(() =>
            {
                lock (_zkemLock)
                {
                    try
                    {
                        if (!_isConnected || _zkem == null)
                        {
                            _logger?.LogWarning($"AddUserAsync: Cihaz bağlı değil - {_deviceInfo.DeviceName}");
                            return false;
                        }

                        if (!int.TryParse(enrollNumber, out int enrollNum))
                        {
                            _logger?.LogError($"AddUserAsync: EnrollNumber geçersiz: {enrollNumber}");
                            return false;
                        }

                        _zkem.EnableDevice(MachineNumber, false);

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
                                _logger?.LogInfo($"[Standalone SDK] {_deviceInfo.DeviceName}: Kullanıcı zaten mevcut: {enrollNumber}, güncelleniyor...");

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

                            _logger?.LogInfo($"[Standalone SDK] {_deviceInfo.DeviceName}: Yeni kullanıcı ekleniyor: {enrollNumber}");

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

                            _logger?.LogInfo($"✅ [Standalone SDK] {_deviceInfo.DeviceName}: Kullanıcı başarıyla eklendi: {enrollNumber}");
                            return true;
                        }
                        finally
                        {
                            _zkem.EnableDevice(MachineNumber, true);
                            _zkem.RefreshData(MachineNumber);
                        }
                    }
                    catch (Exception ex)
                    {
                        try { _zkem.EnableDevice(MachineNumber, true); } catch { }
                        _logger?.LogError($"AddUserAsync exception: {ex.Message}\n{ex.StackTrace}");
                        return false;
                    }
                }
            });
        }
    }
}
