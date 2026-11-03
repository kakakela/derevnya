using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Derevnya;

[Flags]
internal enum PauseReason
{
    None = 0,
    Locked = 1,
    SessionAway = 2,
    Fullscreen = 4,
    Resolution = 8,
    Dpi = 16
}

internal sealed class Supervisor : IDisposable
{
    private readonly Action<string> _log;

    // Эти флаги выставляются из потока SystemEvents, читаются из потока UI
    private volatile bool _locked;
    private volatile bool _sessionAway;
    private volatile bool _dpiChanged;
    private volatile bool _wallpaperEvent;

    private Size _baseSize;
    private PauseReason _current = PauseReason.None;
    private int _lastNotifState = -1;

    public Supervisor(Action<string> log)
    {
        _log = log;
        _baseSize = CurrentSize();
        _log($"Надсмотрщик: базовое разрешение {_baseSize.Width}x{_baseSize.Height}");

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public PauseReason Reasons => _current;
    public bool IsPaused => _current != PauseReason.None;
    public Size BaseSize => _baseSize;

    public static Size CurrentSize() => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN));

    /// <summary>Принять текущее разрешение за базовое и снять паузу по DPI («перезапуск мира»).</summary>
    public void ResetBaseline()
    {
        _baseSize = CurrentSize();
        _dpiChanged = false;
        _log($"Надсмотрщик: новое базовое разрешение {_baseSize.Width}x{_baseSize.Height}");
    }

    public void NotifyDpiChanged()
    {
        _dpiChanged = true;
        _log("Событие: изменился масштаб DPI (WM_DPICHANGED)");
    }

    /// <summary>true один раз после события «изменились обои».</summary>
    public bool TakeWallpaperEvent()
    {
        if (!_wallpaperEvent)
        {
            return false;
        }
        _wallpaperEvent = false;
        return true;
    }

    /// <summary>Пересчитывает причины паузы. Возвращает true, если набор причин изменился.</summary>
    public bool Poll()
    {
        PauseReason reasons = PauseReason.None;

        if (_locked) reasons |= PauseReason.Locked;
        if (_sessionAway) reasons |= PauseReason.SessionAway;
        if (_dpiChanged) reasons |= PauseReason.Dpi;
        if (CurrentSize() != _baseSize) reasons |= PauseReason.Resolution;

        int state = QueryNotificationState();
        if (state != _lastNotifState)
        {
            _log($"Состояние оболочки (SHQueryUserNotificationState): {state} ({DescribeState(state)})");
            _lastNotifState = state;
        }
        // 2 — полноэкранное приложение, 3 — полноэкранный Direct3D, 4 — режим презентации
        if (state is 2 or 3 or 4) reasons |= PauseReason.Fullscreen;

        if (reasons == _current)
        {
            return false;
        }

        _current = reasons;
        return true;
    }

    public static string Describe(PauseReason reasons)
    {
        if (reasons == PauseReason.None) return "нет";
        var parts = new List<string>();
        if (reasons.HasFlag(PauseReason.Locked)) parts.Add("экран заблокирован");
        if (reasons.HasFlag(PauseReason.SessionAway)) parts.Add("сессия не на экране");
        if (reasons.HasFlag(PauseReason.Fullscreen)) parts.Add("полноэкранное приложение");
        if (reasons.HasFlag(PauseReason.Resolution)) parts.Add("изменилось разрешение");
        if (reasons.HasFlag(PauseReason.Dpi)) parts.Add("изменился масштаб DPI");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Проверяет, что окно мира живо и сидит в нужном WorkerW.
    /// Возвращает причину, если нужно прикрепляться заново, иначе null.
    /// expectedSize == null — размер не проверять.
    /// </summary>
    public static string? CheckAttachment(IntPtr hwnd, IntPtr workerW, Size? expectedSize)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return "окно мира уничтожено";
        if (workerW == IntPtr.Zero || !IsWindow(workerW)) return "WorkerW исчез";
        if (GetParent(hwnd) != workerW) return "родитель окна изменился";
        if (!IsWindowVisible(hwnd)) return "окно скрыто";

        if (expectedSize is Size expected && GetClientRect(hwnd, out RECT rc))
        {
            if (rc.Right - rc.Left != expected.Width || rc.Bottom - rc.Top != expected.Height)
            {
                return $"размер окна {rc.Right - rc.Left}x{rc.Bottom - rc.Top} не равен экрану {expected.Width}x{expected.Height}";
            }
        }

        return null;
    }

    private static int QueryNotificationState()
    {
        int hr = SHQueryUserNotificationState(out int state);
        return hr == 0 ? state : -1;
    }

    private static string DescribeState(int state) => state switch
    {
        1 => "экран заблокирован или заставка",
        2 => "полноэкранное приложение",
        3 => "полноэкранный Direct3D",
        4 => "режим презентации",
        5 => "обычная работа",
        6 => "тихие часы",
        7 => "приложение из магазина",
        -1 => "не удалось получить",
        _ => "неизвестно"
    };

    // ---- события (приходят не из потока UI: только флаги и журнал) ----

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        _log($"Событие сессии: {e.Reason}");
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                _locked = true;
                break;
            case SessionSwitchReason.SessionUnlock:
                _locked = false;
                break;
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
            case SessionSwitchReason.SessionLogoff:
                _sessionAway = true;
                break;
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
            case SessionSwitchReason.SessionLogon:
                _sessionAway = false;
                break;
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Size size = CurrentSize();
        _log($"Событие: изменились параметры экрана, сейчас {size.Width}x{size.Height}");
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        _log($"Событие: параметры пользователя изменились, категория {e.Category}");
        if (e.Category == UserPreferenceCategory.Desktop)
        {
            _wallpaperEvent = true;
        }
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    // ---- вызовы Windows ----

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);
}