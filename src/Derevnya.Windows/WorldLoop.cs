using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Derevnya;

internal sealed class WorldLoop
{
    private const int MaxStepsPerIteration = 5;

    private readonly Simulation _simulation;
    private readonly WorldConfig _config;
    private readonly Thread _thread;

    private volatile bool _stop;
    private volatile bool _paused;
    private volatile Action? _frameCallback;
    private bool _callbackErrorLogged;

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    public WorldLoop(Simulation simulation, WorldConfig config)
    {
        _simulation = simulation;
        _config = config;
        _thread = new Thread(Run) { IsBackground = true, Name = "World" };
    }

    /// <summary>Пауза: ничего не считается и не рисуется. Можно выставлять из любого потока.</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>Вызывается из потока мира раз в кадр. Должен быть быстрым и потокобезопасным.</summary>
    public Action? FrameCallback
    {
        get => _frameCallback;
        set => _frameCallback = value;
    }

    public void Start() => _thread.Start();

    public void Stop()
    {
        _stop = true;
        if (_thread.IsAlive && !_thread.Join(2000))
        {
            Logger.Log("ПРЕДУПРЕЖДЕНИЕ: поток мира не остановился за 2 секунды");
        }
    }

    private void Run()
    {
        uint timerResult = timeBeginPeriod(1);
        Logger.Log(
            $"Поток мира запущен: {_config.StepsPerSecond} шагов/с, {_config.FramesPerSecond} кадров/с, " +
            $"timeBeginPeriod(1) вернул {timerResult} (0 = успешно)");
        try
        {
            Loop();
        }
        catch (Exception ex)
        {
            Logger.Log($"ОШИБКА потока мира: {ex}");
        }
        finally
        {
            timeEndPeriod(1);
            Logger.Log("Поток мира остановлен");
        }
    }

    private void Loop()
    {
        double stepMs = 1000.0 / _config.StepsPerSecond;
        double frameMs = 1000.0 / _config.FramesPerSecond;
        double statsMs = _config.StatsLogIntervalSeconds * 1000.0;

        var sw = Stopwatch.StartNew();
        double nextStep = 0;
        double nextFrame = 0;
        double statsStart = 0;
        long frames = 0;
        long steps = 0;
        bool wasPaused = true; // при старте отсчёт тоже начинается заново

        while (!_stop)
        {
            double now = sw.Elapsed.TotalMilliseconds;

            if (_paused)
            {
                wasPaused = true;
                Thread.Sleep(20);
                continue;
            }

            if (wasPaused)
            {
                // После паузы не нагоняем пропущенное время
                wasPaused = false;
                nextStep = now;
                nextFrame = now;
                statsStart = now;
                frames = 0;
                steps = 0;
            }

            // Шаги симуляции: фиксированная частота, не привязана к кадрам
            int doneSteps = 0;
            while (now >= nextStep && doneSteps < MaxStepsPerIteration)
            {
                _simulation.Step();
                steps++;
                doneSteps++;
                nextStep += stepMs;
            }
            if (now - nextStep > stepMs * MaxStepsPerIteration)
            {
                nextStep = now; // сильно отстали: не пытаемся нагнать
            }

            // Кадры
            if (now >= nextFrame)
            {
                RaiseFrame();
                frames++;
                nextFrame += frameMs;
                if (now - nextFrame > frameMs * 3)
                {
                    nextFrame = now + frameMs;
                }
            }

            // Измеренные частоты раз в заданный интервал
            if (now - statsStart >= statsMs)
            {
                double wall = (now - statsStart) / 1000.0;
                Logger.Log(
                    $"Цикл мира: кадров/с {frames / wall:F1} (цель {_config.FramesPerSecond}), " +
                    $"шагов/с {steps / wall:F1} (цель {_config.StepsPerSecond}), " +
                    $"шагов всего {_simulation.State.Ticks}");
                statsStart = now;
                frames = 0;
                steps = 0;
            }

            Thread.Sleep(1);
        }
    }

    private void RaiseFrame()
    {
        Action? callback = _frameCallback;
        if (callback == null)
        {
            return;
        }

        try
        {
            callback();
        }
        catch (Exception ex)
        {
            if (!_callbackErrorLogged)
            {
                _callbackErrorLogged = true;
                Logger.Log($"ОШИБКА в обработчике кадра (дальше не повторяю): {ex.Message}");
            }
        }
    }
}