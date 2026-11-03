using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Derevnya;

internal sealed class BenchForm : Form
{
    private const int TargetFps = 30;
    private const int DurationSec = 60;
    private const int SampleSec = 5;
    private const int Squares = 100;
    private const int SquareSize = 40;

    private volatile bool _stop;
    private Thread? _thread;

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    public BenchForm()
    {
        Text = "Деревня: замер";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
    }

    protected override bool ShowWithoutActivation => true;

    // Окно рисует только рабочий поток замера
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        IntPtr workerW = WallpaperHost.FindWorkerW(Logger.Log);
        if (workerW == IntPtr.Zero)
        {
            Logger.Log("ОШИБКА: WorkerW не найден, замер отменён");
            Close();
            return;
        }

        if (!WallpaperHost.Attach(Handle, workerW, Logger.Log))
        {
            Logger.Log("ОШИБКА: прикрепить окно не удалось, замер отменён");
            Close();
            return;
        }

        int w = Width;
        int h = Height;
        IntPtr hwnd = Handle;

        Logger.Log("=== ЗАМЕР ===");
#if DEBUG
        Logger.Log("СБОРКА: Debug (для честных цифр нужен Release)");
#else
        Logger.Log("СБОРКА: Release");
#endif
        Logger.Log($"Буфер {w}x{h}, цель {TargetFps} кадров/с, длительность {DurationSec} с, квадратов {Squares}");
        Logger.Log($"Логических процессоров: {Environment.ProcessorCount}");

        _thread = new Thread(() => Run(w, h, hwnd)) { IsBackground = true, Name = "Bench" };
        _thread.Start();
    }

    private unsafe void Run(int w, int h, IntPtr hwnd)
    {
        timeBeginPeriod(1);
        try
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using var gBmp = Graphics.FromImage(bmp);
            using var gWin = Graphics.FromHwnd(hwnd);
            var brushes = new[]
            {
                new SolidBrush(Color.Red), new SolidBrush(Color.Yellow),
                new SolidBrush(Color.White), new SolidBrush(Color.Black)
            };
            var rect = new Rectangle(0, 0, w, h);

            var proc = Process.GetCurrentProcess();
            int cores = Environment.ProcessorCount;
            var sw = Stopwatch.StartNew();
            double frameMs = 1000.0 / TargetFps;
            double next = 0;
            long frame = 0;

            // Сэмпл (5 секунд)
            long sampleStartMs = 0;
            TimeSpan sampleStartCpu = proc.TotalProcessorTime;
            long sampleFrames = 0;
            double sampleWork = 0;
            double sampleWorkMax = 0;

            // Итоги
            var cpuSamples = new List<double>();
            double totalWork = 0;
            double maxWork = 0;
            long slowFrames = 0;
            double maxWorkingSetMb = 0;
            double maxPrivateMb = 0;
            double workingSetSum = 0;
            int memSamples = 0;

            while (!_stop && sw.Elapsed.TotalSeconds < DurationSec)
            {
                double now = sw.Elapsed.TotalMilliseconds;
                if (now < next)
                {
                    Thread.Sleep(1);
                    continue;
                }
                next += frameMs;
                if (next < now - frameMs * 3)
                {
                    next = now; // сильно отстали: не пытаемся нагнать
                }

                double t0 = sw.Elapsed.TotalMilliseconds;

                // 1. Заливка всего буфера
                int color = unchecked((255 << 24) | (((int)(frame * 2) & 255) << 16) | (((int)frame & 255) << 8) | 160);
                BitmapData data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    new Span<int>((void*)data.Scan0, data.Stride / 4 * h).Fill(color);
                }
                finally
                {
                    bmp.UnlockBits(data);
                }

                // 2. Движущиеся квадраты
                int maxX = Math.Max(1, w - SquareSize);
                int maxY = Math.Max(1, h - SquareSize);
                for (int i = 0; i < Squares; i++)
                {
                    int x = (int)((i * 37L + frame * 3) % maxX);
                    int y = (int)((i * 53L + frame * 2) % maxY);
                    gBmp.FillRectangle(brushes[i & 3], x, y, SquareSize, SquareSize);
                }

                // 3. Вывод в окно
                gWin.DrawImageUnscaled(bmp, 0, 0);

                double work = sw.Elapsed.TotalMilliseconds - t0;
                frame++;
                sampleFrames++;
                sampleWork += work;
                if (work > sampleWorkMax) sampleWorkMax = work;
                totalWork += work;
                if (work > maxWork) maxWork = work;
                if (work > frameMs) slowFrames++;

                // Сэмпл раз в SampleSec секунд
                long elapsedMs = sw.ElapsedMilliseconds;
                if (elapsedMs - sampleStartMs >= SampleSec * 1000)
                {
                    proc.Refresh();
                    TimeSpan cpuNow = proc.TotalProcessorTime;
                    double wall = (elapsedMs - sampleStartMs) / 1000.0;
                    double cpuSec = (cpuNow - sampleStartCpu).TotalSeconds;
                    double cpuTotalPct = cpuSec / (wall * cores) * 100.0;
                    double cpuCorePct = cpuSec / wall * 100.0;
                    double wsMb = proc.WorkingSet64 / 1048576.0;
                    double privMb = proc.PrivateMemorySize64 / 1048576.0;

                    cpuSamples.Add(cpuTotalPct);
                    workingSetSum += wsMb;
                    memSamples++;
                    if (wsMb > maxWorkingSetMb) maxWorkingSetMb = wsMb;
                    if (privMb > maxPrivateMb) maxPrivateMb = privMb;

                    Logger.Log(
                        $"Сэмпл {elapsedMs / 1000}с: кадров/с {sampleFrames / wall:F1}, " +
                        $"CPU {cpuTotalPct:F1}% всей машины ({cpuCorePct:F1}% одного ядра), " +
                        $"кадр в среднем {sampleWork / sampleFrames:F2} мс (макс {sampleWorkMax:F1}), " +
                        $"память: рабочий набор {wsMb:F0} МБ, приватная {privMb:F0} МБ");

                    sampleStartMs = elapsedMs;
                    sampleStartCpu = cpuNow;
                    sampleFrames = 0;
                    sampleWork = 0;
                    sampleWorkMax = 0;
                }
            }

            double totalSec = sw.Elapsed.TotalSeconds;
            double avgCpu = cpuSamples.Count > 0 ? cpuSamples.Average() : 0;
            double maxCpu = cpuSamples.Count > 0 ? cpuSamples.Max() : 0;
            Logger.Log("=== СВОДКА ЗАМЕРА ===");
            Logger.Log($"Разрешение буфера: {w}x{h}, процессоров: {cores}");
            Logger.Log($"Кадров: {frame} за {totalSec:F1} с, реально {frame / totalSec:F1} кадров/с (цель {TargetFps})");
            Logger.Log($"CPU процесса, % всей машины: среднее {avgCpu:F1}, максимум {maxCpu:F1}");
            Logger.Log($"Кадр: среднее {(frame > 0 ? totalWork / frame : 0):F2} мс, максимум {maxWork:F1} мс, " +
                       $"дольше {frameMs:F1} мс: {slowFrames}");
            Logger.Log($"Память: рабочий набор среднее {(memSamples > 0 ? workingSetSum / memSamples : 0):F0} МБ, " +
                       $"максимум {maxWorkingSetMb:F0} МБ; приватная максимум {maxPrivateMb:F0} МБ");
            Logger.Log($"Сборок мусора: gen0 {GC.CollectionCount(0)}, gen1 {GC.CollectionCount(1)}, gen2 {GC.CollectionCount(2)}");
            Logger.Log("Замер завершён, окно закрывается");

            foreach (var b in brushes) b.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Log($"ОШИБКА замера: {ex}");
        }
        finally
        {
            timeEndPeriod(1);
            try
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(Close));
                }
            }
            catch
            {
                // окно уже закрыто
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _stop = true;
        base.OnFormClosing(e);
    }
}