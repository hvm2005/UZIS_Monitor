using Microsoft.Extensions.DependencyInjection;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using UZIS_Monitor.Services;
using UZIS_Monitor.Services.Interfaces;
using UZIS_Monitor.ViewModels;

namespace UZIS_Monitor
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static Mutex? _mutex;
        private const string UniqueAppName = "UZIS_Monitor_GUID_12345"; // Используйте уникальный ID

        // Импортируем функции из Windows для управления окнами
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        public static IServiceProvider Services { get; private set; } = null!;

        // Статическое свойство для связи с XAML
        public static MainViewModel MainVM => Services.GetRequiredService<MainViewModel>();

        public App()
        {
            Services = ConfigureServices();
        }

        private static IServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection();

            // Регистрируем сервис порта как Singleton (один на всё приложение)
            services.AddSingleton<SerialService>();

            // 1. Регистрируем Protobuf как синглтон (один экземпляр на всё приложение)
            services.AddSingleton<ProtobufFileService>();
            // Связываем интерфейсы с этим конкретным экземпляром
            services.AddSingleton<IDataExporter>(x => x.GetRequiredService<ProtobufFileService>());
            services.AddSingleton<IDataImporter>(x => x.GetRequiredService<ProtobufFileService>());
            // services.AddSingleton<IDataExporter, CsvExporter>();
            // services.AddSingleton<IDataExporter, SqliteExporter>(); // Добавите позже

            // Регистрируем ViewModel
            services.AddTransient<MainViewModel>();

            return services.BuildServiceProvider();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, UniqueAppName, out bool createdNew);

            if (!createdNew)
            {
                // Если приложение уже запущено, ищем его процесс
                Process current = Process.GetCurrentProcess();
                foreach (Process process in Process.GetProcessesByName(current.ProcessName))
                {
                    // Находим процесс с тем же именем, но другим ID
                    if (process.Id != current.Id)
                    {
                        IntPtr handle = process.MainWindowHandle;
                        if (handle != IntPtr.Zero)
                        {
                            // Разворачиваем (если свернуто) и выводим на передний план
                            ShowWindow(handle, SW_RESTORE);
                            SetForegroundWindow(handle);
                        }
                        break;
                    }
                }

                // Закрываем текущую (вторую) копию
                Application.Current.Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Получаем наш Singleton-сервис из контейнера
            var serialService = Services.GetService<SerialService>();

            // Останавливаем фоновые потоки и закрываем порт
            serialService?.StopService();

            _mutex?.ReleaseMutex();
            _mutex?.Dispose();

            base.OnExit(e);
        }

        public static T GetService<T>() where T : class => Services.GetRequiredService<T>();

        // Удобный доступ к App.Services из любой точки
        public new static App Current => (App)Application.Current;
    }
}
