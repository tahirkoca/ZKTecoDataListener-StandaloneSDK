using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading.Tasks;
using Core.Models;
using Services.Interfaces;

namespace Services.Implementations
{
    public class DeviceRepository : IDeviceRepository
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;

        public DeviceRepository(string connectionString, ILogger logger)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<List<DeviceInfo>> GetActiveDevicesAsync()
        {
            return await Task.Run(() =>
            {
                List<DeviceInfo> devices = new List<DeviceInfo>();

                try
                {
                    using (SqlConnection conn = new SqlConnection(_connectionString))
                    {
                        conn.Open();
                        SqlCommand cmd = new SqlCommand(@"
                            SELECT c.CihazId, c.CihazAdi, c.IPAdres, c.Port, c.FirmaId, 
                                   f.FirmaAdi, c.CihazModeli, 
                                   ISNULL(c.Marka, 'ZKTeco') AS Marka,
                                   ISNULL(c.SdkTipi, 'Standalone') AS SdkTipi,
                                   c.BaglantiParametreleri
                            FROM Cihazlar c
                            INNER JOIN Firmalar f ON c.FirmaId = f.FirmaId
                            WHERE c.AktifMi = 1
                            ORDER BY c.CihazAdi", conn);

                        using (SqlDataReader dr = cmd.ExecuteReader())
                        {
                            while (dr.Read())
                            {
                                string cihazModeli = dr.IsDBNull(6) ? "" : dr.GetString(6);
                                
                                // Veritabanından marka ve SDK tipini al, yoksa modeline göre belirle
                                string manufacturer = dr.IsDBNull(7) 
                                    ? DetermineManufacturer(cihazModeli) 
                                    : dr.GetString(7);
                                
                                string sdkType = dr.IsDBNull(8) 
                                    ? DetermineSdkType(cihazModeli) 
                                    : dr.GetString(8);

                                // Kullanıcı isteği: Pull SDK cihazlarını tamamen görmezden gel
                                if (sdkType.Equals("Pull", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
                                
                                string baglantiParametreleri = dr.IsDBNull(9) ? "" : dr.GetString(9);
                                
                                devices.Add(new DeviceInfo
                                {
                                    DeviceId = dr.GetInt32(0),
                                    DeviceName = dr.GetString(1),
                                    IPAddress = dr.GetString(2),
                                    Port = dr.GetInt32(3),
                                    CompanyId = dr.GetInt32(4),
                                    CompanyName = dr.GetString(5),
                                    DeviceModel = cihazModeli,
                                    Manufacturer = manufacturer,
                                    SdkType = sdkType,
                                    ConnectionString = baglantiParametreleri
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"GetActiveDevicesAsync hatası: {ex.Message}");
                }

                return devices;
            });
        }

        public async Task<List<DeviceInfo>> GetDisconnectedDevicesAsync()
        {
            return await Task.Run(() =>
            {
                List<DeviceInfo> devices = new List<DeviceInfo>();

                try
                {
                    using (SqlConnection conn = new SqlConnection(_connectionString))
                    {
                        conn.Open();
                        SqlCommand cmd = new SqlCommand(@"
                            SELECT c.CihazId, c.CihazAdi, c.IPAdres, c.Port, c.FirmaId, 
                                   f.FirmaAdi, c.CihazModeli,
                                   ISNULL(c.Marka, 'ZKTeco') AS Marka,
                                   ISNULL(c.SdkTipi, 'Standalone') AS SdkTipi,
                                   c.BaglantiParametreleri
                            FROM Cihazlar c
                            INNER JOIN Firmalar f ON c.FirmaId = f.FirmaId
                            WHERE c.AktifMi = 1 AND (c.BaglandiMi = 0 OR c.BaglandiMi IS NULL)
                            ORDER BY c.CihazAdi", conn);

                        using (SqlDataReader dr = cmd.ExecuteReader())
                        {
                            while (dr.Read())
                            {
                                string cihazModeli = dr.IsDBNull(6) ? "" : dr.GetString(6);
                                
                                string manufacturer = dr.IsDBNull(7) 
                                    ? DetermineManufacturer(cihazModeli) 
                                    : dr.GetString(7);
                                
                                string sdkType = dr.IsDBNull(8) 
                                    ? DetermineSdkType(cihazModeli) 
                                    : dr.GetString(8);

                                // Kullanıcı isteği: Pull SDK cihazlarını tamamen görmezden gel
                                if (sdkType.Equals("Pull", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
                                
                                string baglantiParametreleri = dr.IsDBNull(9) ? "" : dr.GetString(9);
                                
                                devices.Add(new DeviceInfo
                                {
                                    DeviceId = dr.GetInt32(0),
                                    DeviceName = dr.GetString(1),
                                    IPAddress = dr.GetString(2),
                                    Port = dr.GetInt32(3),
                                    CompanyId = dr.GetInt32(4),
                                    CompanyName = dr.GetString(5),
                                    DeviceModel = cihazModeli,
                                    Manufacturer = manufacturer,
                                    SdkType = sdkType,
                                    ConnectionString = baglantiParametreleri
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"GetDisconnectedDevicesAsync hatası: {ex.Message}");
                }

                return devices;
            });
        }

        private string DetermineManufacturer(string cihazModeli)
        {
            if (string.IsNullOrWhiteSpace(cihazModeli))
                return "ZKTeco";

            string model = cihazModeli.ToUpper();
            
            // ZKTeco modelleri
            if (model.StartsWith("SC") || model.StartsWith("C3") || model.StartsWith("C4") || 
                model.Contains("ZK") || model.Contains("ZKTECO"))
                return "ZKTeco";
            
            // Gelecekte başka markalar eklenebilir
            // if (model.StartsWith("HIK") || model.Contains("HIKVISION"))
            //     return "Hikvision";
            
            return "ZKTeco"; // Varsayılan
        }

        private string DetermineSdkType(string cihazModeli)
        {
            if (string.IsNullOrWhiteSpace(cihazModeli))
                return "Standalone";

            string model = cihazModeli.ToUpper();
                
            if (model.Contains("Pull") || model.Contains("Http"))
                return "Pull";
            
            return "Standalone";
        }

        public async Task UpdateDeviceConnectionStatusAsync(int deviceId, bool isConnected)
        {
            await Task.Run(() =>
            {
                try
                {
                    using (SqlConnection conn = new SqlConnection(_connectionString))
                    {
                        conn.Open();
                        SqlCommand cmd = new SqlCommand(@"
                            UPDATE Cihazlar 
                            SET BaglandiMi = @BaglandiMi 
                            WHERE CihazId = @CihazId", conn);

                        cmd.Parameters.AddWithValue("@BaglandiMi", isConnected ? 1 : 0);
                        cmd.Parameters.AddWithValue("@CihazId", deviceId);
                        cmd.ExecuteNonQuery();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"UpdateDeviceConnectionStatusAsync hatası: {ex.Message}");
                }
            });
        }

        public async Task<DateTime?> GetLastLogDateAsync(int deviceId)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (SqlConnection conn = new SqlConnection(_connectionString))
                    {
                        conn.Open();
                        SqlCommand cmd = new SqlCommand(@"
                            SELECT MAX(Tarih) 
                            FROM KisiHareketler 
                            WHERE CihazId = @CihazId", conn);

                        cmd.Parameters.AddWithValue("@CihazId", deviceId);

                        object result = cmd.ExecuteScalar();

                        if (result == null || result == DBNull.Value)
                        {
                            return (DateTime?)null;
                        }

                        return (DateTime?)Convert.ToDateTime(result);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"GetLastLogDateAsync hatası: {ex.Message}");
                    return (DateTime?)null;
                }
            });
                }
        public async Task<List<dynamic>> GetPendingTriggersAsync()
        {
            return await Task.Run(() =>
            {
                List<dynamic> triggers = new List<dynamic>();
                try
                {
                    using (SqlConnection conn = new SqlConnection(_connectionString))
                    {
                        conn.Open();
                        SqlCommand cmd = new SqlCommand(@"
                            SELECT Id, CihazId, PersonelId, Komut, Tarih 
                            FROM CihazTetikKuyrugu 
                            WHERE OkunduMu = 0 
                            ORDER BY Tarih ASC", conn);

                        using (SqlDataReader dr = cmd.ExecuteReader())
                        {
                            while (dr.Read())
                            {
                                triggers.Add(new {
                                    Id = dr.GetInt32(0),
                                    CihazId = dr.GetInt32(1),
                                    PersonelId = dr.IsDBNull(2) ? "" : dr.GetString(2),
                                    Komut = dr.IsDBNull(3) ? "" : dr.GetString(3),
                                    Tarih = dr.GetDateTime(4)
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"GetPendingTriggersAsync hatası: {ex.Message}");
                }
                return triggers;
            });
        }

        public async Task MarkTriggerAsReadAsync(int id)
        {
            await Task.Run(() =>
            {
                try
                {
                    using (SqlConnection conn = new SqlConnection(_connectionString))
                    {
                        conn.Open();
                        SqlCommand cmd = new SqlCommand(@"
                            UPDATE CihazTetikKuyrugu 
                            SET OkunduMu = 1 
                            WHERE Id = @Id", conn);

                        cmd.Parameters.AddWithValue("@Id", id);
                        cmd.ExecuteNonQuery();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"MarkTriggerAsReadAsync hatası: {ex.Message}");
                }
            });
        }
    }
}

