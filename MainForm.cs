using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Managers;
using Services.Implementations;
using Services.Interfaces;
using Core.Models;

namespace ZKTecoListenerService
{
    public partial class MainForm : Form
    {
        private DeviceManager _deviceManager;
        private ITransactionService _transactionService;
        private INotificationService _notificationService;
        private IMealHallActivationService _mealHallActivationService;
        private ILogger _logger;
        private System.Threading.Timer _activationTimer;
        private System.Threading.Timer _doorTriggerTimer;
        private bool _isRunning = false;

        private ListBox _logListBox;
        private ListBox _transactionListBox;
        private Label _statusLabel;

        // System Tray
        private NotifyIcon _notifyIcon;

        public MainForm()
        {
            InitializeComponent();
            InitializeService();
        }

        private void InitializeComponent()
        {
            this.Text = "ZKTeco Listener Service";
            this.Size = new System.Drawing.Size(1200, 700);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormClosing += MainForm_FormClosing;
            this.Resize += MainForm_Resize; // Resize event'i eklendi

            // NotifyIcon
            _notifyIcon = new NotifyIcon();
            _notifyIcon.Text = "ZKTeco Data Listener";
            _notifyIcon.Visible = false;
            try
            {
                _notifyIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                // İkon yüklenemezse sistem uyarısı ikonu kullan (yedek)
                _notifyIcon.Icon = SystemIcons.Application;
            }
            _notifyIcon.MouseDoubleClick += NotifyIcon_MouseDoubleClick;

            // Status Label
            _statusLabel = new Label
            {
                Text = "Başlatılıyor...",
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = System.Drawing.Color.LightBlue,
                Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Bold),
                Padding = new Padding(10, 5, 10, 5)
            };
            this.Controls.Add(_statusLabel);

            // SplitContainer - üstte loglar, altta transaction'lar
            var splitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 400,
                FixedPanel = FixedPanel.None
            };

            // Log ListBox (üst panel)
            _logListBox = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new System.Drawing.Font("Consolas", 9F),
                BackColor = System.Drawing.Color.FromArgb(30, 30, 30),
                ForeColor = System.Drawing.Color.LightGreen
            };
            splitContainer.Panel1.Controls.Add(_logListBox);

            // Transaction ListBox (alt panel)
            _transactionListBox = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new System.Drawing.Font("Consolas", 9F),
                BackColor = System.Drawing.Color.FromArgb(30, 30, 30),
                ForeColor = System.Drawing.Color.White
            };
            splitContainer.Panel2.Controls.Add(_transactionListBox);

            this.Controls.Add(splitContainer);
        }

        private void InitializeService()
        {
            try
            {
                // Önce Logger'ları oluştur
                var formLogger = new FormLogger(this);
                // Dosya loglama: AppData klasörü altına veya projenin yanına
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "service_log.txt");
                var fileLogger = new Services.Implementations.FileLogger(logPath);

                // Composite Logger sayesinde hem ekrana hem dosyaya yazılacak
                _logger = new CompositeLogger(formLogger, fileLogger);

                UpdateStatus("Servis başlatılıyor...", System.Drawing.Color.LightBlue);
                LogMessage("=== Servis başlatılıyor ===");

                // Configuration'dan connection string'i al
                string connectionString = System.Configuration.ConfigurationManager.ConnectionStrings["CeyPASS"]?.ConnectionString
                    ?? "Server=YOUR_SERVER_ADDRESS;Database=CeyPASS;User Id=sa;Password=YOUR_DB_PASSWORD;;MultipleActiveResultSets=True;";

                _logger.LogInfo($"Connection String: {connectionString.Substring(0, Math.Min(50, connectionString.Length))}...");

                // Servisleri oluştur
                _logger.LogInfo("Servisler oluşturuluyor...");
                IDeviceRepository deviceRepository = new DeviceRepository(connectionString, _logger);

                // DeviceManager'ı önce oluştur (TransactionService'e geçilecek)
                _logger.LogInfo("DeviceManager oluşturuluyor...");
                _deviceManager = new DeviceManager(deviceRepository, _logger);

                // TransactionService'e DeviceManager'ı geç
                _transactionService = new TransactionService(connectionString, _logger, _deviceManager);
                _notificationService = new NotificationService(connectionString, _logger);

                // Event'leri dinle
                _deviceManager.TransactionReceived += OnTransactionReceived;
                _deviceManager.ConnectionStatusChanged += OnConnectionStatusChanged;

                // MealHallActivationService'i oluştur
                _mealHallActivationService = new MealHallActivationService(connectionString, _logger, _deviceManager);

                // Cihazları başlat (async, non-blocking)
                Task.Run(async () =>
                {
                    try
                    {
                        await _deviceManager.InitializeAsync();
                        _isRunning = true;
                        this.Invoke((MethodInvoker)delegate
                        {
                            UpdateStatus("✅ Servis çalışıyor", System.Drawing.Color.LightGreen);
                        });
                        _logger.LogInfo("✅ Tüm cihazlar başlatıldı, servis çalışıyor");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"❌ Cihaz başlatma hatası: {ex.Message}");
                        this.Invoke((MethodInvoker)delegate
                        {
                            UpdateStatus($"❌ Hata: {ex.Message}", System.Drawing.Color.Red);
                        });
                    }
                });

                // Yemekhane aktifleştirme timer'ı (10 dakikada bir)
                _activationTimer = new System.Threading.Timer(async _ =>
                {
                    if (_isRunning && _mealHallActivationService != null)
                    {
                        await _mealHallActivationService.ProcessActivationAsync();
                    }
                }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));
                
                // Kapı tetikleme kuyruğu izleme (1 saniyede bir)
                _doorTriggerTimer = new System.Threading.Timer(async _ =>
                {
                    if (_isRunning)
                    {
                        await ProcessDoorTriggersAsync();
                    }
                }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));

                _logger.LogInfo("Servis başarıyla başlatıldı");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Servis başlatma hatası: {ex.Message}");
                LogMessage($"Stack Trace: {ex.StackTrace}");
                UpdateStatus($"❌ Hata: {ex.Message}", System.Drawing.Color.Red);
            }
        }

        // Simge durumuna küçüldüğünde çalışacak
        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                this.Hide(); // Formu gizle
                _notifyIcon.Visible = true; // Tray ikonunu göster
                _notifyIcon.ShowBalloonTip(1000, "ZKTeco Data Listener", "Program arka planda çalışıyor...", ToolTipIcon.Info);
            }
        }

        // Tray ikonuna çift tıklandığında çalışacak
        private void NotifyIcon_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            this.Show(); // Formu göster
            this.WindowState = FormWindowState.Normal; // Normal boyuta getir
            _notifyIcon.Visible = false; // Tray ikonunu gizle
        }

        private void OnTransactionReceived(object sender, DeviceTransactionEventArgs e)
        {
            // Event handler'ı hemen döndür - hiçbir şeyi bloklama
            // Tüm işlemleri fire-and-forget yap
            // Eski versiyondaki gibi: Event handler HEMEN döner, tüm işlemler background'da yapılır

            // Log'u fire-and-forget yap (blocking olmaması için)
            _ = Task.Run(() =>
            {
                try
                {
                    _logger?.LogInfo($"📥 Transaction alındı: [{e.DeviceInfo.CompanyName}] {e.DeviceInfo.DeviceName} | {e.EnrollNumber} | {e.TransactionTime:yyyy-MM-dd HH:mm:ss} | {e.TransactionType}");
                }
                catch { } // Log hatası event handler'ı bloklamasın
            });

            // UI güncellemesini non-blocking yap (BeginInvoke kullan)
            if (this.InvokeRequired)
            {
                this.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        string log = $"[{DateTime.Now:HH:mm:ss}][{e.DeviceInfo.CompanyName}] {e.DeviceInfo.DeviceName} | {e.EnrollNumber} | {e.TransactionTime:yyyy-MM-dd HH:mm:ss} | {e.TransactionType}";
                        _transactionListBox.Items.Insert(0, log);
                        if (_transactionListBox.Items.Count > 1000)
                            _transactionListBox.Items.RemoveAt(_transactionListBox.Items.Count - 1);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"UI güncelleme hatası: {ex.Message}");
                    }
                });
            }
            else
            {
                // Eğer zaten UI thread'indeysek direkt güncelle
                try
                {
                    string log = $"[{DateTime.Now:HH:mm:ss}][{e.DeviceInfo.CompanyName}] {e.DeviceInfo.DeviceName} | {e.EnrollNumber} | {e.TransactionTime:yyyy-MM-dd HH:mm:ss} | {e.TransactionType}";
                    _transactionListBox.Items.Insert(0, log);
                    if (_transactionListBox.Items.Count > 1000)
                        _transactionListBox.Items.RemoveAt(_transactionListBox.Items.Count - 1);
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"UI güncelleme hatası: {ex.Message}");
                }
            }

            // Transaction'ı kaydet (async, non-blocking, fire-and-forget)
            // Her transaction paralel işlenebilir - birbirini bloklamaz
            _ = Task.Run(async () =>
            {
                try
                {
                    await _transactionService.SaveTransactionAsync(e);

                    // Yemekhane transaction'ı ise özel işlem yap (fire-and-forget, await etme)
                    if (e.TransactionType == "Yemekhane" || e.TransactionType == "QR_YEMEKHANE")
                    {
                        // ProcessMealHallTransactionAsync zaten fire-and-forget içinde, await etme
                        _transactionService.ProcessMealHallTransactionAsync(e);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Transaction kaydetme hatası: {ex.Message}\n{ex.StackTrace}");
                }
            });
        }

        private async Task ProcessDoorTriggersAsync()
        {
            try
            {
                var repo = new DeviceRepository(System.Configuration.ConfigurationManager.ConnectionStrings["CeyPASS"]?.ConnectionString, _logger);
                var pendingTriggers = await repo.GetPendingTriggersAsync();

                foreach (var trigger in pendingTriggers)
                {
                    try
                    {
                        int cihazId = trigger.CihazId;
                        string personelId = trigger.PersonelId;
                        string komut = trigger.Komut; // QR_GIRIS veya QR_YEMEKHANE
                        int triggerId = trigger.Id;

                        _logger.LogInfo($"[Queue] 📥 Yeni tetikleme emri alındı: CihazId={cihazId}, Komut={komut}");

                        // Kapıyı aç
                        bool success = await _deviceManager.OpenDoorAsync(cihazId);

                        if (success)
                        {
                            // Arayüze yansıtması için sanal bir event fırlat (TransactionReceived handler'ını kullan)
                            // Ama IsRemoteTrigger = true yaparak veritabanına tekrar kaydolmasını engelle
                            var deviceInfo = (await repo.GetActiveDevicesAsync()).FirstOrDefault(d => d.DeviceId == cihazId);
                            
                            if (deviceInfo != null)
                            {
                                var e = new DeviceTransactionEventArgs
                                {
                                    DeviceInfo = deviceInfo,
                                    EnrollNumber = personelId,
                                    TransactionTime = DateTime.Now,
                                    TransactionType = komut, // QR_GIRIS veya QR_YEMEKHANE
                                    IsRemoteTrigger = true // KRİTİK: Veritabanına kaydı atlaması için
                                };
                                OnTransactionReceived(this, e);
                            }

                            // Okundu olarak işaretle
                            await repo.MarkTriggerAsReadAsync(triggerId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Tetik işleme hatası (ID={trigger.Id}): {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                // Sessizce logla (Timer'ı patlatmasın)
                _logger.LogDebug($"ProcessDoorTriggersAsync genel hata: {ex.Message}");
            }
        }

        private void OnConnectionStatusChanged(object sender, ConnectionStatusEventArgs e)
        {
            try
            {
                if (!e.IsConnected)
                {
                    // Bağlantı koptu, bildirim gönder
                    Task.Run(async () =>
                    {
                        await _notificationService.SendDeviceDisconnectedNotificationAsync(e);
                    });
                }

                _logger?.LogInfo($"Bağlantı durumu: {e.DeviceInfo.DeviceName} - {(e.IsConnected ? "Bağlı" : "Kopuk")}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Connection status işleme hatası: {ex.Message}");
            }
        }

        public void LogMessage(string message)
        {
            if (this.InvokeRequired)
            {
                this.Invoke((MethodInvoker)delegate { LogMessage(message); });
                return;
            }

            string logEntry = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _logListBox.Items.Insert(0, logEntry);
            if (_logListBox.Items.Count > 500)
                _logListBox.Items.RemoveAt(_logListBox.Items.Count - 1);
        }

        private void UpdateStatus(string text, System.Drawing.Color color)
        {
            if (this.InvokeRequired)
            {
                this.Invoke((MethodInvoker)delegate { UpdateStatus(text, color); });
                return;
            }

            _statusLabel.Text = text;
            _statusLabel.BackColor = color;
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                _logger?.LogInfo("=== Servis durduruluyor ===");
                _isRunning = false;

                // Timer'ları durdur
                _activationTimer?.Dispose();
                _doorTriggerTimer?.Dispose();

                // DeviceManager'ı temizle
                _deviceManager?.Dispose();

                // NotifyIcon'u temizle
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                _logger?.LogInfo("Servis durduruldu");

                // Kısa bir bekleme (logların yazılması için)
                System.Threading.Thread.Sleep(500);

                Application.Exit();
            }
            catch (Exception ex)
            {
                // Logger'a ulaşılamazsa diye basit dosya yazımı
                try
                {
                    System.IO.File.AppendAllText("shutdown_error.txt", ex.ToString());
                }
                catch { }

                _logger?.LogError($"Servis durdurma hatası: {ex.Message}");
            }
        }
    }

    // Hem forma hem dosyaya yazan Logger
    public class CompositeLogger : ILogger
    {
        private readonly FormLogger _formLogger;
        private readonly Services.Implementations.FileLogger _fileLogger;

        public CompositeLogger(FormLogger formLogger, Services.Implementations.FileLogger fileLogger)
        {
            _formLogger = formLogger;
            _fileLogger = fileLogger;
        }

        public void LogDebug(string message)
        {
            _formLogger?.LogDebug(message);
            _fileLogger?.LogDebug(message);
        }

        public void LogError(string message)
        {
            _formLogger?.LogError(message);
            _fileLogger?.LogError(message);
        }

        public void LogInfo(string message)
        {
            _formLogger?.LogInfo(message);
            _fileLogger?.LogInfo(message);
        }

        public void LogWarning(string message)
        {
            _formLogger?.LogWarning(message);
            _fileLogger?.LogWarning(message);
        }
    }

    public class FormLogger : ILogger
    {
        private readonly MainForm _form;

        public FormLogger(MainForm form)
        {
            _form = form;
        }

        public void LogInfo(string message)
        {
            _form?.LogMessage($"[INFO] {message}");
        }

        public void LogError(string message)
        {
            _form?.LogMessage($"[ERROR] {message}");
        }

        public void LogWarning(string message)
        {
            _form?.LogMessage($"[WARNING] {message}");
        }

        public void LogDebug(string message)
        {
            _form?.LogMessage($"[DEBUG] {message}");
        }
    }
}
