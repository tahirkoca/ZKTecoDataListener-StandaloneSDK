using System;
using System.IO;
using Services.Interfaces;

namespace Services.Implementations
{
    public class FileLogger : ILogger
    {
        private readonly string _logFilePath;
        private readonly object _lockObject = new object();

        public FileLogger(string logFilePath = null)
        {
            _logFilePath = logFilePath ?? @"C:\Logs\pdks_log.txt";
            
            string directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        public void LogInfo(string message)
        {
            WriteLog("INFO", message);
        }

        public void LogError(string message)
        {
            WriteLog("ERROR", message);
        }

        public void LogWarning(string message)
        {
            WriteLog("WARNING", message);
        }

        public void LogDebug(string message)
        {
            WriteLog("DEBUG", message);
        }

        private void WriteLog(string level, string message)
        {
            lock (_lockObject)
            {
                try
                {
                    string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
                    File.AppendAllText(_logFilePath, logEntry);
                    
#if DEBUG
                    // DEBUG modunda console'a da yaz
                    try
                    {
                        Console.Write(logEntry);
                    }
                    catch
                    {
                        // Console yoksa (service modunda) sessizce geç
                    }
#endif
                }
                catch
                {
                    // Log yazma hatası - sessizce geç (infinite loop önleme)
                }
            }
        }
    }
}

