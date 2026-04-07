# ZKTeco Listener Service (Standalone SDK) 📠🔄

![ZKTeco](https://img.shields.io/badge/ZKTeco-SDK-blue) ![C#](https://img.shields.io/badge/C%23-.NET%20Framework-purple) ![Status](https://img.shields.io/badge/Status-Active-success)

[🇹🇷 Türkçe](#t%C3%BCrk%C3%A7e) | [🇬🇧 English](#english)

---

## <a name="türkçe"></a>🇹🇷 Türkçe

### 🚀 Proje Hakkında
Bu proje, **ZKTeco** kart okuyucu ve biyometrik cihazlardan (Standalone SDK destekli) anlık olarak veri dinleyen ve işleyen bir Windows Servis / Form uygulamasıdır. 
Cihazlardan gelen parmak izi, kart okuma veya yüz tanıma verilerini anlık olarak yakalar, veritabanına kaydeder ve gerekli işlemleri (yemekhane aktivasyonu, bildirim vb.) gerçekleştirir.

### ✨ Özellikler
- **Anlık Veri Dinleme**: Cihazlardan gelen hareketleri (transaction) anlık olarak yakalar.
- **Automated QR Access**: Mobil uygulama üzerinden üretilen QR kodları ile anlık kapı/turnike tetikleme.
- **Cihaz Tetik Kuyruğu**: Veritabanı tabanlı (`CihazTetikKuyrugu`) uzaktan kapı kontrolü ve komut kuyruklama.
- **Gelişmiş Validasyon**: Hatalı cihaz tarihleri ve mükerrer kayıtlar için otomatik filtreleme.
- **Otomatik Bağlantı Yönetimi**: Kopan cihazlara otomatik olarak yeniden bağlanır.
- **Yemekhane Entegrasyonu**: Yemekhane girişlerinde bakiye ve hak kontrolü yapar.
- **Loglama**: Detaylı işlem ve hata logları tutar.
- **Çoklu Cihaz Desteği**: IP tabanlı yapılandırma ile birden fazla cihazı aynı anda yönetir.

### 🛠 Kurulum

1.  Projeyi indirin veya klonlayın.
2.  `ZKTecoListenerService.sln` dosyasını Visual Studio ile açın.
3.  **App.config** dosyasını kendi ortamınıza göre düzenleyin (Aşağıya bakınız).
4.  Projeyi `Build` edin.
5.  Uygulamayı çalıştırın (`MainForm` arayüzü ile açılacaktır).

### ⚙️ Yapılandırma
`App.config` dosyasında aşağıdaki alanları kendi sunucu ve e-posta bilgilerinizle güncellediğinizden emin olun:

```xml
<connectionStrings>
    <!-- Veritabanı Bağlantı Dizisi -->
    <add name="CeyPASS" connectionString="Server=YOUR_SERVER_ADDRESS;Database=YOUR_DB_NAME;User Id=YOUR_DB_USER;Password=YOUR_DB_PASSWORD;..." />
</connectionStrings>
<appSettings>
    <!-- E-posta ve Log Ayarları -->
    <add key="SmtpServer" value="YOUR_SMTP_SERVER" />
    <add key="SmtpUser" value="YOUR_SMTP_USER" />
    <add key="SmtpPassword" value="YOUR_EMAIL_PASSWORD" />
</appSettings>
```

---

## <a name="english"></a>🇬🇧 English

### 🚀 About the Project
This project is a Windows Service / Forms application that listens to real-time data from **ZKTeco** card readers and biometric devices (supporting Standalone SDK).
It captures real-time transactions (fingerprint, card swipe, face recognition), saves them to the database, and performs necessary actions (cafeteria activation, notifications, etc.).

### ✨ Features
- **Real-Time Data Listening**: Captures transactions from devices instantly.
- **Automated QR Access**: Instant door/turnstile triggering via mobile-generated QR codes.
- **Device Trigger Queue**: Database-driven (`CihazTetikKuyrugu`) remote control and command queueing.
- **Advanced Validation**: Automatic filtering for corrupted device timestamps and duplicate logs.
- **Auto-Reconnection**: Automatically attempts to reconnect to disconnected devices.
- **Cafeteria Integration**: Checks balance and rights for cafeteria entries.
- **Logging**: Comprehensive activity and error logging.
- **Multi-Device Support**: Manages multiple devices simultaneously via IP configuration.

### 🛠 Installation

1.  Clone or download the project.
2.  Open `ZKTecoListenerService.sln` with Visual Studio.
3.  Configure **App.config** according to your environment (See below).
4.  `Build` the project.
5.  Run the application (It will launch with `MainForm` UI).

### ⚙️ Configuration
Make sure to update the following fields in `App.config` with your own server and email credentials:

```xml
<connectionStrings>
    <!-- Database Connection String -->
    <add name="CeyPASS" connectionString="Server=YOUR_SERVER_ADDRESS;Database=YOUR_DB_NAME;User Id=YOUR_DB_USER;Password=YOUR_DB_PASSWORD;..." />
</connectionStrings>
<appSettings>
    <!-- Email and Log Settings -->
    <add key="SmtpServer" value="YOUR_SMTP_SERVER" />
    <add key="SmtpUser" value="YOUR_SMTP_USER" />
    <add key="SmtpPassword" value="YOUR_EMAIL_PASSWORD" />
</appSettings>
```

---

### 📞 Contact / İletişim
Tahir Koca
