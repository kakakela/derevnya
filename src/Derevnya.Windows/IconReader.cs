using System.Runtime.InteropServices;
using System.Text;

namespace Derevnya;

internal readonly record struct IconInfo(string Name, Rectangle Bounds);

internal sealed class IconLayout
{
    public IconLayout(IReadOnlyList<IconInfo> items, bool listViewVisible)
    {
        Items = items;
        ListViewVisible = listViewVisible;
        Signature = (listViewVisible ? "V" : "H") + ";" + string.Join(";",
            items.Select(i => $"{i.Name}|{i.Bounds.Left},{i.Bounds.Top},{i.Bounds.Right},{i.Bounds.Bottom}"));
    }

    public IReadOnlyList<IconInfo> Items { get; }
    public bool ListViewVisible { get; }

    /// <summary>Строка для сравнения раскладок: одинаковая строка означает одинаковую раскладку.</summary>
    public string Signature { get; }
}

internal static class IconReader
{
    private const uint LVM_GETITEMCOUNT = 0x1004;
    private const uint LVM_GETITEMRECT = 0x100E;
    private const uint LVM_GETITEMTEXTW = 0x1073;
    private const int LVIR_BOUNDS = 0;
    private const uint LVIF_TEXT = 0x0001;

    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint TIMEOUT_MS = 500;

    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    private const int MaxItems = 2000;

    // Раскладка буфера в памяти Проводника
    private const int RectOffset = 0;      // RECT, 16 байт
    private const int ItemOffset = 64;     // LVITEMW, 88 байт (64-битная раскладка)
    private const int TextOffset = 256;    // текст подписи
    private const int TextChars = 260;
    private const int BufferSize = 4096;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern int MapWindowPoints(IntPtr hWndFrom, IntPtr hWndTo, ref RECT lpPoints, uint cPoints);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr read);

    /// <summary>Находит список иконок рабочего стола (SysListView32) или возвращает IntPtr.Zero.</summary>
    public static IntPtr FindListView()
    {
        IntPtr defView = IntPtr.Zero;

        IntPtr progman = NativeMethods.FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            defView = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        }

        if (defView == IntPtr.Zero)
        {
            NativeMethods.EnumWindows((top, _) =>
            {
                IntPtr found = NativeMethods.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (found != IntPtr.Zero)
                {
                    defView = found;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        }

        if (defView == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    /// <summary>
    /// Читает иконки рабочего стола. Координаты — в клиентской системе окна targetHwnd.
    /// При ошибке возвращает null и текст причины в error.
    /// </summary>
    public static IconLayout? Read(IntPtr targetHwnd, out string? error)
    {
        error = null;

        if (IntPtr.Size != 8)
        {
            error = "Читалка рассчитана на 64-битный процесс";
            return null;
        }

        IntPtr listView = FindListView();
        if (listView == IntPtr.Zero)
        {
            error = "SysListView32 не найден";
            return null;
        }

        // Если «Отображать значки рабочего стола» выключено, список скрыт: препятствий нет
        if (!IsWindowVisible(listView))
        {
            return new IconLayout(Array.Empty<IconInfo>(), listViewVisible: false);
        }

        GetWindowThreadProcessId(listView, out uint pid);
        IntPtr process = OpenProcess(
            PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION,
            false, pid);
        if (process == IntPtr.Zero)
        {
            error = $"OpenProcess не удался, код Win32: {Marshal.GetLastWin32Error()}";
            return null;
        }

        IntPtr remote = IntPtr.Zero;
        try
        {
            remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)BufferSize,
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remote == IntPtr.Zero)
            {
                error = $"VirtualAllocEx не удался, код Win32: {Marshal.GetLastWin32Error()}";
                return null;
            }

            if (!Send(listView, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero, out IntPtr countRaw))
            {
                error = "LVM_GETITEMCOUNT: Проводник не ответил";
                return null;
            }

            int count = (int)countRaw.ToInt64();
            if (count < 0 || count > MaxItems)
            {
                error = $"Подозрительное число иконок: {count}";
                return null;
            }

            var items = new List<IconInfo>(count);
            for (int i = 0; i < count; i++)
            {
                // 1. Границы (значок вместе с подписью)
                byte[] rectIn = new byte[16];
                BitConverter.GetBytes(LVIR_BOUNDS).CopyTo(rectIn, 0); // rect.left = код запроса
                if (!Write(process, remote + RectOffset, rectIn))
                {
                    error = "Не удалось записать RECT в Проводник";
                    return null;
                }

                if (!Send(listView, LVM_GETITEMRECT, new IntPtr(i), remote + RectOffset, out IntPtr okRect)
                    || okRect == IntPtr.Zero)
                {
                    error = $"LVM_GETITEMRECT не удался для иконки {i}";
                    return null;
                }

                byte[] rectOut = new byte[16];
                if (!Read(process, remote + RectOffset, rectOut))
                {
                    error = "Не удалось прочитать RECT из Проводника";
                    return null;
                }

                var rc = new RECT
                {
                    Left = BitConverter.ToInt32(rectOut, 0),
                    Top = BitConverter.ToInt32(rectOut, 4),
                    Right = BitConverter.ToInt32(rectOut, 8),
                    Bottom = BitConverter.ToInt32(rectOut, 12)
                };
                MapWindowPoints(listView, targetHwnd, ref rc, 2);

                // 2. Подпись
                byte[] item = new byte[88];
                BitConverter.GetBytes(LVIF_TEXT).CopyTo(item, 0);                         // mask
                BitConverter.GetBytes(i).CopyTo(item, 4);                                 // iItem
                BitConverter.GetBytes((remote + TextOffset).ToInt64()).CopyTo(item, 24);  // pszText
                BitConverter.GetBytes(TextChars).CopyTo(item, 32);                        // cchTextMax
                if (!Write(process, remote + ItemOffset, item))
                {
                    error = "Не удалось записать LVITEM в Проводник";
                    return null;
                }

                string name = "";
                if (Send(listView, LVM_GETITEMTEXTW, new IntPtr(i), remote + ItemOffset, out IntPtr len))
                {
                    int chars = Math.Clamp((int)len.ToInt64(), 0, TextChars - 1);
                    if (chars > 0)
                    {
                        byte[] text = new byte[chars * 2];
                        if (Read(process, remote + TextOffset, text))
                        {
                            name = Encoding.Unicode.GetString(text);
                        }
                    }
                }

                items.Add(new IconInfo(name, Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom)));
            }

            return new IconLayout(items, listViewVisible: true);
        }
        finally
        {
            if (remote != IntPtr.Zero)
            {
                VirtualFreeEx(process, remote, UIntPtr.Zero, MEM_RELEASE);
            }
            CloseHandle(process);
        }
    }

    private static bool Send(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        IntPtr ok = NativeMethods.SendMessageTimeout(
            hwnd, msg, wParam, lParam, SMTO_ABORTIFHUNG, TIMEOUT_MS, out result);
        return ok != IntPtr.Zero;
    }

    private static bool Write(IntPtr process, IntPtr address, byte[] data) =>
        WriteProcessMemory(process, address, data, (UIntPtr)data.Length, out _);

    private static bool Read(IntPtr process, IntPtr address, byte[] data) =>
        ReadProcessMemory(process, address, data, (UIntPtr)data.Length, out _);
}