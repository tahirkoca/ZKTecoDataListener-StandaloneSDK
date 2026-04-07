using System;
using System.Data.SqlClient;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using Core.Models;
using Services.Interfaces;
using System.Configuration;

namespace Services.Implementations
{
    public class NotificationService : INotificationService
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;
        private readonly string _smtpServer;
        private readonly int _smtpPort;
        private readonly string _smtpUser;
        private readonly string _smtpPassword;
        private readonly string _fromEmail;

        public NotificationService(string connectionString, ILogger logger, 
            string smtpServer = null, 
            int smtpPort = 0,
            string smtpUser = null,
            string smtpPassword = null,
            string fromEmail = null)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _smtpServer = !string.IsNullOrEmpty(smtpServer) ? smtpServer : ConfigurationManager.AppSettings["SmtpServer"];
            _smtpPort = smtpPort > 0 ? smtpPort : int.Parse(ConfigurationManager.AppSettings["SmtpPort"] ?? "587");
            _smtpUser = !string.IsNullOrEmpty(smtpUser) ? smtpUser : ConfigurationManager.AppSettings["SmtpUser"];
            _smtpPassword = !string.IsNullOrEmpty(smtpPassword) ? smtpPassword : ConfigurationManager.AppSettings["SmtpPassword"];
            _fromEmail = !string.IsNullOrEmpty(fromEmail) ? fromEmail : ConfigurationManager.AppSettings["FromEmail"];
        }

        public async Task SendDeviceDisconnectedNotificationAsync(ConnectionStatusEventArgs status)
        {
            if (status.IsConnected)
                return;

            await Task.Run(() =>
            {
                try
                {
                    string mailAdresi = GetCompanyEmail(status.DeviceInfo.CompanyId);

                    if (string.IsNullOrWhiteSpace(mailAdresi))
                    {
                        _logger.LogWarning($"FirmaId {status.DeviceInfo.CompanyId} için mail adresi bulunamadı.");
                        return;
                    }

                    MailMessage mail = new MailMessage();
                    mail.From = new MailAddress(_fromEmail);
                    mail.To.Add(mailAdresi);
                    mail.Subject = $"PDKS Cihaz Bağlantısı Koptu: {status.DeviceInfo.DeviceName}";
                    mail.Body = $"Cihaz bağlantısı koptu:\n\n" +
                               $"Cihaz: {status.DeviceInfo.DeviceName}\n" +
                               $"IP: {status.DeviceInfo.IPAddress}\n" +
                               $"Zaman: {DateTime.Now}\n" +
                               $"Hata: {status.Message}";

                    SmtpClient smtp = new SmtpClient(_smtpServer)
                    {
                        Port = _smtpPort,
                        Credentials = new NetworkCredential(_smtpUser, _smtpPassword),
                        EnableSsl = true,
                        DeliveryMethod = SmtpDeliveryMethod.Network
                    };

                    smtp.Send(mail);

                    _logger.LogInfo($"📧 Mail gönderildi: {status.DeviceInfo.DeviceName} → {mailAdresi}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"📧 Mail gönderme hatası: {ex.Message}");
                }
            });
        }

        private string GetCompanyEmail(int companyId)
        {
            try
            {
                using (SqlConnection conn = new SqlConnection(_connectionString))
                {
                    conn.Open();
                    SqlCommand cmd = new SqlCommand(@"
                        SELECT ITBirimMail 
                        FROM Firmalar 
                        WHERE FirmaId = @FirmaId", conn);

                    cmd.Parameters.AddWithValue("@FirmaId", companyId);
                    object result = cmd.ExecuteScalar();

                    return result?.ToString() ?? "";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"GetCompanyEmail hatası: {ex.Message}");
                return "";
            }
        }
    }
}

