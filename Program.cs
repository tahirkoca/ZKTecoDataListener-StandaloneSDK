using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZKTecoListenerService
{
    internal static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // Global Exception Handling
            Application.ThreadException += new ThreadExceptionEventHandler(Application_ThreadException);
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);
            TaskScheduler.UnobservedTaskException += new EventHandler<UnobservedTaskExceptionEventArgs>(TaskScheduler_UnobservedTaskException);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            LogFatalError(e.Exception, "Application_ThreadException");
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            LogFatalError(e.ExceptionObject as Exception, "CurrentDomain_UnhandledException");
        }

        private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            LogFatalError(e.Exception, "TaskScheduler_UnobservedTaskException");
            e.SetObserved(); // Programın çökmesini engellemeye çalış
        }

        private static void LogFatalError(Exception ex, string source)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "fatal_errors.txt");
                string directory = Path.GetDirectoryName(logPath);
                
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                string message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [FATAL] [{source}] {ex?.Message}\nSTACK TRACE: {ex?.StackTrace}\n--------------------------------------------------\n";
                
                File.AppendAllText(logPath, message);
                
                MessageBox.Show($"Beklenmedik bir hata oluştu: {ex?.Message}\nLog dosyasına kaydedildi.", "Kritik Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
                // En kötü senaryo: Log bile yazılamıyor
            }
        }
    }
}
