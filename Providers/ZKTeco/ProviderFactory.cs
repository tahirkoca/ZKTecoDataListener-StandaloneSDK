using Core.Interfaces;
using Core.Models;
using Services.Interfaces;
using System;

namespace Providers.ZKTeco
{
    public static class ZKTecoProviderFactory
    {
        public static IDeviceProvider CreateProvider(DeviceInfo deviceInfo, ILogger logger = null)
        {
            if (deviceInfo == null)
                throw new System.ArgumentNullException(nameof(deviceInfo));

            if (deviceInfo.Manufacturer?.ToUpper() != "ZKTECO")
                throw new System.ArgumentException("Bu factory sadece ZKTeco cihazları için");

            if (deviceInfo.SdkType?.ToUpper() == "STANDALONE")
            {
                return new ZKTecoStandaloneProvider(deviceInfo, logger);
            }

            throw new System.ArgumentException($"Desteklenmeyen SDK tipi: {deviceInfo.SdkType}");
        }
    }
}

