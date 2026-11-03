namespace Derevnya;

public static class AppHost
{
    /// <summary>Запускает программу. Вызывать из потока с атрибутом STAThread.</summary>
    public static void Run(string[] args)
    {
        Logger.Init();

        Application.ThreadException += (_, e) =>
            Logger.Log($"ОШИБКА (поток UI): {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Log($"ОШИБКА (необработанное исключение): {e.ExceptionObject}");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        bool bench = args.Contains("--bench");

        try
        {
            if (bench)
            {
                Application.Run(new BenchForm());
            }
            else
            {
                Application.Run(new WorldContext());
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"ОШИБКА при запуске: {ex}");
        }

        WallpaperHost.RestoreWallpaper(Logger.Log);

        Logger.Log("Программа завершена");
    }
}