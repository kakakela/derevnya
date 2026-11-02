using System.Drawing.Drawing2D;

namespace Derevnya;

internal sealed class MainForm : Form
{
    private const int PollIntervalMs = 500;

    private readonly List<string> _lines = new();
    private readonly object _linesSync = new();
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private bool _attached;

    // Опрос иконок: раскладка принимается после двух одинаковых опросов подряд
    private string? _lastPolledSignature;
    private int _samePollCount;
    private string? _acceptedSignature;
    private IconLayout? _accepted;
    private string? _lastError;

    public MainForm()
    {
        Text = "Деревня";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        DoubleBuffered = true;
        KeyPreview = true;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Выход", null, (_, _) => Close());
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Деревня (шаг 2)",
            ContextMenuStrip = menu,
            Visible = true
        };

        _pollTimer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _pollTimer.Tick += (_, _) => PollIcons();

        Logger.LineWritten += OnLogLine;
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        AttachToDesktop();
    }

    private void AttachToDesktop()
    {
        Logger.Log("Ищу WorkerW...");
        IntPtr workerW = WallpaperHost.FindWorkerW(Logger.Log);
        if (workerW == IntPtr.Zero)
        {
            Logger.Log("ОШИБКА: WorkerW не найден, окно остаётся обычным (закрыть: Esc или значок в трее)");
            return;
        }

        _attached = WallpaperHost.Attach(Handle, workerW, Logger.Log);
        Logger.Log(_attached
            ? "Окно прикреплено за иконки"
            : "ОШИБКА: прикрепить окно не удалось");
        Invalidate();

        if (_attached)
        {
            Logger.Log($"Опрос иконок каждые {PollIntervalMs} мс, принятие после двух одинаковых опросов");
            _pollTimer.Start();
        }
    }

    private void PollIcons()
    {
        IconLayout? layout = IconReader.Read(Handle, out string? error);

        if (layout == null)
        {
            // Одну и ту же ошибку пишем в журнал только один раз
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

        try
        {
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(new Action(Invalidate));
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

        // Принятые прямоугольники иконок: красная рамка и номер
        if (_accepted != null)
        {
            using var fill = new SolidBrush(Color.FromArgb(50, 255, 0, 0));
            using var pen = new Pen(Color.Red, 2f);
            using var small = new Font("Consolas", 9f);
            for (int i = 0; i < _accepted.Items.Count; i++)
            {
                Rectangle b = _accepted.Items[i].Bounds;
                g.FillRectangle(fill, b);
                g.DrawRectangle(pen, b);
                g.DrawString($"#{i}", small, Brushes.Yellow, b.Left + 2, b.Top + 2);
            }
        }

        string[] snapshot;
        lock (_linesSync)
        {
            snapshot = _lines.ToArray();
        }

        using var font = new Font("Consolas", 11f);
        float x = Width / 3f;
        float y = 30f;
        string header = _attached ? "Деревня: окно за иконками" : "Деревня: окно НЕ прикреплено";
        g.DrawString(header, font, Brushes.White, x, y);
        y += font.Height * 1.5f;
        foreach (string line in snapshot)
        {
            g.DrawString(line, font, Brushes.White, x, y);
            y += font.Height + 2;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            Close();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _pollTimer.Stop();
        Logger.LineWritten -= OnLogLine;
        _tray.Visible = false;
        _tray.Dispose();
        Logger.Log("Закрытие окна");
        base.OnFormClosing(e);
    }
}