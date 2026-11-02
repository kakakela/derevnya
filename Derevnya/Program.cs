namespace Derevnya;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Logger.Init();

        Application.ThreadException += (_, e) =>
            Logger.Log($"ОШИБКА (поток UI): {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Log($"ОШИБКА (необработанное исключение): {e.ExceptionObject}");

        ApplicationConfiguration.Initialize();

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            Logger.Log($"ОШИБКА при запуске: {ex}");
        }

        WallpaperHost.RestoreWallpaper(Logger.Log);
        
        Logger.Log("Программа завершена");
    }
}