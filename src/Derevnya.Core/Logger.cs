using System.Text;

namespace Derevnya.Core;

public static class Logger
{
    private static readonly object Sync = new();
    private static string? _path;

    /// <summary>Вызывается при каждой новой строке журнала (может прийти из любого потока).</summary>
    public static event Action<string>? LineWritten;

    public static string? FilePath => _path;

    public static void Init()
    {
        string dir = @"C:\Derevnya\logs";
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, $"derevnya-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        Log("=== Запуск Деревни, этап 2 ===");
        Log($"ОС: {Environment.OSVersion.VersionString}");
        Log($"Номер сборки Windows: {Environment.OSVersion.Version.Build}");
        Log($".NET: {Environment.Version}");
        Log($"64-битный процесс: {Environment.Is64BitProcess}");
        Log($"Файл журнала: {_path}");
    }

    public static void Log(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        lock (Sync)
        {
            if (_path != null)
            {
                try
                {
                    File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // журнал не должен ронять программу
                }
            }
        }
        LineWritten?.Invoke(line);
    }
}