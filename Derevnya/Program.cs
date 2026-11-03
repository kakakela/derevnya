namespace Derevnya;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Logger.Init();

        Application.ThreadException += (_, e) =>
            Logger.Log($"ОШИБКА (поток UI): {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Log($"ОШИБКА (необработанное исключение): {e.ExceptionObject}");

        ApplicationConfiguration.Initialize();

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