using System.Drawing.Drawing2D;

namespace Derevnya;

internal sealed class MainForm : Form
{
    private const int PollIntervalMs = 500;
    private const int WorldIntervalMs = 33;
    private const int WM_DPICHANGED = 0x02E0;

    private readonly WorldState _state;
    private readonly Supervisor _supervisor;
    private readonly List<string> _lines = new();
    private readonly object _linesSync = new();
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly System.Windows.Forms.Timer _worldTimer;
    private readonly Font _font = new("Consolas", 11f);
    private readonly Font _bigFont = new("Consolas", 18f, FontStyle.Bold);
    private readonly Font _smallFont = new("Consolas", 9f);

    private bool _attached;
    private bool _quiet;
    private IntPtr _workerW = IntPtr.Zero;
    private PauseReason _paused = PauseReason.None;
    private Rectangle _tickRect;

    // Опрос иконок: раскладка принимается после двух одинаковых опросов подряд
    private string? _lastPolledSignature;
    private int _samePollCount;
    private string? _acceptedSignature;
    private IconLayout? _accepted;
    private string? _lastError;

    public event Action? ExitRequested;

    public MainForm(WorldState state, Supervisor supervisor)
    {
        _state = state;
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

        _worldTimer = new System.Windows.Forms.Timer { Interval = WorldIntervalMs };
        _worldTimer.Tick += (_, _) => WorldTick();

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
            // Раскладку заново читаем после каждого прикрепления
            _lastPolledSignature = null;
            _samePollCount = 0;
            _acceptedSignature = null;
            _accepted = null;
            _pollTimer.Start();
            _worldTimer.Start();
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

    /// <summary>Уничтожает окно без запроса на выход, даже если Windows уже разрушила его дескриптор.</summary>
    public void DestroyQuietly()
    {
        _quiet = true;
        try
        {
            _pollTimer.Stop();
            _worldTimer.Stop();
            Logger.LineWritten -= OnLogLine;
            Dispose();
        }
        catch (Exception ex)
        {
            Logger.Log($"Предупреждение при уничтожении окна: {ex.Message}");
        }
    }

    private void WorldTick()
    {
        if (_paused != PauseReason.None || !IsAlive)
        {
            return; // пауза: без расчёта и отрисовки
        }

        _state.Ticks++;
        Invalidate(_tickRect);
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
                BeginInvoke(new Action(() => { if (IsAlive) Invalidate(); }));
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

        if (_accepted != null)
        {
            using var fill = new SolidBrush(Color.FromArgb(50, 255, 0, 0));
            using var pen = new Pen(Color.Red, 2f);
            for (int i = 0; i < _accepted.Items.Count; i++)
            {
                Rectangle b = _accepted.Items[i].Bounds;
                g.FillRectangle(fill, b);
                g.DrawRectangle(pen, b);
                g.DrawString($"#{i}", _smallFont, Brushes.Yellow, b.Left + 2, b.Top + 2);
            }
        }

        string[] snapshot;
        lock (_linesSync)
        {
            snapshot = _lines.ToArray();
        }

        float x = Width / 3f;
        float y = 30f;
        string header = !_attached
            ? "Деревня: окно НЕ прикреплено"
            : _paused != PauseReason.None
                ? "Деревня: ПАУЗА (" + Supervisor.Describe(_paused) + ")"
                : "Деревня: окно за иконками";
        g.DrawString(header, _font, Brushes.White, x, y);
        y += _font.Height * 1.5f;
        foreach (string line in snapshot)
        {
            g.DrawString(line, _font, Brushes.White, x, y);
            y += _font.Height + 2;
        }

        // Счётчик мира: если он растёт, мир работает; если замер, пауза
        string tickText = _paused != PauseReason.None
            ? $"Тик мира: {_state.Ticks}  (пауза)"
            : $"Тик мира: {_state.Ticks}";
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
            _worldTimer.Dispose();
            _font.Dispose();
            _bigFont.Dispose();
            _smallFont.Dispose();
        }
        base.Dispose(disposing);
    }
}