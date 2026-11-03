using System.Drawing.Drawing2D;
using Derevnya.Render;

namespace Derevnya;

internal sealed class MainForm : Form
{
    private const int PollIntervalMs = 500;
    private const int WM_DPICHANGED = 0x02E0;

    private readonly WorldState _state;
    private readonly World _world;
    private readonly Supervisor _supervisor;
    private readonly List<string> _lines = new();
    private readonly object _linesSync = new();
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly Font _font = new("Consolas", 11f);
    private readonly Font _bigFont = new("Consolas", 18f, FontStyle.Bold);
    private readonly Font _smallFont = new("Consolas", 9f);

    private bool _attached;
    private bool _quiet;
    private IntPtr _workerW = IntPtr.Zero;
    private volatile PauseReason _paused = PauseReason.None;
    private Rectangle _tickRect;
    private int _framePending;

    // Опрос иконок: раскладка принимается после двух одинаковых опросов подряд
    private string? _lastPolledSignature;
    private int _samePollCount;
    private string? _acceptedSignature;
    private IconLayout? _accepted;
    private string? _lastError;

    // Что уже превращено в маску препятствий
    private WorldGrid? _appliedGrid;
    private string? _appliedSignature;
    private Rectangle _appliedWorkArea;

    public event Action? ExitRequested;

    public MainForm(WorldState state, World world, Supervisor supervisor)
    {
        _state = state;
        _world = world;
        _supervisor = supervisor;

        Text = "Деревня";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        DoubleBuffered = true;
        KeyPreview = true;

        _tickRect = new Rectangle((int)(Width / 3f), 370, 760, 60);

        _pollTimer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _pollTimer.Tick += (_, _) => PollIcons();

        Logger.LineWritten += OnLogLine;
    }

    protected override bool ShowWithoutActivation => true;

    public IntPtr Hwnd => IsHandleCreated ? Handle : IntPtr.Zero;
    public IntPtr WorkerW => _workerW;
    public bool IsAlive => !IsDisposed && IsHandleCreated;

    /// <summary>Прикрепляет окно к WorkerW (в первый раз или после пересоздания WorkerW).</summary>
    public bool AttachTo(IntPtr workerW)
    {
        if (!IsAlive)
        {
            return false;
        }

        _workerW = workerW;
        _attached = WallpaperHost.Attach(Handle, workerW, Logger.Log);
        _tickRect = new Rectangle((int)(Width / 3f), 370, 760, 60);

        if (_attached)
        {
            // Раскладку заново читаем после каждого прикрепления;
            // прежняя маска остаётся в мире, пока не будет принята новая раскладка
            _lastPolledSignature = null;
            _samePollCount = 0;
            _acceptedSignature = null;
            _accepted = null;
            _pollTimer.Start();
        }

        Invalidate();
        return _attached;
    }

    /// <summary>Вызывается надсмотрщиком каждые 0,5 с: сообщает текущие причины паузы.</summary>
    public void SetPaused(PauseReason reasons)
    {
        if (reasons == _paused)
        {
            return;
        }

        _paused = reasons;
        if (IsAlive)
        {
            Invalidate(); // один раз, чтобы показать или убрать надпись о паузе
        }
    }

    /// <summary>
    /// Вызывается из потока мира раз в кадр. Просит окно перерисовать счётчик.
    /// Не копит очередь: пока предыдущий запрос не выполнен, новый не отправляется.
    /// </summary>
    public void RequestFrame()
    {
        if (_paused != PauseReason.None || _quiet || !IsAlive)
        {
            return;
        }

        if (Interlocked.Exchange(ref _framePending, 1) == 1)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _framePending, 0);
                if (IsAlive && !_quiet)
                {
                    Invalidate(_tickRect);
                }
            }));
        }
        catch
        {
            // окно закрывается
            Interlocked.Exchange(ref _framePending, 0);
        }
    }

    /// <summary>Уничтожает окно без запроса на выход, даже если Windows уже разрушила его дескриптор.</summary>
    public void DestroyQuietly()
    {
        _quiet = true;
        try
        {
            _pollTimer.Stop();
            Logger.LineWritten -= OnLogLine;
            Dispose();
        }
        catch (Exception ex)
        {
            Logger.Log($"Предупреждение при уничтожении окна: {ex.Message}");
        }
    }

    private void PollIcons()
    {
        if (_paused != PauseReason.None || !IsAlive)
        {
            return;
        }

        IconLayout? layout = IconReader.Read(Handle, out string? error);

        if (layout == null)
        {
            if (error != _lastError)
            {
                Logger.Log($"ОШИБКА опроса иконок: {error}");
                _lastError = error;
            }
            _samePollCount = 0;
            _lastPolledSignature = null;
            return;
        }

        if (_lastError != null)
        {
            Logger.Log("Опрос иконок снова работает");
            _lastError = null;
        }

        if (layout.Signature == _lastPolledSignature)
        {
            _samePollCount++;
        }
        else
        {
            _lastPolledSignature = layout.Signature;
            _samePollCount = 1;
            if (layout.Signature != _acceptedSignature)
            {
                Logger.Log($"Опрос: раскладка отличается от принятой, жду подтверждения (иконок: {layout.Items.Count})");
            }
        }

        if (_samePollCount >= 2 && layout.Signature != _acceptedSignature)
        {
            _accepted = layout;
            _acceptedSignature = layout.Signature;
            LogAccepted(layout);
            Invalidate();
        }

        // Маска строится только из принятой раскладки (и пересчитывается,
        // если изменилась рабочая область или мир был построен заново)
        ApplyMaskIfNeeded();
    }

    private void ApplyMaskIfNeeded()
    {
        if (_accepted == null)
        {
            return;
        }

        WorldGrid grid = _world.Grid;
        // Маска строится только при том разрешении, под которое создан мир.
        // При смене разрешения пауза включается с задержкой до 0,5 с: за это время маску не трогаем.
        if (Supervisor.CurrentSize() != grid.ScreenSize)
        {
            return;
        }
        Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(Point.Empty, grid.ScreenSize);

        if (ReferenceEquals(grid, _appliedGrid)
            && _acceptedSignature == _appliedSignature
            && workArea == _appliedWorkArea)
        {
            return;
        }

        var icons = new List<Rectangle>(_accepted.Items.Count);
        foreach (IconInfo item in _accepted.Items)
        {
            icons.Add(item.Bounds);
        }

        ObstacleMask mask = grid.SetObstacles(icons, workArea);
        _appliedGrid = grid;
        _appliedSignature = _acceptedSignature;
        _appliedWorkArea = workArea;

        Logger.Log(
            $"МАСКА: версия {mask.Version}, заблокировано {mask.BlockedCount} из {grid.TilesX * grid.TilesY} тайлов " +
            $"(мир {grid.TilesX}x{grid.TilesY}), рабочая область x={workArea.Left} y={workArea.Top} " +
            $"w={workArea.Width} h={workArea.Height}");

        for (int i = 0; i < _accepted.Items.Count; i++)
        {
            IconInfo item = _accepted.Items[i];
            Rectangle t = grid.TilesOf(item.Bounds);
            Logger.Log($"  иконка #{i} '{item.Name}': тайлы x={t.Left} y={t.Top} w={t.Width} h={t.Height}");
        }

        Invalidate();
    }

    private static void LogAccepted(IconLayout layout)
    {
        if (!layout.ListViewVisible)
        {
            Logger.Log("РАСКЛАДКА ПРИНЯТА: значки рабочего стола скрыты, препятствий 0");
            return;
        }

        Logger.Log($"РАСКЛАДКА ПРИНЯТА: иконок {layout.Items.Count}");
        for (int i = 0; i < layout.Items.Count; i++)
        {
            IconInfo item = layout.Items[i];
            Rectangle b = item.Bounds;
            Logger.Log($"  #{i} '{item.Name}' x={b.Left} y={b.Top} w={b.Width} h={b.Height}");
        }
    }

        private void OnLogLine(string line)
    {
        lock (_linesSync)
        {
            _lines.Add(line);
            while (_lines.Count > 14)
            {
                _lines.RemoveAt(0);
            }
        }

        if (_paused != PauseReason.None)
        {
            return; // во время паузы не перерисовываем
        }

        try
        {
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(new Action(() => { if (IsAlive && !_quiet) Invalidate(); }));
            }
        }
        catch
        {
            // окно уже закрывается
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        using (var brush = new LinearGradientBrush(
            ClientRectangle,
            Color.FromArgb(30, 110, 200),
            Color.FromArgb(130, 210, 120),
            45f))
        {
            g.FillRectangle(brush, ClientRectangle);
        }

        // Отладочная картинка: сетка тайлов и маска препятствий
        if (_world.Config.DebugOverlay)
        {
            DebugOverlay.Draw(g, _world.Grid);
        }

        if (_accepted != null)
        {
            using var pen = new Pen(Color.Red, 2f);
            for (int i = 0; i < _accepted.Items.Count; i++)
            {
                Rectangle b = _accepted.Items[i].Bounds;
                g.DrawRectangle(pen, b);
                g.DrawString($"#{i}", _smallFont, Brushes.Yellow, b.Left + 2, b.Top + 2);
            }
        }

        string[] snapshot;
        lock (_linesSync)
        {
            snapshot = _lines.ToArray();
        }

        PauseReason paused = _paused;
        float x = Width / 3f;
        float y = 30f;
        string header = !_attached
            ? "Деревня: окно НЕ прикреплено"
            : paused != PauseReason.None
                ? "Деревня: ПАУЗА (" + Supervisor.Describe(paused) + ")"
                : "Деревня: окно за иконками";
        g.DrawString(header, _font, Brushes.White, x, y);
        y += _font.Height * 1.5f;
        foreach (string line in snapshot)
        {
            g.DrawString(line, _font, Brushes.White, x, y);
            y += _font.Height + 2;
        }

        // Счётчик шагов мира: если он растёт, мир работает; если замер, пауза
        string tickText = paused != PauseReason.None
            ? $"Шаг мира: {_state.Ticks}  (пауза)"
            : $"Шаг мира: {_state.Ticks}";
        g.DrawString(tickText, _bigFont, Brushes.Yellow, _tickRect.Left, _tickRect.Top);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DPICHANGED)
        {
            _supervisor.NotifyDpiChanged();
        }
        base.WndProc(ref m);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            ExitRequested?.Invoke();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_quiet)
        {
            // Закрывать окно может только WorldContext (через «Выход»)
            e.Cancel = true;
            ExitRequested?.Invoke();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollTimer.Dispose();
            _font.Dispose();
            _bigFont.Dispose();
            _smallFont.Dispose();
        }
        base.Dispose(disposing);
    }
}