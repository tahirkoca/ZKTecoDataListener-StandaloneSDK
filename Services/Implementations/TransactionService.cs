using System;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Core.Models;
using Services.Interfaces;

namespace Services.Implementations
{
    public class TransactionService : ITransactionService
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;
        private readonly Managers.DeviceManager _deviceManager;

        public TransactionService(string connectionString, ILogger logger, Managers.DeviceManager deviceManager = null)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _deviceManager = deviceManager;
        }

        public async Task SaveTransactionAsync(DeviceTransactionEventArgs transaction)
        {
            await Task.Run(() =>
            {
                if (transaction.IsRemoteTrigger)
                {
                    _logger.LogDebug($"Transaction atlandı (Remote Trigger): {transaction.DeviceInfo.DeviceName} | {transaction.EnrollNumber}");
                    return;
                }

                try
                {
                    using (SqlConnection conn = new SqlConnection(_connectionString))
                    {
                        conn.Open();
                        SqlCommand cmd = new SqlCommand(@"
                            INSERT INTO KisiHareketler 
                            (FirmaId, CihazId, PersonelId, Tarih, Tip, KayitZamani, AktifMi)
                            VALUES 
                            (@FirmaId, @CihazId, @PersonelId, @Tarih, @Tip, GETDATE(), 1)", conn);

                        cmd.Parameters.AddWithValue("@FirmaId", transaction.DeviceInfo.CompanyId);
                        cmd.Parameters.AddWithValue("@CihazId", transaction.DeviceInfo.DeviceId);
                        cmd.Parameters.AddWithValue("@PersonelId", transaction.EnrollNumber);
                        cmd.Parameters.AddWithValue("@Tarih", transaction.TransactionTime);
                        string dbTip = transaction.TransactionType;
                        if (dbTip == "QR_GIRIS") dbTip = "Giriş";
                        else if (dbTip == "QR_YEMEKHANE") dbTip = "Yemekhane";
                        
                        cmd.Parameters.AddWithValue("@Tip", dbTip);
                        cmd.ExecuteNonQuery();

                        _logger.LogDebug($"Transaction kaydedildi: {transaction.DeviceInfo.DeviceName} | {transaction.EnrollNumber} | {transaction.TransactionType}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"SaveTransactionAsync hatası: {ex.Message}");
                }
            });
        }

        public async Task ProcessMealHallTransactionAsync(DeviceTransactionEventArgs transaction)
        {
            if (transaction.TransactionType != "Yemekhane" && transaction.TransactionType != "QR_YEMEKHANE")
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    using (SqlConnection conn = new SqlConnection(_connectionString))
                    {
                        conn.Open();

                        // Personel bilgilerini al
                        SqlCommand personelCmd = new SqlCommand(@"
                            SELECT PersonelId, KartNo, Ad + ' ' + Soyad AS AdSoyad 
                            FROM Kisiler WHERE PersonelId = @PersonelId", conn);
                        personelCmd.Parameters.AddWithValue("@PersonelId", transaction.EnrollNumber);

                        SqlDataReader dr = personelCmd.ExecuteReader();
                        if (!dr.Read())
                        {
                            dr.Close();
                            return;
                        }

                        int personelId = Convert.ToInt32(dr["PersonelId"]);
                        string kartNo = dr["KartNo"].ToString();
                        string adSoyad = dr["AdSoyad"].ToString();
                        dr.Close();

                        // Bugünkü geçiş sayısını kontrol et
                        SqlCommand sayCmd = new SqlCommand(@"
                            SELECT COUNT(*) FROM YemekhaneGecisHareketler
                            WHERE PersonelId = @PersonelId 
                              AND CAST(Tarih AS DATE) = CAST(GETDATE() AS DATE) 
                              AND AktifMi = 1", conn);
                        sayCmd.Parameters.AddWithValue("@PersonelId", personelId);
                        int gecisSayisi = (int)sayCmd.ExecuteScalar();

                        // Günlük limiti kontrol et
                        SqlCommand limitCmd = new SqlCommand(@"
                            SELECT GunlukLimit FROM YemekhaneGirisLimitler
                            WHERE PersonelId = @PersonelId AND AktifMi = 1", conn);
                        limitCmd.Parameters.AddWithValue("@PersonelId", personelId);
                        object limitObj = limitCmd.ExecuteScalar();
                        
                        if (limitObj == null)
                        {
                            // Limit yoksa sadece kaydet
                            KaydetYemekhaneGecisi(conn, transaction.DeviceInfo.DeviceId, personelId, transaction.TransactionTime);
                            
                            // Kapıyı/Turnikeyi tetikle
                            if (_deviceManager != null)
                            {
                                _ = _deviceManager.OpenDoorAsync(transaction.DeviceInfo.DeviceId);
                            }
                            return;
                        }

                        int gunlukLimit = Convert.ToInt32(limitObj);

                        if (gecisSayisi + 1 == gunlukLimit)
                        {
                            // Limit doldu, kaydet ve cihazdan sil
                            KaydetYemekhaneGecisi(conn, transaction.DeviceInfo.DeviceId, personelId, transaction.TransactionTime);
                            
                            // Kapıyı/Turnikeyi tetikle
                            if (_deviceManager != null)
                            {
                                _ = _deviceManager.OpenDoorAsync(transaction.DeviceInfo.DeviceId);
                            }
                            
                            // Engelleme kaydı oluştur
                            EngellemeKaydiOlustur(conn, transaction.DeviceInfo.DeviceId, personelId, kartNo, adSoyad);
                            
                            // Cihazdan kullanıcıyı sil (DeviceManager üzerinden) - fire-and-forget
                            if (_deviceManager != null)
                            {
                                _ = Task.Run(async () =>
                                {
                                    bool deleted = await _deviceManager.DeleteUserFromDeviceAsync(
                                        transaction.DeviceInfo.DeviceId, 
                                        transaction.EnrollNumber);
                                    
                                    if (deleted)
                                    {
                                        _logger.LogInfo($"🔒 {adSoyad} (EnrollNumber: {transaction.EnrollNumber}) limiti aştığı için yemekhane cihazından silindi.");
                                    }
                                    else
                                    {
                                        _logger.LogWarning($"⚠️ {adSoyad} (EnrollNumber: {transaction.EnrollNumber}) cihazdan silinemedi. Manuel kontrol gerekebilir.");
                                    }
                                });
                            }
                            else
                            {
                                _logger.LogWarning($"⚠️ DeviceManager bulunamadı. {adSoyad} cihazdan silinemedi.");
                            }
                        }
                        else if (gecisSayisi + 1 > gunlukLimit)
                        {
                            // Limit aşıldı, kayıt yapma
                            _logger.LogWarning($"⚠️ {adSoyad} günlük limiti aştı, geçiş kaydedilmedi.");
                            return;
                        }
                        else
                        {
                            // Normal kayıt
                            KaydetYemekhaneGecisi(conn, transaction.DeviceInfo.DeviceId, personelId, transaction.TransactionTime);
                            
                            // Kapıyı/Turnikeyi tetikle
                            if (_deviceManager != null)
                            {
                                _ = _deviceManager.OpenDoorAsync(transaction.DeviceInfo.DeviceId);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"ProcessMealHallTransactionAsync hatası: {ex.Message}\n{ex.StackTrace}");
                }
            });
        }

        private void KaydetYemekhaneGecisi(SqlConnection conn, int cihazId, int personelId, DateTime tarihSaat)
        {
            SqlCommand insertCmd = new SqlCommand(@"
                INSERT INTO YemekhaneGecisHareketler (CihazId, PersonelId, Tarih, Saat, KayitZamani, AktifMi)
                VALUES (@CihazId, @PersonelId, @Tarih, @Saat, GETDATE(), 1)", conn);

            insertCmd.Parameters.AddWithValue("@CihazId", cihazId);
            insertCmd.Parameters.AddWithValue("@PersonelId", personelId);
            insertCmd.Parameters.AddWithValue("@Tarih", tarihSaat.Date);
            insertCmd.Parameters.AddWithValue("@Saat", tarihSaat.ToString("HH:mm:ss"));
            insertCmd.ExecuteNonQuery();
        }

        private void EngellemeKaydiOlustur(SqlConnection conn, int cihazId, int personelId, string kartNo, string adSoyad)
        {
            SqlCommand engelleCmd = new SqlCommand(@"
                INSERT INTO YemekhaneEngellenenKullanicilar
                (PersonelId, CihazId, KartNo, AdSoyad, EngellemeTarihi, TekrarEklendiMi)
                VALUES (@PersonelId, @CihazId, @KartNo, @AdSoyad, GETDATE(), 0)", conn);

            engelleCmd.Parameters.AddWithValue("@PersonelId", personelId);
            engelleCmd.Parameters.AddWithValue("@CihazId", cihazId);
            engelleCmd.Parameters.AddWithValue("@KartNo", kartNo);
            engelleCmd.Parameters.AddWithValue("@AdSoyad", adSoyad);
            engelleCmd.ExecuteNonQuery();
        }
    }
}

