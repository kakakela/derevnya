using System.Text;
using System.Runtime.InteropServices;

namespace Derevnya;

internal static class WallpaperHost
{
    /// <summary>
    /// Просит Progman создать WorkerW за иконками и возвращает его дескриптор.
    /// Возвращает IntPtr.Zero, если найти не удалось.
    /// </summary>
    public static IntPtr FindWorkerW(Action<string> log)
    {
        IntPtr progman = NativeMethods.FindWindow("Progman", null);
        log($"Progman: 0x{progman.ToInt64():X}");
        if (progman == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr sendResult = NativeMethods.SendMessageTimeout(
            progman,
            NativeMethods.WM_SPAWN_WORKER,
            new IntPtr(0xD),
            new IntPtr(1),
            NativeMethods.SMTO_NORMAL,
            1000,
            out IntPtr messageResult);
        log($"Сообщение 0x052C отправлено: ответ={sendResult.ToInt64()}, результат={messageResult.ToInt64()}");

        // Ищем окно верхнего уровня, в котором лежит SHELLDLL_DefView (иконки),
        // и берём WorkerW, идущий сразу за ним: он находится позади иконок.
        IntPtr workerW = IntPtr.Zero;
        NativeMethods.EnumWindows((top, _) =>
        {
            IntPtr defView = NativeMethods.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                log($"SHELLDLL_DefView найден в окне 0x{top.ToInt64():X} (класс {NativeMethods.GetClassNameString(top)})");
                workerW = NativeMethods.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                if (workerW != IntPtr.Zero)
                {
                    return false; // нашли, останавливаем перебор
                }
            }
            return true;
        }, IntPtr.Zero);

        // Запасной вариант: WorkerW как дочернее окно Progman
        if (workerW == IntPtr.Zero)
        {
            workerW = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            log($"Запасной поиск WorkerW внутри Progman: 0x{workerW.ToInt64():X}");
        }

        log($"WorkerW: 0x{workerW.ToInt64():X}");
        return workerW;
    }

    /// <summary>
    /// Делает окно дочерним для WorkerW и растягивает его на основной экран.
    /// </summary>
    public static bool Attach(IntPtr hwnd, IntPtr workerW, Action<string> log)
    {
        // Убираем рамку и заголовок, помечаем окно как дочернее
        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~(NativeMethods.WS_POPUP | NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME);
        style |= NativeMethods.WS_CHILD;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));

        // Окно не должно появляться в панели задач и в Alt+Tab
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        IntPtr oldParent = NativeMethods.SetParent(hwnd, workerW);
        if (oldParent == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            log($"SetParent вернул 0, код ошибки Win32: {error}");
        }
        else
        {
            log($"SetParent выполнен, прежний родитель 0x{oldParent.ToInt64():X}");
        }

        int width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        int height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

        bool moved = NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0, 0, width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_SHOWWINDOW);
        log($"SetWindowPos: {moved}, размер {width}x{height}");

        return moved;
    }
        private const uint SPI_GETDESKWALLPAPER = 0x0073;
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_SENDCHANGE = 0x0002;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfoGet(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfoSet(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    /// <summary>
    /// Заново применяет текущие обои, чтобы рабочий стол перерисовался после закрытия окна мира.
    /// </summary>
    public static void RestoreWallpaper(Action<string> log)
    {
        var path = new StringBuilder(520);
        if (!SystemParametersInfoGet(SPI_GETDESKWALLPAPER, (uint)path.Capacity, path, 0))
        {
            log($"Не удалось прочитать путь обоев, код Win32: {Marshal.GetLastWin32Error()}");
            return;
        }

        log($"Путь текущих обоев: '{path}'");
        bool ok = SystemParametersInfoSet(SPI_SETDESKWALLPAPER, 0, path.ToString(), SPIF_SENDCHANGE);
        log(ok
            ? "Обои применены заново"
            : $"Не удалось применить обои, код Win32: {Marshal.GetLastWin32Error()}");
    }
}