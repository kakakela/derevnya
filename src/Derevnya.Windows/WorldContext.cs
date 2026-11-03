namespace Derevnya;

internal sealed class WorldContext : ApplicationContext
{
    private const int TickMs = 500;
    private const int MinReattachGapMs = 1000;

    private readonly WorldConfig _config;
    private readonly WorldState _state;
    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly WorldLoop _loop;
    private readonly Supervisor _supervisor;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;

    // Читается из потока мира (обработчик кадра), пишется из потока UI
    private volatile MainForm? _form;
    private bool _exiting;
    private bool _waitingWorkerW;
    private long _lastReattachTick = -100000;

    public WorldContext()
    {
        Logger.Log("=== Этап 2, задание 1: мир в отдельном потоке ===");

        _config = WorldConfig.Load();
        _state = new WorldState();
        _world = new World(_config, Supervisor.CurrentSize());
        _simulation = new Simulation(_state);
        _loop = new WorldLoop(_simulation, _config);
        _loop.FrameCallback = () => _form?.RequestFrame();

        _supervisor = new Supervisor(Logger.Log);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Перезапустить мир", null, (_, _) => RestartWorld());
        menu.Items.Add("Выход", null, (_, _) => ExitWorld());
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Деревня (этап 2)",
            ContextMenuStrip = menu,
            Visible = true
        };

        _timer = new System.Windows.Forms.Timer { Interval = TickMs };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        _loop.Start();
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
                Logger.Log($"ПАУЗА СНЯТА, мир продолжает с шага {_state.Ticks}");
            }
        }

        // Пауза касается и потока мира: без расчёта и без отрисовки
        _loop.Paused = _supervisor.IsPaused;
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
        MainForm? form = _form;
        string? reason;
        if (form == null)
        {
            reason = "окно мира ещё не создано";
        }
        else
        {
            reason = Supervisor.CheckAttachment(form.Hwnd, form.WorkerW, Supervisor.CurrentSize());
            if (reason == null && wallpaperEvent)
            {
                IntPtr current = WallpaperHost.FindWorkerW(_ => { });
                if (current != IntPtr.Zero && current != form.WorkerW)
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

        MainForm? form = _form;
        if (form == null || !form.IsAlive)
        {
            Logger.Log("Создаю окно мира заново");
            form?.DestroyQuietly();
            form = new MainForm(_state, _world, _supervisor);
            form.ExitRequested += ExitWorld;
            _form = form;
            form.Show();
        }

        bool ok = form.AttachTo(workerW);
        form.SetPaused(_supervisor.Reasons);
        Logger.Log(ok
            ? "Окно мира прикреплено, мир продолжает работу"
            : "ОШИБКА: прикрепить окно мира не удалось");
    }

    private void RestartWorld()
    {
        _state.Reset();
        _supervisor.ResetBaseline();
        _world.Rebuild(Supervisor.CurrentSize());
        Logger.Log("Команда: мир перезапущен (шаги обнулены, текущее разрешение принято за базовое)");
    }

    private void ExitWorld()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;

        _timer.Stop();
        Logger.Log("Выход: останавливаю поток мира и закрываю окно");

        _loop.FrameCallback = null;
        _loop.Stop();

        MainForm? form = _form;
        _form = null;
        form?.DestroyQuietly();

        _tray.Visible = false;
        _tray.Dispose();
        _supervisor.Dispose();

        ExitThread();
    }
}