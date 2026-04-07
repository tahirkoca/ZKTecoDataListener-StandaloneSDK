using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using Services.Interfaces;

namespace Services.Implementations
{
    public class MealHallActivationService : IMealHallActivationService
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;
        private readonly Managers.DeviceManager _deviceManager;

        public MealHallActivationService(string connectionString, ILogger logger, Managers.DeviceManager deviceManager)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        }

        public async Task ProcessActivationAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    _logger.LogInfo("🔄 Aktifleştirme kontrolü başlatıldı...");

                    var bekleyenler = GetBekleyenEngellenenler();
                    if (bekleyenler.Count == 0)
                    {
                        _logger.LogInfo("ℹ️ Bekleyen kullanıcı yok.");
                        return;
                    }

                    foreach (var kisi in bekleyenler)
                    {
                        ProcessPersonActivation(kisi);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ Aktifleştirme Timer hata: {ex}");
                }
            });
        }

        private void ProcessPersonActivation(EngellenenPersonel kisi)
        {
            try
            {
                _logger.LogInfo($"👤 Kontrol edilen: {kisi.AdSoyad} ({kisi.PersonelId})");
                _logger.LogInfo($"📌 Yetkili vardiyalar: {kisi.AdSoyad} için {string.Join(",", kisi.YetkiliVardiyaIdleri)}");

                DateTime? yemekSaat = GetSonYemekSaat(kisi.PersonelId);
                if (!yemekSaat.HasValue)
                {
                    _logger.LogWarning($"⚠️ {kisi.AdSoyad} için son yemek geçişi bulunamadı.");
                    return;
                }

                bool dahaOnceAktiflestiMi = KisiDahaOnceAktiflestiMi(kisi.PersonelId);
                var vardiyalar = GetVardiyalar(kisi.YetkiliVardiyaIdleri);

                if (vardiyalar.Count == 0)
                {
                    _logger.LogWarning($"⚠️ {kisi.AdSoyad} için vardiya bilgisi DB'den gelmedi.");
                    return;
                }

                var aktifVardiya = BelirleAktifVardiya(yemekSaat.Value.TimeOfDay, vardiyalar);
                if (aktifVardiya == null)
                {
                    _logger.LogInfo($"⏳ {kisi.AdSoyad} için uygun vardiya bulunamadı.");
                    return;
                }

                DateTime aktifZaman = HesaplaAktiflestirmeZamani(aktifVardiya, yemekSaat.Value, dahaOnceAktiflestiMi);
                _logger.LogInfo($"🕒 {kisi.AdSoyad} için hesaplanan aktifleştirme zamanı: {aktifZaman:dd.MM HH:mm}");

                if (DateTime.Now >= aktifZaman)
                {
                    if (aktifZaman == DateTime.Now.Date.Add(aktifVardiya.Baslangic) && !dahaOnceAktiflestiMi)
                    {
                        _logger.LogInfo($"🟢 TELAFİ → {kisi.AdSoyad} için dünkü aktifleştirme kaçırıldığı için {DateTime.Now:dd.MM.yyyy} vardiya başlangıcında aktifleme yapılıyor!");
                        _logger.LogInfo($"ℹ️ TELAFİ sonrası → {kisi.AdSoyad} için bugünkü YemekAktif süreci {DateTime.Now:dd.MM.yyyy} normal akışta devam edecek.");
                    }
                    else
                    {
                        _logger.LogInfo($"✅ {kisi.AdSoyad} → Aktifleştirme zamanı geldi (normal süreç), cihazda aktifleniyor...");
                    }

                    PersoneliCihazaTekrarEkle(kisi);
                }
                else
                {
                    _logger.LogInfo($"⏳ {kisi.AdSoyad} → Aktifleştirme zamanı henüz gelmedi ({aktifZaman:dd.MM.yyyy HH:mm}).");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Person activation error: {ex.Message}");
            }
        }

        private List<EngellenenPersonel> GetBekleyenEngellenenler()
        {
            List<EngellenenPersonel> liste = new List<EngellenenPersonel>();

            try
            {
                using (SqlConnection conn = new SqlConnection(_connectionString))
                {
                    conn.Open();
                    SqlCommand cmd = new SqlCommand(@"
                        SELECT e.PersonelId, e.CihazId, e.KartNo, e.AdSoyad, ISNULL(k.CalismaSekli,'') AS CalismaSekli
                        FROM YemekhaneEngellenenKullanicilar e
                        INNER JOIN Kisiler k ON e.PersonelId = k.PersonelId
                        WHERE e.TekrarEklendiMi = 0
                          AND CAST(e.EngellemeTarihi AS DATE) >= DATEADD(DAY, -1, CAST(GETDATE() AS DATE))", conn);

                    using (SqlDataReader dr = cmd.ExecuteReader())
                    {
                        while (dr.Read())
                        {
                            string calismaSekliStr = dr["CalismaSekli"].ToString();
                            List<int> vardiyalar = string.IsNullOrWhiteSpace(calismaSekliStr)
                                ? new List<int>()
                                : calismaSekliStr.Split(',')
                                    .Where(x => int.TryParse(x.Trim(), out _))
                                    .Select(x => int.Parse(x.Trim()))
                                    .ToList();

                            liste.Add(new EngellenenPersonel
                            {
                                PersonelId = Convert.ToInt32(dr["PersonelId"]),
                                CihazId = Convert.ToInt32(dr["CihazId"]),
                                KartNo = dr["KartNo"].ToString(),
                                AdSoyad = dr["AdSoyad"].ToString(),
                                YetkiliVardiyaIdleri = vardiyalar
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"GetBekleyenEngellenenler hata: {ex.Message}");
            }

            return liste;
        }

        private DateTime? GetSonYemekSaat(int personelId)
        {
            try
            {
                using (SqlConnection conn = new SqlConnection(_connectionString))
                {
                    conn.Open();
                    SqlCommand cmd = new SqlCommand(@"
                        SELECT TOP 1 KayitZamani 
                        FROM YemekhaneGecisHareketler
                        WHERE PersonelId = @id AND AktifMi = 1
                        ORDER BY KayitZamani DESC", conn);
                    cmd.Parameters.AddWithValue("@id", personelId);

                    object o = cmd.ExecuteScalar();
                    return o == null ? (DateTime?)null : Convert.ToDateTime(o);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"GetSonYemekSaat hata: {ex.Message}");
                return null;
            }
        }

        private bool KisiDahaOnceAktiflestiMi(int personelId)
        {
            try
            {
                using (SqlConnection conn = new SqlConnection(_connectionString))
                {
                    conn.Open();
                    SqlCommand cmd = new SqlCommand(@"
                        SELECT COUNT(*) FROM YemekhaneEngellenenKullanicilar
                        WHERE PersonelId = @id 
                          AND TekrarEklendiMi = 1
                          AND CAST(EngellemeTarihi AS DATE) = DATEADD(DAY, -1, CAST(GETDATE() AS DATE))", conn);
                    cmd.Parameters.AddWithValue("@id", personelId);
                    int count = (int)cmd.ExecuteScalar();
                    return count > 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"KisiDahaOnceAktiflestiMi hata: {ex.Message}");
                return false;
            }
        }

        private List<Vardiya> GetVardiyalar(List<int> ids)
        {
            List<Vardiya> liste = new List<Vardiya>();

            if (ids == null || ids.Count == 0)
            {
                _logger.LogWarning("⚠️ GetVardiyalar: ID listesi boş, sorgu yapılmadı.");
                return liste;
            }

            string inClause = string.Join(",", ids);
            try
            {
                using (SqlConnection conn = new SqlConnection(_connectionString))
                {
                    conn.Open();
                    SqlCommand cmd = new SqlCommand($@"
                        SELECT CalismaSekilId, BaslangicToleransZaman, BitisToleransZaman, YemekAktiflestirmeZaman
                        FROM CalismaSekilleri
                        WHERE CalismaSekilId IN ({inClause})", conn);

                    using (SqlDataReader dr = cmd.ExecuteReader())
                    {
                        while (dr.Read())
                        {
                            TimeSpan bas = ((DateTime)dr["BaslangicToleransZaman"]).TimeOfDay;
                            TimeSpan bit = ((DateTime)dr["BitisToleransZaman"]).TimeOfDay;
                            TimeSpan akt = ((DateTime)dr["YemekAktiflestirmeZaman"]).TimeOfDay;

                            liste.Add(new Vardiya
                            {
                                Id = dr.GetInt32(0),
                                Baslangic = bas,
                                Bitis = bit,
                                YemekAktif = akt
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"GetVardiyalar hata: {ex.Message}");
            }

            return liste;
        }

        private Vardiya BelirleAktifVardiya(TimeSpan yemekSaat, List<Vardiya> vardiyalar)
        {
            foreach (var v in vardiyalar)
            {
                bool icinde;

                if (v.Bitis < v.Baslangic)
                    icinde = (yemekSaat >= v.Baslangic || yemekSaat <= v.Bitis);
                else
                    icinde = (yemekSaat >= v.Baslangic && yemekSaat <= v.Bitis);

                if (icinde) return v;
            }
            return null;
        }

        private DateTime HesaplaAktiflestirmeZamani(Vardiya v, DateTime sonYemekSaat, bool dahaOnceAktiflestiMi)
        {
            DateTime vardiyaBaslangic = VardiyaBaslangicZamaniHesapla(v.Baslangic, v.Bitis);
            DateTime planlananAktif = sonYemekSaat.Date.Add(v.YemekAktif);

            if (dahaOnceAktiflestiMi)
            {
                _logger.LogInfo($"ℹ️ Dünkü yemek aktifleştirmesi zaten yapılmış, {DateTime.Now:dd.MM.yyyy} için normal YemekAktif saati → {DateTime.Now.Date.Add(v.YemekAktif):HH:mm}");
                return DateTime.Now.Date.Add(v.YemekAktif);
            }

            if (planlananAktif.Date < DateTime.Now.Date)
            {
                _logger.LogInfo($"⚠️ Dünkü yemek aktifleştirmesi KAÇIRILMIŞ! → {DateTime.Now:dd.MM.yyyy} vardiya başlangıcında ({v.Baslangic:hh\\:mm}) telafi edilecek.");
                return DateTime.Now.Date.Add(v.Baslangic);
            }

            _logger.LogInfo($"ℹ️ Normal YemekAktif zamanı kullanılacak → {planlananAktif:dd.MM.yyyy HH:mm}");
            return planlananAktif;
        }

        private DateTime VardiyaBaslangicZamaniHesapla(TimeSpan baslangic, TimeSpan bitis)
        {
            try
            {
                DateTime simdi = DateTime.Now;
                DateTime bugun = DateTime.Today;

                if (bitis < baslangic) // Gece vardiyası
                {
                    if (simdi.TimeOfDay >= baslangic)
                        return bugun.Add(baslangic);
                    else
                        return bugun.AddDays(-1).Add(baslangic);
                }
                else // Gündüz vardiyası
                {
                    if (simdi.TimeOfDay >= baslangic && simdi.TimeOfDay <= bitis)
                        return bugun.Add(baslangic);
                    else if (simdi.TimeOfDay < baslangic)
                        return bugun.Add(baslangic);
                    else
                        return bugun.AddDays(1).Add(baslangic);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ VardiyaBaslangicZamaniHesapla hatası: {ex.Message}");
                return DateTime.Today;
            }
        }

        private async void PersoneliCihazaTekrarEkle(EngellenenPersonel kisi)
        {
            const int maxRetries = 3;
            int retryCount = 0;
            bool addedToDevice = false;

            while (!addedToDevice && retryCount < maxRetries)
            {
                try
                {
                    _logger.LogInfo($"🔄 {kisi.AdSoyad} cihaza ekleniyor... (Deneme {retryCount + 1}/{maxRetries})");

                    addedToDevice = await _deviceManager.AddUserToDeviceAsync(
                        kisi.CihazId,
                        kisi.PersonelId.ToString(),
                        kisi.AdSoyad,
                        kisi.KartNo
                    );

                    if (addedToDevice)
                    {
                        _logger.LogInfo($"✅ {kisi.AdSoyad} (KartNo: {kisi.KartNo}) cihaza başarıyla eklendi");

                        // DB'yi güncelle
                        UpdateDatabaseRecord(kisi.PersonelId, kisi.CihazId);
                        break; // Başarılı, döngüden çık
                    }
                    else
                    {
                        retryCount++;
                        if (retryCount < maxRetries)
                        {
                            _logger.LogWarning($"⚠️ {kisi.AdSoyad} cihaza eklenemedi, {retryCount * 5} saniye sonra tekrar denenecek...");
                            await Task.Delay(retryCount * 5000);
                        }
                        else
                        {
                            _logger.LogError($"❌ {kisi.AdSoyad} {maxRetries} denemeden sonra cihaza eklenemedi! Manuel kontrol gerekli.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    retryCount++;
                    _logger.LogError($"❌ {kisi.AdSoyad} ekleme hatası (Deneme {retryCount}/{maxRetries}): {ex.Message}");

                    if (retryCount < maxRetries)
                    {
                        await Task.Delay(retryCount * 5000);
                    }
                }
            }
        }

        private void UpdateDatabaseRecord(int personelId, int cihazId)
        {
            try
            {
                using (SqlConnection conn = new SqlConnection(_connectionString))
                {
                    conn.Open();
                    SqlCommand cmd = new SqlCommand(@"
                UPDATE YemekhaneEngellenenKullanicilar 
                SET TekrarEklendiMi = 1
                WHERE PersonelId = @id AND CihazId = @cid", conn);
                    cmd.Parameters.AddWithValue("@id", personelId);
                    cmd.Parameters.AddWithValue("@cid", cihazId);
                    int affected = cmd.ExecuteNonQuery();

                    if (affected > 0)
                    {
                        _logger.LogInfo($"🟢 DB güncellendi: PersonelId={personelId}, CihazId={cihazId}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ DB güncelleme hatası: {ex.Message}");
            }
        }
    }

    public class EngellenenPersonel
    {
        public int PersonelId { get; set; }
        public int CihazId { get; set; }
        public string KartNo { get; set; }
        public string AdSoyad { get; set; }
        public List<int> YetkiliVardiyaIdleri { get; set; }
    }

    public class Vardiya
    {
        public int Id { get; set; }
        public TimeSpan Baslangic { get; set; }
        public TimeSpan Bitis { get; set; }
        public TimeSpan YemekAktif { get; set; }
    }
}
