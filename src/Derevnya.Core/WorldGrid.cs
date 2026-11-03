using System.Drawing;

namespace Derevnya.Core;

/// <summary>
/// Маска препятствий. Неизменяемая: при смене раскладки создаётся новая,
/// поэтому её безопасно читать из любого потока.
/// </summary>
public sealed class ObstacleMask
{
    private readonly bool[] _blocked;

    internal ObstacleMask(int tilesX, int tilesY, bool[] blocked, int version)
    {
        TilesX = tilesX;
        TilesY = tilesY;
        _blocked = blocked;
        Version = version;

        int count = 0;
        foreach (bool b in blocked)
        {
            if (b)
            {
                count++;
            }
        }
        BlockedCount = count;
    }

    public int TilesX { get; }
    public int TilesY { get; }

    /// <summary>0 — раскладка иконок ещё не принята (всё заблокировано); дальше растёт с каждой новой маской.</summary>
    public int Version { get; }

    public int BlockedCount { get; }

    /// <summary>Тайлы вне мира считаются заблокированными.</summary>
    public bool IsBlocked(int x, int y) =>
        x < 0 || y < 0 || x >= TilesX || y >= TilesY || _blocked[y * TilesX + x];
}

/// <summary>Сетка тайлов мира. Размер считается один раз при создании: один пиксель мира равен одному пикселю экрана.</summary>
public sealed class WorldGrid
{
    private ObstacleMask _mask;
    private int _version;

    public WorldGrid(Size screen, int tileSize)
    {
        ScreenSize = screen;
        TileSize = tileSize;
        TilesX = (screen.Width + tileSize - 1) / tileSize;
        TilesY = (screen.Height + tileSize - 1) / tileSize;

        var allBlocked = new bool[TilesX * TilesY];
        Array.Fill(allBlocked, true);
        _mask = new ObstacleMask(TilesX, TilesY, allBlocked, 0);
    }

    public Size ScreenSize { get; }
    public int TileSize { get; }
    public int TilesX { get; }
    public int TilesY { get; }

    /// <summary>Текущая маска. Можно читать из любого потока.</summary>
    public ObstacleMask Mask => Volatile.Read(ref _mask);

    /// <summary>
    /// Строит новую маску: тайл заблокирован, если пересекается с прямоугольником иконки
    /// или хоть частью лежит вне рабочей области. Вызывать после принятия раскладки.
    /// </summary>
    public ObstacleMask SetObstacles(IReadOnlyList<Rectangle> icons, Rectangle workArea)
    {
        var blocked = new bool[TilesX * TilesY];

        for (int ty = 0; ty < TilesY; ty++)
        {
            for (int tx = 0; tx < TilesX; tx++)
            {
                var tile = new Rectangle(tx * TileSize, ty * TileSize, TileSize, TileSize);
                bool isBlocked = !workArea.Contains(tile);

                if (!isBlocked)
                {
                    foreach (Rectangle icon in icons)
                    {
                        if (icon.Width > 0 && icon.Height > 0 && tile.IntersectsWith(icon))
                        {
                            isBlocked = true;
                            break;
                        }
                    }
                }

                blocked[ty * TilesX + tx] = isBlocked;
            }
        }

        var mask = new ObstacleMask(TilesX, TilesY, blocked, Interlocked.Increment(ref _version));
        Volatile.Write(ref _mask, mask);
        return mask;
    }

    /// <summary>Тайлы, которые накрывает прямоугольник в пикселях (для журнала): x, y, ширина и высота в тайлах.</summary>
    public Rectangle TilesOf(Rectangle pixels)
    {
        int x0 = Math.Clamp(pixels.Left / TileSize, 0, TilesX - 1);
        int y0 = Math.Clamp(pixels.Top / TileSize, 0, TilesY - 1);
        int x1 = Math.Clamp((pixels.Right - 1) / TileSize, 0, TilesX - 1);
        int y1 = Math.Clamp((pixels.Bottom - 1) / TileSize, 0, TilesY - 1);
        return new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }
}