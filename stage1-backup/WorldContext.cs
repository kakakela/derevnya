namespace Derevnya;

internal sealed class WorldContext : ApplicationContext
{
    private const int TickMs = 500;
    private const int MinReattachGapMs = 1000;

    private readonly WorldState _state = new();
    private readonly Supervisor _supervisor;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;

    private MainForm? _form;
    private bool _exiting;
    private bool _waitingWorkerW;
    private long _lastReattachTick = -100000;

    public WorldContext()
    {
        Logger.Log("=== Шаг 3: надсмотрщик ===");
        _supervisor = new Supervisor(Logger.Log);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Перезапустить мир", null, (_, _) => RestartWorld());
        menu.Items.Add("Выход", null, (_, _) => ExitWorld());
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Деревня (шаг 3)",
            ContextMenuStrip = menu,
            Visible = true
        };

        _timer = new System.Windows.Forms.Timer { Interval = TickMs };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    private void OnTick()
    {
        if (_exiting)
        {
            return;
        }

        try
        {
            Supervise();
        }
        catch (Exception ex)
        {
            Logger.Log($"ОШИБКА надсмотрщика: {ex}");
        }
    }

    private void Supervise()
    {
        // 1. Причины паузы
        bool changed = _supervisor.Poll();
        if (changed)
        {
            if (_supervisor.IsPaused)
            {
                Logger.Log($"ПАУЗА: {Supervisor.Describe(_supervisor.Reasons)}");
            }
            else
            {
                Logger.Log($"ПАУЗА СНЯТА, мир продолжает с тика {_state.Ticks}");
            }
        }
        _form?.SetPaused(_supervisor.Reasons);

        bool wallpaperEvent = _supervisor.TakeWallpaperEvent();
        if (wallpaperEvent)
        {
            Logger.Log("Событие: сменились обои или параметры рабочего стола, проверяю окно мира");
        }

        // 2. Пока экран заблокирован или сессия не на экране, окно не трогаем
        if ((_supervisor.Reasons & (PauseReason.Locked | PauseReason.SessionAway)) != 0)
        {
            return;
        }

        // 3. Окно мира на месте?
        string? reason;
        if (_form == null)
        {
            reason = "окно мира ещё не создано";
        }
        else
        {
            reason = Supervisor.CheckAttachment(_form.Hwnd, _form.WorkerW, Supervisor.CurrentSize());
            if (reason == null && wallpaperEvent)
            {
                IntPtr current = WallpaperHost.FindWorkerW(_ => { });
                if (current != IntPtr.Zero && current != _form.WorkerW)
                {
                    reason = "после смены обоев появился другой WorkerW";
                }
            }
        }

        if (reason == null)
        {
            _waitingWorkerW = false;
            return;
        }

        Reattach(reason);
    }

    private void Reattach(string reason)
    {
        long now = Environment.TickCount64;
        if (now - _lastReattachTick < MinReattachGapMs)
        {
            return;
        }
        _lastReattachTick = now;

        if (!_waitingWorkerW)
        {
            Logger.Log($"Надсмотрщик: нужно прикрепиться заново ({reason})");
        }

        Action<string> findLog = _waitingWorkerW ? (_ => { }) : Logger.Log;
        IntPtr workerW = WallpaperHost.FindWorkerW(findLog);
        if (workerW == IntPtr.Zero)
        {
            if (!_waitingWorkerW)
            {
                Logger.Log("WorkerW пока не найден (Проводник перезапускается?), буду пробовать снова");
                _waitingWorkerW = true;
            }
            return;
        }

        if (_waitingWorkerW)
        {
            Logger.Log($"WorkerW найден: 0x{workerW.ToInt64():X}");
        }
        _waitingWorkerW = false;

        if (_form == null || !_form.IsAlive)
        {
            Logger.Log("Создаю окно мира заново");
            _form?.DestroyQuietly();
            _form = new MainForm(_state, _supervisor);
            _form.ExitRequested += ExitWorld;
            _form.Show();
        }

        bool ok = _form.AttachTo(workerW);
        _form.SetPaused(_supervisor.Reasons);
        Logger.Log(ok
            ? "Окно мира прикреплено, мир продолжает работу"
            : "ОШИБКА: прикрепить окно мира не удалось");
    }

    private void RestartWorld()
    {
        _state.Ticks = 0;
        _supervisor.ResetBaseline();
        Logger.Log("Команда: мир перезапущен (тики обнулены, текущее разрешение принято за базовое)");
    }

    private void ExitWorld()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;

        _timer.Stop();
        Logger.Log("Выход: закрываю окно мира");
        _form?.DestroyQuietly();
        _form = null;

        _tray.Visible = false;
        _tray.Dispose();
        _supervisor.Dispose();

        ExitThread();
    }
}